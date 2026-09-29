using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FreemanMultiCopier.Core;
using FreemanMultiCopier.Core.Devices;
using FreemanMultiCopier.Core.Engine;
using FreemanMultiCopier.Core.Model;
using FreemanMultiCopier.Core.Util;

namespace FreemanMultiCopier.UI;

/// <summary>
/// Ventana principal: buscador a la izquierda, dispositivos a la derecha, barra de
/// acciones abajo. Es solo presentacion: todo el trabajo lo hace <see cref="CopyService"/>.
///
/// Lo unico que hace esta ventana que no es dibujarse es mantener vivos los contadores
/// con un temporizador. Recorrer las colas cuatro veces por segundo es ridiculo al lado
/// del trabajo real, que es mover bytes, y evita el enredo de notificar desde hilos de
/// copia a un arbol de bindings.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CopyService _service = new();
    private readonly DispatcherTimer _ticker;
    private CancellationTokenSource? _searchCts;
    private string _searchRoot = "";
    private readonly List<string> _searchExcluded = new();

    /// <summary>
    /// La copia que arranco el ultimo arrastre. Se guarda para poder esperarla al cerrar
    /// y para que el recolector no se lleve por delante una tarea en vuelo; no se mira
    /// nunca mientras corre, porque el que informa del avance es el servicio.
    /// </summary>
    private Task _copiaEnMarcha = Task.CompletedTask;

    private string _searchTerm = "";
    private string _searchStatus = "Elige una carpeta de origen para buscar.";
    private string _statusText = "Listo. Arrastra archivos sobre un dispositivo para copiarlos.";
    private StructureKind _structure = StructureKind.Folders;

    /// <summary>
    /// Cadencia del panel "Copia en curso". De 1 a 3 ms la barra se ve liquida; a 250 ms
    /// el equipo carga mucho menos y a los ojos casi no se nota la diferencia. Se elige
    /// con el boton de la barra de estado y se recuerda al cerrar.
    /// </summary>
    private static readonly int[] RefreshSteps = { 1, 2, 3, 5, 8, 16, 33, 50, 100, 250 };

    private int _refreshIndex = 2;
    private int _ticksSinceSlow;

    /// <summary>Lo que hace cada tarjeta al recibir un clic.</summary>
    public ICommand ToggleDeviceCommand { get; }

    public MainWindow()
    {
        ToggleDeviceCommand = new RelayCommand(p =>
        {
            if (p is DeviceState device) _service.Toggle(device);
            RefreshCounters();
        });

        InitializeComponent();

        // La ventana es su propio contexto de datos: todos los bindings apuntan a MainWindow.
        // Se fija aqui y no con StartupUri porque el modo prueba tiene que poder saltarse
        // la apertura de la ventana sin tocar nada mas.
        DataContext = this;

        _service.Registry.DeviceAdded += OnDeviceAdded;
        _service.Registry.DeviceRemoved += OnDeviceRemoved;
        _service.Registry.DeviceChanged += OnDeviceChanged;
        _service.Message += OnEngineMessage;
        _service.ProgressChanged += OnEngineProgress;

        // El intervalo guardado manda. Si no hay ninguno se arranca en 3 ms, que es la
        // cifra que deja la copia en curso realmente fluida sin cargar el equipo.
        if (AppSettings.RefreshMs is int saved && saved > 0)
        {
            int at = Array.IndexOf(RefreshSteps, saved);
            if (at >= 0) _refreshIndex = at;
        }

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshSteps[_refreshIndex]) };
        _ticker.Tick += (_, _) => Tick();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- datos para el binding

    public ObservableCollection<DeviceState> Devices { get; } = new();

    public ObservableCollection<CopyItem> Results => _service.SearchResults;

    /// <summary>
    /// Filas del panel "Copia en curso": una por dispositivo, y estan siempre todas.
    /// La lista nunca se reordena ni se vacia, para que el cliente no pierda de vista
    /// una memoria que estaba copiando por culpa de otra.
    /// </summary>
    public ObservableCollection<LiveCopyRow> CopyActivity { get; } = new();

    /// <summary>Lo que esta saliendo del origen y hacia donde va. Se enlaza al bloque
    /// de arriba de la pestana "Copia en curso".</summary>
    public OriginReadout Origin { get; } = new();

    public int TotalDevices => Devices.Count;

    public int SelectedDevices => Devices.Count(d => d.IsSelected);

    public int WorkingDevices => Devices.Count(d => d.Job?.State == JobState.Working);

    public long SelectedBytes => Devices.Where(d => d.IsSelected)
                                       .SelectMany(d => d.Job?.Queue ?? (IEnumerable<CopyItem>)Array.Empty<CopyItem>())
                                       .Where(i => i.State != ItemState.Completed)
                                       .Sum(i => i.Length);

    public string SelectionSummary =>
        SelectedDevices == 0
            ? "Ninguno seleccionado"
            : $"{SelectedDevices} seleccionado(s) - {Format(SelectedBytes)}";

    public string BufferUsed => Format(_service.Budget.WorstCaseBytes);

    public string TotalThroughput
    {
        get
        {
            long bps = _service.ObservedThroughput;
            if (bps <= 0) return string.Empty;
            double b = bps;
            string[] u = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {u[i]} en total";
        }
    }

    /// <summary>Texto del boton de refresco de la barra de estado.</summary>
    public string RefreshLabel => $"Refresco: {RefreshSteps[_refreshIndex]} ms";

    /// <summary>El boton se llama MODO y detras va el valor que tiene ahora mismo.</summary>
    public string ModeLabel => "MODO: " + _service.ModeLabel;

    public string StructureLabel => "ESTRUCTURA: " + _structure switch
    {
        StructureKind.Structure => "arbol completo",
        StructureKind.Folders => "carpetas",
        StructureKind.Files => "solo ficheros",
        StructureKind.CustomFolder => "carpeta personalizada",
        StructureKind.NoStructure => "sin estructura",
        StructureKind.TopFolder => "carpeta superior",
        StructureKind.EmptyDirs => "solo carpetas vacias",
        StructureKind.ZeroKb => "ficheros de 0 KB",
        _ => "carpetas",
    };

    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            if (_searchTerm == value) return;
            _searchTerm = value;
            OnPropChanged();
            _ = RunSearchAsync();
        }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set { _searchStatus = value; OnPropChanged(); }
    }

    /// <summary>Carpeta donde se busca ahora, recortada para que quepa en la etiqueta.</summary>
    public string SearchRootLabel { get; private set; } = "";

    /// <summary>Texto del boton que abre el explorador de carpetas.</summary>
    public string SearchRootToolTip => $"Buscando en: {_searchRoot}";

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---------------------------------------------------------------- ciclo de vida

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.Start();
        _ticker.Start();
        PickSearchRoot();
        StatusText = "Conecta los dispositivos y arrastra los archivos encima. Ctrl+clic selecciona varios.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var working = Devices.Where(d => d.Job?.State == JobState.Working).ToArray();
        if (working.Length > 0)
        {
            var res = MessageBox.Show(
                $"Hay {working.Length} dispositivo(s) copiando.\n\n" +
                "Si sales ahora se queda la copia a medias.\n\n¿Salir de todos modos?",
                "Freeman MULTICOPIER", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        _ticker.Stop();
        _searchCts?.Cancel();
        AppSettings.SearchRoot = _searchRoot;
        AppSettings.Save();
        _service.Dispose();
    }

    // ---------------------------------------------------------------- dispositivos

    private void OnDeviceAdded(DeviceState d) => Dispatcher.Invoke(() =>
    {
        Devices.Add(d);
        _service.JobFor(d);
        CopyActivity.Add(new LiveCopyRow(d));
        Log.Info($"Conectado: {d.Volume.Id} {d.Volume.KindLabel}");
        StatusText = $"{d.Volume.Id} - {d.Volume.KindLabel} ({Format(d.Volume.FreeSpace)} libres)";
    });

    private void OnDeviceRemoved(DeviceState d) => Dispatcher.Invoke(() =>
    {
        Devices.Remove(d);
        foreach (var row in CopyActivity.Where(r => r.DeviceId == d.Volume.Id).ToList())
        {
            CopyActivity.Remove(row);
        }
        if (d.Job?.State == JobState.Working)
        {
            StatusText = $"DESCONECTADO {d.Volume.Id}: la copia se quedo incompleta.";
            Log.Warn($"Desconectado con trabajo en curso: {d.Volume.Id}");
        }
        else
        {
            Log.Info($"Desconectado: {d.Volume.Id}");
        }
    });

    private void OnDeviceChanged(DeviceState d)
    {
        // El watcher repasa cada dos segundos: solo hace falta redibujar si el espacio
        // libre cambio de verdad, que es lo unico que se ve Moving.
        void Apply()
        {
            if (d.Job is not null) d.Job.Refresh();
            OnPropChanged(nameof(TotalDevices));
            OnPropChanged(nameof(SelectedDevices));
            OnPropChanged(nameof(WorkingDevices));
        }

        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke(Apply);
    }

    // ---------------------------------------------------------------- buscador

    private void PickSearchRoot()
    {
        // Antes se elegia el volumen mas grande que no fuera el de arranque. En una
        // maquina con varios discos de varios terabytes eso es una broma: el buscador
        // arrancaba sobre la coleccion de peliculas y tardaba una eternidad en dar el
        // primer resultado. Ahora se arranca donde de verdad estan los archivos del
        // cliente: Documentos, y si no hay, el Escritorio.
        // La carpeta de busqueda se recuerda entre sesiones, que es lo que hace util el
        // buscador: no hay que volver a buscarla cada vez que se abre el programa.
        string saved = AppSettings.SearchRoot;
        _searchRoot = Directory.Exists(saved) ? EnsureSlash(saved) : DefaultSearchRoot();
        AppSettings.SearchRoot = _searchRoot;
        AppSettings.Save();

        SearchRootLabel = Describe(_searchRoot);
        OnPropChanged(nameof(SearchRootToolTip));
        SearchStatus = $"Buscando en {_searchRoot} - {Results.Count} resultado(s)";
    }

    private static string DefaultSearchRoot()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            try
            {
                string dir = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return EnsureSlash(dir);
            }
            catch { /* carpeta reasignada a algo que no existe */ }
        }

        return "C:\\";
    }

    /// <summary>Deja el camino con barra final, que es como lo espera el buscador.</summary>
    private static string EnsureSlash(string dir) =>
        dir.EndsWith('\\') ? dir : dir + "\\";

    /// <summary>Los caminos largos no caben en la etiqueta: se recortan por el medio.</summary>
    private static string Describe(string root) =>
        root.Length <= 30 ? root : "..." + root[^27..];

    /// <summary>Deja que el cliente elija donde buscar. Sin esto no hay forma de cambiarlo.</summary>
    private void OnBrowseSearchRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Carpeta donde buscar",
            InitialDirectory = Directory.Exists(_searchRoot) ? _searchRoot : null,
        };

        bool? ok = dialog.ShowDialog(this);
        if (ok != true) return;

        _searchRoot = EnsureSlash(dialog.FolderName);
        AppSettings.SearchRoot = _searchRoot;
        AppSettings.Save();
        SearchRootLabel = Describe(_searchRoot);
        OnPropChanged(nameof(SearchRootToolTip));
        SearchStatus = $"Buscando en {_searchRoot}";
        _searchTerm = string.Empty;
        Results.Clear();
        OnPropChanged(nameof(SearchTerm));
    }

    private async Task RunSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(_searchRoot)) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // Se espera un poco a que el usuario termine de teclear. Sin esto, cada tecla
        // recorre el disco entero y el buscador se vuelve inutilizable.
        try { await Task.Delay(320, ct); } catch (OperationCanceledException) { return; }

        if (_searchTerm.Length == 0 && Results.Count == 0)
        {
            SearchStatus = "Escribe para buscar.";
            return;
        }

        SearchStatus = "Buscando...";
        var progress = new Progress<(int Found, bool Truncated)>(p =>
            SearchStatus = p.Truncated
                ? $"Mas de {p.Found} resultados: se ha cortado la lista."
                : $"{p.Found} resultado(s)");

        try
        {
            await Task.Run(() => _service.SearchAsync(_searchRoot, _searchTerm, _searchExcluded, progress, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            SearchStatus = "Error al buscar.";
            Log.Error("Buscador", ex);
        }
    }

    // ---------------------------------------------------------------- arrastre

    /// <summary>
    /// Punto de entrada del arrastre. Se atiende en la ventana, en la lista de resultados
    /// y en cada tarjeta, porque el cliente suelta el archivo justo donde esta mirando.
    /// </summary>
    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <remarks>
    /// Este manejador devuelve el control a OLE enseguida y deja la copia en marcha.
    /// Antes esperaba a que acabara, y el efecto era que el arrastre no se cerraba hasta
    /// el ultimo byte: doce segundos de raton clavado con el cursor de "copiando" para
    /// una sola llamada, y mientras tanto la ventana de origen seguia bloqueada. La
    /// copia sigue igual de hecha, solo que ya no sujeta al que arrastra.
    /// </remarks>
    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!HasFiles(e)) return;

        string[] paths = GetFiles(e);
        if (paths.Length == 0) return;

        var target = FindDeviceFromSource(e.OriginalSource);
        var targets = SelectedTargets(target);

        if (targets.Count == 0)
        {
            StatusText = "Selecciona un dispositivo o marca varios antes de soltar.";
            Log.Warn("Soltada sin destino: no hay ningun dispositivo marcado.");
            e.Handled = true;
            return;
        }

        // Boton derecho o Alt: todo suelto en la raiz, sin carpetas.
        bool flat = e.AllowedEffects == DragDropEffects.Link ||
                    (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0;

        string sourceRoot = ResolveSourceRoot(paths);

        // El reseteo a Normal lo hace el servicio al encolar, para que ningun camino de
        // entrada pueda dejarlo puesto. Aqui solo se apunta que ha pasado, y se avisa a
        // la etiqueta del boton, que es lo unico que hay que redibujar.
        bool wasDestructive = _service.Mode != CopyMode.Normal;

        var items = _service.Enqueue(targets, paths, sourceRoot, flat);
        foreach (var t in targets) _service.JobFor(t).Refresh();

        // En el registro queda escrito lo que se solto y donde. Sin esto, un arrastre
        // que no arrive a copiarse no deja ninguna pista, y el sintoma es solo "no
        // pasa nada" en pantalla.
        Log.Info($"Soltados {paths.Length} ruta(s) -> {items.Count} en la cola para "
                 + string.Join(", ", targets.Select(t => t.Volume.Id))
                 + (flat ? " [en la raiz]" : " [con carpetas]"));

        OnPropChanged(nameof(ModeLabel));
        e.Handled = true;

        // Y arranca. Soltar ya es la orden: quien suelta quince archivos sobre quince
        // memorias quiere copiarlos, no solo encolarlos. El boton COPIAR sigue ahi, que
        // hace falta para reintentar o para copiarlos a un destino nuevo lo que ya estaba
        // en la cola; lo que ya no hace falta es obligar a pulsarlo.
        //
        // Sin esperar. EmpezarAsync coloca el estado, avisa por la etiqueta y se marcha;
        // esperar aqui seria dejar el arrastre de OLE abierto hasta que acabase la
        // copia, con el raton del que arrastra sin poder moverse. El servicio ya
        // encadena las tandas por su cuenta con su semaforo, asi que soltar dos veces
        // seguidas no se pisan.
        _copiaEnMarcha = EmpezarAsync(targets, items.Count, wasDestructive);
    }

    private static bool HasFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.AllowedEffects != DragDropEffects.None;

    private static string[] GetFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] s ? s : Array.Empty<string>();

    /// <summary>Raiz desde la que se arrastro: la unidad comun a todo lo soltado.</summary>
    private static string ResolveSourceRoot(IReadOnlyList<string> paths)
    {
        string? root = null;
        foreach (var p in paths)
        {
            try
            {
                string? r = Path.GetPathRoot(Path.GetFullPath(p));
                if (string.IsNullOrEmpty(r)) continue;
                if (root is null) { root = r; continue; }
                if (!string.Equals(root, r, StringComparison.OrdinalIgnoreCase)) continue;
            }
            catch { }
        }
        return root ?? "C:\\";
    }

    /// <summary>
    /// Dispositivos destino. Si hay varios marcados van todos, que es la forma normal de
    /// trabajar: preparas quince memorias iguales en un solo arrastre. Si no hay ninguno
    /// marcado, solo el dispositivo sobre el que se solto.
    /// </summary>
    private List<DeviceState> SelectedTargets(DeviceState? dropped)
    {
        var selected = Devices.Where(d => d.IsSelected).ToList();
        if (selected.Count > 0) return selected;
        return dropped is null ? new List<DeviceState>() : new List<DeviceState> { dropped };
    }

    private DeviceState? FindDeviceFromSource(object? source)
    {
        if (source is not DependencyObject dep) return null;
        while (dep is not null)
        {
            if (dep is FrameworkElement { DataContext: DeviceState d }) return d;
            dep = VisualTreeHelper.GetParent(dep);
            if (dep is null || dep is Window) return null;
        }
        return null;
    }

    // ---------------------------------------------------------------- acciones

    private void OnCycleMode(object sender, RoutedEventArgs e)
    {
        _service.Mode = Step(_service.Mode, forward: true, 4);
        StatusText = _service.ModeDescription;
        OnPropChanged(nameof(ModeLabel));
    }

    private void OnCycleModeBack(object sender, MouseButtonEventArgs e)
    {
        _service.Mode = Step(_service.Mode, forward: false, 4);
        StatusText = _service.ModeDescription;
        OnPropChanged(nameof(ModeLabel));
        e.Handled = true;
    }

    private void OnCycleRefresh(object sender, RoutedEventArgs e) => StepRefresh(forward: true);

    private void OnCycleRefreshBack(object sender, MouseButtonEventArgs e)
    {
        StepRefresh(forward: false);
        e.Handled = true;
    }

    private void StepRefresh(bool forward)
    {
        int n = RefreshSteps.Length;
        _refreshIndex = forward ? (_refreshIndex + 1) % n : (_refreshIndex + n - 1) % n;

        int ms = RefreshSteps[_refreshIndex];
        _ticker.Interval = TimeSpan.FromMilliseconds(ms);
        AppSettings.RefreshMs = ms;
        AppSettings.Save();

        OnPropChanged(nameof(RefreshLabel));
        StatusText = ms <= 3
            ? $"Refresco a {ms} ms: la copia en curso se ve muy fluida."
            : $"Refresco a {ms} ms: el equipo carga menos.";
    }

    private void OnCycleStructure(object sender, RoutedEventArgs e) => ApplyStructure(forward: true);

    private void OnCycleStructureBack(object sender, MouseButtonEventArgs e)
    {
        ApplyStructure(forward: false);
        e.Handled = true;
    }

    private void ApplyStructure(bool forward)
    {
        var targets = SelectedTargets(null);
        _structure = Step(_structure, forward, 8);

        foreach (var t in targets)
        {
            t.StructureOverride = _structure;
            if (_service.JobOf(t.Volume.Id) is { } job) { job.Structure = _structure; job.Refresh(); }
        }
        OnPropChanged(nameof(StructureLabel));
        StatusText = "Estructura aplicada a los dispositivos seleccionados. Vuelve a encolar para que cambie el destino.";
    }

    private static StructureKind Step(StructureKind current, bool forward, int count) =>
        (StructureKind)(((int)current + (forward ? 1 : count - 1)) % count);

    private static CopyMode Step(CopyMode current, bool forward, int count) =>
        (CopyMode)(((int)current + (forward ? 1 : count - 1)) % count);

    /// <summary>
    /// Arranca la copia de una lista de destinos. La llaman el boton COPIAR y el
    /// arrastre, porque soltar archivos ya ES la orden de copiar: pedir despues un
    /// segundo gesto era un paso de mas que solo servia para poder olvidarse.
    /// </summary>
    /// <param name="encolados">
    /// Cuantos archivos acaba de encolar el arrastre. -1 cuando quien llama es el boton,
    /// que no ha encolado nada y no tiene que dar la cuenta.
    /// </param>
    /// <param name="aviso">
    /// Que el servicio ha devuelto el modo a Normal y conviene decirlo, porque quien
    /// suelta no ha visto el cambio.
    /// </param>
    private async Task EmpezarAsync(IReadOnlyList<DeviceState> targets, int encolados = -1, bool aviso = false)
    {
        if (targets.Count == 0)
        {
            StatusText = "No hay nada que copiar: marca un dispositivo con archivos en la cola.";
            return;
        }

        if (encolados == 0)
        {
            StatusText = "No se ha copiado nada: todo lo soltado estaba excluido.";
            return;
        }

        foreach (var t in targets) _service.JobFor(t).ResetCancel();

        // El orden de las partes importa: primero que esta copiando, luego lo que
        // espera, y el reseteo del modo al final, que es lo unico que se ajoute.
        var texto = new List<string>
        {
            encolados > 0
                ? $"{encolados} archivo(s) hacia {targets.Count} dispositivo(s). Copiando..."
                : $"Copiando a {targets.Count} dispositivo(s)..."
        };
        if (_service.IsBusy) texto.Add("Habia otra copia en marcha: estos van detras.");
        if (aviso) texto.Add("El modo vuelve a Normal.");
        StatusText = string.Join(" ", texto);

        try
        {
            Log.Info("Arranca la copia: " + StatusText);
            await _service.StartAllAsync(targets).ConfigureAwait(true);

            // El recuento final sale de los trabajos, no de lo que se encolo ahora:
            // el servicio encadena las llamadas, asi que aqui puede estar terminando
            // mas de una tanda de las que se han pedido desde la ventana.
            int ok = 0, mal = 0, pendientes = 0;
            foreach (var t in targets)
            {
                var job = _service.JobFor(t);
                ok += job.Queue.Count(i => i.State == ItemState.Completed);
                mal += job.Queue.Count(i => i.State is ItemState.Error or ItemState.Cancelled);
                pendientes += job.Queue.Count(i => i.State is ItemState.Waiting or ItemState.Copying);
            }

            Log.Info($"Termina la copia: {ok} entero(s), {mal} con problema, {pendientes} sin tocar");
            StatusText = "TRABAJO FINALIZADO";
        }
        catch (Exception ex)
        {
            StatusText = "Error al copiar: " + ex.Message;
            Log.Error("Copia", ex);
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) =>
        _copiaEnMarcha = EmpezarAsync(
            Devices.Where(d => d.IsSelected && d.Job is { Queue.Count: > 0 }).ToArray());

    private void OnPauseAll(object sender, RoutedEventArgs e) => _service.PauseAll();

    private void OnResumeAll(object sender, RoutedEventArgs e) => _service.ResumeAll();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var d in Devices) d.IsSelected = true;
        RefreshCounters();
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var d in Devices) d.IsSelected = false;
        RefreshCounters();
    }

    private void OnClone(object sender, RoutedEventArgs e)
    {
        var source = Devices.FirstOrDefault(d => d.Job is { Queue.Count: > 0 });
        if (source is null)
        {
            StatusText = "No hay ninguna lista que clonar todavia.";
            return;
        }

        var targets = Devices.Where(d => !ReferenceEquals(d, source)).ToArray();
        _service.CloneList(source, targets);
        foreach (var t in targets) _service.JobFor(t).Refresh();

        StatusText = $"Lista de {source.Volume.Id} clonada a {targets.Length} dispositivo(s).";
    }

    private void OnClearFinished(object sender, RoutedEventArgs e)
    {
        _service.ClearFinished();
        RefreshCounters();
    }

    private void OnRetryErrors(object sender, RoutedEventArgs e)
    {
        foreach (var d in Devices.Where(d => d.IsSelected)) _service.RetryErrors(d.Volume.Id);
        StatusText = "Archivos con error devueltos a la cola.";
    }

    private void OnEjectSelected(object sender, RoutedEventArgs e)
    {
        var targets = Devices.Where(d => d.IsSelected).ToArray();
        if (targets.Length == 0)
        {
            StatusText = "Marca los dispositivos que quieres expulsar.";
            return;
        }

        var busy = targets.Where(d => d.Job?.State == JobState.Working).ToArray();
        if (busy.Length > 0)
        {
            var res = MessageBox.Show(
                $"{busy.Length} dispositivo(s) siguen copiando.\n\n" +
                "Si expulsas ahora, la copia se quedara incompleta.\n\n¿Expulsar de todos modos?",
                "Freeman MULTICOPIER", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }

        foreach (var d in targets)
        {
            var r = _service.Eject(d);
            StatusText = r switch
            {
                EjectResult.Success => $"EXPULSADO {d.Volume.Id}. Ya se puede retirar con seguridad.",
                EjectResult.Busy => $"{d.Volume.Id} sigue ocupado. Cierra lo que tenga abierto.",
                EjectResult.NotPermitted => $"NO SE PUEDE EXPULSAR {d.Volume.Id}: falta permiso de administrador.",
                EjectResult.NotFound => $"{d.Volume.Id} no aparece en el sistema. Cierralo desde el Explorador.",
                _ => $"No se pudo expulsar {d.Volume.Id}.",
            };
        }
    }

    // ---------------------------------------------------------------- temporizador

    /// <summary>
    /// Dos cadencias y no una. La rapida solo lee contadores y compara cadenas cortas,
    /// asi que se puede repetir cada 1 ms sin quitarle tiempo al motor de copia: de ahi
    /// sale la barra fina del archivo en marcha. La lenta recorre las colas enteras, y
    /// eso se hace medio segundo como muy tarde, porque los estados cambian poco.
    /// </summary>
    private void Tick()
    {
        foreach (var row in CopyActivity)
        {
            var job = row.Device.Job;
            if (job is null) continue;

            // En el tick rapido solo las filas con trabajo. Recorrer trece filas
            // paradas cada milisegundo no se ve, pero cada vuelta del bucle le quita
            // un instante al hilo de la interfaz, que es de donde sale la barra que
            // se esta moviendo. Las paradas las vuelve a mirar el tick lento.
            if (!row.IsActive && job.Queue.Count == 0) continue;

            job.RefreshLight();
            row.Update();
        }

        int ms = RefreshSteps[_refreshIndex];
        if (ms < 250) _ticksSinceSlow++;
        if ((long)_ticksSinceSlow * ms < 500) return;
        _ticksSinceSlow = 0;

        foreach (var d in Devices)
        {
            if (d.Job is { } job) job.Refresh();
        }

        // Las filas que el tick rapido se ha saltado, y el bloque del origen. Este
        // si va aqui y no antes: agrupar por archivo, sumar y armar texto son unas
        // decenas de operaciones, y a 1 ms serian trabajo de mas en el hilo de la
        // interfaz. Cada medio segundo es suficiente para leerlo.
        foreach (var row in CopyActivity) row.Update();
        Origin.Update(CopyActivity);

        RefreshCounters();
    }

    private void RefreshCounters()
    {
        OnPropChanged(nameof(TotalDevices));
        OnPropChanged(nameof(SelectedDevices));
        OnPropChanged(nameof(WorkingDevices));
        OnPropChanged(nameof(SelectionSummary));
        OnPropChanged(nameof(BufferUsed));
        OnPropChanged(nameof(TotalThroughput));
    }

    private void OnEngineProgress(string deviceId, CopyItem item, long bytes, long bps)
    {
        if (Dispatcher.CheckAccess()) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Devices.FirstOrDefault(d => d.Volume.Id == deviceId) is { } d) d.Speed = bps;
        }));
    }

    private void OnEngineMessage(EngineMessageKind kind, string message)
    {
        Dispatcher.BeginInvoke(new Action(() => StatusText = message));
    }

    private static string Format(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} B" : $"{b:0.##} {units[i]}";
    }
}
