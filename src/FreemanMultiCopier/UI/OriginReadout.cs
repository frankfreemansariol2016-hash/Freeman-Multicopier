using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.UI;

/// <summary>
/// Lo que esta leyendo el ORIGEN ahora mismo, y hacia donde se va lo que lee.
///
/// El motivo de que esto sea una pieza aparte y no una fila mas: cuando el mismo archivo
/// va a cinco destinos, el origen lo lee una sola vez y lo reparte. Cada fila ensena su
/// parte, que es un trozo de la misma lectura. Sumadas dan la transferencia total, y el
/// origen va a una sola cifra. Sin esto, cinco filas enseñando 26 MB/s cada una parecen
/// cinco copias lentas cuando en realidad es un solo origen a 130 MB/s repartido.
///
/// Se calcula en la ventana, con los datos que las filas ya tienen. No toca los hilos de
/// copia ni avisa a nadie: se relee cada vez que el panel se refresca.
/// </summary>
public sealed class OriginReadout : INotifyPropertyChanged
{
    private bool _hasWork;
    private string _fileName = string.Empty;
    private string _folder = string.Empty;
    private string _sizeText = string.Empty;
    private string _readText = string.Empty;
    private string _transferText = string.Empty;
    private string _targetsText = string.Empty;
    private string _othersText = string.Empty;

    /// <summary>Hay algo saliendo del origen ahora mismo.</summary>
    public bool HasWork
    {
        get => _hasWork;
        private set => Set(ref _hasWork, value);
    }

    /// <summary>Archivo que se esta leyendo del origen.</summary>
    public string FileName
    {
        get => _fileName;
        private set => Set(ref _fileName, value);
    }

    /// <summary>Carpeta de donde sale, para saber de que archivo se habla.</summary>
    public string Folder
    {
        get => _folder;
        private set => Set(ref _folder, value);
    }

    /// <summary>Tamano del archivo.</summary>
    public string SizeText
    {
        get => _sizeText;
        private set => Set(ref _sizeText, value);
    }

    /// <summary>A que velocidad sale del origen. Es la misma para todos sus destinos.</summary>
    public string ReadText
    {
        get => _readText;
        private set => Set(ref _readText, value);
    }

    /// <summary>A que velocidad se esta escribiendo en total, sumando todos los destinos.</summary>
    public string TransferText
    {
        get => _transferText;
        private set => Set(ref _transferText, value);
    }

    /// <summary>Destino y velocidad de cada uno: "C: 26 MB/s   D: 24 MB/s".</summary>
    public string TargetsText
    {
        get => _targetsText;
        private set => Set(ref _targetsText, value);
    }

    /// <summary>
    /// Los demas archivos que estan saliendo del origen al mismo tiempo, con lo que
    /// lleva cada uno. Vacio cuando el origen solo tiene un archivo en marcha, que es
    /// lo normal.
    /// </summary>
    public string OthersText
    {
        get => _othersText;
        private set => Set(ref _othersText, value);
    }

    public void Update(IReadOnlyList<LiveCopyRow> rows)
    {
        // Se agrupa por archivo de origen. Si todos los destinos van al mismo hay un
        // solo grupo, y por eso sale una sola lectura, que es lo que pasa de verdad: el
        // origen lo lee una vez y lo reparte. Si ademas se estan copiando otros
        // archivos a la vez, cada uno sale en su propia linea con lo que lleva.
        var grupos = new List<Grupo>();

        foreach (var g in rows.Where(r => r.IsCopying)
                               .GroupBy(r => r.Device.Job!.ActiveSourcePath,
                                        StringComparer.OrdinalIgnoreCase))
        {
            var grupo = new Grupo(g.Key);
            foreach (var r in g)
            {
                var j = r.Device.Job!;
                grupo.Transfer += j.ActiveWriteBps;
                grupo.Read = Math.Max(grupo.Read, j.ActiveReadBps);
                grupo.Filas.Add(r);
            }
            grupos.Add(grupo);
        }

        if (grupos.Count == 0)
        {
            HasWork = false;
            FileName = "(nada en marcha)";
            Folder = "";
            SizeText = "";
            ReadText = "";
            TransferText = "";
            TargetsText = "";
            OthersText = "";
            return;
        }

        grupos.Sort((a, b) => b.Transfer.CompareTo(a.Transfer));
        var mejor = grupos[0];

        HasWork = true;
        string nombre = Path.GetFileName(mejor.Ruta);
        FileName = nombre.Length > 0 ? nombre : mejor.Ruta;
        Folder = Path.GetDirectoryName(mejor.Ruta) ?? "";

        // El tamano se saca del propio archivo de la cola y NO de preguntar al disco:
        // esto se refresca cada medio segundo y una llamada al sistema por refresco se
        // nota cuando el origen esta en un disco lento o en la red.
        long largo = mejor.Filas[0].Device.Job!.ActiveItem?.Length ?? 0;
        SizeText = largo > 0 ? Txt.Bytes(largo) : "";

        string textoLectura = Txt.Rate(mejor.Read);
        ReadText = textoLectura.Length == 0 ? "LECTURA --" : "LECTURA " + textoLectura;

        string textoTransfer = Txt.Rate(mejor.Transfer);
        TransferText = textoTransfer.Length == 0
            ? "TRANSFERENCIA --"
            : $"TRANSFERENCIA {textoTransfer} hacia {mejor.Filas.Count} destino{(mejor.Filas.Count == 1 ? "" : "s")}";

        var trozos = new List<string>(mejor.Filas.Count);
        foreach (var r in mejor.Filas)
        {
            string lleva = Txt.Rate(r.Device.Job!.ActiveWriteBps);
            trozos.Add($"{r.Device.Volume.Id} {(lleva.Length == 0 ? "--" : lleva)}");
        }
        TargetsText = string.Join("     ", trozos);

        if (grupos.Count == 1)
        {
            OthersText = "";
            return;
        }

        var resto = new List<string>(grupos.Count - 1);
        for (int i = 1; i < grupos.Count; i++)
        {
            var g = grupos[i];
            string lee = Txt.Rate(g.Read);
            string lleva = Txt.Rate(g.Transfer);
            string cab = $"{Path.GetFileName(g.Ruta)}  {(lee.Length > 0 ? lee + " de origen, " : "")}"
                      + $"{(lleva.Length == 0 ? "--" : lleva)} hacia {g.Filas.Count} destino{(g.Filas.Count == 1 ? "" : "s")}";
            resto.Add(cab);
        }
        OthersText = "ADEMAS  " + string.Join("     ", resto);
    }

    private sealed class Grupo
    {
        public Grupo(string ruta) => Ruta = ruta;

        public string Ruta { get; }

        public List<LiveCopyRow> Filas { get; } = new();

        public long Transfer;

        public long Read;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
