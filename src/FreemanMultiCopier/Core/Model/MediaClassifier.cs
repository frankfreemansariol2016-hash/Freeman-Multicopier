using System.IO;

namespace FreemanMultiCopier.Core.Model;

/// <summary>Clasifica por extension. Se usa para el icono de la cola y para separar video de audio.</summary>
public static class MediaClassifier
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".vob", ".3gp", ".rmvb", ".divx", ".asf", ".ogv", ".mts",
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".oga", ".wma", ".opus", ".aiff",
        ".ape", ".mka", ".ac3", ".dts",
    };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".heic", ".avif",
    };

    private static readonly HashSet<string> Document = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".epub",
        ".mobi", ".azw3", ".djvu", ".cbz", ".cbr",
    };

    private static readonly HashSet<string> Archive = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab", ".z",
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".md", ".log", ".cue",
    };

    public static MediaKind Classify(string path)
    {
        string ext;
        try { ext = Path.GetExtension(path); }
        catch { return MediaKind.Other; }

        if (string.IsNullOrEmpty(ext)) return MediaKind.Other;
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        if (Image.Contains(ext)) return MediaKind.Image;
        if (Archive.Contains(ext)) return MediaKind.Archive;
        if (Text.Contains(ext)) return MediaKind.Text;
        if (Document.Contains(ext)) return MediaKind.Document;
        return MediaKind.Other;
    }

    public static bool IsVideo(string path) => Classify(path) == MediaKind.Video;

    public static bool IsAudio(string path) => Classify(path) == MediaKind.Audio;

    public static IReadOnlyCollection<string> VideoExtensions => Video;

    public static IReadOnlyCollection<string> AudioExtensions => Audio;
}
