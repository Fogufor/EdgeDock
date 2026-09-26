using System.Diagnostics;
using System.IO;

namespace EdgeDock.Core;

/// <summary>
/// Однофайловый EdgeDock распаковывает свои системные библиотеки в %TEMP%\.net\EdgeDock\&lt;хэш версии&gt;, и каждая
/// версия оставляет там свою папку (~8 МБ). При запуске убираем папки прошлых версий; свою — ту, откуда загружены
/// библиотеки этого процесса, — не трогаем.
/// </summary>
internal static class BundleCleanup
{
    public static void DeleteOldVersions()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), ".net", "EdgeDock");
            if (!Directory.Exists(root)) return;

            // Сборка из bin\ ничего не распаковывает — тогда своей папки нет и чистить не нужно.
            string? current = CurrentFolder(root);
            if (current == null) return;

            foreach (string folder in Directory.GetDirectories(root))
            {
                if (string.Equals(folder, current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Файлы заняты — уберём в следующий раз.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось убрать распакованные файлы прошлых версий.", ex);
        }
    }

    /// <summary>Папка распаковки этой версии: из неё загружены библиотеки процесса.</summary>
    private static string? CurrentFolder(string root)
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            string? file = module.FileName;
            if (file != null && file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(file);
        }
        return null;
    }
}
