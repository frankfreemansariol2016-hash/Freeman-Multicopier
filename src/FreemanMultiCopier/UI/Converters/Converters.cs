using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.UI.Converters;

/// <summary>Convierte bytes en "1.4 GB". Se usa en todas las cifras visibles.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "-";
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0
            ? $"{bytes:N0} {units[i]}"
            : string.Format(culture, "{0:0.##} {1}", b, units[i]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Porcentaje 0..1 a texto "45%".</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? string.Format(culture, "{0:0}%", d * 100) : "-";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Bytes por segundo a "42.3 MB/s".</summary>
public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bps || bps <= 0) return "";
        double b = bps;
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return string.Format(culture, "{0:0.#} {1}", b, units[i]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Elige el color del semaforo segun el estado del trabajo.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            JobState.Working => "AccentBrush",
            JobState.Completed => "OkBrush",
            JobState.CompletedWithErrors => "WarnBrush",
            JobState.Failed => "ErrorBrush",
            JobState.Cancelled => "IdleBrush",
            JobState.Paused => "WarnBrush",
            _ => "IdleBrush",
        };
        return Application.Current?.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Texto del estado, para la tarjeta.</summary>
public sealed class StateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            JobState.Idle => "Listo",
            JobState.Queued => "En espera",
            JobState.Working => "Copiando",
            JobState.Paused => "Pausado",
            JobState.Completed => "Finalizado",
            JobState.CompletedWithErrors => "Finalizado con errores",
            JobState.Cancelled => "Cancelado",
            JobState.Failed => "Error",
            _ => "",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Icono del tipo de dispositivo. Distingue tambien el puerto lento.</summary>
public sealed class DeviceIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DeviceKind kind) return DeviceIcons.Unknown;
        return DeviceIcons.For(kind, isHighSpeed: true, isSlowPort: false);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Visible solo cuando el booleano vale el valor indicado.</summary>
public sealed class BoolEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool expected = parameter?.ToString() == "True";
        return value is bool b && b == expected;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Invierte un booleano, para visibilidad en negativo.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Booleano a Visible/Collapsed, para la barra fina del archivo en marcha.</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out bool p) && p,
            null => false,
            _ => true,
        };
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Cantidad de segundos a "h:mm:ss" o "mm:ss". Para el tiempo restante.</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts) return string.Empty;
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>El nombre de archivo sin la ruta, recortado por el medio si es muy largo.</summary>
public sealed class MiddleEllipsisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = value?.ToString() ?? string.Empty;
        int max = parameter is string p && int.TryParse(p, out int m) ? m : 48;
        return TrimMiddle(s, max);
    }

    /// <summary>
    /// Recorta por el medio y no por el final. Un nombre de pelicula largo comparte
    /// principio y extension con todos los demas, asi que perder el final es justo lo
    /// que hace inutil la lista. Conservando la extension se sigue leyendo.
    /// </summary>
    public static string TrimMiddle(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;

        string ext = Path.GetExtension(s);
        if (ext.Length > 8) ext = string.Empty;      // no es una extension de verdad
        int keep = max - ext.Length - 1;
        if (keep < 6) keep = max / 2;

        int head = (int)Math.Ceiling(keep * 0.62);
        int tail = keep - head;
        return s[..head] + "..." + (tail > 0 ? s[^tail..] : string.Empty) + ext;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
