// Genera el .ico y los .png del icono a partir de branding\icono.svg.
//
// El SVG es la fuente. Aqui solo se leen sus <path>, y como estan escritos con
// comandos absolutos M/L/C/Z, que es el mismo idioma que Geometry.Parse de WPF,
// no hace falta ningun traductor de SVG: la geometria del icono y la que se
// dibuja son literalmente la misma cosa.
//
//   dotnet run --project branding\icono
//
// No hay nada que instalar ni ningun paquete: el rasterizado lo hace WPF, que ya
// esta en la casa, con el mismo suavizado que usa la ventana.

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreemanMultiCopier.Branding;

internal static class Program
{
    // Tamanos del .ico. Hasta 48 va como DIB de 32 bits, que es lo que entienden
    // todos los Exploradores sin excusas; de 64 para arriba, PNG, que es lo que
    // saben colocar Explorer y la barra de tareas sin reescalar a mano.
    private static readonly int[] IcoDib = { 16, 20, 24, 32, 40, 48 };
    private static readonly int[] IcoPng = { 64, 96, 128, 256 };

    // Los .png sueltos van para el manual y para la ficha del programa. No hacen
    // falta para la casa, pero es el mismo render y de paso se tienen.
    private static readonly int[] Png = { 16, 24, 32, 48, 64, 128, 256, 512 };

    private const double Rejilla = 32.0;

    private static readonly Regex EtiquetaPath = new(
        "<path\\b(?<todo>[^>]*)/>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Atributo = new(
        "(?<clave>[\\w-]+)\\s*=\\s*\"(?<valor>[^\"]*)\"",
        RegexOptions.Compiled);

    private static int Main(string[] args)
    {
        string raiz = Raiz();
        string svg = Path.Combine(raiz, "branding", "icono.svg");
        if (!File.Exists(svg))
        {
            Console.Error.WriteLine("No esta el SVG: " + svg);
            return 1;
        }

        List<Forma> formas = Leer(File.ReadAllText(svg));
        if (formas.Count == 0)
        {
            Console.Error.WriteLine("El SVG no tiene ningun <path>.");
            return 1;
        }

        // Un solo render por lado, reutilizado por el .ico y por los .png.
        var pixeles = new Dictionary<int, byte[]>();
        foreach (int lado in IcoDib.Union(IcoPng).Union(Png).OrderBy(x => x))
            pixeles[lado] = lado >= 64 ? PngBytes(formas, lado) : Dib(formas, lado);

        var entradas = new List<(int Size, byte[] Data)>();
        entradas.AddRange(IcoDib.Select(l => (l, pixeles[l])));
        entradas.AddRange(IcoPng.Select(l => (l, pixeles[l])));

        string ico = Path.Combine(raiz, "src", "FreemanMultiCopier", "Resources", "app.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(ico)!);
        File.WriteAllBytes(ico, Ico(entradas));
        Console.WriteLine("ico   {0} bytes, {1} tamanos: {2}",
            new FileInfo(ico).Length, entradas.Count,
            string.Join(" ", entradas.Select(e => e.Size.ToString(CultureInfo.InvariantCulture))));

        string pngDir = Path.Combine(raiz, "branding", "png");
        Directory.CreateDirectory(pngDir);
        foreach (int lado in Png)
        {
            if (lado < 64) continue;   // abajo de 64 el .ico lleva DIB
            File.WriteAllBytes(Path.Combine(pngDir, "app-" + lado + ".png"), pixeles[lado]);
        }
        Console.WriteLine("png   {0} y {1} en {2}", 64, pixeles[512].Length, pngDir);

        // Modo volcar: el icono por pantalla de texto. Sin esto no hay forma de
        // revisar un cambio de dos pixeles sin abrir el .ico en un explorador, y
        // un icono mal trazado no se ve hasta que ya esta en los escritorios.
        if (args.Contains("volcar", StringComparer.OrdinalIgnoreCase))
        {
            foreach (int lado in new[] { 32, 16 })
            {
                Console.WriteLine();
                Console.WriteLine("--- " + lado + " px ---");
                Volcar(formas, lado);
            }
        }

        return 0;
    }

    /// <summary>
    /// Un caracter por pixel: el que mas se le parezca de los cuatro colores del
    /// icono, contando el alfa. Los bordes suavizados caen en el color mas
    /// cercano, que es lo que se ve a simple vista de todos modos.
    /// </summary>
    private static void Volcar(List<Forma> formas, int lado)
    {
        var rtb = Render(formas, lado);
        int fila = rtb.PixelWidth * 4;
        var px = new byte[fila * rtb.PixelHeight];
        rtb.CopyPixels(px, fila, 0);

        (char Letra, Color Color)[] paleta =
        {
            ('#', Color.FromRgb(0x2B, 0x2E, 0x36)),   // cuerpo
            ('=', Color.FromRgb(0x1B, 0x1D, 0x22)),   // peana
            ('F', Color.FromRgb(0x3D, 0x8B, 0xFF)),   // la letra
            ('o', Color.FromRgb(0x56, 0x5D, 0x6C)),   // plato
        };

        Console.WriteLine();
        for (int y = 0; y < rtb.PixelHeight; y++)
        {
            var linea = new System.Text.StringBuilder(rtb.PixelWidth);
            for (int x = 0; x < rtb.PixelWidth; x++)
            {
                int p = y * fila + x * 4;
                byte a = px[p + 3];
                if (a < 40) { linea.Append('.'); continue; }

                // El alfa se compone contra el fondo del escritorio, que aqui se
                // supone el gris medio de Windows. Asi un borde a media tinta
                // sale del color que de verdad se ve.
                double f = a / 255.0;
                var plano = Color.FromRgb(
                    (byte)(px[p + 2] * f + 0x80 * (1 - f)),
                    (byte)(px[p + 1] * f + 0x80 * (1 - f)),
                    (byte)(px[p + 0] * f + 0x80 * (1 - f)));

                (char Letra, Color Color) mejor = paleta[0];
                double distancia = double.MaxValue;
                foreach (var e in paleta)
                {
                    double d = Distancia(plano, e.Color);
                    if (d < distancia) { distancia = d; mejor = e; }
                }

                linea.Append(distancia < 24 ? mejor.Letra : '·');
            }
            Console.WriteLine("  " + linea);
        }
        Console.WriteLine();
    }

    private static double Distancia(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    // ------------------------------------------------------------------ el SVG

    private sealed record Forma(Geometry Geometria, Color Relleno);

    private static List<Forma> Leer(string texto)
    {
        var formas = new List<Forma>();
        string cuerpo = Regex.Replace(texto, @"<!--.*?-->", " ", RegexOptions.Singleline);

        foreach (Match etiqueta in EtiquetaPath.Matches(cuerpo))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Atributo.Matches(etiqueta.Groups["todo"].Value))
                attrs[a.Groups["clave"].Value] = a.Groups["valor"].Value;

            if (!attrs.TryGetValue("d", out var d)) continue;

            // Geometry no tiene FillRule: lo tienen las dos clases que de verdad
            // pueden tener mas de una figura, que es justo lo que hace falta aquí.
            var g = (Geometry)Geometry.Parse(d);
            FillRule regla = attrs.TryGetValue("fill-rule", out var valor)
                          && string.Equals(valor, "evenodd", StringComparison.OrdinalIgnoreCase)
                            ? FillRule.EvenOdd
                            : FillRule.Nonzero;
            switch (g)
            {
                case PathGeometry camino: camino.FillRule = regla; break;
                case GeometryGroup grupo: grupo.FillRule = regla; break;
            }
            g.Freeze();

            Color color = attrs.TryGetValue("fill", out var hex)
                ? (Color)ColorConverter.ConvertFromString(hex)
                : Colors.Black;

            formas.Add(new Forma(g, color));
        }

        return formas;
    }

    // ------------------------------------------------------------------ dibujo

    private static byte[] PngBytes(List<Forma> formas, int lado)
    {
        var rtb = Render(formas, lado);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static RenderTargetBitmap Render(List<Forma> formas, int lado)
    {
        var grupo = new DrawingGroup();
        foreach (var f in formas)
        {
            // La escala va en la geometria y no en el lienzo para que cada
            // forma conserve su regla de relleno y su pincel.
            var g = f.Geometria.Clone();
            g.Transform = new ScaleTransform(lado / Rejilla, lado / Rejilla);
            grupo.Children.Add(new GeometryDrawing(new SolidColorBrush(f.Relleno), null, g));
        }
        grupo.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawDrawing(grupo);

        var rtb = new RenderTargetBitmap(lado, lado, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ------------------------------------------------------------------- el ico

    /// <summary>
    /// DIB de 32 bits como los quiere el formato ICO: cabecera con la altura
    /// duplicada porque van la mascara de color y la de transparencia, los
    /// pixeles de abajo arriba y la mascara AND al final, a filas de 4 bytes.
    /// Con alpha de 32 bits la mascara AND va toda a cero, que es lo que hace
    /// cualquiera desde Windows 2000.
    /// </summary>
    private static byte[] Dib(List<Forma> formas, int lado)
    {
        var rtb = Render(formas, lado);
        int fila = rtb.PixelWidth * 4;
        int alto = rtb.PixelHeight;
        var pixeles = new byte[fila * alto];
        rtb.CopyPixels(pixeles, fila, 0);

        int mascaraFila = ((lado + 31) / 32) * 4;
        int xor = pixeles.Length;
        int mascara = mascaraFila * alto;

        var d = new byte[40 + xor + mascara];
        Escribe(d, 0, 40);                  // biSize
        Escribe(d, 4, lado);                // biWidth
        Escribe(d, 8, lado * 2);            // biHeight: XOR + AND
        Escribe(d, 12, 1);                  // biPlanes
        Escribe(d, 14, 32);                 // biBitCount
        Escribe(d, 16, 0);                  // biCompression = BI_RGB
        Escribe(d, 20, xor + mascara);      // biSizeImage
        Escribe(d, 24, 3780);               // biXPelsPerMeter: 96 dpi
        Escribe(d, 28, 3780);               // biYPelsPerMeter
        Escribe(d, 32, 0);                  // biClrUsed
        Escribe(d, 36, 0);                  // biClrImportant

        // De abajo arriba, que es como los lee el formato ICO.
        for (int y = 0; y < alto; y++)
            Buffer.BlockCopy(pixeles, (alto - 1 - y) * fila, d, 40 + y * fila, fila);

        return d;
    }

    private static byte[] Ico(List<(int Size, byte[] Data)> entradas)
    {
        var cab = new byte[6 + 16 * entradas.Count];
        Escribe(cab, 0, 0);                                   // reservado
        Escribe(cab, 2, 1);                                   // 1 = icono
        Escribe(cab, 4, entradas.Count);

        int offset = cab.Length;
        for (int i = 0; i < entradas.Count; i++)
        {
            var (lado, datos) = entradas[i];
            int p = 6 + 16 * i;
            // 256 se escribe como 0: en un byte no cabe, y es lo que marca el tamano.
            cab[p] = cab[p + 1] = lado >= 256 ? (byte)0 : (byte)lado;
            Escribe(cab, p + 4, 1);                           // planos
            Escribe(cab, p + 6, 32);                          // bits por pixel
            Escribe(cab, p + 8, datos.Length);
            Escribe(cab, p + 12, offset);
            offset += datos.Length;
        }

        using var salida = new MemoryStream();
        salida.Write(cab, 0, cab.Length);
        foreach (var (_, datos) in entradas) salida.Write(datos, 0, datos.Length);
        return salida.ToArray();
    }

    private static void Escribe(byte[] d, int pos, int valor)
    {
        d[pos] = (byte)(valor & 0xFF);
        d[pos + 1] = (byte)((valor >> 8) & 0xFF);
        d[pos + 2] = (byte)((valor >> 16) & 0xFF);
        d[pos + 3] = (byte)((valor >> 24) & 0xFF);
    }

    // -------------------------------------------------------------------- rutas

    private static string Raiz()
    {
        // Sube desde branding\icono\bin\... hasta la raiz del proyecto, que es
        // donde vive el SVG. Se reconoce por la carpeta branding.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "branding")) &&
                File.Exists(Path.Combine(dir.FullName, "branding", "icono.svg")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Environment.CurrentDirectory;
    }
}
