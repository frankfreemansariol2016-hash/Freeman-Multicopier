using System.IO;
using System.Text;

namespace FreemanMultiCopier;

/// <summary>
/// Registro en archivo, junto a la carpeta Data de la aplicacion. Un copiador que falla
/// sin dejar rastro es imposible de diagnosticar en casa del cliente.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _failed;

    public static string DataDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FreemanMultiCopier");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogPath => _path ??= Path.Combine(DataDirectory, "registro.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message) => Write("AVISO", message, null);

    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_failed) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [").Append(level).Append("] ");
            sb.Append(message);
            if (ex is not null) sb.AppendLine().Append("    ").Append(ex);
            sb.AppendLine();

            lock (Gate)
            {
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Si el registro no se puede escribir, no se puede hacer nada mas. Se deja de
            // intentar para no ralentizar la copia con un error de disco por linea.
            _failed = true;
        }
    }
}
