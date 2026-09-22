using System.IO;

namespace EdgeDock.Core;

/// <summary>Где EdgeDock хранит свои файлы: %LocalAppData%\EdgeDock.</summary>
internal static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeDock");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");
    public static string StateFile { get; } = Path.Combine(Root, "state.json");
    public static string Logs { get; } = Path.Combine(Root, "logs");
}
