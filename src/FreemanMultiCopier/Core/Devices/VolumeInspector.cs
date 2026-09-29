using System.IO;
using FreemanMultiCopier.Core.Model;
using FreemanMultiCopier.Core.Util;

// El descriptor fisico vive dentro de la clase Native. Estos alias evitan tener que
// escribir Native.STORAGE_DEVICE_DESCRIPTOR en cada linea.
using StorageDeviceDescriptor = FreemanMultiCopier.Core.Util.Native.STORAGE_DEVICE_DESCRIPTOR;
using StorageBusType = FreemanMultiCopier.Core.Util.Native.StorageBusType;

namespace FreemanMultiCopier.Core.Devices;

/// <summary>
/// Deduce de que tipo es cada volumen. Se apoya en tres fuentes, de la mas fiable a la
/// mas aproximada: el descriptor fisico del disco, el tipo de unidad de Windows y, si
/// ninguna sirve, el tamano y la etiqueta.
/// </summary>
public static class VolumeInspector
{
    public static VolumeRef Inspect(DriveInfo drive)
    {
        var vol = new VolumeRef
        {
            Id = drive.Name.Length >= 2 ? drive.Name[..2] : drive.Name,
            Root = drive.RootDirectory.FullName,
            ArrivalTime = DateTime.UtcNow,
        };

        try
        {
            if (drive.IsReady)
            {
                vol.Label = drive.VolumeLabel;
                vol.TotalSize = drive.TotalSize;
                vol.FreeSpace = drive.AvailableFreeSpace;
                // DriveInfo no expone si la unidad es de solo lectura: hay que mirar los
                // atributos de su carpeta raiz, que es lo unico disponible.
                try { vol.IsReadOnly = (File.GetAttributes(vol.Root) & FileAttributes.ReadOnly) != 0; }
                catch { vol.IsReadOnly = false; }
            }
        }
        catch { /* el volumen puede desaparecer en mitad de la consulta */ }

        vol.IsRemovable = drive.DriveType == DriveType.Removable;

        var desc = Native.QueryStorageDescriptor(vol.Root, 1 /*StorageDeviceProperty*/);
        bool haveDescriptor = desc is not null;
        var bus = desc?.BusType ?? StorageBusType.Unknown;

        // Version USB que declara el propio dispositivo: 0x0200 = USB 2.0, 0x0300 o mas = USB 3.x.
        // Ojo con lo que esto NO es: la velocidad realmente negociada en el puerto. Saber eso
        // exige preguntar al concentrador (IOCTL_USB_GET_NODE_CONNECTION_INFORMATION), que
        // todavia no esta hecho. Asi que IsSlowPort se queda en falso en vez de inventarse un
        // dato: es preferible no avisar a avisar mal: el cliente lo comprobaria de inmediato.
        int? usbVersion = Native.QueryUsbDeviceVersion(vol.Root);
        vol.IsHighSpeed = usbVersion is >= 3;
        vol.IsSlowPort = false;

        vol.Kind = Classify(drive, bus, haveDescriptor);

        // El disco de arranque se marca aparte para que nadie lo toque por error.
        if ((vol.Kind == DeviceKind.Internal || vol.Kind == DeviceKind.UsbDisk) && IsSystemRoot(drive))
            vol.Kind = DeviceKind.Boot;

        vol.SerialNumber = ReadSerial(vol.Root);
        return vol;
    }

    /// <summary>
    /// Clasifica el volumen. Se apoya primero en el tipo de unidad que da Windows, que es
    /// fiable de sobra para separar red de disco, y despues en el bus fisico, que es lo que
    /// separa una memoria de un disco externo (los dos son USB, pero no se tratan igual:
    /// en un disco cabe la estructura de carpetas, en una memoria conviene aplanarla).
    /// </summary>
    private static DeviceKind Classify(DriveInfo drive, StorageBusType bus, bool haveDescriptor)
    {
        switch (drive.DriveType)
        {
            case DriveType.Network:
                return DeviceKind.Network;

            case DriveType.CDRom:
            case DriveType.Ram:
                return DeviceKind.Unknown;
        }

        if (haveDescriptor)
        {
            bool usb = bus == StorageBusType.Usb;

            if (usb && drive.DriveType == DriveType.Removable) return DeviceKind.UsbFlash;
            if (usb) return DeviceKind.UsbDisk;

            if (drive.DriveType == DriveType.Removable) return DeviceKind.UsbFlash;
            if (bus is StorageBusType.Nvme or StorageBusType.Sata or StorageBusType.Ata
                       or StorageBusType.Sas or StorageBusType.Raid)
                return DeviceKind.Internal;
        }

        // Sin descriptor (permisos o driver raro): se deduce solo del tipo de unidad.
        return drive.DriveType switch
        {
            DriveType.Removable => DeviceKind.UsbFlash,
            DriveType.Fixed => DeviceKind.Internal,
            _ => DeviceKind.Unknown,
        };
    }

    private static bool IsSystemRoot(DriveInfo drive)
    {
        try
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrEmpty(win) &&
                   win.StartsWith(drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadSerial(string root)
    {
        try
        {
            uint? diskNumber = Native.QueryDeviceNumber(root);
            if (diskNumber is null) return string.Empty;

            // El numero de disco fisico se busca en WMI por indice; es la via mas barata
            // sin abrir una sesion de ManagementObjectSearcher completa.
            var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT SerialNumber FROM Win32_DiskDrive WHERE Index={diskNumber.Value}");
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject mo in results)
            {
                var s = mo["SerialNumber"]?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>Estructura de fabrica por tipo de dispositivo, segun lo que usa el original.</summary>
    public static StructureKind DefaultStructure(DeviceKind kind) => kind switch
    {
        // Un disco tiene espacio de sobra: se respeta el orden original.
        DeviceKind.Internal or DeviceKind.Boot or DeviceKind.UsbDisk or DeviceKind.Network => StructureKind.Structure,

        // En una memoria interesa que todo este a mano, no enterrado en diez niveles.
        DeviceKind.UsbFlash => StructureKind.Folders,

        _ => StructureKind.Folders,
    };
}
