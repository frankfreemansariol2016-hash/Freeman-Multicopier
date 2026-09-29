namespace FreemanMultiCopier.Core.Model;

/// <summary>Modos de copia. Decide que pasa con lo que YA existe en el destino.</summary>
public enum CopyMode
{
    /// <summary>Copia lo soltado y no toca nada de lo que ya habia. El de siempre.</summary>
    Normal = 0,

    /// <summary>No copia nada: borra del destino todo lo que no este en el origen, y quita carpetas vacias.</summary>
    RemoveDifferences = 1,

    /// <summary>Borra lo que sobra y despues copia lo que falta. Destino identico al origen.</summary>
    Mirror = 2,

    /// <summary>Como Mirror pero mueve en vez de borrar+recopiar cuando el archivo ya existe en otra carpeta.</summary>
    Sync = 3,
}

/// <summary>Estructura de carpetas dentro del dispositivo de destino.</summary>
public enum StructureKind
{
    /// <summary>Reproduce el arbol completo desde la raiz del origen.</summary>
    Structure = 0,

    /// <summary>Copia la carpeta arrastrada con su contenido, sin las carpetas de encima.</summary>
    Folders = 1,

    /// <summary>Todo suelto en la raiz, sin ninguna carpeta.</summary>
    Files = 2,

    /// <summary>Todo dentro de una carpeta con el nombre que elige el usuario. Por dentro, como Folders.</summary>
    CustomFolder = 3,

    /// <summary>Si arrastras carpeta -> Folders. Si arrastras ficheros sueltos -> raiz.</summary>
    NoStructure = 4,

    /// <summary>Como Folders pero anadiendo una carpeta mas por encima: la que contenia a la arrastrada.</summary>
    TopFolder = 5,

    /// <summary>Solo crea el arbol de carpetas vacio, sin copiar archivos.</summary>
    EmptyDirs = 6,

    /// <summary>Crea las carpetas y dentro archivos con el mismo nombre pero vacios (catalogo).</summary>
    ZeroKb = 7,
}

/// <summary>Tipo fisico/logico del dispositivo, para icono, estructura por defecto y stats.</summary>
public enum DeviceKind
{
    Unknown = 0,
    UsbFlash = 1,      // memoria USB / pendrive
    UsbDisk = 2,       // disco duro externo
    Internal = 3,      // disco interno
    Boot = 4,          // disco de arranque (Windows)
    Network = 5,       // carpeta compartida de red
    Phone = 6,         // celular / tablet (MTP)
    Reader = 7,        // dispositivo del que se lee
    Writer = 8,        // dispositivo en el que se escribe
    Unavailable = 9,   // dejo de responder
}

/// <summary>Estado global de un trabajo de copia hacia un dispositivo.</summary>
public enum JobState
{
    Idle = 0,
    Queued = 1,
    Working = 2,
    Paused = 3,
    Completed = 4,
    CompletedWithErrors = 5,
    Cancelled = 6,
    Failed = 7,
}

/// <summary>Estado de un item dentro de una cola.</summary>
public enum ItemState
{
    Waiting = 0,
    Copying = 1,
    Completed = 2,
    Error = 3,
    Cancelled = 4,
    Skipped = 5,   // ya existia identico y el modo lo permite
}

/// <summary>Categoria de un archivo, para icono y para decidir si cuenta como video o audio.</summary>
public enum MediaKind
{
    Other = 0,
    Video = 1,
    Audio = 2,
    Image = 3,
    Document = 4,
    Archive = 5,
    Text = 6,
    Folder = 7,
}

/// <summary>Prioridad de un dispositivo: cuantas razones tiene el planificador para atenderlo antes.</summary>
public enum PriorityLevel
{
    Low = -2,
    BelowNormal = -1,
    Normal = 0,
    AboveNormal = 1,
    High = 2,
    Immediate = 3,
}
