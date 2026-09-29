using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using FreemanMultiCopier.Core.Devices;
using FreemanMultiCopier.Core.Engine;
using FreemanMultiCopier.Core.Model;
using FreemanMultiCopier.Core.Util;

namespace FreemanMultiCopier.Core;

/// <summary>
/// Une el motor, el vigilante de dispositivos y el planificador, y expone todo lo que
/// la interfaz necesita. Los eventos llegan en hilo de fondo: la capa de presentacion
/// es la que los lleva al hilo de la interfaz.
/// </summary>
public sealed class CopyService : IDisposable
{
    private readonly DeviceRegistry _registry = new();
    private readonly DeviceWatcher _watcher;
    private readonly MultiCopyEngine _engine;
    private readonly CopyScheduler _scheduler = new();
    private readonly BufferBudget _budget = BufferBudget.CreateDefault();
    private readonly ObservableCollection<CopyItem> _searchResults = new();
    private readonly Dictionary<string, CopyJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _rebalance;

    private CopyMode _mode = CopyMode.Normal;
    private bool _running;

    /// <summary>Uno solo. Ver <see cref="StartAllAsync"/>: las llamadas se encadenan.</summary>
    private readonly SemaphoreSlim _pista = new(1, 1);

    private int _enCurso;

    public CopyService()
    {
        // El presupuesto de RAM vive en BufferBudget.CreateDefault, junto al motor, para
        // que el banco de pruebas mida exactamente lo mismo que el programa.
        _engine = new MultiCopyEngine(_budget);
        _watcher = new DeviceWatcher(_registry);

        _engine.ProgressChanged += OnProgress;
        _engine.ItemStateChanged += OnItemState;
        _engine.JobStateChanged += OnJobState;
        _engine.Message += OnEngineMessage;

        _rebalance = new Timer(_ => Rebalance(), null, 1200, 1200);
    }

    // ---------------------------------------------------------------- eventos

    public event Action<DeviceState>? DevicesChanged;
    public event Action<string, CopyItem, long, long>? ProgressChanged;
    public event Action<EngineMessageKind, string>? Message;
    public event Action<string, JobState>? JobStateChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------------------------------------------------------- estado

    public DeviceRegistry Registry => _registry;

    public BufferBudget Budget => _budget;

    public CopyScheduler Scheduler => _scheduler;

    public ObservableCollection<CopyItem> SearchResults => _searchResults;

    public CopyMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            // Tras cada arrastre el modo vuelve solo a Normal. Es deliberado: evita que
            // un Espejo olvidado borre el contenido de la memoria siguiente.
            OnPropChanged(nameof(Mode));
        }
    }

    public bool IsRunning => _running;

    /// <summary>
    /// Si ya hay una copia en marcha. Solo para poder avisar: el servicio encadena las
    /// llamadas igualmente, de modo que esto no decide nada.
    /// </summary>
    public bool IsBusy => _pista.CurrentCount == 0;

    public int ActiveDeviceCount => _jobs.Values.Count(j => j.State is JobState.Working or JobState.Queued);

    public long ObservedThroughput => _scheduler.ObservedThroughput;

    public string ModeLabel => _mode switch
    {
        CopyMode.Normal => "Normal",
        CopyMode.RemoveDifferences => "Eliminar Diferencias",
        CopyMode.Mirror => "Espejo",
        CopyMode.Sync => "Sincronizacion",
        _ => "Normal",
    };

    public string ModeDescription => _mode switch
    {
        CopyMode.Normal => "Copia lo que sueltas y no toca lo que ya habia.",
        CopyMode.RemoveDifferences => "Borra del dispositivo todo lo que no este en el origen.",
        CopyMode.Mirror => "Borra lo que sobra y copia lo que falta. Queda identico al origen.",
        CopyMode.Sync => "Como Espejo, pero mueve en vez de borrar y recopiar.",
        _ => string.Empty,
    };

    // ---------------------------------------------------------------- ciclo de vida

    public void Start()
    {
        _registry.DeviceAdded += d => DevicesChanged?.Invoke(d);
        _registry.DeviceRemoved += d => DevicesChanged?.Invoke(d);
        _registry.DeviceChanged += d => DevicesChanged?.Invoke(d);
        _watcher.Start();
    }

    private void Rebalance()
    {
        try
        {
            var list = _jobs.Values.Where(j => j.State is JobState.Working or JobState.Paused or JobState.Queued).ToArray();
            if (list.Length == 0) return;
            _scheduler.Rebalance(list, _engine);
        }
        catch { /* el planificador nunca debe tumbar la copia */ }
    }

    // ---------------------------------------------------------------- busqueda

    /// <summary>
    /// Busca por nombre bajo una raiz. Es la via rapida para encontrar algo sin saber
    /// en que carpeta esta, que es como trabaja el cliente.
    /// </summary>
    public async Task SearchAsync(string root, string term, IEnumerable<string> excluded,
                                  IProgress<(int Found, bool Truncated)>? onProgress,
                                  CancellationToken ct)
    {
        _searchResults.Clear();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var skip = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        int found = 0;
        const int cap = 5000;
        bool truncated = false;
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<string>();
        stack.Push(root);
        int guard = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (++guard > 400_000) { truncated = true; break; }

            string cur = stack.Pop();
            if (IsExcluded(cur, skip)) continue;
            if (!seenDirs.Add(cur)) continue;

            bool full = false;
            string[] files;
            try { files = Directory.GetFiles(cur); }
            catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
            catch (IOException) { files = Array.Empty<string>(); }

            foreach (var f in files)
            {
                if (found >= cap) { truncated = true; full = true; break; }
                if (term.Length > 0 && !Path.GetFileName(f).Contains(term, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    _searchResults.Add(new CopyItem
                    {
                        SourcePath = f,
                        Length = fi.Length,
                        Kind = MediaClassifier.Classify(f),
                        SourceWriteTimeUtc = fi.LastWriteTimeUtc,
                    });
                    found++;
                }
                catch { }
            }

            onProgress?.Report((found, false));
            if (full) break;

            string[] dirs;
            try { dirs = Directory.GetDirectories(cur); }
            catch (UnauthorizedAccessException) { dirs = Array.Empty<string>(); }
            catch (IOException) { dirs = Array.Empty<string>(); }

            foreach (var d in dirs)
            {
                if (!IsExcluded(d, skip)) stack.Push(d);
            }
        }

        onProgress?.Report((found, truncated));
    }

    private static bool IsExcluded(string path, HashSet<string> excluded)
    {
        if (excluded.Count == 0) return false;
        string norm = StructureResolver.NormalizeDir(path);
        foreach (var e in excluded)
        {
            string en = StructureResolver.NormalizeDir(e);
            if (norm.StartsWith(en, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- encolar

    /// <summary>
    /// Anade a cada dispositivo indicado la lista de lo solto, ya con las rutas de
    /// destino resueltas segun la estructura de ese tipo de dispositivo. Si no hay
    /// ninguno seleccionado, solo se aplica al dispositivo sobre el que se solto.
    /// </summary>
    public IReadOnlyList<CopyItem> Enqueue(IReadOnlyList<DeviceState> targets, IReadOnlyList<string> dropped,
                                           string sourceRoot, bool flat)
    {
        if (targets.Count == 0 || dropped.Count == 0) return Array.Empty<CopyItem>();

        var perDevice = new List<CopyItem>();

        foreach (var device in targets)
        {
            var job = JobFor(device);
            var structure = device.StructureOverride ?? VolumeInspector.DefaultStructure(device.Volume.Kind);

            var plan = StructureResolver.Resolve(new StructureRequest
            {
                DroppedPaths = dropped,
                SourceRoot = sourceRoot,
                Kind = structure,
                CustomFolderName = device.CustomFolderName,
                ForceFlat = flat,
            });

            // Solo lo NUEVO de este dispositivo. Antes se añadia la cola entera, y con
            // quince dispositivos la lista devuelta salia quince veces mas larga de lo
            // que se encolo, que es lo que se leia en el mensaje de la barra.
            int before = job.Queue.Count;
            foreach (var planned in plan.Files)
                job.Queue.Add(ToItem(planned, job.Queue.Count));

            if (device == targets[0])
            {
                for (int i = before; i < job.Queue.Count; i++)
                    perDevice.Add(job.Queue[i]);
            }

            // El modo es el de la copia; la estructura se guarda por dispositivo.
            job.Mode = Mode;
            job.Structure = structure;
            job.RecalculateTotals();
        }

        // En cuanto se encola, el modo global se vuelve a Normal. Se resetea aqui y
        // no en el arrastre para que ningun camino de entrada pueda dejar un modo
        // peligroso puesto: es el trabajo ya encolado el que conserva el modo que
        // tenia, y a partir de aqui se empieza de cero. Un Espejo olvidado borra el
        // contenido de la memoria que venga despues.
        _mode = CopyMode.Normal;

        return perDevice;
    }

    private static CopyItem ToItem(PlannedFile p, int order) => new()
    {
        SourcePath = p.SourcePath,
        DestRelative = p.DestRelative,
        Length = p.Length,
        Kind = p.Kind,
        Order = order,
        SourceWriteTimeUtc = p.WriteTimeUtc,
    };

    /// <summary>Quita de la cola los archivos indicados. Es el equivalente a soltar con Ctrl.</summary>
    public void RemoveFromQueue(IReadOnlyList<DeviceState> targets, IReadOnlyList<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var device in targets)
        {
            if (!_jobs.TryGetValue(device.Volume.Id, out var job)) continue;
            foreach (var item in job.Queue.Where(i => set.Contains(i.SourcePath)).ToArray())
                job.Queue.Remove(item);
            job.RecalculateTotals();
        }
    }

    /// <summary>Copia la cola de un dispositivo a todos los demas: memorias exactamente iguales.</summary>
    public void CloneList(DeviceState source, IEnumerable<DeviceState> targets)
    {
        if (!_jobs.TryGetValue(source.Volume.Id, out var origin) || origin.Queue.Count == 0) return;

        foreach (var device in targets.Where(d => !ReferenceEquals(d, source)))
        {
            var job = JobFor(device);
            job.Mode = origin.Mode;
            job.Structure = origin.Structure;

            // Las rutas ya vienen resueltas para el origen; se recalculan por si la
            // estructura de este dispositivo es distinta (disco vs memoria).
            foreach (var item in origin.Queue)
            {
                var plan = StructureResolver.Resolve(new StructureRequest
                {
                    DroppedPaths = new[] { item.SourcePath },
                    SourceRoot = origin.Queue.Count > 0 ? RootOf(item.SourcePath) : item.SourcePath,
                    Kind = job.Structure,
                    CustomFolderName = device.CustomFolderName,
                });
                var rel = plan.Files.Count > 0 ? plan.Files[0].DestRelative : item.DestRelative;
                job.Queue.Add(item.CloneFor(rel));
            }
            job.RecalculateTotals();
        }
    }

    private static string RootOf(string path)
    {
        string? root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? path : root;
    }

    public CopyJob JobFor(DeviceState device)
    {
        if (_jobs.TryGetValue(device.Volume.Id, out var existing) && !existing.Cancel.IsCancellationRequested)
            return existing;

        var job = new CopyJob
        {
            Device = device,
            Structure = device.StructureOverride ?? VolumeInspector.DefaultStructure(device.Volume.Kind),
            Mode = Mode,
        };
        device.Job = job;
        _jobs[device.Volume.Id] = job;
        return job;
    }

    public CopyJob? JobOf(string deviceId) => _jobs.GetValueOrDefault(deviceId);

    // ---------------------------------------------------------------- ejecutar

    public async Task StartAllAsync(IReadOnlyList<DeviceState> devices)
    {
        // Encadenar, y no abrir un segundo motor sobre el mismo trabajo. Antes solo lo
        // podia pedir el boton COPIAR y no habia dos a la vez; desde que soltar archivos
        // copia solo, soltar en mitad de una copia es un gesto de lo mas normal, y
        // CopyQueueAsync parte de una foto de la cola: dos motores sobre un trabajo se
        // comerian el mismo archivo a la vez y la mitad se quedaria sin escribir.
        await _pista.WaitAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _enCurso);
        _running = true;
        OnPropChanged(nameof(IsRunning));

        try
        {
            // Varias vueltas a propósito. La foto de la cola se toma al empezar, asi
            // que lo encolado mientras corria la vuelta anterior se recoge aqui. Por
            // eso la segunda llamada no necesita hacer nada especial: cuando le llegue
            // el turno ya no quedara nada pendiente y saldra sin tocar el motor.
            int vueltas = 0;
            int antes = int.MaxValue;

            while (true)
            {
                var list = devices.Select(JobFor).Where(j => Pendientes(j) > 0).ToArray();
                if (list.Length == 0) break;

                // El tope de vueltas y la comparacion con la vuelta anterior son una
                // red: si un trabajo fallara antes de tocar la cola, sus archivos seguirian
                // esperando y este bucle no terminaria nunca. Con las dos cosas, en el
                // peor caso Copies lo que falta una vez y para.
                int ahora = list.Sum(Pendientes);
                if (++vueltas > VueltasMaximas || ahora >= antes) break;
                antes = ahora;

                // Se les dice a los trabajos cuantos destinos son. Con esa cifra el motor
                // sabe que tiene que abrir la ventana de union: los destinos que van al
                // mismo archivo leen el origen una vez entre todos en lugar de una por
                // destino.
                foreach (var j in list) j.PlannedTargets = list.Length;

                // ConfigureAwait(false) NO es cosmetico aqui. Sin el, esta continuacion
                // queda encolada en el despachador de WPF, y la tarea que devuelve este
                // metodo no termina hasta que el hilo de la interfaz vuelve a bombear
                // mensajes. Con la ventana viva no se nota; en cualquier otro sitio
                // (pruebas, trabajos en segundo plano) la copia se queda colgada para
                // siempre con todo ya escrito.
                await Task.WhenAll(list.Select(j => _engine.RunAsync(j))).ConfigureAwait(false);
            }
        }
        finally
        {
            _pista.Release();
            if (Interlocked.Decrement(ref _enCurso) == 0)
            {
                _running = false;
                OnPropChanged(nameof(IsRunning));
            }
        }
    }

    /// <summary>Tope de vueltas de copia encadenadas. Es una red, no una medida.</summary>
    private const int VueltasMaximas = 64;

    private static int Pendientes(CopyJob job) =>
        job.Queue.Count(i => i.State == ItemState.Waiting);

    public void Pause(string deviceId)
    {
        if (_jobs.TryGetValue(deviceId, out var job)) job.Pause();
    }

    public void Resume(string deviceId)
    {
        if (_jobs.TryGetValue(deviceId, out var job)) job.Resume();
    }

    public void PauseAll()
    {
        foreach (var job in _jobs.Values) job.Pause();
    }

    public void ResumeAll()
    {
        foreach (var job in _jobs.Values) job.Resume();
    }

    /// <summary>Detiene y descarta la cola del dispositivo.</summary>
    public void CancelDevice(string deviceId)
    {
        if (!_jobs.TryGetValue(deviceId, out var job)) return;
        try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        job.Queue.Clear();
        job.RecalculateTotals();
    }

    public void ClearFinished()
    {
        foreach (var device in _registry.All)
        {
            if (!_jobs.TryGetValue(device.Volume.Id, out var job)) continue;
            foreach (var item in job.Queue.Where(i => i.State is ItemState.Completed or ItemState.Skipped).ToArray())
                job.Queue.Remove(item);
            job.RecalculateTotals();
        }
    }

    /// <summary>Vuelve a copiar solo los archivos que fallaron.</summary>
    public void RetryErrors(string deviceId)
    {
        if (!_jobs.TryGetValue(deviceId, out var job)) return;
        foreach (var item in job.Queue.Where(i => i.State == ItemState.Error))
        {
            item.State = ItemState.Waiting;
            item.Error = null;
            item.BytesDone = 0;
        }
        job.RecalculateTotals();
    }

    public void ResetJob(string deviceId)
    {
        if (_jobs.TryGetValue(deviceId, out var job)) job.ResetForNextRun();
    }

    public void RemoveFromQueue(string deviceId, CopyItem item)
    {
        if (!_jobs.TryGetValue(deviceId, out var job)) return;
        job.Queue.Remove(item);
        job.RecalculateTotals();
    }

    public void MoveInQueue(string deviceId, CopyItem item, int delta)
    {
        if (!_jobs.TryGetValue(deviceId, out var job)) return;
        int i = job.Queue.IndexOf(item);
        if (i < 0) return;
        int j = Math.Clamp(i + delta, 0, job.Queue.Count - 1);
        if (i == j) return;
        job.Queue.Move(i, j);
    }

    public void MoveToTop(string deviceId, CopyItem item)
    {
        if (!_jobs.TryGetValue(deviceId, out var job)) return;
        int i = job.Queue.IndexOf(item);
        if (i <= 0) return;
        job.Queue.Move(i, 0);
    }

    public void MoveToBottom(string deviceId, CopyItem item)
    {
        if (!_jobs.TryGetValue(deviceId, out var job)) return;
        int i = job.Queue.IndexOf(item);
        if (i < 0 || i == job.Queue.Count - 1) return;
        job.Queue.Move(i, job.Queue.Count - 1);
    }

    // ---------------------------------------------------------------- expulsion

    public EjectResult Eject(DeviceState device)
    {
        var result = _watcher.Eject(device.Volume.Root);
        if (result == EjectResult.Success)
        {
            _scheduler.Forget(device.Volume.Id);
            CancelDevice(device.Volume.Id);
            _jobs.Remove(device.Volume.Id);
        }
        return result;
    }

    public void EjectAll(IEnumerable<DeviceState> devices)
    {
        foreach (var d in devices.ToArray())
        {
            if (d.IsSelected && d.SwitchExpulsar) Eject(d);
        }
    }

    // ---------------------------------------------------------------- seleccion

    /// <summary>
    /// Marca o desmarca un dispositivo. Al desmarcarlo se vacia su cola: dejarlo con
    /// archivos cogidos y sin destino solo genera sorpresas al volver a pulsarlo.
    /// </summary>
    public void Toggle(DeviceState device)
    {
        if (device is null) return;

        bool next = !device.IsSelected;
        device.IsSelected = next;

        if (!next)
        {
            if (_jobs.TryGetValue(device.Volume.Id, out var job) && job.State == JobState.Idle)
                job.ClearQueue();
        }
    }

    /// <summary>Selecciona o deselecciona en bloque lo que se pueda copiar.</summary>
    public void SelectAll(bool selected, IEnumerable<DeviceState> devices)
    {
        foreach (var d in devices.ToArray())
        {
            // El disco de arranque se deja fuera: casi siempre es un error querer
            // sobrescribir el disco donde esta instalado Windows.
            if (!d.IsAvailable || d.Volume.Kind == DeviceKind.Boot) continue;
            if (d.IsSelected == selected) continue;

            d.IsSelected = selected;
            if (!selected && _jobs.TryGetValue(d.Volume.Id, out var job) && job.State == JobState.Idle)
                job.ClearQueue();
        }
    }

    public void SelectNone(IEnumerable<DeviceState> devices)
    {
        foreach (var d in devices.ToArray())
        {
            if (!d.IsSelected) continue;
            d.IsSelected = false;
            if (_jobs.TryGetValue(d.Volume.Id, out var job) && job.State == JobState.Idle)
                job.ClearQueue();
        }
    }

    // ---------------------------------------------------------------- eventos internos

    private void OnProgress(object? sender, DeviceProgressEventArgs e)
    {
        _scheduler.ReportSpeed(e.DeviceId, e.BytesPerSecond);
        ProgressChanged?.Invoke(e.DeviceId, e.Item, e.BytesDone, e.BytesPerSecond);
    }

    private void OnEngineMessage(object? sender, (EngineMessageKind Kind, string Message) e) =>
        Message?.Invoke(e.Kind, e.Message);

    private void OnItemState(object? sender, ItemStateEventArgs e) => OnPropChanged("Jobs");

    private void OnJobState(object? sender, JobStateEventArgs e)
    {
        JobStateChanged?.Invoke(e.DeviceId, e.State);
        if (e.State is JobState.Completed or JobState.CompletedWithErrors)
        {
            if (_jobs.Values.All(j => j.State is JobState.Completed or JobState.CompletedWithErrors
                                                 or JobState.Cancelled or JobState.Failed or JobState.Idle))
            {
                _running = false;
                OnPropChanged(nameof(IsRunning));
            }
        }
    }

    private void OnPropChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _rebalance.Dispose();
        _watcher.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
