using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EdgeDock.Modules.Media;

public partial class MediaView : UserControl
{
    /// <summary>Шаг громкости на одно деление колеса мыши, в процентах.</summary>
    private const double WheelStep = 2;

    private readonly MediaModule _module;

    internal MediaView(MediaModule module)
    {
        InitializeComponent();
        _module = module;
        DataContext = module.State;
        VolumeRow.DataContext = module.Volume;
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => _module.Previous();

    private void OnPlayPause(object sender, RoutedEventArgs e) => _module.PlayPause();

    private void OnNext(object sender, RoutedEventArgs e) => _module.Next();

    private void OnMute(object sender, RoutedEventArgs e) => _module.Volume.ToggleMute();

    private void OnLike(object sender, RoutedEventArgs e) => _module.ToggleLike();

    // Перемотка: пока ползунок держат, секундомер его не двигает; отпустили — перематываем туда.
    private void OnSeekStart(object sender, MouseButtonEventArgs e) => _module.BeginSeek();

    private void OnSeekEnd(object sender, MouseButtonEventArgs e) => _module.Seek(((Slider)sender).Value);

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        _module.Volume.Level += Math.Sign(e.Delta) * WheelStep;
        e.Handled = true;
    }
}
