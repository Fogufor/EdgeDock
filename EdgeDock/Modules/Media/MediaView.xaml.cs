using System.Windows;
using System.Windows.Controls;

namespace EdgeDock.Modules.Media;

public partial class MediaView : UserControl
{
    private readonly MediaModule _module;

    internal MediaView(MediaModule module)
    {
        InitializeComponent();
        _module = module;
        DataContext = module.State;
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => _module.Previous();

    private void OnPlayPause(object sender, RoutedEventArgs e) => _module.PlayPause();

    private void OnNext(object sender, RoutedEventArgs e) => _module.Next();
}
