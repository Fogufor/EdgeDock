using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EdgeDock.Core;

/// <summary>Фон Mica, как у окон Windows 11, в тон текущей теме — для обычных окон (установка, правка закреплённого).</summary>
internal static class WindowBackdrop
{
    /// <summary>Вызывать из SourceInitialized. Mica недоступна — сплошной фон из токенов.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ThemeService.IsDark ? 1 : 0);
        if (Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_MAINWINDOW) != 0)
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Window.Fallback");
    }
}
