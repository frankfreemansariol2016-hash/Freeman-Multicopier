using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.Core.Engine;

public sealed class DeviceProgressEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
    public required CopyItem Item { get; init; }
    public long BytesDone { get; init; }
    public long BytesPerSecond { get; init; }
}

public sealed class ItemStateEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
    public required CopyItem Item { get; init; }
    public required ItemState State { get; init; }
    public string? Error { get; init; }
}

public sealed class JobStateEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
    public required JobState State { get; init; }
    public string? Message { get; init; }
}

public enum EngineMessageKind
{
    Info,
    Ok,
    Error,
    NoSpace,
    Disconnected,
}

/// <summary>
/// Motor de copia simultanea.
///
/// La idea central: cuando varios dispositivos necesitan el MISMO archivo al mismo
/// tiempo, se unen a una misma <see cref="CopySession"/>. El pump lee el origen una vez
/// y reparte los bloques entre todos los destinos. Por eso copiar a 15 memorias no
/// cuesta 15 veces mas que copiar a una.
///
/// Cuando los dispositivos van cada uno por su cuenta (colas distintas, distintos
/// archivos) cada uno copia por separado, que es lo unico correcto posible.
/// </summary>
public sealed class MultiCopyEngine
{
    private readonly BufferBudget _budget;
    private readonly SemaphoreSlim _sessionSlots;

    /// <summary>Trabajos copiando ahora mismo. Lo consulta la sesion compartida.</summary>
    private int _runningJobs;

    /// <summary>Espacio libre por dispositivo, consultado una vez y no en cada archivo.</summary>
    private readonly ConcurrentDictionary<string, DriveInfo?> _drives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CopySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IWriteThrottle> _throttles = new(StringComparer.OrdinalIgnoreCase);

    public MultiCopyEngine(BufferBudget budget)
    {
        _budget = budget;
        _sessionSlots = new SemaphoreSlim(budget.MaxConcurrentSessions, budget.MaxConcurrentSessions);
    }

    public BufferBudget Budget => _budget;

    public event EventHandler<DeviceProgressEventArgs>? ProgressChanged;
    public event EventHandler<ItemStateEventArgs>? ItemStateChanged;
    public event EventHandler<JobStateEventArgs>? JobStateChanged;
    public event EventHandler<(EngineMessageKind Kind, string Message)>? Message;

    /// <summary>Fija el techo de velocidad de un dispositivo. 0 = sin limite.</summary>
    public void SetThrottle(string deviceId, long bytesPerSecond) =>
        _throttles.AddOrUpdate(deviceId,
            _ => bytesPerSecond <= 0 ? NoThrottle.Instance : new TokenBucketThrottle(bytesPerSecond),
            (_, old) => bytesPerSecond <= 0 ? NoThrottle.Instance : new TokenBucketThrottle(bytesPerSecond));

    private IWriteThrottle ThrottleFor(string deviceId) =>
        _throttles.GetValueOrDefault(deviceId, NoThrottle.Instance);

    /// <summary>
    /// Cuantos trabajos se estan copiando ahora mismo. La sesion compartida lo necesita
    /// para saber si vale la pena esperar a que se una otro destino: si solo hay un
    /// trabajo, no hay nadie mas que pueda unirse y esperar es tiempo perdido.
    /// </summary>
    public int RunningJobs => Volatile.Read(ref _runningJobs);

    public async Task RunAsync(CopyJob job)
    {
        var ct = job.Cancel.Token;
        job.StartedAtUtc ??= DateTime.UtcNow;
        job.FinishedAtUtc = null;
        Interlocked.Increment(ref _runningJobs);

        try
        {
            if (job.Mode is CopyMode.Mirror or CopyMode.Sync or CopyMode.RemoveDifferences)
                await ApplyDifferencesAsync(job, ct).ConfigureAwait(false);

            if (job.Mode == CopyMode.RemoveDifferences)
            {
                job.SetState(JobState.Completed);
                job.RecalculateTotals();
                return;
            }

            await CopyQueueAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.SetState(JobState.Cancelled);
        }
        catch (Exception ex)
        {
            job.SetState(JobState.Failed);
            Raise(EngineMessageKind.Error, $"{job.Device.Volume.Id}: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _runningJobs);
            job.FinishedAtUtc = DateTime.UtcNow;
            job.RecalculateTotals();
            SettleFinalState(job);
        }
    }

    private void SettleFinalState(CopyJob job)
    {
        JobState final;

        if (job.State == JobState.Cancelled) final = JobState.Cancelled;
        else if (job.FilesErrored > 0) final = JobState.CompletedWithErrors;
        else final = JobState.Completed;

        // Importante que se asigne en el trabajo y no solo se avise por evento: la tarjeta
        // de cada dispositivo lee esta propiedad, y si aqui solo se notificara, los
        // dispositivos seguirian mostrando "Trabajando" con la copia ya terminada.
        job.SetState(final);
        ReportState(job, final);
    }

    private async Task CopyQueueAsync(CopyJob job, CancellationToken ct)
    {
        // Foto del espacio libre de partida. A partir de aqui se muestra en vivo lo que
        // queda, restando byte a byte lo que este trabajo va escribiendo.
        job.CaptureFreeSpace();
        job.SetState(JobState.Working);
        ReportState(job, JobState.Working);

        foreach (var item in job.Queue.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            await job.WaitWhilePausedAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (item.State == ItemState.Completed) continue;

            await CopyOneAsync(job, item, ct).ConfigureAwait(false);
        }
    }

    private async Task CopyOneAsync(CopyJob job, CopyItem item, CancellationToken ct)
    {
        string destRoot = job.Device.Volume.Root;
        string destFull = Path.Combine(destRoot, item.DestRelative);

        if (!TryEnsureDirectory(job, item, destFull)) return;

        if (job.Structure is StructureKind.ZeroKb || item.Length == 0)
        {
            await CreateEmptyFileAsync(destFull, ct).ConfigureAwait(false);
            Complete(job, item);
            return;
        }

        if (job.Structure is StructureKind.EmptyDirs)
        {
            // Solo el arbol de carpetas: el archivo no se copia.
            item.State = ItemState.Skipped;
            item.BytesDone = 0;
            RaiseItem(job, item, ItemState.Skipped);
            return;
        }

        if (!HasRoom(job, item.Length))
        {
            item.State = ItemState.Error;
            item.Error = "SIN ESPACIO";
            RaiseItem(job, item, ItemState.Error);
            Raise(EngineMessageKind.NoSpace, $"SIN ESPACIO EN {job.Device.Volume.Id}");
            return;
        }

        if (IsUpToDate(destFull, item))
        {
            item.BytesDone = item.Length;
            item.State = ItemState.Skipped;
            RaiseItem(job, item, ItemState.Skipped);
            return;
        }

        item.State = ItemState.Copying;
        item.Error = null;
        item.BytesDone = 0;
        // Las velocidades son de ESTE archivo. Se ponen a cero para que el panel no
        // este enseñando la del archivo anterior mientras este aun no da un bloque.
        item.ReadBps = 0;
        item.WriteBps = 0;
        // A partir de aqui el trabajo lleva la cuenta por su cuenta, para que la barra de
        // la ventana pueda moverse cada pocos milisegundos sin recorrer la cola.
        job.BeginItem(item);
        RaiseItem(job, item, ItemState.Copying);

        var meter = new SpeedMeter();

        try
        {
            var throttle = ThrottleFor(job.Device.Volume.Id);

            // Intento 1: unirse a la sesion que ya esta leyendo ese archivo.
            var session = _sessions.GetOrAdd(item.SourcePath,
                _ => NewSession(item.SourcePath, item.Length, job.PlannedTargets));

            SessionMember? member = null;
            if (session.CanAcceptMembers)
            {
                member = CreateMember(destFull, throttle, meter, job, item, session);
            }

            if (member is not null && session.TryJoin(member))
            {
                // Ventana de union antes de arrancar: mientras esta abierta, los otros
                // destinos que lleguen con este mismo archivo se suman a la misma sesion y
                // el origen se lee una sola vez para todos.
                await session.WaitJoinWindowAsync().ConfigureAwait(false);
                session.Start();
                try
                {
                    await member.ConsumeAsync(ct).ConfigureAwait(false);
                    if (member.Error is not null) throw WrapIfCancelled(member.Error, ct);

                    item.BytesDone = item.Length;
                }
                finally
                {
                    member.Dispose();
                    session.ReleaseWriter();
                }
            }
            else
            {
                // Llego tarde, o nadie mas esta con ese archivo: copia en solitario.
                member?.Dispose();
                await CopyAloneAsync(job.Device.Volume.Id, item, destFull, throttle, meter, ct, job)
                    .ConfigureAwait(false);
            }

            ApplyTimestamp(destFull, item);
            Complete(job, item);
        }
        catch (OperationCanceledException)
        {
            // Se borra el archivo a medias y ademas se relanza la cancelacion. Si aqui se
            // tragase la excepcion, el trabajo terminaria "con exito" y el cliente veria un
            // dispositivo en verde con la memoria a la mitad, que es lo peor que puede pasar.
            item.State = ItemState.Cancelled;
            job.EndItem(item);
            RaiseItem(job, item, ItemState.Cancelled);
            TryDelete(destFull);
            throw;
        }
        catch (Exception ex)
        {
            item.State = ItemState.Error;
            item.Error = ex.Message;
            job.EndItem(item);
            RaiseItem(job, item, ItemState.Error, ex.Message);
        }
    }

    private SessionMember CreateMember(string destFull, IWriteThrottle throttle, SpeedMeter meter,
                                       CopyJob job, CopyItem item, CopySession session)
    {
        var fs = OpenDest(destFull);
        return new SessionMember(fs, throttle, session.FeedDepth, written =>
        {
            job.ReportBytes(item, written);
            long bps = meter.Sample(written);
            item.WriteBps = bps;
            ProgressChanged?.Invoke(this, new DeviceProgressEventArgs
            {
                DeviceId = job.Device.Volume.Id,
                Item = item,
                BytesDone = written,
                BytesPerSecond = bps,
            });
        }, job, item);
    }

    private CopySession NewSession(string sourcePath, long length, int plannedTargets)
    {
        // Ventana de union para que los destinos que van al MISMO archivo lean el
        // origen una vez entre todos en vez de una por destino. Sin esto, soltar un
        // archivo a cinco memorias lo leia cinco veces, y el origen se convertia en el
        // cuello de botella: en el banco, cuatro destinos al mismo archivo leian 2,6
        // GB/s en total en vez de los 0,9 GB/s que da una sola lectura.
        //
        // Solo se abre si el archivo es de los que tardan. En uno de 50 KB, 40 ms serian
        // la mitad del tiempo de copia y el otro destino tardaria mas en esperarnos
        // que en copiarlo por su cuenta; y con un solo destino no hay a quien esperar.
        bool hayMas = plannedTargets > 1;
        int window = (length >= 8L * 1024 * 1024 && hayMas) ? 40 : 0;
        var s = new CopySession(sourcePath, length, _budget, window,
                               () => hayMas || Volatile.Read(ref _runningJobs) > 1);
        s.Finished += OnSessionFinished;
        return s;
    }

    private void OnSessionFinished(CopySession session)
    {
        // La clave es la ruta de origen. Se borra solo si sigue siendo ESTA sesion la que
        // esta en el mapa: si otra se coloco despues, esa manda y esta se limita a
        // soltando sus recursos.
        string key = session.SourcePath;
        ((ICollection<KeyValuePair<string, CopySession>>)_sessions)
            .Remove(new KeyValuePair<string, CopySession>(key, session));

        session.Finished -= OnSessionFinished;
        session.Dispose();
    }

    private static Stream OpenDest(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                              bufferSize: 1,
                              options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task CopyAloneAsync(string deviceId, CopyItem item, string destFull,
                                      IWriteThrottle throttle, SpeedMeter meter, CancellationToken ct,
                                      CopyJob? job = null)
    {
        // Limite de sesiones simultaneas: cada una lleva su propia RAM.
        await _sessionSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var src = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                 bufferSize: 1,
                                                 options: FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using var dst = OpenDest(destFull);

            var readMeter = new SpeedMeter();
            byte[]? bufferA = CopyBlock.RentBuffer(_budget.BlockSize);
            byte[]? bufferB = CopyBlock.RentBuffer(_budget.BlockSize);
            try
            {
                long limit = item.Length;
                long written = 0;
                long readTotal = 0;

                int inA = await ReadBlockAsync(src, bufferA, limit, ct).ConfigureAwait(false);
                readTotal += inA;
                item.ReadBps = readMeter.Sample(readTotal);

                while (inA > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    // Pausa bloque a bloque, por el mismo motivo que en la sesion.
                    if (job is not null) await job.WaitWhilePausedAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    // Aqui esta el arreglo de verdad. La lectura del bloque siguiente
                    // arranca ANTES de escribir este, en el otro buffer. Antes iban en
                    // serie: se leia, se escribia, se leia, se escribia, y en serie la
                    // copia va a la SUMA de las dos velocidades, no a la mas rapida. Con
                    // el banco se veia: 155 MB/s en solitario y 51 con la lectura y la
                    // escritura encadenadas.
                    Task<int> next = ReadBlockAsync(src, bufferB, limit - (written + inA), ct);

                    await throttle.WaitAsync(inA, ct).ConfigureAwait(false);
                    await dst.WriteAsync(bufferA.AsMemory(0, inA), ct).ConfigureAwait(false);

                    written += inA;
                    long bps = meter.Sample(written);
                    item.WriteBps = bps;
                    job?.ReportBytes(item, written);
                    ProgressChanged?.Invoke(this, new DeviceProgressEventArgs
                    {
                        DeviceId = deviceId,
                        Item = item,
                        BytesDone = written,
                        BytesPerSecond = bps,
                    });

                    int inB = await next.ConfigureAwait(false);
                    readTotal += inB;
                    item.ReadBps = readMeter.Sample(readTotal);

                    (bufferA, bufferB) = (bufferB, bufferA);
                    inA = inB;
                }
            }
            finally
            {
                if (bufferA is not null) CopyBlock.ReturnBuffer(bufferA);
                if (bufferB is not null) CopyBlock.ReturnBuffer(bufferB);
            }
        }
        finally
        {
            _sessionSlots.Release();
        }
    }

    /// <summary>Lee hasta llenar el bloque. Un ReadAsync puede devolver menos de lo pedido.</summary>
    private static async Task<int> ReadBlockAsync(FileStream src, byte[]? buffer, long remaining,
                                               CancellationToken ct)
    {
        if (buffer is null || remaining <= 0) return 0;
        int want = (int)Math.Min(buffer.Length, remaining);
        int total = 0;
        while (total < want)
        {
            int r = await src.ReadAsync(buffer.AsMemory(total, want - total), ct).ConfigureAwait(false);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    // ---------------------------------------------------------------- modos

    /// <summary>
    /// Calcula que hay de mas en el destino y lo quita. En Sync intenta mover en vez de
    /// borrar cuando el mismo archivo ya esta en otra carpeta del destino.
    /// </summary>
    private async Task ApplyDifferencesAsync(CopyJob job, CancellationToken ct)
    {
        string destRoot = job.Device.Volume.Root;
        var wanted = new HashSet<string>(job.Queue.Select(i => i.DestRelative), StringComparer.OrdinalIgnoreCase);

        // Primero los archivos y despues las carpetas, y las carpetas de dentro hacia
        // fuera. Al reves, una carpeta que todavia tiene dentro un archivo sobrante no se
        // podria borrar, y en el siguiente espejo ya no habria manera de quitarla porque
        // el archivo interior ya no estara.
        foreach (var file in EnumerateFilesSafe(destRoot))
        {
            ct.ThrowIfCancellationRequested();
            string rel = StructureResolver.CleanRelative(StructureResolver.RelClamped(file, destRoot));
            if (rel.Length == 0) continue;
            if (wanted.Contains(rel)) continue;

            string full = Path.Combine(destRoot, rel);

            if (job.Mode == CopyMode.Sync && TryRelocate(destRoot, wanted, rel, out string? movedTo))
            {
                if (movedTo is not null && string.Equals(movedTo, full, StringComparison.OrdinalIgnoreCase))
                {
                    // Ya estaba en su sitio: solo falta actualizar la fecha.
                    var planned = job.Queue.First(i =>
                        string.Equals(i.DestRelative, rel, StringComparison.OrdinalIgnoreCase));
                    ApplyTimestamp(full, planned);
                    continue;                }
            }

            TryDelete(full);
        }

        var dirs = EnumerateDirsSafe(destRoot).ToList();
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length));   // las mas hondas primero
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            string rel = StructureResolver.CleanRelative(StructureResolver.RelClamped(dir, destRoot));
            if (rel.Length == 0) continue;

            bool keepDir = job.Queue.Any(i =>
                i.DestRelative.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase));

            if (!keepDir) TryDeleteDir(dir);
        }

        if (job.Mode == CopyMode.Sync)
        {
            // Tras mover, los destinos planificados que no existen se re-apuntan al archivo real.
            foreach (var item in job.Queue)
            {
                string full = Path.Combine(destRoot, item.DestRelative);
                if (File.Exists(full) || !File.Exists(item.SourcePath)) continue;
                var byName = job.Queue.FirstOrDefault(i =>
                    string.Equals(Path.GetFileName(i.DestRelative), Path.GetFileName(item.DestRelative),
                                  StringComparison.OrdinalIgnoreCase));
                if (byName is not null) item.SourcePath = byName.SourcePath;
            }
        }
    }

    private static bool TryRelocate(string destRoot, HashSet<string> wanted, string rel,
                                    out string? movedTo)
    {
        movedTo = null;
        string fileName = Path.GetFileName(rel);
        foreach (var candidate in wanted)
        {
            if (!string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(destRoot, candidate);
            if (File.Exists(target)) { movedTo = target; return true; }
        }
        return false;
    }

    // ---------------------------------------------------------------- util

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int guard = 0;
        while (stack.Count > 0)
        {
            if (++guard > 200_000) yield break;
            string cur = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(cur); } catch { continue; }
            foreach (var f in files) yield return f;
            string[] dirs;
            try { dirs = Directory.GetDirectories(cur); } catch { continue; }
            foreach (var d in dirs) stack.Push(d);
        }
    }

    private static IEnumerable<string> EnumerateDirsSafe(string root)
    {
        var stack = new Stack<string>();
        string[] top;
        try { top = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var d in top) stack.Push(d);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            yield return cur;
            string[] dirs;
            try { dirs = Directory.GetDirectories(cur); } catch { continue; }
            foreach (var d in dirs) stack.Push(d);
        }
    }

    private bool TryEnsureDirectory(CopyJob job, CopyItem item, string destFile)
    {
        string? dir = Path.GetDirectoryName(destFile);
        if (string.IsNullOrEmpty(dir)) return true;
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception ex)
        {
            item.State = ItemState.Error;
            item.Error = ex.Message;
            RaiseItem(job, item, ItemState.Error, ex.Message);
            return false;
        }
    }

    private static async Task CreateEmptyFileAsync(string destFull, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(destFull);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destFull) && new FileInfo(destFull).Length == 0) return;
        await using var fs = new FileStream(destFull, FileMode.Create, FileAccess.Write, FileShare.Read,
                                            bufferSize: 1, options: FileOptions.Asynchronous);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyTimestamp(string destFull, CopyItem item)
    {
        if (item.SourceWriteTimeUtc == default) return;
        try { File.SetLastWriteTimeUtc(destFull, item.SourceWriteTimeUtc); }
        catch { /* algunos volumenes no lo permiten */ }
    }

    private static bool IsUpToDate(string destFull, CopyItem item)
    {
        try
        {
            var fi = new FileInfo(destFull);
            if (!fi.Exists) return false;
            if (fi.Length != item.Length) return false;
            if (item.SourceWriteTimeUtc == default) return true;
            // Tolerancia de 2 s: los volumenes FAT guardan la fecha con poco detalle.
            return Math.Abs((fi.LastWriteTimeUtc - item.SourceWriteTimeUtc).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    /// <summary>
    /// Espacio libre del destino. El DriveInfo se construye una vez por dispositivo y
    /// se guarda: AvailableFreeSpace es una llamada al sistema, y preguntar por cada
    /// archivo hacia que una carpeta de mil imagenes se notase en la barra de progreso.
    /// </summary>
    private bool HasRoom(CopyJob job, long incoming)
    {
        string id = job.Device.Volume.Id;
        DriveInfo? di;
        if (!_drives.TryGetValue(id, out di))
        {
            di = null;
            try
            {
                string? root = Path.GetPathRoot(job.Device.Volume.Root);
                if (!string.IsNullOrEmpty(root)) di = new DriveInfo(root);
            }
            catch { }
            _drives[id] = di;
        }

        if (di is null) return true;
        try
        {
            return di.AvailableFreeSpace > incoming + (8L * 1024 * 1024);
        }
        catch { return true; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch { }
    }

    private static Exception WrapIfCancelled(Exception ex, CancellationToken ct) =>
        ct.IsCancellationRequested ? new OperationCanceledException(ct) : ex;

    private void Complete(CopyJob job, CopyItem item)
    {
        item.State = ItemState.Completed;
        item.BytesDone = item.Length;
        job.EndItem(item);
        job.RecalculateTotals();
        RaiseItem(job, item, ItemState.Completed);
    }

    private void RaiseItem(CopyJob job, CopyItem item, ItemState state, string? error = null) =>
        ItemStateChanged?.Invoke(this, new ItemStateEventArgs
        {
            DeviceId = job.Device.Volume.Id,
            Item = item,
            State = state,
            Error = error,
        });

    private void ReportState(CopyJob job, JobState state) =>
        JobStateChanged?.Invoke(this, new JobStateEventArgs { DeviceId = job.Device.Volume.Id, State = state });

    private void Raise(EngineMessageKind kind, string message) =>
        Message?.Invoke(this, (kind, message));

}

/// <summary>
/// Velocidad suavizada, con ventana corta y media movil. Se usa tanto para lo que se
/// lee del origen como para lo que se escribe en cada destino, y por eso vive aqui y
/// no dentro del motor: lo necesita tambien la sesion de copia compartida.
/// </summary>
internal sealed class SpeedMeter
{
    /// <summary>
    /// 150 ms. Con la ventana de un cuarto de segundo de antes, al final de un archivo
    /// grande la cifra mostrada era el promedio de toda la copia, y por eso un disco que
    /// iba a 150 MB/s aparecia a 39: el numero era cierto y no servia para nada.
    /// </summary>
    private const int WindowMs = 150;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastBytes;
    private long _lastTicks;
    private double _ema;

    public long Sample(long bytes)
    {
        long ticks = _clock.ElapsedTicks;
        long elapsed = ticks - _lastTicks;
        long minTicks = Stopwatch.Frequency * WindowMs / 1000;
        if (elapsed < minTicks) return (long)_ema;

        long delta = bytes - _lastBytes;
        _lastBytes = bytes;
        _lastTicks = ticks;
        if (delta < 0) delta = 0;

        double instant = delta * (double)Stopwatch.Frequency / elapsed;
        _ema = _ema == 0 ? instant : _ema * 0.45 + instant * 0.55;
        return (long)_ema;
    }
}
