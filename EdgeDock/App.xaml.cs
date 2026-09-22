using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EdgeDock.Core;

namespace EdgeDock;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsService _settings = null!;
    private DockWindow _dock = null!;
    private TrayIcon? _tray;
    private FullscreenWatcher? _fullscreen;
    private HealthCheck? _health;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Только один экземпляр: автозапуск + ручной запуск не должны дать два дока.
        _singleInstance = new Mutex(true, @"Local\EdgeDock.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        Log.Rotate();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Необработанное исключение.", args.Exception);
            args.Handled = true; // док продолжает работать
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Необработанное исключение, процесс завершается.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанное исключение в фоновой задаче.", args.Exception);
            args.SetObserved();
        };

        // Программная отрисовка: интерфейс крошечный и почти всегда неподвижен, а без Direct3D процесс занимает
        // в разы меньше памяти (≈12 МБ вместо ≈60 МБ в Диспетчере задач). Прозрачность и акрил DWM не страдают.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        _settings = new SettingsService();
        _settings.LoadSettings();
        _settings.LoadState();
        ThemeService.Apply();
        Autostart.Sync(_settings.Settings.Dock.Autostart);

        _dock = new DockWindow(_settings);
        _dock.Show();

        _tray = new TrayIcon(() => _settings.State.Dock.Locked);
        _tray.Command += OnTrayCommand;

        _health = new HealthCheck();

        _fullscreen = new FullscreenWatcher(() => _dock.CurrentMonitor);
        _fullscreen.Changed += OnFullscreenChanged;
        _dock.Moved += () => _fullscreen?.Check();
        if (_fullscreen.IsActive) OnFullscreenChanged(true);
    }

    private void OnFullscreenChanged(bool active)
    {
        _dock.SetSuspended(active);
        if (active) _health?.Suspend();
        else _health?.Resume();
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.ToggleLock:
                _dock.ToggleLock();
                break;
            case TrayCommand.ResetPosition:
                _dock.ResetPosition();
                break;
            case TrayCommand.OpenSettings:
                if (!File.Exists(AppPaths.SettingsFile)) _settings.LoadSettings(); // создаст файл
                OpenInShell(AppPaths.SettingsFile, fallback: "notepad.exe");
                break;
            case TrayCommand.OpenLogs:
                Directory.CreateDirectory(AppPaths.Logs);
                OpenInShell(AppPaths.Logs);
                break;
            case TrayCommand.ReloadSettings:
                if (_settings.LoadSettings())
                {
                    _dock.ApplySettings();
                    Autostart.Sync(_settings.Settings.Dock.Autostart);
                }
                break;
            case TrayCommand.Exit:
                Shutdown();
                break;
        }
    }

    /// <summary>Открыть файл или папку тем, чем Windows открывает их по умолчанию.</summary>
    private static void OpenInShell(string path, string? fallback = null)
    {
        try
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception) when (fallback != null)
            {
                // Для .json может не быть программы по умолчанию — тогда Блокнот.
                Process.Start(new ProcessStartInfo(fallback, $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {path}.", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _fullscreen?.Dispose();
        _health?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
