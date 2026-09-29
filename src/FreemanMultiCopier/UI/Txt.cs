using System.Globalization;

namespace FreemanMultiCopier.UI;

/// <summary>
/// Formatos de texto del panel. Vive aqui para que una misma cifra se lea igual en todos
/// los sitios: si cada fila compusiera su numero por su cuenta, en un momento se escribia
/// "58.2 MB/s" y en otro "58,2 MB/s" y el cliente creia que eran dos medidas distintas.
/// </summary>
internal static class Txt
{
    private static readonly string[] BytesUnits = { "B", "KB", "MB", "GB", "TB" };
    private static readonly string[] RateUnits = { "B/s", "KB/s", "MB/s", "GB/s" };

    public static string Bytes(long b)
    {
        if (b < 0) b = 0;
        double v = b;
        int i = 0;
        while (v >= 1024 && i < BytesUnits.Length - 1) { v /= 1024; i++; }
        return i == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} B", b)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", v, BytesUnits[i]);
    }

    /// <summary>Velocidad. Sin cifra todavia se devuelve vacio, no un 0,0 MB/s.</summary>
    public static string Rate(long bps)
    {
        if (bps <= 0) return string.Empty;
        double b = bps;
        int i = 0;
        while (b >= 1024 && i < RateUnits.Length - 1) { b /= 1024; i++; }
        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", b, RateUnits[i]);
    }

    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : $"{Math.Max(0, (int)t.TotalSeconds)}s";
}
