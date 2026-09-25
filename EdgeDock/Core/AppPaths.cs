using System.IO;

namespace EdgeDock.Core;

/// <summary>Где EdgeDock хранит свои файлы: %LocalAppData%\EdgeDock.</summary>
internal static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeDock");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");
    public static string StateFile { get; } = Path.Combine(Root, "state.json");

    /// <summary>Закреплённое: то, что ввёл пользователь. Отдельно от state.json, чтобы сброс состояния его не задел.</summary>
    public static string PinsFile { get; } = Path.Combine(Root, "pins.json");
    public static string Logs { get; } = Path.Combine(Root, "logs");

    /// <summary>Куда установлен виджет (пишет окно установки).</summary>
    public static string InstallInfo { get; } = Path.Combine(Root, "install.json");

    /// <summary>Миниатюры превью (маленькие PNG), чтобы не декодировать снимки заново.</summary>
    public static string Cache { get; } = Path.Combine(Root, "cache");

    /// <summary>Файлы, брошенные на полку без пути на диске (картинки из браузера, вложения).</summary>
    public static string Shelf { get; } = Path.Combine(Root, "shelf");
}
