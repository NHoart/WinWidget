using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CustomWidgets.WidgetProvider.Desktop;

/// <summary>
/// Positions the host as a borderless, non-activating tool window on the desktop.
/// The host hides itself when another app is foreground; this layer only keeps it
/// above the desktop surface and applies the saved position. This works with DWM
/// composition on current Windows 11 builds, where child windows of Progman are
/// not reliably composited into the desktop surface.
/// </summary>
internal sealed class DesktopLayer
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int SwRestore = 9;
    private const int SwHide = 0;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private IntPtr _window;

    public bool IsAttached => _window != IntPtr.Zero && IsWindow(_window);

    public bool Attach(Window window, double widthDip, double heightDip)
    {
        _window = new WindowInteropHelper(window).Handle;
        if (_window == IntPtr.Zero || widthDip <= 0 || heightDip <= 0)
        {
            return false;
        }

        int exStyle = GetWindowLong(_window, GwlExStyle);
        SetWindowLong(_window, GwlExStyle, exStyle | WsExNoActivate | WsExToolWindow);

        ShowWindow(_window, SwRestore);
        Position(window, widthDip, heightDip);
        Log.Info("Desktop host window positioned on the desktop.");
        return IsAttached;
    }

    public void Reattach(Window window, double widthDip, double heightDip)
    {
        if (!IsAttached)
        {
            Attach(window, widthDip, heightDip);
            return;
        }

        ShowWindow(_window, SwRestore);
        Position(window, widthDip, heightDip);
    }

    public void SetDesktopVisible(bool visible)
    {
        if (!IsAttached)
        {
            return;
        }

        if (!visible)
        {
            ShowWindow(_window, SwHide);
            return;
        }

        ShowWindow(_window, SwRestore);
        SetWindowPos(
            _window,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void Position(Window window, double widthDip, double heightDip)
    {
        if (!IsAttached)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Round(widthDip * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Round(heightDip * dpi.DpiScaleY));
        int left = (int)Math.Round(window.Left * dpi.DpiScaleX);
        int top = (int)Math.Round(window.Top * dpi.DpiScaleY);

        // Raising directly with HWND_TOP is ignored for this WPF tool window on
        // Windows 11: it stays below Progman and is therefore invisible. Making
        // it briefly topmost and immediately non-topmost reliably places it at
        // the top of the normal z-order, above the desktop but below applications.
        SetWindowPos(
            _window,
            HwndTopmost,
            left,
            top,
            width,
            height,
            SwpNoActivate | SwpShowWindow | SwpFrameChanged);

        SetWindowPos(
            _window,
            HwndNotTopmost,
            left,
            top,
            width,
            height,
            SwpNoActivate | SwpShowWindow | SwpFrameChanged);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
}
