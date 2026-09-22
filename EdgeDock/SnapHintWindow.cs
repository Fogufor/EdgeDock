using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EdgeDock.Core;

namespace EdgeDock;

/// <summary>
/// Подсказка во время перетаскивания: бледная акцентная черта там, где док прилипнет, если отпустить сейчас.
/// Сквозная для мыши и никогда не получает фокус.
/// </summary>
internal sealed class SnapHintWindow : Window
{
    private readonly Border _bar = new();
    private IntPtr _hwnd;

    public SnapHintWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Background = Brushes.Transparent;
        Title = "EdgeDock";

        _bar.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        _bar.SetResourceReference(Border.CornerRadiusProperty, "Radius.Strip");
        _bar.SetResourceReference(OpacityProperty, "Opacity.SnapHint");
        Content = _bar;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.PrepareToolWindow(HwndSource.FromHwnd(_hwnd), clickThrough: true);
            Native.SetDwm(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
            Native.SetDwm(_hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);
        };
    }

    /// <summary>Показать черту в зоне будущей полоски rect (пиксели экрана).</summary>
    public void ShowAt(PixelRect rect, DockEdge edge)
    {
        double thickness = (double)FindResource("Strip.Thickness");
        bool horizontal = edge == DockEdge.Top;
        _bar.Width = horizontal ? double.NaN : thickness;
        _bar.Height = horizontal ? thickness : double.NaN;
        _bar.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _bar.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, Native.SWP_NOACTIVATE);
        if (!IsVisible) Show();
    }
}
