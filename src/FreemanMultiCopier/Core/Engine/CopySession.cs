using System.IO;
using System.Threading.Channels;

namespace FreemanMultiCopier.Core.Engine;

/// <summary>
/// Sesion de copia de UN archivo a VARIOS destinos a la vez.
/// El canal es de un solo lector y N consumidores: el pump lee el origen una unica vez
/// y cada bloque lo consume exactamente un escritor. La memoria de la sesion es
/// capacidad x tamano de bloque, independiente de cuantos dispositivos se enganchen.
/// </summary>
internal sealed class CopySession : IDisposable
{
    private readonly List<SessionMember> _members = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly string _sourcePath;
    private readonly BufferBudget _budget;
    private readonly TimeSpan _joinWindow;
    private readonly Func<bool>? _someoneElseRunning;
    private readonly SpeedMeter _readMeter = new SpeedMeter();
    private Task? _pump;
    private long _length;
    private int _writersPending;
    private int _finishedRaised;
    private bool _disposed;
    private bool _accepting = true;
    private SessionMember[]? _fixed;

    /// <summary>
    /// Se dispara cuando ya no queda nadie escribiendo en esta sesion: el pump termino y
    /// todos los destinos soltaron su bloque. El motor usa esto para quitar la sesion del
    /// mapa, porque si se queda dentro, el siguiente archivo igual que se copie ira por la
    /// sesion vieja y acabaria copiandose en solitario aunque quepan quince dispositivos.
    /// </summary>
    public event Action<CopySession>? Finished;

    /// <param name="joinWindowMs">
    /// Margen para que se unan los destinos que llegan en el mismo instante. Si se pone a
    /// cero, la sesion empieza a leer en cuanto se une el primero.
    /// </param>
    /// <param name="someoneElseRunning">
    /// Si dice que no hay ningun otro trabajo copiando, el margen se salta. Antes se
    /// espero el plazo entero siempre, y con un solo destino eso eran 100 ms perdidos
    /// en cada archivo sin que nadie se uniera nunca.
    /// </param>
    public CopySession(string sourcePath, long length, BufferBudget budget, int joinWindowMs = 100,
                        Func<bool>? someoneElseRunning = null)
    {
        _sourcePath = sourcePath;
        _length = length;
        _budget = budget;
        _joinWindow = TimeSpan.FromMilliseconds(joinWindowMs);
        _someoneElseRunning = someoneElseRunning;

        // El token se copia aqui y no se vuelve a leer de la fuente. Dispose puede llegar
        // mientras el pump sigue vivo, y leer _cts.Token sobre un CTS ya liberado lanza.
        _token = _cts.Token;
    }

    public string SourcePath => _sourcePath;

    public long Length => Volatile.Read(ref _length);

    public int MemberCount { get { lock (_gate) return _members.Count; } }

    public bool IsRunning => _pump is { IsCompleted: false };

    public bool HasStarted { get { lock (_gate) return _pump is not null; } }

    /// <summary>
    /// Si se puede sumar un destino todavia. Mientras el pump esta en su margen de union
    /// se sigue admitiendo, que es justo lo que hace que quince memorias conectadas a la
    /// vez lean el origen una sola vez. En cuanto el pump empieza a repartir bloques se
    /// cierra: un destino que llegase tarde se quedaria sin la parte del archivo que ya
    /// se ha repartido, y es peor que copie por su cuenta.
    /// </summary>
    public bool CanAcceptMembers { get { lock (_gate) return _accepting; } }

    public Exception? SourceError { get; private set; }

    /// <summary>Cuantos bloques caben en la cola de cada destino.</summary>
    public int FeedDepth => _budget.MaxBlocksPerSession;


    /// <summary>
    /// Da de alta un destino antes de que arranque el pump. Devuelve false si ya empezo
    /// a leer: el que llega tarde copia por su cuenta, no se pierde ni se duplica nada.
    /// </summary>
    public bool TryJoin(SessionMember member)
    {
        lock (_gate)
        {
            if (!_accepting) return false;
            _members.Add(member);
            _writersPending++;
            return true;
        }
    }

    /// <summary>Un destino termino (bien o mal). Cuenta hacia el cierre de la sesion.</summary>
    public void ReleaseWriter()
    {
        int remaining;
        lock (_gate) remaining = --_writersPending;
        if (remaining <= 0) RaiseFinished();
    }

    /// <summary>
    /// Arranca el pump y cierra la lista de destinos de una vez.
    /// </summary>
    /// <remarks>
    /// La puerta se cierra AQUI y no mas adelante, en el pump, porque la lista tiene que
    /// ser exactamente la que el pump va a usar. Si se cerrara dentro de la tarea del
    /// pump, un destino que llegase entre medias entraria a consumir bloques cuyo
    /// recuento de referencias no lo tiene: el ultimo en soltarlo devolveria el buffer
    /// al reutilizador mientras ese destino seguia escribiendo desde el, y el pump se lo
    /// podria volver a prestar para el bloque siguiente. Salia un archivo hecho polvo
    /// sin ningun error de ningun tipo. Por eso quien se une espera la ventana y luego
    /// arranca, y desde este punto ya no entra nadie mas.
    /// </remarks>
    public void Start()
    {
        lock (_gate)
        {
            if (_pump is not null) return;
            _accepting = false;
            _fixed = _members.ToArray();
            _pump = Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        var ct = _token;

        // La lista la fijo Start() antes de lanzar esta tarea: estos son todos los
        // destinos que van a recibir bloques, ni uno mas ni uno menos. Se lee aqui y
        // no dentro del try porque los bloques catch y finally tambien tienen que
        // poder cerrar las colas de todos.
        SessionMember[] members = _fixed ?? Array.Empty<SessionMember>();
        int memberCount = Math.Max(1, members.Length);

        try
        {
            using var src = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           bufferSize: 1,
                                           options: FileOptions.SequentialScan | FileOptions.Asynchronous);

            long total = new FileInfo(_sourcePath).Length;
            Volatile.Write(ref _length, total);
            if (total == 0)
            {
                foreach (var m in members) m.CompleteFeed();
                return;
            }

            var blockSize = _budget.BlockSize;
            long offset = 0;

            while (offset < total)
            {
                ct.ThrowIfCancellationRequested();

                int want = (int)Math.Min(blockSize, total - offset);
                byte[] rented = CopyBlock.RentBuffer(want);

                int read;
                try
                {
                    read = await ReadFullyAsync(src, rented, want, ct).ConfigureAwait(false);
                }
                catch
                {
                    CopyBlock.ReturnBuffer(rented);
                    throw;
                }

                if (read == 0)
                {
                    CopyBlock.ReturnBuffer(rented);
                    break;
                }

                // La propiedad del buffer pasa al bloque. El pump ya no lo suelta.
                var block = CopyBlock.FromRented(rented, read, memberCount);

                // Y aqui esta el reparto que faltaba: el MISMO bloque va a la cola de
                // cada destino. Con una sola cola comun, el primer destino en leer se
                // lo llevaba y los demas se quedaban sin el: con ocho destinos cada
                // uno se llevaba una octava parte del archivo y los otros siete se
                // quedaban con un archivo a medias que el motor daba por bueno.
                // Las colas guardan referencias al mismo bloque, asi que la memoria
                // sigue siendo una sola copia: lo unico que se multiplica es la ranura
                // de la cola, que son unos cuantos bytes por bloque y destino.
                foreach (var m in members) await m.FeedAsync(block, ct).ConfigureAwait(false);

                offset += read;

                // A que velocidad esta saliendo este archivo del origen. Se publica en
                // los archivos de TODOS los destinos de la sesion, y es la misma cifra
                // para todos: la lectura del origen es una sola, y lo que se reparte
                // despues son trozos de esos mismos bytes. Asi el panel puede decir que
                // el origen va a 60 MB/s aunque en el destino de al lado bajen 12.
                long readBps = _readMeter.Sample(offset);
                for (int i = 0; i < members.Length; i++) members[i].PublishReadRate(readBps);
            }

            foreach (var m in members) m.CompleteFeed();
        }
        catch (OperationCanceledException)
        {
            foreach (var m in members) m.CompleteFeed();
        }
        catch (Exception ex)
        {
            SourceError = ex;
            foreach (var m in members) m.CompleteFeed(ex);
        }
        finally
        {
            bool nobodyWaiting;
            lock (_gate) nobodyWaiting = _writersPending <= 0;
            if (nobodyWaiting) RaiseFinished();
        }
    }

    private void RaiseFinished()
    {
        if (Interlocked.Exchange(ref _finishedRaised, 1) == 1) return;
        Finished?.Invoke(this);
    }

    private static async Task<int> ReadFullyAsync(Stream src, byte[] buffer, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int r = await src.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    /// <summary>
    /// Margen para que los destinos que llegan en el mismo instante se junten antes de la
    /// primera lectura. Es lo que hace que quince memorias iguales conectadas a la vez lean
    /// el origen una sola vez.
    /// </summary>
    /// <remarks>
    /// La espera la hace el destino que se une primero, y no el pump: el pump todavia no
    /// existe, y mientras este margen esta abierto la sesion admite a quien llegue. Con un
    /// solo trabajo en marcha el margen es cero y no se pierde nada. No se cancela: son
    /// 40 ms y asi el que se unio no puede quedarse a medias con un destino dentro.
    /// </remarks>
    public Task WaitJoinWindowAsync()
    {
        if (_joinWindow <= TimeSpan.Zero) return Task.CompletedTask;
        if (_someoneElseRunning is not null && !_someoneElseRunning()) return Task.CompletedTask;
        return Task.Delay(_joinWindow);
    }

    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        SessionMember[] members;
        lock (_gate) members = _members.ToArray();
        foreach (var m in members) m.CompleteFeed();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Cancel();
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// Un destino dentro de una sesion: su stream, su progreso y su limitador.
/// Si falla o se cancela NO deja de consumir: sigue drenando y descartando. Si dejara de
/// leer, el canal se llenaria y el pump se quedaria bloqueado para siempre, arrastrando
/// a los demas dispositivos.
/// </summary>
internal sealed class SessionMember : IDisposable
{
    private readonly Stream _dest;
    private readonly IWriteThrottle _throttle;
    private readonly Action<long>? _onProgress;
    private readonly Core.Model.CopyJob? _job;
    private readonly Core.Model.CopyItem? _item;

    // Cola propia de este destino. Cada destino recibe TODOS los bloques, no un
    // reparto: por eso tiene su cola. Todas las colas apuntan al mismo bloque, asi que
    // la RAM de una sesion sigue siendo una copia de los bloques, no una por destino.
    private readonly Channel<CopyBlock> _feed;

    public SessionMember(Stream dest, IWriteThrottle throttle, int feedDepth,
                         Action<long>? onProgress = null,
                         Core.Model.CopyJob? job = null, Core.Model.CopyItem? item = null)
    {
        _dest = dest;
        _throttle = throttle;
        _onProgress = onProgress;
        _job = job;
        _item = item;
        _feed = Channel.CreateBounded<CopyBlock>(new BoundedChannelOptions(feedDepth)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>El pump deja este bloque en la cola de este destino.</summary>
    public ValueTask FeedAsync(CopyBlock block, CancellationToken ct) =>
        _feed.Writer.WriteAsync(block, ct);

    /// <summary>Ya no habra mas bloques para este destino.</summary>
    public void CompleteFeed(Exception? error = null) => _feed.Writer.TryComplete(error);

    /// <summary>
    /// Velocidad a la que el pump esta sacando bytes del origen. Va al archivo de este
    /// destino: es la MISMA lectura para todos los que se unieron, porque el origen se
    /// lee una sola vez.
    /// </summary>
    public void PublishReadRate(long bytesPerSecond)
    {
        if (_item is not null) _item.ReadBps = bytesPerSecond;
    }

    public void Dispose()
    {
        try { _dest.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    public Exception? Error { get; private set; }

    public bool Failed => Error is not null;

    public long Written { get; private set; }

    public async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var block in _feed.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (Error is null)
                    {
                        // La pausa se comprueba aqui, bloque a bloque. Si solo se mirase
                        // entre archivos, un dispositivo en pausa seguiria escribiendo
                        // mientras el trabajo de la tarjeta dice "Pausado".
                        //
                        // Ojo con lo que pasa aqui: un destino en pausa deja de vaciar
                        // su cola, se llena, y cuando se llena el pump se para. Es decir,
                        // que pausar un destino detiene el ARCHIVO ENTERO, y con el se
                        // paran los demas destinos que van al mismo archivo. No se puede
                        // evitar sin inventar una copia de cada bloque por destino en
                        // memoria: un destino parado tendria que seguir guardando lo que
                        // le llega, y eso no tiene tope. Se asume que pausar es raro y
                        // que el que pausa quiere detener esa copia, no solo su tarjeta.
                        if (_job is not null) await _job.WaitWhilePausedAsync().ConfigureAwait(false);

                        await _throttle.WaitAsync(block.Length, ct).ConfigureAwait(false);
                        await _dest.WriteAsync(block.Buffer.AsMemory(0, block.Length), ct).ConfigureAwait(false);
                        Written += block.Length;
                        _onProgress?.Invoke(Written);
                    }
                }
                catch (OperationCanceledException) { Error ??= new OperationCanceledException(); }
                catch (Exception ex) { Error ??= ex; }
                finally
                {
                    // Siempre se suelta, haya ido bien o mal. Si no, el pool se agota.
                    block.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Error ??= new OperationCanceledException();
            await DrainAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error ??= ex;
        }
    }

    /// <summary>Consume y descarta lo que quede, para que el pump no se quede esperando.</summary>
    private async Task DrainAsync()
    {
        try
        {
            while (await _feed.Reader.WaitToReadAsync().ConfigureAwait(false))
                while (_feed.Reader.TryRead(out var block)) block.Release();
        }
        catch { }
    }
}
