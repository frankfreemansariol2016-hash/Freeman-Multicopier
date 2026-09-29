using System.ComponentModel;
using System.Runtime.CompilerServices;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.UI;

/// <summary>
/// Una fila del panel "Copia en curso": que dispositivo es, que archivo se esta leyendo
/// del origen ahora mismo, como va, y cuanto espacio le queda.
///
/// No guarda nada: todo sale de <see cref="DeviceState"/> y de su <see cref="CopyJob"/>,
/// y <see cref="Update"/> solo avisa de lo que ha cambiado. Por eso se puede llamar cada
/// dos milisegundos sin que se note: comparar cadenas cortas y, casi siempre, no avisar.
/// </summary>
public sealed class LiveCopyRow : INotifyPropertyChanged
{
    private string _deviceId;
    private string _displayName;
    private string _stateLabel;
    private string _currentFile;
    private string _stateDetail;
    private double _progress;
    private double _activeFraction;
    private string _percentText;
    private string _filesText;
    private string _freeText;
    private string _remainingText;
    private string _readText;
    private string _writeText;
    private bool _isActive;
    private bool _isCopying;
    private bool _hasWork;

    public LiveCopyRow(DeviceState device)
    {
        Device = device;
        _deviceId = device.Volume.Id;
        _displayName = device.Volume.DisplayName;
        _stateLabel = "";
        _currentFile = "";
        _stateDetail = "";
        _percentText = "";
        _filesText = "";
        _freeText = "";
        _remainingText = "";
        _readText = "";
        _writeText = "";

        // Un archivo de 900 MB pasa de 0 a 100 en un segundo. Con solo la barra gruesa se
        // ve un salto; esta es la del archivo suelto, que si se mueve a pasos pequenos.
        Update();
    }

    public DeviceState Device { get; }

    public string DeviceId => _deviceId;
    public string DisplayName => _displayName;
    public string StateLabel => _stateLabel;
    public string CurrentFile => _currentFile;
    public string StateDetail => _stateDetail;
    public double Progress => _progress;
    public double ActiveFraction => _activeFraction;
    public string PercentText => _percentText;
    public string FilesText => _filesText;
    public string FreeText => _freeText;
    public string RemainingText => _remainingText;

    /// <summary>
    /// A que velocidad se esta LEYENDO del origen el archivo de esta fila. Es la misma
    /// cifra en todas las filas que comparten el archivo: el origen lo lee una vez.
    /// </summary>
    public string ReadText => _readText;

    /// <summary>A que velocidad se esta ESCRIBIENDO en este destino.</summary>
    public string WriteText => _writeText;

    public bool IsActive => _isActive;

    /// <summary>Copiando de verdad ahora mismo: ni en pausa, ni encolado, ni parado.</summary>
    public bool IsCopying => _isCopying;

    public bool HasWork => _hasWork;

    /// <summary>Relee el estado y avisa solo de lo que ha cambiado de verdad.</summary>
    public void Update()
    {
        var vol = Device.Volume;
        Set(ref _deviceId, vol.Id);
        Set(ref _displayName, vol.DisplayName);

        var job = Device.Job;
        bool hasWork = job is not null && job.Queue.Count > 0;
        Set(ref _hasWork, hasWork);

        if (!hasWork)
        {
            Set(ref _stateLabel, Device.IsSelected ? "Marcado" : "Sin trabajo");
            Set(ref _currentFile, "");
            Set(ref _stateDetail, Device.IsSelected ? "Esperando a que le sueltes algo" : "");
            Set(ref _progress, 0d);
            Set(ref _activeFraction, 0d);
            Set(ref _percentText, "");
            Set(ref _filesText, "");
            Set(ref _remainingText, "");
            Set(ref _readText, "");
            Set(ref _writeText, "");
            Set(ref _freeText, Txt.Bytes(vol.FreeSpace) + " libres");
            Set(ref _isActive, false);
            Set(ref _isCopying, false);
            return;
        }

        var j = job!;
        Set(ref _isActive, j.State is JobState.Working or JobState.Paused);
        Set(ref _isCopying, j.State == JobState.Working && j.ActiveFileName.Length > 0);
        Set(ref _stateLabel, StateText(j.State));

        // Lo que se esta copiando desde el origen, con su propio nombre de archivo.
        string active = j.ActiveFileName;
        Set(ref _currentFile, active.Length == 0 ? "(sin archivo en marcha)" : active);
        Set(ref _activeFraction, j.ActiveFraction);
        Set(ref _progress, j.Progress);
        Set(ref _percentText, (j.Progress * 100).ToString("0") + "%");
        Set(ref _filesText, $"{j.FilesCompleted} de {j.FilesTotal} archivos");
        Set(ref _remainingText, j.Remaining is { } r ? "queda " + Txt.Time(r) : "");
        Set(ref _freeText, Txt.Bytes(j.FreeSpaceLive) + " libres");

        // Las dos mitades de la transferencia de ESTE archivo: lo que sale del origen y
        // lo que entra en este destino. Sin cifra se ensena "en espera" y no un 0,0
        // MB/s, que parece que el destino esta parado cuando lo que pasa es que todavia
        // no ha dado un bloque.
        Set(ref _readText, $"LECTURA {Txt.Rate(j.ActiveReadBps)}".TrimEnd());
        Set(ref _writeText, $"ESCRITURA {Txt.Rate(j.ActiveWriteBps)}".TrimEnd());
    }

    private static string StateText(JobState s) => s switch
    {
        JobState.Idle => "En espera",
        JobState.Queued => "Encolado",
        JobState.Working => "Copiando",
        JobState.Paused => "Pausado",
        JobState.Completed => "Terminado",
        JobState.CompletedWithErrors => "Terminado con errores",
        JobState.Cancelled => "Cancelado",
        JobState.Failed => "Fallido",
        _ => "En espera",
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
