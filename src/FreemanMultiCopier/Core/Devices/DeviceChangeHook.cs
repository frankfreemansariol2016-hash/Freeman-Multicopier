using System.Runtime.InteropServices;

namespace FreemanMultiCopier.Core.Devices;

/// <summary>
/// Ventana oculta que existe unicamente para recibir los avisos de conexion y desconexion
/// de unidades que manda Windows (WM_DEVICECHANGE).
///
/// Se registra a mano con la API de Win32 en vez de usar HwndSource porque HwndSource no
/// admite el pseudo-handle HWND_MESSAGE, y sin ese pseudo-handle la ventana apareceria
/// un instante en la barra de tareas cada vez que arranca la aplicacion.
///
/// El aviso es solo una pista. Quien decide es <see cref="DeviceWatcher"/>, que siempre
/// contrasta con la enumeracion real de unidades.
/// </summary>
internal sealed class DeviceChangeHook : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DESTROY = 0x0002;

    // HWND_MESSAGE vale (HWND)-3.
    //
    // Importante declararlo como int y NO como uint: en 64 bits, new IntPtr(0xFFFFFFFDu)
    // vale 4294967293, no -3, y CreateWindowExW responde con el error 1400
    // (identificador de ventana no valido). El pseudo-handle es un -3 con signo
    // extendido a 64 bits, no un numero sin signo de 32 bits.
    private const int HWND_MESSAGE = -3;

    private const uint WINDOW_CLASS_NAME = 0x8000 + 1;   // identificador propio de la clase

    private delegate IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    // OJO CON LA DLL DE CADA UNA. El registro de clases de ventana, la creacion de la
    // ventana, su procedimiento de mensajes y su destruccion exporta user32.dll, NO
    // kernel32.dll. Solo GetModuleHandleW es de kernel32. Ponerlo todo en kernel32
    // compila sin quejarse y revienta en tiempo de ejecucion con
    // "Unable to find an entry point", que es el fallo mas caro de diagnosticar que existe.

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpWindowName, int dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool UnregisterClassW([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static readonly WindowProc _proc = WndProcThunk;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _instance;
    private readonly string _className;
    private bool _disposed;

    /// <summary>Se dispara ante cualquier cambio en las unidades conectadas.</summary>
    public event Action? Changed;

    public DeviceChangeHook()
    {
        _className = "FreemanMultiCopierDeviceHook_" + Environment.ProcessId;
        _instance = GetModuleHandleW(null);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = _instance,
            lpszClassName = _className,
        };

        if (RegisterClassExW(ref wc) == 0)
            Fail("RegisterClassExW", Marshal.GetLastWin32Error());

        // Sin titulo, sin estilo y con HWND_MESSAGE: no existe visualmente.
        _hwnd = CreateWindowExW(0, _className, null, 0,
                                0, 0, 0, 0,
                                new IntPtr(HWND_MESSAGE), IntPtr.Zero, _instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            Fail("CreateWindowExW", Marshal.GetLastWin32Error());
    }

    /// <summary>Por que no se pudo crear la ventana. Vacio si todo fue bien.</summary>
    public string? LastError { get; private set; }

    public bool IsValid => _hwnd != IntPtr.Zero;

    private void Fail(string step, int code) =>
        LastError = $"{step} fallo con el error {code}: {new System.ComponentModel.Win32Exception(code).Message}";

    private static IntPtr WndProcThunk(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) =>
        _current?.Handle(hwnd, msg, wParam, lParam) ?? DefWindowProcW(hwnd, msg, wParam, lParam);

    // La clase se resuelve por variable estatica porque el delegado de Win32 es estatico
    // por naturaleza: no admite estado. Solo hay una instancia viva a la vez.
    private static DeviceChangeHook? _current;

    private IntPtr Handle(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WM_DEVICECHANGE or WM_DISPLAYCHANGE)
            Changed?.Invoke();
        else if (msg == WM_DESTROY)
            _disposed = true;

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Crea el gancho y lo deja escuchando. Devuelve null si no se pudo.</summary>
    public static DeviceChangeHook? TryCreate()
    {
        var hook = new DeviceChangeHook();
        if (!hook.IsValid)
        {
            // Se avisa por el registro. El vigilante sigue funcionando con el repaso
            // periodico, pero sin este aviso nadie sabria nunca por que las unidades
            // aparecen con un par de segundos de retraso.
            Log.Warn("No se pudo crear la ventana de aviso de dispositivos: " + hook.LastError);
            hook.Dispose();
            return null;
        }
        _current = hook;
        return hook;
    }

    public void Dispose()
    {
        if (_disposed && _hwnd == IntPtr.Zero) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        UnregisterClassW(_className, _instance);
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
