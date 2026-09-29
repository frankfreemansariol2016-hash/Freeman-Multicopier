using System.Globalization;

namespace FreemanMultiCopier.Core.Util;

/// <summary>
/// Lo unico que se recuerda entre sesiones: donde buscar y cada cuanto refrescar la
/// ventana. Va en un archivo de texto liso junto al registro y no en el registro de
/// Windows, porque la aplicacion no pide permisos de administrador y un cliente que no
/// los tiene no puede ni leer ni escribir ahi.
///
/// De lo que se copia no se guarda nada. Las colas son de una sesion: si
/// cierras el programa a mitad de una copia, ese trabajo se pierde. Guardar las colas y
/// poder retomarlas es una de las cosas de la version 1.0.
/// </summary>
public static class AppSettings
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _values;

    private static string FilePath =>
        System.IO.Path.Combine(Log.DataDirectory, "ajustes.txt");

    /// <summary>Carpeta de origen del buscador. Vacia si nunca se eligio ninguna.</summary>
    public static string SearchRoot
    {
        get => Get("busqueda", "");
        set => Set("busqueda", value);
    }

    /// <summary>
    /// Intervalo de refresco de la ventana en milisegundos. Cero si no se guardo nunca,
    /// que es como se pide la foto de partida antes de elegir un valor de la lista.
    /// </summary>
    public static int RefreshMs
    {
        get
        {
            string raw = Get("refresco", "");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0
                ? v
                : 0;
        }
        set => Set("refresco", value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Vuelca a disco lo que haya cambiado. Se llama al cambiar y al cerrar.</summary>
    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                var snapshot = _values is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);

                System.IO.File.WriteAllLines(
                    FilePath,
                    snapshot.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudieron guardar los ajustes: " + ex.Message);
            }
        }
    }

    private static string Get(string key, string fallback)
    {
        lock (Gate)
        {
            _values ??= Load();
            return _values.TryGetValue(key, out string? v) ? v : fallback;
        }
    }

    private static void Set(string key, string value)
    {
        lock (Gate)
        {
            _values ??= Load();
            _values[key] = value ?? "";
        }
    }

    /// <summary>
    /// Se lee una vez y se queda en memoria. El archivo son cuatro lineas: no tiene
    /// sentido pagarlo en cada consulta, y asi el cambio de un ajuste no toca el disco
    /// mas que una vez, al guardar.
    /// </summary>
    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                foreach (string line in System.IO.File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudieron leer los ajustes: " + ex.Message);
        }
        return map;
    }
}
