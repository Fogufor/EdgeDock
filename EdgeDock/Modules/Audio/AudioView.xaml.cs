using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EdgeDock.Modules.Audio;

public partial class AudioView : UserControl
{
    private readonly AudioModule _module;

    internal AudioView(AudioModule module)
    {
        InitializeComponent();
        _module = module;
        DataContext = module;
    }

    private void OnCallMode(object sender, RoutedEventArgs e) => _module.ToggleCallMode();

    // Нажатие на строку держит мышь, чтобы показать «нажато»; клик — если отпустили над той же строкой.
    private void OnRowDown(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnRowUp(object sender, MouseButtonEventArgs e)
    {
        var row = (FrameworkElement)sender;
        if (!row.IsMouseCaptured) return;
        row.ReleaseMouseCapture();
        e.Handled = true;

        var p = e.GetPosition(row);
        if (p.X >= 0 && p.Y >= 0 && p.X < row.ActualWidth && p.Y < row.ActualHeight)
            _module.Select((DeviceItem)row.DataContext);
    }
}
