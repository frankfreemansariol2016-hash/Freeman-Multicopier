using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FreemanMultiCopier.Core.Model;

/// <summary>
/// El trabajo de copia hacia UN dispositivo. Contiene su propia cola con las rutas
/// ya resueltas, por eso dos dispositivos con la misma lista siguen siendo independientes.
/// </summary>
public sealed class CopyJob : INotifyPropertyChanged
{
    public required DeviceState Device { get; init; }

    private CopyMode _mode = CopyMode.Normal;
    private StructureKind _structure;
    private JobState _state = JobState.Idle;
    private long _totalToProcess;

    /// <summary>
    /// Bytes ya escritos. Se toca desde los hilos de copia, asi que siempre con
    /// Interlocked. Los avisos de cambio los da la ventana, nunca aqui.
    /// </summary>
    private long _bytesCopiedRaw;

    /// <summary>
    /// Bytes REALMENTE escritos en el destino. Distinto de <see cref="_bytesCopiedRaw"/>:
    /// un archivo que ya estaba al dia se salta sin ocupar nada, y en Espejo ademas se
    /// borra, que devuelve espacio. Para decir cuanto queda libre solo vale este.
    /// </summary>
    private long _bytesWrittenRaw;

    /// <summary>Espacio libre medido en el destino al empezar el trabajo.</summary>
    private long _freeSpaceAtStart = -1;

    private int _filesCompleted;
    private int _filesErrored;
    private CopyItem? _active;

    public CopyMode Mode
    {
        get => _mode;
        set { if (_mode != value) { _mode = value; Raise(); } }
    }

    public StructureKind Structure
    {
        get => _structure;
        set { if (_structure != value) { _structure = value; Raise(); } }
    }

    public ObservableCollection<CopyItem> Queue { get; } = new();

    public long TotalToProcess
    {
        get => _totalToProcess;
        private set { if (_totalToProcess != value) { _totalToProcess = value; Raise(); } }
    }

    public long BytesCopied
    {
        get => Interlocked.Read(ref _bytesCopiedRaw);
        private set { if (Interlocked.Exchange(ref _bytesCopiedRaw, value) != value) Raise(nameof(Progress)); }
    }

    public int FilesCompleted
    {
        get => _filesCompleted;
        private set { if (_filesCompleted != value) { _filesCompleted = value; Raise(); } }
    }

    public int FilesErrored
    {
        get => _filesErrored;
        private set { if (_filesErrored != value) { _filesErrored = value; Raise(); } }
    }

    public JobState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; Raise(); } }
    }

    /// <summary>
    /// Cuantos destinos van a copiar esta misma lista. Lo sabe quien la reparte, no el
    /// motor: el motor solo ve un trabajo cada vez.
    /// </summary>
    /// <remarks>
    /// Antes se preguntaba al motor cuantos trabajos tenia en marcha para decidir si
    /// merecia la pena esperar a que se unieran, y eso nunca daba true en el caso que
    /// mas importa: al soltar cinco archivos a la vez, el primer trabajo llega a abrir
    /// su sesion en el mismo instante en que se reparten, y para entonces el contador
    /// de trabajos en marcha todavia vale uno. Con este numero no hay carrera: quien
    /// reparte ya sabe cuantos son.
    /// </remarks>
    public int PlannedTargets { get; set; } = 1;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public CancellationTokenSource Cancel { get; private set; } = new();

    private readonly PauseGate _pause = new();

    public double Progress => TotalToProcess <= 0 ? 0d : Math.Clamp((double)BytesCopied / TotalToProcess, 0d, 1d);

    /// <summary>Archivo que se esta copiando ahora mismo en este dispositivo, si hay alguno.</summary>
    public CopyItem? ActiveItem => Volatile.Read(ref _active);

    public string ActiveFileName => ActiveItem?.FileName ?? string.Empty;

    /// <summary>Ruta completa del archivo que se esta leyendo del origen ahora mismo.</summary>
    public string ActiveSourcePath => ActiveItem?.SourcePath ?? string.Empty;

    /// <summary>
    /// Velocidad de LECTURA del origen para el archivo en marcha. Es la misma en todos
    /// los destinos que comparten ese archivo, porque el origen se lee una sola vez.
    /// </summary>
    public long ActiveReadBps => ActiveItem?.ReadBps ?? 0L;

    /// <summary>Velocidad de ESCRITURA en este destino para el archivo en marcha.</summary>
    public long ActiveWriteBps => ActiveItem?.WriteBps ?? 0L;

    /// <summary>Progreso del archivo actual, de 0 a 1. Es la barra fina del panel izquierdo.</summary>
    public double ActiveFraction => ActiveItem?.Fraction ?? 0d;

    /// <summary>Archivos que tiene la cola. Lo lee la barra gruesa.</summary>
    public int FilesTotal => Queue.Count;

    /// <summary>Cuanto se ha ocupado de verdad en el destino, en bytes.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWrittenRaw);

    /// <summary>
    /// Espacio libre que queda en el destino AHORA MISMO, restando lo que se ha escrito.
    /// Windows no da un dato instantaneo sin una llamada por bloque, que es justo lo que
    /// no se quiere en un copiador, asi que se parte del espacio medido al empezar y se
    /// descuenta byte a byte. No es una aproximacion: el total de bytes escritos
    /// no miente, y el espacio de partida lo midio el inspector al enumerar.
    /// Fuera de un trabajo en marcha devuelve el valor real medido por el vigilante.
    /// </summary>
    public long FreeSpaceLive
    {
        get
        {
            if (_freeSpaceAtStart < 0) return Device.Volume.FreeSpace;
            if (State is not (JobState.Working or JobState.Paused or JobState.Queued))
                return Device.Volume.FreeSpace;
            return Math.Max(0, _freeSpaceAtStart - BytesWritten);
        }
    }

    /// <summary>Se fija una vez al empezar el trabajo: es la foto del espacio de partida.</summary>
    public void CaptureFreeSpace() =>
        Interlocked.Exchange(ref _freeSpaceAtStart, Math.Max(0, Device.Volume.FreeSpace));

    /// <summary>
    /// Un archivo entra en marcha. Se descuenta el que estaba activo para que el total
    /// advance solo: asi la barra se mueve sin tener que recorrer la cola entera.
    /// </summary>
    public void BeginItem(CopyItem item)
    {
        var previous = Interlocked.Exchange(ref _active, item);
        if (previous is not null) Interlocked.Add(ref _bytesCopiedRaw, -previous.BytesDone);
        Interlocked.Add(ref _bytesCopiedRaw, item.BytesDone);
    }

    /// <summary>
    /// Avance de bytes del archivo en curso. Lo llaman los hilos de copia VARIAS VECES por
    /// bloque, asi que no recorre nada y no avisa a nadie: solo mueve contadores. Quien
    /// avisa a la interfaz es el temporizador de la ventana, en su hilo.
    /// </summary>
    public void ReportBytes(CopyItem item, long written)
    {
        long previous = item.BytesDone;
        item.BytesDone = written;
        if (ReferenceEquals(ActiveItem, item))
        {
            Interlocked.Add(ref _bytesCopiedRaw, written - previous);
            // El espacio que se ocupa va por su cuenta: el total copiado incluye lo saltado.
            Interlocked.Add(ref _bytesWrittenRaw, written - previous);
        }
    }

    /// <summary>El archivo activo termino. Ya no hay nada que destacar en la barra fina.</summary>
    public void EndItem(CopyItem item)
    {
        if (!ReferenceEquals(Interlocked.Exchange(ref _active, null), item)) return;
        Interlocked.Add(ref _bytesCopiedRaw, -item.BytesDone);
    }

    /// <summary>
    /// Aviso de cambio sin recorrer la cola. Es lo que llama el temporizador rapido: leer
    /// unos contadores yRaises es trabajo de microsegundos, asi que se puede refrescar
    /// muchas veces por segundo sin quitarle tiempo al motor de copia.
    /// </summary>
    public void RefreshLight()
    {
        Raise(nameof(BytesCopied));
        Raise(nameof(Progress));
        Raise(nameof(Elapsed));
        Raise(nameof(Remaining));
        Raise(nameof(ActiveFileName));
        Raise(nameof(ActiveFraction));
        Raise(nameof(ActiveSourcePath));
        Raise(nameof(ActiveReadBps));
        Raise(nameof(ActiveWriteBps));
        Raise(nameof(BytesWritten));
        Raise(nameof(FreeSpaceLive));
    }

    public TimeSpan Elapsed => StartedAtUtc is null
        ? TimeSpan.Zero
        : (FinishedAtUtc ?? DateTime.UtcNow) - StartedAtUtc.Value;

    public TimeSpan? Remaining
    {
        get
        {
            if (State != JobState.Working || StartedAtUtc is null) return null;
            var done = (DateTime.UtcNow - StartedAtUtc.Value).TotalSeconds;
            if (done < 0.5 || Progress <= 0.01) return null;
            double totalSeconds = done / Progress;
            double left = totalSeconds - done;
            return left <= 0 || left > TimeSpan.MaxValue.TotalSeconds ? null : TimeSpan.FromSeconds(left);
        }
    }

    /// <summary>
    /// Recalcula los contadores y avisa a la interfaz. Se llama desde un temporizador de la
    /// propia ventana: recorrer la cola cuatro veces por segundo es barato en cuanto al
    /// trabajo de mas valor que hace la aplicacion, que es copiar.
    /// </summary>
    public void Refresh()
    {
        long total = 0, done = 0;
        int ok = 0, bad = 0;
        foreach (var i in Queue)
        {
            total += i.Length;
            done += i.BytesDone;
            if (i.State == ItemState.Completed) ok++;
            else if (i.State == ItemState.Error) bad++;
        }

        TotalToProcess = total;
        BytesCopied = done;
        FilesCompleted = ok;
        FilesErrored = bad;
        Raise(nameof(Progress));
        Raise(nameof(Elapsed));
        Raise(nameof(Remaining));
    }

    public void RecalculateTotals() => Refresh();

    public void ResetForNextRun()
    {
        foreach (var item in Queue)
        {
            item.State = ItemState.Waiting;
            item.BytesDone = 0;
            item.Error = null;
        }
        Interlocked.Exchange(ref _active, null);
        Interlocked.Exchange(ref _bytesCopiedRaw, 0);
        Interlocked.Exchange(ref _bytesWrittenRaw, 0);
        Interlocked.Exchange(ref _freeSpaceAtStart, -1);
        StartedAtUtc = null;
        FinishedAtUtc = null;
        _state = JobState.Idle;
        State = JobState.Idle;
        Refresh();
    }

    /// <summary>
    /// Espera si esta en pausa. Se llama en CADA bloque del bucle de copia, no solo entre
    /// archivos: si se mirase solo entre archivos, pausar con una pelicula de 4 GB en
    /// marcha no haria nada hasta los 4 GB, y eso no es una pausa.
    /// Distingue pausa de cancelacion porque el token de cancelacion es el de este trabajo.
    /// </summary>
    public async Task WaitWhilePausedAsync()
    {
        if (!_pause.IsPaused)
        {
            if (_state == JobState.Paused) SetState(JobState.Working);
            return;
        }

        SetState(JobState.Paused);
        await _pause.WaitAsync(Cancel.Token).ConfigureAwait(false);

        if (_state == JobState.Paused) SetState(JobState.Working);
    }

    public void Resume()
    {
        if (_state == JobState.Paused) SetState(JobState.Working);
        _pause.Release();
    }

    public void Pause() => _pause.Pause();

    public void SetState(JobState s) => State = s;

    /// <summary>
    /// Vacia la cola. Solo tiene sentido con el trabajo parado: si se llama en mitad de una
    /// copia, los archivos que faltan se quedarian a medias en el destino.
    /// </summary>
    public void ClearQueue()
    {
        if (State is JobState.Working or JobState.Paused) return;

        Queue.Clear();
        ResetForNextRun();
    }

    public void ResetCancel()
    {
        if (Cancel.IsCancellationRequested)
        {
            Cancel.Dispose();
            Cancel = new CancellationTokenSource();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Semaforo de pausa cooperativo. Sin esperas activas ni plumberias de hilos.
///
/// Se consulta DENTRO del bucle de copia, no solo entre un archivo y el siguiente. Antes
/// solo se miraba entre archivos, asi que pausar con una pelicula de 4 GB en marcha no
/// hacia nada hasta acabar los 4 GB, que no es lo que nadie entiende por "pausar".
/// </summary>
public sealed class PauseGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _paused;

    public bool IsPaused => _paused;

    /// <summary>
    /// Pausa. Toma el semaforo en vez de solo poner una bandera: el que lo tiene es el que
    /// puede pasar, asi que tomarlo es justamente lo que deja parados a los que esperan.
    /// Con solo la bandera, <see cref="WaitAsync"/> comprobaba, veía que si, y acto
    /// seguido devolvia el semaforo sin haber esperado de verdad a nada.
    /// </summary>
    public void Pause()
    {
        if (_paused) return;
        _paused = true;

        // No puede bloquear: el contador esta a 1 y solo lo suelta Release().
        _gate.Wait();
    }

    public void Release()
    {
        if (!_paused) return;
        _paused = false;
        try { _gate.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Se queda parado mientras siga en pausa. Vuelve solo cuando hay Release.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        if (!_paused) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        // No se devuelve el semaforo: Release() ya lo ha repuesto, y si lo devolvieramos
        // aqui, un segundo escritor pasaria mientras el primero sigue esperando.
    }
}
