using System.IO;
using System.Windows;
using EdgeDock.Core;
using Microsoft.Win32;

namespace EdgeDock.Setup;

/// <summary>Окно установки: выбрать папку → установить и запустить → по желанию подключить расширение.</summary>
public partial class SetupWindow : Window
{
    private string _installedFolder = "";

    public SetupWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Версия {Installer.Version}";

        string? installed = Installer.InstalledFolder();
        FolderBox.Text = installed ?? Installer.DefaultFolder;
        if (installed != null) InstallButton.Content = "Обновить и запустить";

        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для EdgeDock" };
        if (dialog.ShowDialog(this) != true) return;

        // Выбрали общую папку (например, «D:\Программы») — кладём в подпапку, а не россыпью.
        string folder = dialog.FolderName;
        if (!string.Equals(Path.GetFileName(folder.TrimEnd('\\')), "EdgeDock", StringComparison.OrdinalIgnoreCase))
            folder = Path.Combine(folder, "EdgeDock");
        FolderBox.Text = folder;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string folder;
        try
        {
            folder = Installer.NormalizeFolder(FolderBox.Text);
        }
        catch (Exception)
        {
            ShowError("Не получается разобрать путь к папке. Проверьте, что он написан полностью, например C:\\Users\\Имя\\EdgeDock.");
            return;
        }

        bool autostart = AutostartBox.IsChecked == true, startMenu = StartMenuBox.IsChecked == true;
        object caption = InstallButton.Content;
        InstallButton.IsEnabled = false;
        InstallButton.Content = "Устанавливаю…";
        try
        {
            // В фоне: остановка запущенного виджета может занять несколько секунд.
            await Task.Run(() => Installer.Install(folder, autostart, startMenu));
            _installedFolder = folder;
            InstallPage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Visible;
        }
        catch (UnauthorizedAccessException)
        {
            ShowError($"Нет прав на запись в «{folder}». Выберите папку в своём профиле, например {Installer.DefaultFolder}.");
        }
        catch (Exception ex)
        {
            Log.Error("Установка не удалась.", ex);
            ShowError($"Не получилось установить: {ex.Message}");
        }
        finally
        {
            InstallButton.IsEnabled = true;
            InstallButton.Content = caption;
        }
    }

    private void OnOpenExtensions(object sender, RoutedEventArgs e)
    {
        string extension = Installer.ExtensionFolder(_installedFolder);
        bool copied = TryCopy(extension);
        bool opened = false;
        try
        {
            opened = Installer.OpenYandexExtensionsPage();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть Яндекс Браузер.", ex);
        }

        string where = opened ? "Страница расширений открыта в Яндекс Браузере." : "Яндекс Браузер не найден — откройте в нём адрес browser://extensions.";
        string path = copied ? $"Путь скопирован: {extension}" : $"Путь к расширению: {extension}";
        ExtensionHint.Text = where + " " + path;
        ExtensionHint.Visibility = Visibility.Visible;
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static bool TryCopy(string text)
    {
        // Буфер обмена бывает занят другой программой — пара повторов.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }
}
