using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using EdgeDock.Core;

namespace EdgeDock;

/// <summary>
/// Выбор оформления в панели. Сам ничего не сохраняет: сообщает выбор доку (LookChosen),
/// а отметку берёт из ThemeService — поэтому выбор из меню трея тоже виден здесь.
/// </summary>
public partial class AppearanceView : UserControl
{
    /// <summary>Пользователь выбрал другое оформление.</summary>
    public event Action<PanelLook>? LookChosen;

    public AppearanceView()
    {
        InitializeComponent();
        ThemeService.Changed += Sync;
        Sync();
    }

    private void Sync()
    {
        var look = ThemeService.Look;
        StandardOption.IsChecked = look == PanelLook.Standard;
        DarkOption.IsChecked = look == PanelLook.Dark;
        GlassOption.IsChecked = look == PanelLook.Glass;
        Description.Text = look switch
        {
            // Одной строкой: иначе при переключении панель прыгала бы по высоте.
            PanelLook.Dark => "Всегда тёмное, почти непрозрачное",
            PanelLook.Glass => "Дымчатое стекло: видно, что под панелью",
            _ => "Светлое или тёмное — как Windows",
        };
    }

    private void OnLookChecked(object sender, RoutedEventArgs e)
    {
        var look = sender == DarkOption ? PanelLook.Dark : sender == GlassOption ? PanelLook.Glass : PanelLook.Standard;
        if (look != ThemeService.Look) LookChosen?.Invoke(look);
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:colors") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть параметры цвета Windows.", ex);
        }
    }
}
