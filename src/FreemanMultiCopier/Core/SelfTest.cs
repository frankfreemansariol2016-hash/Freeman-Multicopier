using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FreemanMultiCopier.Core.Devices;
using FreemanMultiCopier.Core.Engine;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.Core;

/// <summary>
/// Bateria de pruebas del motor, sin interfaz grafica.
///
/// Existe porque lo que hace este programa (leer una vez, escribir en quince sitios a la
/// vez, no perder ni un byte por el camino) es justamente lo que no se puede comprobar
/// mirando la ventana. Se ejecuta con <c>FreemanMultiCopier.exe --selftest</c>.
///
/// Las pruebas usan carpetas de disco como si fueran dispositivos: al motor le da igual,
/// lo que le importa es la ruta del destino.
/// </summary>
public static class SelfTest
{
    private static readonly List<string> _failures = new();

    /// <summary>Por que fallo la ultima comprobacion, para que el informe lo diga.</summary>
    private static string _motivo = string.Empty;
    private static int _passed;
    private static int _total;

    /// <summary>Codigo de salida: 0 si todo paso, 1 si hubo algun fallo.</summary>
    public static int Run(string outputPath)
    {
        var report = new StringBuilder();

        // El informe se va escribiendo linea a linea, no solo al final. Si una prueba se
        // queda colgada, el archivo dice exactamente cual fue la ultima que empezo, que
        // es justo lo que hace falta y se pierde todo si se escribe al concluir.
        void Log(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
            TryWrite(outputPath, report.ToString());
        }

        // Un reloj de seguridad. Estas pruebas mueven decenas de megas; si el motor se
        // queda esperando a si mismo, no hay forma de que este proceso termine solo.
        using var watchdog = new Timer(_ =>
        {
            TryWrite(outputPath, report.ToString() + Environment.NewLine +
                "LA PRUEBA SE QUEDO COLGADA Y EL RELOJ DE SEGURIDAD LA CORTO." + Environment.NewLine);
            Environment.Exit(3);
        }, null, TimeSpan.FromMinutes(4), TimeSpan.FromMilliseconds(-1));

        // Se arranca sin contexto de sincronizacion, como si esto no fuera una ventana.
        // Esta aplicacion es de WPF, asi que el hilo de la interfaz trae un despachador
        // pegado. Si alguna parte del motor hace un await sin ConfigureAwait(false), su
        // continuacion se queda esperando a que el despachador bombee mensajes, y aqui no
        // hay nadie que lo haga: la prueba se cuelga sin decir por que.
        SynchronizationContext.SetSynchronizationContext(null);

        Log("=========================================================");
        Log("  Freeman MULTICOPIER - prueba del motor");
        Log($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log("=========================================================");
        Log("");

        string root = Path.Combine(Path.GetTempPath(),
            "FreemanSelfTest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        try
        {
            Directory.CreateDirectory(root);

            Log("-- Estructuras de carpetas --");
            TestStructures(root, Log);

            Log("");
            Log("-- Copia simple a un destino --");
            TestSingleCopy(root, Log);

            Log("");
            Log("-- Copia simultanea a 8 destinos --");
            TestMultiTarget(root, Log);

            Log("");
            Log("-- Saltar lo que ya esta al dia --");
            TestSkipUpToDate(root, Log);

            Log("");
            Log("-- Espejo: borra lo que sobra --");
            TestMirror(root, Log);

            Log("");
            Log("-- Cancelar a mitad --");
            TestCancel(root, Log);

            Log("");
            Log("-- Nomes raros, espacios y acentos --");
            TestAwkwardNames(root, Log);

            Log("");
            Log("-- Pausa y reanudar --");
            TestPauseResume(root, Log);

            Log("");
            Log("-- Entradas de Windows (P/Invoke) --");
            TestNativeCalls(root, Log);

            Log("");
            Log("-- Servicio completo, con dispositivos --");
            TestService(root, Log);

            Log("");
            Log("-- Arrastrar y soltar de punta a punta --");
            TestEndToEnd(root, Log);
        }
        catch (Exception ex)
        {
            _failures.Add("EXCEPCION NO CONTROLADA: " + ex.Message);
        }
        finally
        {
            TryDeleteTree(root);
        }

        Log("");
        Log("=========================================================");
        Log($"  {_passed}/{_total} pruebas correctas");
        if (_failures.Count == 0)
        {
            Log("  RESULTADO: TODO CORRECTO");
        }
        else
        {
            Log($"  RESULTADO: {_failures.Count} FALLO(S)");
            foreach (var f in _failures) Log("   - " + f);
        }
        Log("=========================================================");

        TryWrite(outputPath, report.ToString());
        return _failures.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- pruebas

    private static void TestStructures(string root, Action<string> log)
    {
        // Arbol de origen:  origen\Mis Datos\a.txt  y  origen\Mis Datos\sub\b.txt
        string src = NewDir(root, "src");
        string baseDir = NewDir(src, "Mis Datos");
        NewDir(baseDir, "sub");
        WriteFile(Path.Combine(baseDir, "a.txt"), "a");
        WriteFile(Path.Combine(baseDir, "sub", "b.txt"), "b");

        // "Carpetas": relativo a la carpeta soltada. El nombre de esa carpeta NO aparece.
        var plan = Plan(new[] { baseDir }, src, StructureKind.Folders);
        CheckPlan("Estructura Carpetas: a.txt queda en la raiz", log, plan,
            p => p.Files.Any(f => f.DestRelative == "a.txt"));
        CheckPlan("Estructura Carpetas: sub/b.txt conserva la subcarpeta", log, plan,
            p => p.Files.Any(f => f.DestRelative == "sub\\b.txt"));
        CheckPlan("Estructura Carpetas: no sube un nivel de mas", log, plan,
            p => p.Files.All(f => !f.DestRelative.Contains("..")));

        // Carpeta Superior: relativo al PADRE de la carpeta soltada, o sea una mas arriba.
        var top = Plan(new[] { baseDir }, src, StructureKind.TopFolder);
        CheckPlan("Carpeta Superior: mete la carpeta soltada", log, top,
            p => p.Files.Any(f => f.DestRelative == "Mis Datos\\a.txt"));

        // Arbol completo: relativo a la raiz del origen.
        var full = Plan(new[] { baseDir }, src, StructureKind.Structure);
        CheckPlan("Estructura completa: mete el camino entero", log, full,
            p => p.Files.Any(f => f.DestRelative == "Mis Datos\\a.txt"));

        // Solo ficheros: todo en la raiz, sin carpetas.
        var flat = Plan(new[] { baseDir }, src, StructureKind.Files);
        CheckPlan("Sin estructura: aplana a nombres sueltos", log, flat,
            p => p.Files.Select(f => f.DestRelative).OrderBy(x => x, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "a.txt", "b.txt" }, StringComparer.Ordinal));

        // Carpeta personalizada: todo debajo del nombre elegido.
        var custom = Plan(new[] { baseDir }, src, StructureKind.CustomFolder, "Mi Copia");
        CheckPlan("Carpeta personalizada: mete una carpeta por encima", log, custom,
            p => p.Files.All(f => f.DestRelative.StartsWith("Mi Copia\\")));

        // Un fichero suelto, no una carpeta.
        var loose = Plan(new[] { Path.Combine(baseDir, "a.txt") }, src, StructureKind.Folders);
        CheckPlan("Fichero suelto con Carpetas: queda en la raiz", log, loose,
            p => p.Files.Count == 1 && p.Files[0].DestRelative == "a.txt");

        // Base equivocada: nada puede salirse del dispositivo.
        var evil = Plan(new[] { baseDir }, "C:\\", StructureKind.Folders);
        CheckPlan("Recorte de rutas: ningun destino escapa del dispositivo", log, evil,
            p => p.Files.All(f => !f.DestRelative.Contains("..")
                                 && !Path.IsPathRooted(f.DestRelative)));

        // Solo carpetas vacias: el arbol sin los archivos.
        var emptyDirs = Plan(new[] { baseDir }, src, StructureKind.EmptyDirs);
        CheckPlan("Solo carpetas vacias: marca que no copie archivos", log, emptyDirs,
            p => p.FoldersOnly);
    }

    private static void TestSingleCopy(string root, Action<string> log)
    {
        string src = NewDir(root, "single-src");
        string dst = NewDir(root, "single-dst");
        string file = WriteFile(Path.Combine(src, "video.mp4"), NewBytes(3 * 1024 * 1024 + 12345));

        var job = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, new[] { file });
        await0(RunJob(job));

        CheckJob("Copia simple: termina bien", log, job, j => j.State == JobState.Completed);
        CheckDir("Copia simple: el archivo cae integro", log, dst,
            d => SameBytes(Path.Combine(d, "video.mp4"), file));
        CheckJob("Copia simple: progreso al 100 %", log, job, j => j.Progress >= 0.999);
    }

    private static void TestMultiTarget(string root, Action<string> log)
    {
        const int Devices = 8;
        string src = NewDir(root, "multi-src");
        NewDir(src, "carpeta");
        // Por encima de los 8 MB del umbral de union a proposito: por debajo, cada
        // destino copiaria el archivo por su cuenta y esta prueba no probaria el
        // reparto, que es justo donde se puede perder el contenido.
        string big = WriteFile(Path.Combine(src, "grande.bin"), NewBytes(12 * 1024 * 1024));
        string small = WriteFile(Path.Combine(src, "carpeta", "chico.txt"), "hola");
        string empty = WriteFile(Path.Combine(src, "vacio.txt"), "");

        var engine = new MultiCopyEngine(new BufferBudget(256 * 1024, 4L * 1024 * 1024, 4));
        var jobs = new List<CopyJob>();

        for (int i = 0; i < Devices; i++)
        {
            string dst = NewDir(root, $"multi-dst-{i}");
            var job = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src,
                             new[] { big, small, empty });
            // Esto es lo que hace el programa de verdad al soltar una lista sobre
            // varios dispositivos, y es lo que hace que lean el origen una vez entre
            // todos en lugar de una por destino.
            job.PlannedTargets = Devices;
            engine.SetThrottle("D" + i, 0);
            jobs.Add(job);
        }

        // Todos a la vez, que es justo cuando las sesiones deben agrupar.
        await0(Task.WhenAll(jobs.Select(j => engine.RunAsync(j))));

        for (int i = 0; i < Devices; i++)
        {
            int n = i;
            string dst = Path.Combine(root, $"multi-dst-{n}");
            CheckJob($"Multidestino {n}: termina bien", log, jobs[n], j => j.State == JobState.Completed);
            CheckDir($"Multidestino {n}: grande.bin integro", log, dst,
                d => SameBytes(Path.Combine(d, "grande.bin"), big));
            CheckDir($"Multidestino {n}: carpeta\\chico.txt integro", log, dst,
                d => SameBytes(Path.Combine(d, "carpeta", "chico.txt"), small));
            CheckDir($"Multidestino {n}: vacio.txt creado y vacio", log, dst,
                d => File.Exists(Path.Combine(d, "vacio.txt"))
                     && new FileInfo(Path.Combine(d, "vacio.txt")).Length == 0);
        }
    }

    private static void TestSkipUpToDate(string root, Action<string> log)
    {
        string src = NewDir(root, "skip-src");
        string dst = NewDir(root, "skip-dst");
        string file = WriteFile(Path.Combine(src, "dato.txt"), "contenido original");

        var job1 = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, new[] { file });
        await0(RunJob(job1));
        CheckJob("Recopia: la primera pasada copia", log, job1, j => j.State == JobState.Completed);

        // Se estropea el destino: la segunda pasada debe enterarse.
        WriteFile(Path.Combine(dst, "dato.txt"), "contenido modificado");

        var job2 = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, new[] { file });
        await0(RunJob(job2));
        CheckDir("Recopia: la segunda pasada lo arregla", log, dst,
            d => File.ReadAllText(Path.Combine(d, "dato.txt")) == "contenido original");

        // Sin cambios: debe saltarselo entero.
        var job3 = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, new[] { file });
        await0(RunJob(job3));
        CheckJob("Recopia: sin cambios no gasta trabajo", log, job3,
            j => j.Queue.All(i => i.State == ItemState.Skipped));
    }

    private static void TestMirror(string root, Action<string> log)
    {
        string src = NewDir(root, "mirror-src");
        string dst = NewDir(root, "mirror-dst");
        string keep = WriteFile(Path.Combine(src, "se-queda.txt"), "este va");
        string drop = WriteFile(Path.Combine(src, "se-va.txt"), "este no");

        // Se deja en destino material que no esta en origen.
        WriteFile(Path.Combine(dst, "intruso.txt"), "no deberia sobrevivir");
        Directory.CreateDirectory(Path.Combine(dst, "carpeta-intrusa"));
        WriteFile(Path.Combine(dst, "carpeta-intrusa", "dentro.txt"), "tampoco");

        var job = JobFor(dst, CopyMode.Mirror, StructureKind.Folders, src, new[] { keep, drop });
        await0(RunJob(job));

        CheckDir("Espejo: copia lo que falta", log, dst,
            d => File.Exists(Path.Combine(d, "se-queda.txt")) && File.Exists(Path.Combine(d, "se-va.txt")));
        CheckDir("Espejo: borra el archivo sobrante", log, dst,
            d => !File.Exists(Path.Combine(d, "intruso.txt")));
        CheckDir("Espejo: borra la carpeta sobrante", log, dst,
            d => !Directory.Exists(Path.Combine(d, "carpeta-intrusa")));
    }

    private static void TestCancel(string root, Action<string> log)
    {
        string src = NewDir(root, "cancel-src");
        string dst = NewDir(root, "cancel-dst");
        // 300 MB, no 80. Con el motor ya rápido, 80 MB se acababan antes de que la
        // prueba llegara a cancelar y la prueba fallaba por culpa de la prueba.
        string file = WriteFile(Path.Combine(src, "enorme.bin"), NewBytes(300 * 1024 * 1024));

        var job = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, new[] { file });

        // Y no se duerme un tiempo fijo a ciegas: se espera a que el trabajo haya
        // empezado a escribir de verdad, que es lo que se quiere cancelar. El tiempo
        // de espera lo pone el tamano del archivo y la maquina, no la prueba.
        Task running = RunJob(job);
        bool arranco = EsperaHasta(() => job.BytesWritten > 0, 30000);
        job.Cancel.Cancel();
        await0(SafeAwait(running));

        Check("Cancelar: se llega a empezar a copiar", log, () => arranco);
        CheckJob("Cancelar: el trabajo queda cancelado", log, job, j => j.State == JobState.Cancelled);

        // Aunque se cancele en marcha, el destino no debe quedar con un archivo a medias
        // que el programa luego no sepa distinguir de uno bueno.
        CheckDir("Cancelar: no deja medias copias que parezcan completas", log, dst,
            d => !File.Exists(Path.Combine(d, "enorme.bin"))
                 || SameBytes(Path.Combine(d, "enorme.bin"), file));
    }

    private static void TestAwkwardNames(string root, Action<string> log)
    {
        string src = NewDir(root, "names-src");
        string dst = NewDir(root, "names-dst");
        string deep = NewDir(NewDir(src, "con espacios y & simbolos"), "acentos ñ 中文");

        var files = new List<string>
        {
            WriteFile(Path.Combine(deep, "archivo con espacios.txt"), "uno"),
            WriteFile(Path.Combine(deep, "acentuado con enye.txt"), "dos"),
            WriteFile(Path.Combine(deep, "sin-extension"), "tres"),
        };

        var job = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, files);
        await0(RunJob(job));

        CheckJob("Nombres raros: el trabajo termina bien", log, job, j => j.State == JobState.Completed);
        foreach (var f in files)
        {
            string expected = Path.GetRelativePath(src, f);
            CheckDir($"Nombres raros: {Path.GetFileName(f)} en su sitio", log, dst,
                d => SameBytes(Path.Combine(d, expected), f));
        }
    }

    private static void TestPauseResume(string root, Action<string> log)
    {
        string src = NewDir(root, "pause-src");
        string dst = NewDir(root, "pause-dst");
        var files = new List<string>();
        for (int i = 0; i < 4; i++)
            files.Add(WriteFile(Path.Combine(src, $"parte{i:00}.bin"), NewBytes(2 * 1024 * 1024)));

        var job = JobFor(dst, CopyMode.Normal, StructureKind.Folders, src, files);

        // La copia va limitada a 1 MB/s, o sea 8 segundos de trabajo. Sin esto la prueba
        // seria una carrera: en un disco SSD rapido el trabajo se acabaria antes de que el
        // hilo de la prueba se molestara en pausar, y se mediria el reloj, no el motor.
        const long Limit = 1024 * 1024;
        var engine = new MultiCopyEngine(new BufferBudget(256 * 1024, 4L * 1024 * 1024, 4));
        engine.SetThrottle(job.Device.Volume.Id, Limit);

        Task running = engine.RunAsync(job);

        Thread.Sleep(700);
        job.Pause();
        Thread.Sleep(400);

        long doneWhilePaused = job.FilesCompleted;
        long bytesWhilePaused = job.BytesCopied;
        Thread.Sleep(600);
        bool frozen = job.FilesCompleted == doneWhilePaused
                   && job.BytesCopied <= bytesWhilePaused + (Limit / 2);   // margen de un bloque

        job.Resume();
        await0(SafeAwait(running));

        Check("Pausa: el trabajo se detiene de verdad", log, () => frozen);
        CheckJob("Pausa: al reanudar termina todo", log, job, j => j.State == JobState.Completed);
        CheckDir("Pausa: los 4 archivos quedan completos", log, dst,
            d => files.All(f => SameBytes(Path.Combine(d, Path.GetFileName(f)), f)));
    }

    /// <summary>
    /// El camino completo que recorren los dedos del cliente: soltar, encolar, copiar.
    /// Las pruebas del motor de arriba podrian pasar todas mientras el boton COPIAR no
    /// maken nada, porque usan una lista de archivos hecha a mano. Esta usa el mismo
    /// servicio que la ventana.
    /// </summary>
    private static void TestEndToEnd(string root, Action<string> log)
    {
        using var service = new CopyService();

        // Origen con contenido variado, incluidas carpetas.
        string origin = NewDir(root, "origen-usuario");
        string sub = NewDir(origin, "Album 2024");
        string big = WriteFile(Path.Combine(origin, "video.mp4"), NewBytes(2 * 1024 * 1024));
        string pic = WriteFile(Path.Combine(sub, "foto.jpg"), NewBytes(120_000));
        string txt = WriteFile(Path.Combine(origin, "nota.txt"), "recuerdame esto");

        // Cuatro destinos falsos que hacen de dispositivos.
        var targets = new List<DeviceState>();
        for (int i = 0; i < 4; i++)
        {
            string dir = NewDir(root, $"destino-{i}");
            targets.Add(new DeviceState
            {
                Volume = new VolumeRef
                {
                    Id = $"PRUEBA-{i}",
                    Root = dir,
                    Kind = DeviceKind.UsbFlash,
                    Label = $"Prueba {i}",
                    FreeSpace = 64L * 1024 * 1024 * 1024,
                },
            });
        }

        IReadOnlyList<CopyItem> queued = service.Enqueue(targets,
            new[] { origin }, origin, flat: false);

        Check("Arrastrar: se encolan archivos", log, () => queued.Count >= 3);
        Check("Arrastrar: los 4 destinos reciben la misma lista", log,
            () => targets.All(d => service.JobFor(d).Queue.Count == queued.Count));
        Check("Arrastrar: las carpetas del origen se respetan", log,
            () => targets[0].Job!.Queue.Any(i => i.DestRelative.Contains("Album 2024")));

        // El modo se pone solo en Normal en cuanto se encola. No es un descuido: un
        // Espejo olvidado borra el contenido de la memoria que venga despues. Lo que
        // ya esta en la cola conserva el modo que tenia, que es lo unico razonable.
        service.Mode = CopyMode.Mirror;
        service.Enqueue(targets, new[] { origin }, origin, flat: false);
        Check("Modo: al encolar se vuelve solo a Normal", log,
            () => service.Mode == CopyMode.Normal);
        Check("Modo: lo ya encolado conserva el modo que tenia", log,
            () => targets.All(d => d.Job!.Mode == CopyMode.Mirror));

        // Y ahora se copia de verdad, por el mismo camino que el boton.
        var states = new List<JobState>();
        service.JobStateChanged += (_, st) => states.Add(st);

        var clock = Stopwatch.StartNew();
        Task copying = service.StartAllAsync(targets);

        // Con tope de tiempo. Si el motor se queda esperando a si mismo, esta prueba tiene
        // que contarlo y seguir, no colgarse y dejar al que la lanzo sin saber nada.
        bool finished = WaitAsync(copying, TimeSpan.FromSeconds(45));
        log($"           -> la copia tardo {clock.Elapsed.TotalSeconds:0.0} s " +
            $"(tarea terminada: {finished})");

        if (!finished)
        {
            foreach (var d in targets)
            {
                var st = d.Job;
                var stuck = st?.Queue.FirstOrDefault(i => i.State == ItemState.Copying);
                log($"           -> {d.Volume.Id}: estado={st?.State}, " +
                    $"hechos={st?.FilesCompleted}/{st?.Queue.Count}" +
                    (stuck is null ? ", ninguno en curso" : $", parado en {stuck.SourcePath}"));
            }
        }

        Check("Copiar: los 4 destinos terminan bien", log,
            () => finished && targets.All(d => d.Job?.State == JobState.Completed));
        Check("Copiar: no se queda colgada la espera", log, () => finished);
        Check("Copiar: el video llega integro a los 4", log,
            () => targets.All(d => SameBytes(Path.Combine(d.Volume.Root, "video.mp4"), big)));
        Check("Copiar: la foto llega a su subcarpeta", log,
            () => targets.All(d => SameBytes(
                Path.Combine(d.Volume.Root, "Album 2024", "foto.jpg"), pic)));
        Check("Copiar: el texto llega integro", log,
            () => targets.All(d => SameBytes(Path.Combine(d.Volume.Root, "nota.txt"), txt)));

        // La lista de un destino se puede pasar a otro, que es el boton "Clonar lista".
        string copyFrom = targets[0].Job!.Queue[0].DestRelative;
        service.CloneList(targets[0], targets.Skip(1));
        Check("Clonar lista: copia la ruta relativa, no la de origen", log,
            () => targets[1].Job!.Queue.Any(i => i.DestRelative == copyFrom));

        Check("Copiar: se avisa del estado por evento", log, () => states.Count >= 4);
    }

    // ---------------------------------------------------------------- entrada de Windows

    /// <summary>
    /// Comprueba que las llamadas a Windows existen DE VERDAD.
    ///
    /// No es un detalle menor. Un DllImport con el nombre de la funcion en la DLL
    /// equivocada compila sin decir nada y revienta en cuanto se ejecuta, con un
    /// "Unable to find an entry point" que no señala la causa. Pasaron weeks desde el
    /// primer exito de compilacion hasta que esto se comprobo de verdad.
    /// </summary>
    private static void TestNativeCalls(string root, Action<string> log)
    {
        // 1. La ventana oculta del gancho de dispositivos.
        DeviceChangeHook? hook = null;
        try { hook = DeviceChangeHook.TryCreate(); }
        catch (Exception ex) { _failures.Add("Gancho de dispositivos: " + ex.Message); }
        finally { hook?.Dispose(); }

        Check("P/Invoke: la ventana del gancho de dispositivos se crea", log,
            () => hook is not null);
        if (hook is null)
            log("           -> el gancho avisara por el registro; las unidades llegan " +
                "con dos segundos de retraso");

        // 2. El descriptor fisico del volumen donde esta la carpeta de pruebas.
        DeviceKind? kind = null;
        string? label = null;
        try
        {
            var vol = Devices.VolumeInspector.Inspect(new DriveInfo(Path.GetPathRoot(root)!));
            kind = vol.Kind;
            label = vol.KindLabel;
        }
        catch (Exception ex) { _failures.Add("Descriptor de volumen: " + ex.Message); }

        Check("P/Invoke: se lee el descriptor fisico del volumen", log, () => kind is not null);
        Check("P/Invoke: el volumen recibe un tipo de dispositivo", log,
            () => kind is not null and not DeviceKind.Unknown);
        Check("P/Invoke: hay una etiqueta legible para el cliente", log,
            () => !string.IsNullOrWhiteSpace(label));

        // 3. La salida a la consola. Sin esto el informe no se ve al lanzarlo desde una
        //    consola de Windows, que es como se ejecuta estas pruebas.
        try
        {
            Check("P/Invoke: enganchar a la consola del proceso padre", log,
                () => AttachConsole(-1) || Console.Error is not null);
        }
        catch (Exception ex) { _failures.Add("AttachConsole: " + ex.Message); }
    }

    /// <summary>Levanta el servicio entero, con su vigilante, y comprueba que aguanta.</summary>
    private static void TestService(string root, Action<string> log)
    {
        using var service = new CopyService();

        Exception? failure = null;
        try
        {
            service.Start();
        }
        catch (Exception ex) { failure = ex; }

        Check("Servicio: arranca sin lanzar excepciones", log, () => failure is null);
        if (failure is not null) log($"           -> {failure.GetType().Name}: {failure.Message}");

        Thread.Sleep(400);

        Check("Servicio: el vigilante encuentra volumenos", log,
            () => service.Registry.All.Count > 0);

        // Y que las tarjetas tengan trabajo al que repartirse.
        service.SelectAll(true, service.Registry.All);
        Check("Servicio: se pueden seleccionar dispositivos", log,
            () => service.Registry.All.Any(d => d.IsSelected));

        service.SelectNone(service.Registry.All);
        Check("Servicio: se puede quitar la seleccion", log,
            () => service.Registry.All.All(d => !d.IsSelected));
    }

    // ---------------------------------------------------------------- utilidades

    private static CopyPlan Plan(IReadOnlyList<string> dropped, string sourceRoot, StructureKind kind,
                                 string? custom = null) =>
        StructureResolver.Resolve(new StructureRequest
        {
            DroppedPaths = dropped,
            SourceRoot = sourceRoot,
            Kind = kind,
            CustomFolderName = custom,
        });

    private static CopyJob JobFor(string destRoot, CopyMode mode, StructureKind structure,
                                  string sourceRoot, IReadOnlyList<string> files)
    {
        var state = new DeviceState
        {
            Volume = new VolumeRef
            {
                Id = destRoot.TrimEnd('\\').Split('\\').Last(),
                Root = destRoot,
                Kind = DeviceKind.UsbFlash,
            },
        };

        var job = new CopyJob { Device = state, Mode = mode, Structure = structure };

        foreach (var f in files)
        {
            string rel = StructureResolver.CleanRelative(
                StructureResolver.RelClamped(f, sourceRoot));
            job.Queue.Add(new CopyItem
            {
                SourcePath = f,
                DestRelative = rel,
                Length = new FileInfo(f).Length,
                SourceWriteTimeUtc = File.GetLastWriteTimeUtc(f),
            });
        }

        return job;
    }

    private static Task RunJob(CopyJob job) =>
        new MultiCopyEngine(new BufferBudget(256 * 1024, 4L * 1024 * 1024, 4)).RunAsync(job);

    private static async Task SafeAwait(Task t)
    {
        try { await t.ConfigureAwait(false); } catch { }
    }

    private static void await0(Task t) => t.GetAwaiter().GetResult();

    /// <summary>Espera con tope. Devuelve false si se acabo el tiempo.</summary>
    private static bool WaitAsync(Task t, TimeSpan limit) =>
        Task.WhenAny(t, Task.Delay(limit)).GetAwaiter().GetResult() == t;

    // ---------------------------------------------------------------- comprobaciones

    private static void Check(string name, Action<string> log, Func<bool> cond) =>
        Report(name, log, () => cond());

    private static void CheckPlan(string name, Action<string> log, CopyPlan plan, Func<CopyPlan, bool> cond) =>
        Report(name, log, () => cond(plan));

    private static void CheckJob(string name, Action<string> log, CopyJob job, Func<CopyJob, bool> cond) =>
        Report(name, log, () => cond(job));

    private static void CheckDir(string name, Action<string> log, string dir, Func<string, bool> cond) =>
        Report(name, log, () => cond(dir));

    private static void Report(string name, Action<string> log, Func<bool> probe)
    {
        _total++;
        _motivo = string.Empty;
        bool ok;
        string? why = null;
        try { ok = probe(); }
        catch (Exception ex) { ok = false; why = ex.GetType().Name; }
        why ??= _motivo.Length > 0 ? _motivo : null;

        if (ok)
        {
            _passed++;
            log($"  [ OK  ] {name}");
        }
        else
        {
            _failures.Add(name);
            log($"  [FALLO] {name}" + (why is null ? "" : $"  ({why})"));
        }
    }

    /// <summary>Espera a que se cumpla algo, en vez de dormir un tiempo y confiar.</summary>
    private static bool EsperaHasta(Func<bool> cond, int ms)
    {
        var reloj = Stopwatch.StartNew();
        while (reloj.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    private static string NewDir(string parent, string name)
    {
        string p = Path.Combine(parent, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Contenido no repetitivo, para que un fallo de lectura se note de inmediato.</summary>
    private static byte[] NewBytes(int size)
    {
        var rng = new Random(1234);
        var buf = new byte[size];
        rng.NextBytes(buf);
        return buf;
    }

    private static bool SameBytes(string a, string b)
    {
        _motivo = ComparaBytes(a, b);
        return _motivo.Length == 0;
    }

    /// <summary>Por que no son iguales. Vacio si lo son. El motivo va al informe.</summary>
    private static string ComparaBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists) return $"falta el origen {a}";
            if (!fb.Exists) return $"falta el destino {b}";
            if (fa.Length != fb.Length)
                return $"tamano distinto: origen {fa.Length} bytes, destino {fb.Length} bytes";

            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            var ba = new byte[64 * 1024];
            var bb = new byte[64 * 1024];
            long pos = 0;
            while (true)
            {
                int ra = sa.Read(ba, 0, ba.Length);
                int rb = sb.Read(bb, 0, bb.Length);
                if (ra != rb) return $"lecturas distintas en {pos}: {ra} contra {rb}";
                if (ra == 0) return string.Empty;
                for (int i = 0; i < ra; i++)
                    if (ba[i] != bb[i])
                        return $"primer byte distinto en {pos + i}: origen {ba[i]}, destino {bb[i]}";
                pos += ra;
            }
        }
        catch (Exception ex) { return "excepcion " + ex.GetType().Name + ": " + ex.Message; }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* la carpeta temporal se limpiara sola */ }
    }

    private static void TryWrite(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// El ejecutable es WinExe y por eso no tiene consola propia. Cuando se lanza desde
    /// una consola hay que engancharse a la del proceso que lo invoco, si no las lineas
    /// se pierden y la prueba parece no decir nada.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static void AttachParentConsole()
    {
        try
        {
            if (!AttachConsole(-1)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch { }
    }
}
