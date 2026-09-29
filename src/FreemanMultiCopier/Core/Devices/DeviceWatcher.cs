using System.Collections.Concurrent;
using System.IO;
using FreemanMultiCopier.Core.Model;
using FreemanMultiCopier.Core.Util;

namespace FreemanMultiCopier.Core.Devices;

/// <summary>
/// Vigila los volumenes conectados.
///
/// Dos mecanismos, con papeles distintos:
///
/// 1. Un gancho de WM_DEVICECHANGE, que avisa en el momento en que enchufas una memoria.
///    Es rapido, pero es una pista: Windows a veces lo manda con retraso y a veces no lo
///    manda, sobre todo al volver de suspensión.
///
/// 2. Un repaso completo cada dos segundos. Es la fuente de verdad.
///
/// Se necesitan los dos. Con solo el gancho, el fallo clasico es "no me aparece la memoria
/// que acabo de enchufar" y el cliente se queda con la sensacion de que el programa esta roto.
/// Con solo el repaso, la tarjeta aparece con dos segundos de retraso y parece lento.
/// El coste del repaso es una lista de unidades, que es nada.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly ConcurrentDictionary<string, DateTime> _arrivals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private DeviceChangeHook? _hook;
    private Timer? _poller;
    private int _rescanInProgress;
    private bool _disposed;

    public DeviceWatcher(DeviceRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Repaso completo. Es publico para poder forzar una actualizacion desde la interfaz,
    /// por ejemplo cuando el usuario pulsa "Refrescar" porque no ve un dispositivo.
    /// </summary>
    public void Rescan()
    {
        if (Interlocked.Exchange(ref _rescanInProgress, 1) == 1) return;

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var present = new List<VolumeRef>();

            foreach (var drive in SafeDrives())
            {
                if (drive.DriveType is DriveType.CDRom or DriveType.Ram) continue;

                VolumeRef vol;
                try
                {
                    if (!drive.IsReady && drive.DriveType == DriveType.Unknown) continue;
                    vol = VolumeInspector.Inspect(drive);
                }
                catch
                {
                    // Un volumen puede desaparecer mientras se le mira. Es normal con una
                    // memoria que se extrae; no es un error que merezca un mensaje.
                    continue;
                }

                if (string.IsNullOrEmpty(vol.Id)) continue;
                if (_hidden.Contains(vol.Id)) continue;

                seen.Add(vol.Id);
                present.Add(vol);
            }

            // Altas y cambios de los que siguen conectados.
            foreach (var vol in present)
            {
                if (_arrivals.TryGetValue(vol.Id, out var arrived))
                    vol.ArrivalTime = arrived;          // conserva el orden de llegada real
                else
                    _arrivals[vol.Id] = vol.ArrivalTime;

                var (state, isNew) = _registry.Upsert(vol);
                if (isNew) _registry.RaiseAdded(state);
                else _registry.RaiseChanged(state);
            }

            // Bajas de los que ya no aparecen.
            foreach (var id in _registry.All.Select(d => d.Volume.Id).ToArray())
            {
                if (seen.Contains(id)) continue;
                _registry.Remove(id);
                _arrivals.TryRemove(id, out _);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _rescanInProgress, 0);
        }
    }

    public void Start()
    {
        // Si la ventana oculta no se puede crear, no pasa nada: el repaso periodico
        // sigue detecting las unidades, solo que con un pelin de retraso.
        _hook = DeviceChangeHook.TryCreate();
        if (_hook is not null) _hook.Changed += OnDeviceChanged;

        Rescan();
        _poller = new Timer(_ => Rescan(), null, 2000, 2000);
    }

    private void OnDeviceChanged()
    {
        // El aviso llega en el hilo de la interfaz. El repaso completo puede tardar
        // decenas de milisegundos, asi que se manda a un hilo de fondo para no congelar
        // el arrastre ni los botones mientras se consulta el hardware.
        Task.Run(Rescan);
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var d in drives)
        {
            DriveInfo copy;
            try { copy = new DriveInfo(d.Name); }
            catch { continue; }
            yield return copy;
        }
    }

    /// <summary>
    /// Expulsa un dispositivo de forma segura: primero se avisa a Windows y luego se espera
    /// a que el sistema confirme. Devuelve el resultado para que la interfaz pueda decir
    /// algo util en vez de un "error" generico.
    /// </summary>
    public EjectResult Eject(string volumeRoot) => Native.SafeEject(volumeRoot);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _poller?.Dispose();
        if (_hook is not null)
        {
            _hook.Changed -= OnDeviceChanged;
            _hook.Dispose();
        }
    }
}
