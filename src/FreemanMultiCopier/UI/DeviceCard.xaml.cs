using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreemanMultiCopier.Core.Model;

namespace FreemanMultiCopier.UI;

/// <summary>
/// Tarjeta de un dispositivo. No tiene logica propia: refleja el <see cref="DeviceState"/>
/// y avisa de los clics. Las decisiones viven en el servicio, para que la tarjeta se
/// pueda redibujar sin riesgo de desincronizarse del trabajo real.
/// </summary>
public partial class DeviceCard : UserControl
{
    /// <summary>Orden de palabra del comando que conmuta la seleccion de la tarjeta.</summary>
    public static readonly DependencyProperty ToggleSelectCommandProperty =
        DependencyProperty.Register(nameof(ToggleSelectCommand), typeof(ICommand), typeof(DeviceCard));

    public ICommand? ToggleSelectCommand
    {
        get => (ICommand?)GetValue(ToggleSelectCommandProperty);
        set => SetValue(ToggleSelectCommandProperty, value);
    }

    public DeviceCard()
    {
        InitializeComponent();
        Cursor = Cursors.Hand;
    }

    public DeviceState? Device => DataContext as DeviceState;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // El clic en la tarjeta alterna la seleccion. Es el gesto mas usado: marcar quince
        // memorias una a una con el raton seria un castigo.
        if (Device is { } device && ToggleSelectCommand?.CanExecute(device) == true)
            ToggleSelectCommand.Execute(device);
    }
}
