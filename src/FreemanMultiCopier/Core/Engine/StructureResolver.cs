using System.IO;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.Core.Engine;

/// <summary>Un archivo ya con su ruta de destino resuelta.</summary>
public sealed class PlannedFile
{
    public required string SourcePath { get; init; }
    public required string DestRelative { get; init; }
    public long Length { get; init; }
    public DateTime WriteTimeUtc { get; init; }
    public MediaKind Kind { get; init; }
    public string FileName => Path.GetFileName(SourcePath);
}

/// <summary>Resultado de traducir "lo que solto el usuario" + "estructura elegida" a una lista concreta.</summary>
public sealed class CopyPlan
{
    public List<PlannedFile> Files { get; } = new();
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cuando es true no se copian archivos, solo el arbol de carpetas.</summary>
    public bool FoldersOnly { get; set; }

    /// <summary>Cuando es true los archivos se crean vacios (catalogo).</summary>
    public bool ZeroByteFiles { get; set; }

    public long TotalBytes => Files.Sum(f => f.Length);
}

/// <summary>Parametros de una resolucion.</summary>
public sealed class StructureRequest
{
    /// <summary>Lo que el usuario solto: archivos sueltos y/o carpetas.</summary>
    public required IReadOnlyList<string> DroppedPaths { get; init; }

    /// <summary>Raiz del origen (normalmente la unidad o carpeta desde la que se arrastro).</summary>
    public required string SourceRoot { get; init; }

    public required StructureKind Kind { get; init; }

    /// <summary>Solo para StructureKind.CustomFolder.</summary>
    public string? CustomFolderName { get; init; }

    /// <summary>Fuerza "todo suelto en la raiz", como hace el arrastre con el boton derecho.</summary>
    public bool ForceFlat { get; init; }
}

/// <summary>
/// Traduce la estructura elegida a rutas concretas de destino.
///
/// Regla general, sea cual sea la estructura: la ruta destino es la ruta del origen
/// recortada desde una base distinta.
///   Structure     -> desde la raiz del origen (arrastra tambien las carpetas de encima)
///   Folders       -> desde la carpeta que se solto
///   TopFolder     -> desde la carpetaPadre de la que se solto (una carpeta mas por encima)
///   Files         -> solo el nombre
///   NoStructure   -> como Folders si se solto una carpeta; como Files si fueron ficheros sueltos
///   CustomFolder  -> como Folders, pero debajo de una carpeta con el nombre elegido
/// </summary>
public static class StructureResolver
{
    public static CopyPlan Resolve(StructureRequest req)
    {
        var plan = new CopyPlan();
        if (req.DroppedPaths.Count == 0) return plan;

        string root = NormalizeDir(req.SourceRoot);
        bool anyDir = req.DroppedPaths.Any(Directory.Exists);
        bool forceFlat = req.ForceFlat;

        StructureKind kind = forceFlat && req.Kind != StructureKind.CustomFolder
            ? StructureKind.Files
            : req.Kind;

        switch (kind)
        {
            case StructureKind.EmptyDirs:
                plan.FoldersOnly = true;
                break;
            case StructureKind.ZeroKb:
                plan.ZeroByteFiles = true;
                break;
        }

        string? custom = Sanitize(req.CustomFolderName);

        foreach (var dropped in req.DroppedPaths)
        {
            string full = Path.GetFullPath(dropped);

            if (Directory.Exists(full))
            {
                string dropRoot = NormalizeDir(full);
                EnumerateTree(full, (file, _) =>
                    AddFile(plan, root, dropRoot, file, kind, custom, anyDir));
            }
            else if (File.Exists(full))
            {
                // Un fichero suelto: su "carpeta arrastrada" es la que lo contiene.
                string dir = NormalizeDir(Path.GetDirectoryName(full) ?? root);
                AddFile(plan, root, dir, full, kind, custom, anyDir);
            }
        }

        // Las carpetas vacias del origen solo se crean si se pidio el arbol completo.
        if (kind is StructureKind.Structure or StructureKind.EmptyDirs or StructureKind.ZeroKb)
            CollectEmptyDirs(plan, req.DroppedPaths, root, kind, custom);

        return plan;
    }

    private static void AddFile(CopyPlan plan, string root, string dropRoot, string file,
                                StructureKind kind, string? custom, bool anyDir)
    {
        string rel = kind switch
        {
            StructureKind.Structure => RelClamped(file, root),
            StructureKind.Folders => RelClamped(file, dropRoot),
            StructureKind.TopFolder => RelClamped(file, ParentOf(dropRoot)),
            StructureKind.Files => Path.GetFileName(file),
            StructureKind.NoStructure => anyDir ? RelClamped(file, dropRoot) : Path.GetFileName(file),
            StructureKind.CustomFolder => Combine(custom, RelClamped(file, dropRoot)),
            StructureKind.EmptyDirs => RelClamped(file, root),
            StructureKind.ZeroKb => RelClamped(file, root),
            _ => Path.GetFileName(file),
        };

        rel = CleanRelative(rel);
        if (string.IsNullOrEmpty(rel)) return;

        plan.Directories.Add(GetDir(rel));
        plan.Files.Add(Describe(file, rel));
    }

    private static void CollectEmptyDirs(CopyPlan plan, IEnumerable<string> dropped, string root,
                                         StructureKind kind, string? custom)
    {
        string rootN = NormalizeDir(root);
        foreach (var d in dropped.Where(Directory.Exists))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(d, "*", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                string rel = kind switch
                {
                    StructureKind.CustomFolder => Combine(custom, RelClamped(dir, NormalizeDir(d))),
                    _ => RelClamped(dir, rootN),
                };
                rel = CleanRelative(rel);
                if (string.IsNullOrEmpty(rel)) continue;
                plan.Directories.Add(rel);
            }
        }
    }

    private static PlannedFile Describe(string file, string rel)
    {
        var fi = new FileInfo(file);
        return new PlannedFile
        {
            SourcePath = file,
            DestRelative = rel,
            Length = fi.Exists ? fi.Length : 0,
            WriteTimeUtc = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue,
            Kind = MediaClassifier.Classify(file),
        };
    }

    private static void EnumerateTree(string dir, Action<string, int> onFile)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        int guard = 0;
        while (stack.Count > 0)
        {
            if (++guard > 200_000) return;   // proteccion contra arboles patologicos
            string cur = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(cur); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files) onFile(f, 0);

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(cur); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var s in subdirs) stack.Push(s);
        }
    }

    /// <summary>
    /// Ruta de <paramref name="full"/> relativa a <paramref name="baseDir"/>, recortada para que
    /// nunca suba por encima de la base. Si el archivo esta por encima de la base, se recorta
    /// al primer segmento real en vez de devolver "..\..\".
    /// </summary>
    internal static string RelClamped(string full, string baseDir)
    {
        string f = Path.GetFullPath(full);
        string b = NormalizeDir(baseDir);

        if (f.StartsWith(b, StringComparison.OrdinalIgnoreCase))
        {
            string rest = f[b.Length..].TrimStart('\\', '/');
            return rest;
        }

        // El archivo no cuelga de la base: devolvemos la parte " distinguishable".
        var fb = f.Split('\\', '/');
        var bb = b.Split('\\', '/');
        int i = 0;
        while (i < fb.Length - 1 && i < bb.Length - 1 &&
               string.Equals(fb[i], bb[i], StringComparison.OrdinalIgnoreCase)) i++;
        return string.Join('\\', fb.Skip(i));
    }

    internal static string ParentOf(string dir)
    {
        var d = NormalizeDir(dir);

        // OJO: GetDirectoryName se come la barra final, asi que "...\Mis Datos\" devolveria
        // "...\Mis Datos" y el padre seria la propia carpeta. Hay que quitar la barra
        // ANTES de preguntar.
        var trimmed = d.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return d;   // raiz de la unidad: no hay padre

        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
        {
            // "...\Mis Datos" sin padre solo pasa si trimmed es la raiz: se devuelve la raiz.
            return NormalizeDir(Path.GetPathRoot(trimmed) ?? trimmed);
        }

        return NormalizeDir(parent);
    }

    internal static string NormalizeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            string p = Path.GetFullPath(path);
            if (!p.EndsWith('\\') && !p.EndsWith('/')) p += "\\";
            return p;
        }
        catch { return path; }
    }

    internal static string Combine(string? prefix, string rel)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return rel;
        return string.IsNullOrEmpty(rel) ? prefix : prefix + "\\" + rel;
    }

    /// <summary>Limpia segmentos vacios, "." y cualquier ".." que se haya colado.</summary>
    internal static string CleanRelative(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return string.Empty;
        var parts = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => p != "." && p != "..")
                       .ToArray();
        return string.Join('\\', parts);
    }

    /// <summary>Sustituye los caracteres que Windows no admite en un nombre de carpeta.</summary>
    internal static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null!;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().Trim('.');
        return string.IsNullOrEmpty(s) ? null! : s;
    }

    private static string GetDir(string rel)
    {
        int i = rel.LastIndexOf('\\');
        return i <= 0 ? string.Empty : rel[..i];
    }
}
