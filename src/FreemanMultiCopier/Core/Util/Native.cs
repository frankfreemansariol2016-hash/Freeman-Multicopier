using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FreemanMultiCopier.Core.Util;

/// <summary>
/// Acceso minimo a la API de Windows que hace falta: descriptor fisico del disco,
/// version del enlace USB y expulsion limpia. Todo aqui es opcional: si algo falla,
/// la aplicacion sigue funcionando y solo se pierde ese dato concreto.
/// </summary>
internal static class Native
{
    // ---------------------------------------------------------------- Storage

    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    internal const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x2D4814;
    internal const uint IOCTL_USB_GET_NODE_INFORMATION = 0x00220A04;

    internal const int StorageDeviceProperty = 1;
    internal const int StorageAdapterProperty = 2;

    internal enum StorageBusType : byte
    {
        Unknown = 0x00,
        Scsi = 0x01,
        Atapi = 0x02,
        Ata = 0x03,
        Ieee1394 = 0x04,
        FibreChannel = 0x05,
        Usb = 0x07,
        Raid = 0x08,
        Nvme = 0x11,
        Sata = 0x0B,
        Sd = 0x0C,
        Mmc = 0x0D,
        Sas = 0x0E,
        Spaces = 0x0F,
        FileBackedVirtual = 0x0F,
        Nand = 0x0A,
        Ufs = 0x10,
        NvmeOf = 0x12,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;          // 0 = PropertyStandardQuery
        public IntPtr AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_DESCRIPTOR
    {
        public int Version;
        public int Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
        public int VendorIdOffset;
        public int ProductIdOffset;
        public int ProductRevisionOffset;
        public int SerialNumberOffset;
        public StorageBusType BusType;
        public int RawPropertiesLength;
        // Detras de aqui viene la cadena de propiedades en ANSI; no la copiamos.
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct USB_NODE_INFORMATION
    {
        public int NodeType;                       // 1 = USB_DEVICE_NODE de hub
        public USB_DEVICE_DESCRIPTOR DeviceDescriptor;
        public byte CurrentConfigurationValue;
        public byte NumberOfChildren;
        public USB_DEVICE_DESCRIPTOR[] Children;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct USB_DEVICE_DESCRIPTOR
    {
        public ushort bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    // ---------------------------------------------------------------- SetupAPI

    internal const uint DIGCF_PRESENT = 0x02;
    internal const uint DIGCF_DEVICEINTERFACE = 0x10;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const uint SPDRP_HARDWAREID = 0x00000001;
    internal const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public IntPtr ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property, out uint PropertyRegDataType, IntPtr PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        IntPtr DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    // ---------------------------------------------------------------- ConfigManager

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Locate_DevNodeW(out uint DevInst, string DeviceId, uint Flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Request_Device_Eject(uint DevInst);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Get_Device_IDW(uint DevInst, StringBuilder Buffer, int BufferLen, uint Flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_Parent(out uint ParentDevInst, uint DevInst, uint Flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_DevNode_Status(out uint Status, uint DevInst, uint Flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string FileName, uint DesiredAccess, uint ShareMode,
        IntPtr SecurityAttributes, uint CreationDisposition, uint FlagsAndAttributes, IntPtr TemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle Device, uint IoControlCode,
        IntPtr InBuffer, uint InBufferSize, IntPtr OutBuffer, uint OutBufferSize,
        out uint BytesReturned, IntPtr Overlapped);

    internal static Guid GUID_DEVINTERFACE_VOLUME = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");
    internal static Guid GUID_DEVINTERFACE_USB_DEVICE = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    // ---------------------------------------------------------------- volumen

    /// <summary>Abre el volumen. Devuelve null si no hay permisos o no existe.</summary>
    internal static SafeFileHandle? OpenVolume(string root, uint desiredAccess = 0)
    {
        try
        {
            const uint FILE_SHARE_READ_WRITE = 0x03;
            var h = CreateFileW(root, desiredAccess, FILE_SHARE_READ_WRITE, IntPtr.Zero, 3, 0, IntPtr.Zero);
            return h.IsInvalid ? null : h;
        }
        catch { return null; }
    }

    internal static STORAGE_DEVICE_DESCRIPTOR? QueryStorageDescriptor(string root, int propertyId)
    {
        using var handle = OpenVolume(root);
        if (handle is null) return null;

        const int size = 2048;
        IntPtr outBuf = Marshal.AllocHGlobal(size);
        IntPtr qBuf = Marshal.AllocHGlobal(Marshal.SizeOf<STORAGE_PROPERTY_QUERY>());
        try
        {
            var query = new STORAGE_PROPERTY_QUERY { PropertyId = propertyId, QueryType = 0, AdditionalParameters = IntPtr.Zero };
            Marshal.StructureToPtr(query, qBuf, false);

            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, qBuf,
                                 (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                                 outBuf, size, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(outBuf);
        }
        catch { return null; }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
            Marshal.FreeHGlobal(qBuf);
        }
    }

    internal static uint? QueryDeviceNumber(string root)
    {
        using var handle = OpenVolume(root);
        if (handle is null) return null;
        IntPtr buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (!DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buffer, 64, out _, IntPtr.Zero))
                return null;
            return (uint)Marshal.ReadInt32(buffer, 4);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Version USB que declara el propio dispositivo: 0x0200 = USB 2.0, 0x0300 o mas = USB 3.x.
    ///
    /// Importante: esto es lo que el dispositivo DICE ser, no la velocidad negociada en el
    /// puerto. Una memoria USB 2.0 enchufada en un puerto USB 3.0 sigue diciendo 0x0200, y
    /// ahi va a ir rapida. Por eso el campo se llama IsHighSpeed y no "puerto lento": no se
    /// inventa ese dato hasta poder preguntarselo al concentrador.
    /// Devuelve null cuando el volumen no cuelga de un concentrador USB consultable.
    /// </summary>
    internal static int? QueryUsbDeviceVersion(string volumeRoot)
    {
        try
        {
            var devInst = FindVolumeDevNode(volumeRoot);
            if (devInst is null) return null;

            // Subimos desde el volumen hasta el primer ancestro que sea un USB Device.
            uint current = devInst.Value;
            for (int hop = 0; hop < 12 && current != 0; hop++)
            {
                if (TryGetUsbLinkVersion(current, out int version)) return version;
                if (Native.CM_Get_Parent(out uint parent, current, 0) != 0) break;
                current = parent;
            }
            return null;
        }
        catch { return null; }
    }

    private static bool TryGetUsbLinkVersion(uint devInst, out int version)
    {
        version = 0;
        string? id = GetDeviceInstanceId(devInst);
        if (id is null || !id.Contains("VID_", StringComparison.OrdinalIgnoreCase)) return false;

        // IOCTL_USB_GET_NODE_INFORMATION espera un bucle de 8 URBs de 8 KB.
        const int urbSize = 8 * (8 * 1024);
        IntPtr buffer = Marshal.AllocHGlobal(urbSize);
        var handle = (SafeFileHandle?)null;
        try
        {
            handle = CreateFileW(@"\\.\" + ShortDeviceId(id), 0, 0x03, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;

            if (!DeviceIoControl(handle, IOCTL_USB_GET_NODE_INFORMATION, IntPtr.Zero, 0, buffer, (uint)urbSize, out _, IntPtr.Zero))
                return false;

            var node = Marshal.PtrToStructure<USB_NODE_INFORMATION>(buffer);
            version = node.DeviceDescriptor.bcdUSB & 0x00FF;   // 0x0002 = USB 2.0, 0x0300 = USB 3.x
            if (version == 0) version = (node.DeviceDescriptor.bcdUSB >> 8) & 0xFF;
            return version > 0;
        }
        catch { return false; }
        finally
        {
            handle?.Dispose();
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Windows solo admite el path \\.\USB#VID_&PID_#...&MI_00 sin barras invertidas.</summary>
    private static string ShortDeviceId(string instanceId)
    {
        int idx = instanceId.IndexOf('\\');
        return idx >= 0 ? instanceId[idx..] : instanceId;
    }

    /// <summary>
    /// Identificador de instancia de un nodo de dispositivo, del tipo
    /// USB\VID_1234&PID_5678&MI_00. Sale directo de CM_Get_Device_ID, que no necesita el
    /// conjunto de informacion de SetupAPI y por eso no hay que abrirlo.
    /// </summary>
    internal static string? GetDeviceInstanceId(uint devInst)
    {
        const int CR_BUFFER_SMALL = 0x15;
        int size = 512;
        var sb = new StringBuilder(size);

        if (CM_Get_Device_IDW(devInst, sb, size, 0) == CR_BUFFER_SMALL)
        {
            sb = new StringBuilder(4096);
            if (CM_Get_Device_IDW(devInst, sb, 4096, 0) != 0) return null;
        }
        else if (sb.Length == 0)
        {
            return null;
        }

        return sb.ToString();
    }

    /// <summary>Busca el nodo de dispositivo de un volumen a traves de su GUID de interfaz.</summary>
    internal static uint? FindVolumeDevNode(string volumeRoot)
    {
        string? guidPath = VolumeInterfaceGuid(volumeRoot);
        if (guidPath is null) return null;
        if (Native.CM_Locate_DevNodeW(out uint devInst, guidPath, 0) != 0) return null;
        return devInst;
    }

    /// <summary>Devuelve la ruta de interfaz (sin la raiz) del volumen, para CM_Locate_DevNode.</summary>
    private static string? VolumeInterfaceGuid(string volumeRoot)
    {
        IntPtr devInfoSet = IntPtr.Zero;
        try
        {
            devInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfoSet == IntPtr.Zero || devInfoSet == -1) return null;

            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            var classGuid = GUID_DEVINTERFACE_VOLUME;
            uint index = 0;

            // El segundo parametro (SP_DEVINFO_DATA) se pasa a null: se quiere recorrer
            // todas las interfaces de la clase, no las de un dispositivo concreto.
            while (SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref classGuid, index++, ref ifData))
            {
                SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifData, detail, required, out _, IntPtr.Zero))
                        continue;

                    int cbSize = Marshal.ReadInt32(detail);
                    string path = Marshal.PtrToStringUni(detail + 4, (int)((cbSize - 4) / 2)) ?? string.Empty;
                    int brace = path.LastIndexOf('}');
                    if (brace < 0) continue;

                    string guid = path[(brace + 1)..];
                    string? expected = VolumeGuidFromRoot(volumeRoot);
                    if (!string.IsNullOrEmpty(expected) &&
                        string.Equals(guid, expected, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        catch { }
        finally { if (devInfoSet != IntPtr.Zero && devInfoSet != -1) SetupDiDestroyDeviceInfoList(devInfoSet); }
        return null;
    }

    /// <summary>Convierte la ruta del volumen (E:\ o \\servidor\recurso) en su GUID de volumen.</summary>
    internal static string? VolumeGuidFromRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try
            {
                var di = new System.IO.DriveInfo(root);
                if (!di.IsReady) return null;
                root = di.RootDirectory.FullName;
            }
            catch { return null; }
        }

        if (root.Length < 2 || root[1] != ':') return null;
        string drive = root[..2];

        // El nombre de la carpeta de montaje se puede leer del registro sin permisos.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\MountedDevices");
            if (key?.GetValue(drive) is byte[] raw && raw.Length >= 24)
            {
                // Formato: 4 bytes de cuenta, 16 de GUID, luego el nombre en UTF-16.
                var g = new Guid(raw.AsSpan(4, 16).ToArray());
                return g.ToString("B").ToUpperInvariant();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Engancha el seguro de medios para poder expulsar sin que el sistema lo rechace.</summary>
    internal static bool LockMedia(string root)
    {
        using var handle = OpenVolume(root, 0);
        if (handle is null) return false;
        try
        {
            return DeviceIoControl(handle, IOCTL_STORAGE_MEDIA_REMOVAL, IntPtr.Zero, 0,
                                   IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { return false; }
    }

    /// <summary>
    /// Expulsa un volumen. Se intenta primero el nodo de interfaz; si el sistema lo rechaza
    /// se sube al primer ancestro USB, que es el que en realidad controla la alimentacion.
    /// </summary>
    internal static EjectResult SafeEject(string volumeRoot)
    {
        try
        {
            LockMedia(volumeRoot);

            uint? devInst = FindVolumeDevNode(volumeRoot);
            if (devInst is null) return EjectResult.NotFound;

            if (Native.CM_Request_Device_Eject(devInst.Value) == 0)
                return EjectResult.Success;

            uint current = devInst.Value;
            for (int hop = 0; hop < 8 && current != 0; hop++)
            {
                if (Native.CM_Get_Parent(out uint parent, current, 0) != 0) break;
                if (parent == 0) break;
                if (Native.CM_Request_Device_Eject(parent) == 0) return EjectResult.Success;
                current = parent;
            }
            return EjectResult.Busy;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DllNotFoundException)
        {
            return EjectResult.NotPermitted;
        }
        catch { return EjectResult.Failed; }
    }
}

/// <summary>Como ha ido la expulsion. La interfaz traduce esto al mensaje que ve el cliente.</summary>
public enum EjectResult
{
    /// <summary>Ya se puede retirar con seguridad.</summary>
    Success,

    /// <summary>Algo mantiene el volumen abierto.</summary>
    Busy,

    /// <summary>Windows no expone ese volumen como expulsable.</summary>
    NotFound,

    /// <summary>Falta permiso de administrador.</summary>
    NotPermitted,

    /// <summary>Fallo por otro motivo.</summary>
    Failed,
}
