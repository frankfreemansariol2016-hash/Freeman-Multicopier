using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FreemanMultiCopier.Core.Model;

/// <summary>
/// Un volumen montado en Windows. Se identifica por la letra (o ruta de red) y se
/// enriquece con el bus fisico para distinguir un USB 2.0 de un 3.0.
/// </summary>
public sealed class VolumeRef
{
    public required string Id { get; init; }

    public required string Root { get; init; }

    public string? Label { get; set; }

    public DeviceKind Kind { get; set; } = DeviceKind.Unknown;

    /// <summary>Nombre del puerto USB fisico, si se pudo averiguar.</summary>
    public string? PortName { get; set; }

    /// <summary>El enlace USB es 3.0 o superior.</summary>
    public bool IsHighSpeed { get; set; }

    /// <summary>Conectado a un concentrador o puerto que no da la velocidad nominal.</summary>
    public bool IsSlowPort { get; set; }

    public bool IsRemovable { get; set; }

    public bool IsReadOnly { get; set; }

    public long TotalSize { get; set; }

    public long FreeSpace { get; set; }

    /// <summary>Instante en que Windows notifico la conexion. Da el orden "Llegada".</summary>
    public DateTime ArrivalTime { get; set; } = DateTime.UtcNow;

    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Alias que le pone el usuario ("Memoria de Juan").</summary>
    public string? Alias { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Alias) ? Alias
        : !string.IsNullOrWhiteSpace(Label) ? $"{Label} ({Id})"
        : Id;

    public string KindLabel => Kind switch
    {
        DeviceKind.UsbFlash => IsSlowPort ? "Memoria USB (puerto lento)" : "Memoria USB",
        DeviceKind.UsbDisk => "Disco externo",
        DeviceKind.Internal => "Disco interno",
        DeviceKind.Boot => "Disco de arranque",
        DeviceKind.Network => "Unidad de red",
        DeviceKind.Phone => "Celular / tablet",
        DeviceKind.Reader => "Lector",
        DeviceKind.Writer => "Escritor",
        DeviceKind.Unavailable => "No disponible",
        _ => "Desconocido",
    };

    public override string ToString() => $"{Id} {KindLabel} {Label}";
}

/// <summary>
/// Estado mutable de un dispositivo dentro de la sesion: lo que se ve en la tarjeta.
/// Se separa de VolumeRef porque cambia a alta velocidad y porque lleva la sesion de copia.
/// </summary>
public sealed class DeviceState : INotifyPropertyChanged
{
    private readonly Dictionary<string, long> _speeds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isSelected;
    private bool _isHidden;
    private PriorityLevel _priority = PriorityLevel.Normal;

    public required VolumeRef Volume { get; init; }

    public CopyJob? Job { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); } }
    }

    public bool IsHidden
    {
        get => _isHidden;
        set { if (_isHidden != value) { _isHidden = value; Raise(); } }
    }

    public PriorityLevel Priority
    {
        get => _priority;
        set { if (_priority != value) { _priority = value; Raise(); } }
    }

    // Los seis interruptores por dispositivo.
    public bool SwitchOcultar { get; set; }
    public bool SwitchFormatear { get; set; }
    public bool SwitchExpulsar { get; set; }
    public bool SwitchCobrar { get; set; } = true;
    public bool SwitchEliminarMover { get; set; } = true;
    public bool SwitchCopiaPublicidad { get; set; }

    /// <summary>Estructura que se usa en ESTE dispositivo. Si es null, va la de su tipo.</summary>
    public StructureKind? StructureOverride { get; set; }

    public string? CustomFolderName { get; set; }

    public bool IsAvailable => Volume.Kind != DeviceKind.Unavailable;

    public string DisplayName => Volume.DisplayName;

    /// <summary>Velocidad medida del dispositivo. La lleva el motor, la muestra la tarjeta.</summary>
    public long Speed
    {
        get => _speeds.TryGetValue(Volume.Id, out long v) ? v : 0;
        set
        {
            _speeds[Volume.Id] = value;
            Raise(nameof(SpeedText));
        }
    }

    public string SpeedText
    {
        get
        {
            long bps = Speed;
            if (bps <= 0) return string.Empty;
            double b = bps;
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {units[i]}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Registro de todos los dispositivos conectados, notificado por eventos.</summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DeviceState> All => _devices.Values.ToArray();

    public event Action<DeviceState>? DeviceAdded;
    public event Action<DeviceState>? DeviceRemoved;
    public event Action<DeviceState>? DeviceChanged;

    public DeviceState? Find(string id) => _devices.GetValueOrDefault(id);

    /// <summary>Alta o actualizacion de un volumen. Devuelve el estado y si es nuevo.</summary>
    public (DeviceState State, bool IsNew) Upsert(VolumeRef vol)
    {
        if (_devices.TryGetValue(vol.Id, out var existing))
        {
            existing.Volume.Label = vol.Label;
            existing.Volume.TotalSize = vol.TotalSize;
            existing.Volume.FreeSpace = vol.FreeSpace;
            existing.Volume.Kind = vol.Kind;
            existing.Volume.IsHighSpeed = vol.IsHighSpeed;
            existing.Volume.IsSlowPort = vol.IsSlowPort;
            existing.Volume.PortName = vol.PortName ?? existing.Volume.PortName;
            return (existing, false);
        }

        var state = new DeviceState { Volume = vol };
        _devices[vol.Id] = state;
        return (state, true);
    }

    public void RaiseChanged(DeviceState s) => DeviceChanged?.Invoke(s);

    public void RaiseAdded(DeviceState s) => DeviceAdded?.Invoke(s);

    public bool Remove(string id)
    {
        if (_devices.TryRemove(id, out var s))
        {
            DeviceRemoved?.Invoke(s);
            return true;
        }
        return false;
    }
}
