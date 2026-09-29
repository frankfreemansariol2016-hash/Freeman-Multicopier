using System.IO;

namespace FreemanMultiCopier.Core.Model;

/// <summary>
/// Un archivo dentro de una cola de copia. Se clona por dispositivo: cada destino
/// tiene su propia instancia con su ruta relativa ya resuelta.
/// </summary>
public sealed class CopyItem
{
    /// <summary>
    /// Ruta del archivo de origen. Es mutable solo porque la Sincronizacion puede reapuntar
    /// un destino al archivo equivalente que ya existe en otra carpeta del dispositivo.
    /// </summary>
    public required string SourcePath { get; set; }

    /// <summary>Ruta relativa dentro del destino. La resuelve StructureResolver al encolar.</summary>
    public string DestRelative { get; set; } = string.Empty;

    public long Length { get; set; }

    public string FileName => Path.GetFileName(SourcePath);

    public MediaKind Kind { get; init; } = MediaKind.Other;

    public ItemState State { get; set; } = ItemState.Waiting;

    public long BytesDone { get; set; }

    private long _readBps;
    private long _writeBps;

    /// <summary>
    /// A que velocidad se esta leyendo ESTE archivo del origen. Lo escriben los hilos
    /// de copia, asi que va con Interlocked. En un archivo que va a varios destinos es
    /// la misma cifra en todos: la lectura del origen es una sola, y despues se reparte.
    /// </summary>
    public long ReadBps
    {
        get => Interlocked.Read(ref _readBps);
        set => Interlocked.Exchange(ref _readBps, value);
    }

    /// <summary>A que velocidad se esta escribiendo en el destino de este trabajo.</summary>
    public long WriteBps
    {
        get => Interlocked.Read(ref _writeBps);
        set => Interlocked.Exchange(ref _writeBps, value);
    }

    public string? Error { get; set; }

    /// <summary>Orden manual dentro de la cola (Inicio/RePag/AvPág/Fin).</summary>
    public int Order { get; set; }

    /// <summary>Fecha de ultima modificacion del origen, para decidir si hay que recopiar.</summary>
    public DateTime SourceWriteTimeUtc { get; init; }

    /// <summary>Copia superficial con la ruta ya resuelta, para clonar la lista a otro dispositivo.</summary>
    public CopyItem CloneFor(string destRelativeOverride) => new()
    {
        SourcePath = SourcePath,
        DestRelative = destRelativeOverride,
        Length = Length,
        Kind = Kind,
        Order = Order,
        SourceWriteTimeUtc = SourceWriteTimeUtc,
    };

    public double Fraction => Length <= 0 ? (State == ItemState.Completed ? 1d : 0d)
                                      : Math.Clamp((double)BytesDone / Length, 0d, 1d);
}
