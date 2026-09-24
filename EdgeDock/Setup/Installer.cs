using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeDock.Core;
using Microsoft.Win32;

namespace EdgeDock.Setup;

/// <summary>
/// Установка «одним exe». Скачанный EdgeDock.exe, запущенный не из папки установки, показывает окно установки:
/// копирует себя в выбранную папку, раскладывает рядом расширение для Яндекс Браузера (оно лежит внутри exe),
/// делает ярлык в меню «Пуск», записывает настройку автозапуска и запускает виджет.
/// Так же ставится и обновление: запущенный виджет сначала просят закрыться.
/// </summary>
internal static class Installer
{
    public const string ExeName = "EdgeDock.exe";

    /// <summary>Сигнал «закройся» для запущенного виджета — чтобы заменить его exe.</summary>
    public const string ExitSignalName = @"Local\EdgeDock.Exit";

    private const string ExtensionResourcePrefix = "BrowserExtension/";

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EdgeDock");

    public static string Version { get; } = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "";

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "EdgeDock.lnk");

    private sealed class InstallInfo
    {
        public string Folder { get; set; } = "";
        public string Version { get; set; } = "";
    }

    /// <summary>Показать окно установки вместо виджета?</summary>
    public static bool ShouldShowSetup(string[] args)
    {
        if (args.Contains("--setup", StringComparer.OrdinalIgnoreCase)) return true;

        // Отладочная сборка из исходников (bin\) — сразу виджет.
        string exe = Environment.ProcessPath!;
        if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return false;

        return !(InstalledFolder() is string folder && SamePath(Path.Combine(folder, ExeName), exe));
    }

    /// <summary>Куда виджет уже установлен, или null.</summary>
    public static string? InstalledFolder()
    {
        try
        {
            if (!File.Exists(AppPaths.InstallInfo)) return null;
            var info = JsonSerializer.Deserialize<InstallInfo>(File.ReadAllText(AppPaths.InstallInfo), Json);
            return string.IsNullOrWhiteSpace(info?.Folder) ? null : info.Folder;
        }
        catch (Exception ex)
        {
            Log.Error("install.json не читается.", ex);
            return null;
        }
    }

    public static string ExtensionFolder(string installFolder) => Path.Combine(installFolder, "BrowserExtension");

    /// <summary>Нормализовать путь из поля ввода: переменные окружения, полный путь.</summary>
    public static string NormalizeFolder(string folder) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')));

    /// <summary>Установить (или обновить) и запустить. Ошибки — исключениями, их показывает окно установки.</summary>
    public static void Install(string folder, bool autostart, bool startMenu)
    {
        Directory.CreateDirectory(folder);
        StopRunningWidget();

        string target = Path.Combine(folder, ExeName);
        string source = Environment.ProcessPath!;
        if (!SamePath(source, target)) CopyWithRetry(source, target);

        ExtractExtension(ExtensionFolder(folder));
        SetAutostartSetting(autostart);

        if (startMenu) Autostart.CreateShortcut(StartMenuShortcut, target);
        else if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut);

        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.InstallInfo, JsonSerializer.Serialize(new InstallInfo { Folder = folder, Version = Version }, Json));

        // Ярлык автозапуска виджет создаст (или уберёт) сам при старте — по настройке autostart.
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = folder });
    }

    /// <summary>Запущенный виджет держит свой exe: просим его закрыться, старую версию без сигнала — закрываем принудительно.</summary>
    private static void StopRunningWidget()
    {
        if (EventWaitHandle.TryOpenExisting(ExitSignalName, out var signal))
        {
            using (signal) signal.Set();
        }

        foreach (var process in Process.GetProcessesByName("EdgeDock").Where(p => p.Id != Environment.ProcessId))
        {
            using (process)
            {
                if (process.WaitForExit(5000)) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Уже закрылся или не наш — копирование ниже скажет, если файл всё ещё занят.
                }
            }
        }
    }

    private static void CopyWithRetry(string source, string target)
    {
        // Только что закрытый процесс может ещё мгновение держать файл.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Разложить расширение для Яндекс Браузера из ресурсов exe в папку рядом с виджетом.</summary>
    private static void ExtractExtension(string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly = typeof(Installer).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ExtensionResourcePrefix)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(folder, name[ExtensionResourcePrefix.Length..]));
            stream.CopyTo(file);
        }
    }

    /// <summary>
    /// Записать галочку «Запускать вместе с Windows» в settings.json. Меняется только это поле:
    /// остальное пользователь мог настроить руками. Файла нет — создаётся с настройками по умолчанию.
    /// </summary>
    private static void SetAutostartSetting(bool autostart)
    {
        var settings = new SettingsService();
        settings.LoadSettings();
        if (settings.Settings.Dock.Autostart == autostart) return;

        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            if (JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile), documentOptions: options) is not JsonObject root) return;
            if (root["dock"] is not JsonObject dock) root["dock"] = dock = new JsonObject();
            dock["autostart"] = autostart;
            File.WriteAllText(AppPaths.SettingsFile, root.ToJsonString(Json));
        }
        catch (Exception ex)
        {
            // Сломанный файл не трогаем — виджет сам сообщит о нём в логе.
            Log.Error("Установка: не удалось записать автозапуск в settings.json.", ex);
        }
    }

    /// <summary>Открыть страницу расширений Яндекс Браузера. false — браузер не найден.</summary>
    public static bool OpenYandexExtensionsPage()
    {
        string? browser = FindYandexBrowser();
        if (browser == null) return false;
        Process.Start(new ProcessStartInfo(browser, "browser://extensions") { UseShellExecute = false });
        return true;
    }

    private static string? FindYandexBrowser()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\browser.exe");
            // «browser.exe» — имя не только Яндекса, поэтому проверяем путь.
            if (key?.GetValue(null) is string path && path.Contains("Yandex", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                return path;
        }

        string[] known =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Yandex\YandexBrowser\Application\browser.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Yandex\YandexBrowser\Application\browser.exe"),
        ];
        return known.FirstOrDefault(File.Exists);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
