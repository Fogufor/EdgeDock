using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EdgeDock.Modules.Pins;

public partial class PinsView : UserControl
{
    private readonly PinsModule _module;

    internal PinsView(PinsModule module)
    {
        _module = module;
        InitializeComponent();
        DataContext = module;
    }

    private void OnAdd(object sender, RoutedEventArgs e) => _module.OpenEditor(null);

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PinItem item) _module.Copy(item);
    }

    private void OnPinRightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PinItem item) return;
        e.Handled = true;
        _module.OpenEditor(item);
    }
}
