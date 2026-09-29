using System.IO;
using System.Windows;
using System.Windows.Threading;
using FreemanMultiCopier.Core;

namespace FreemanMultiCopier;

public partial class App : Application
{
    /// <summary>Con <c>--selftest</c> la aplicacion se convierte en una bateria de pruebas
    /// y no abre ninguna ventana. Se usa para comprobar el motor sin depender de memorias
    /// USB de verdad.</summary>
    public const string SelfTestSwitch = "--selftest";

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals(SelfTestSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.AttachParentConsole();
            int code = SelfTest.Run(SelfTestReportPath());
            Shutdown(code);
            return;
        }

        // Ante cualquier error no controlado, se registra y se avisa. Un copiador que
        // se cierra solo a mitad de un trabajo deja al cliente con la memoria a medias.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        // La ventana se abre aqui y no con StartupUri, para que el modo prueba pueda
        // Saltarse este paso por completo.
        MainWindow = new UI.MainWindow();
        MainWindow.Show();
    }

    /// <summary>
    /// El informe va a la carpeta de datos del cliente, no junto al ejecutable. Una vez
    /// instalado, esa carpeta esta bajo Program Files y ahi no se puede escribir nada sin
    /// permisos de administrador, asi que el informe se perdia en silencio justo cuando
    /// mas falta hacia: en casa de quien lo instalo.
    /// </summary>
    private static string SelfTestReportPath() =>
        Path.Combine(Log.DataDirectory, "prueba.txt");

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI no controlada", e.Exception);
        MessageBox.Show(
            $"Ocurrio un error inesperado:\n\n{e.Exception.Message}\n\n" +
            "Las copias que estaban en marcha se han detenido. El detalle esta en la carpeta Data\\registro.log",
            "Freeman MULTICOPIER", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log.Error("Dominio no controlado", ex);
    }
}
