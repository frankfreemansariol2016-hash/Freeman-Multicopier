using System.Windows.Media;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.UI;

/// <summary>
/// Iconos del tipo de dispositivo, dibujados a mano con Paths simples en vez de usar la
/// fuente de iconos de Windows. Motivo doble: los glifos del sistema cambian entre
/// versiones de Windows, y un icono de memoria que parece el de una carpeta confunde
/// al cliente justo en el momento de marcar con que dispositivo.
/// </summary>
public static class DeviceIcons
{
    public static readonly Geometry UsbFlash = Freeze(
        "M5.5,7.5 L14,7.5 L14,16.5 L5.5,16.5 Z M14,9.8 L18.5,9.8 L18.5,14.2 L14,14.2 Z M7.4,9.2 L9,9.2 L9,14.8 L7.4,14.8 Z");

    public static readonly Geometry UsbFlashSlow = Freeze(
        "M5.5,7.5 L14,7.5 L14,16.5 L5.5,16.5 Z M14,9.8 L18.5,9.8 L18.5,14.2 L14,14.2 Z M12.6,9.2 L14,9.2 L14,14.8 L12.6,14.8 Z M11.9,5 L19.5,5 L19.5,6.2 L11.9,6.2 Z");

    public static readonly Geometry UsbDisk = Freeze(
        "M2.5,5.5 L21.5,5.5 L21.5,15 L2.5,15 Z M4.5,7.5 L19.5,7.5 M4.5,13 L8,13 M17,11.6 A1.4,1.4 0 1 0 17.01,11.6");

    public static readonly Geometry InternalDisk = Freeze(
        "M2,7 L22,7 L22,17 L2,17 Z M4,10 L20,10 M4,14 L11,14 M14.5,13.2 A1.1,1.1 0 1 0 14.51,13.2 M17.5,13.2 A1.1,1.1 0 1 0 17.51,13.2");

    public static readonly Geometry BootDisk = Freeze(
        "M2,7 L22,7 L22,17 L2,17 Z M4,10 L20,10 M12,12.4 L12,15.6 M9.6,13.2 A2.6,2.6 0 1 0 14.4,13.2 A2.6,2.6 0 1 0 9.6,13.2 M12,11.1 L12.9,12.4 L12,13.7 L11.1,12.4 Z");

    public static readonly Geometry Network = Freeze(
        "M2,4 L10,4 L10,10 L2,10 Z M14,14 L22,14 L22,20 L14,20 Z M6,10 L6,13 L18,13 L18,14");

    public static readonly Geometry Phone = Freeze(
        "M7,2.5 L17,2.5 L17,21.5 L7,21.5 Z M9.4,4.6 L14.6,4.6 M10.6,19.2 L13.4,19.2");

    public static readonly Geometry Unknown = Freeze(
        "M12,3 L21,7.5 L21,16.5 L12,21 L3,16.5 L3,7.5 Z M12,3 L12,12 L21,7.5 M12,12 L3,7.5 M12,12 L12,21");

    public static readonly Geometry Unavailable = Freeze(
        "M12,3 L21,7.5 L21,16.5 L12,21 L3,16.5 L3,7.5 Z M8.5,8.5 L15.5,15.5 M15.5,8.5 L8.5,15.5");

    public static Geometry For(DeviceKind kind, bool isHighSpeed, bool isSlowPort) => kind switch
    {
        DeviceKind.UsbFlash => isSlowPort ? UsbFlashSlow : UsbFlash,
        DeviceKind.UsbDisk => UsbDisk,
        DeviceKind.Internal => InternalDisk,
        DeviceKind.Boot => BootDisk,
        DeviceKind.Network => Network,
        DeviceKind.Phone => Phone,
        DeviceKind.Unavailable => Unavailable,
        _ => Unknown,
    };

    private static Geometry Freeze(string data)
    {
        var geo = Geometry.Parse(data);
        geo.Freeze();
        return geo;
    }
}
