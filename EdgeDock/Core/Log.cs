using System.IO;

namespace EdgeDock.Core;

/// <summary>
/// Тихий лог: пишет только при проблемах. Когда всё в порядке, папка остаётся пустой.
/// Один файл на день в %LocalAppData%\EdgeDock\logs.
/// </summary>
internal static class Log
{
    private const long MaxFolderBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static readonly object Gate = new();
    private static bool _limitReached;

    /// <summary>При запуске: удалить файлы старше 14 дней, затем самые старые, пока папка не уложится в 2 МБ.</summary>
    public static void Rotate()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Logs)) return;

            var files = new DirectoryInfo(AppPaths.Logs).GetFiles("*.log")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var old in files.Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > MaxAge).ToList())
            {
                old.Delete();
                files.Remove(old);
            }

            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= MaxFolderBytes) break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch
        {
            // Сломанная папка логов не должна мешать доку.
        }
    }

    public static void Error(string message, Exception? ex = null) => Write("ОШИБКА", message, ex);

    public static void Warn(string message) => Write("ВНИМАНИЕ", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                if (_limitReached) return;

                Directory.CreateDirectory(AppPaths.Logs);
                string file = Path.Combine(AppPaths.Logs, $"edgedock-{DateTime.Now:yyyy-MM-dd}.log");
                string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
                if (ex != null) line += Environment.NewLine + ex;

                // Защита от ошибки в цикле: за день пишем не больше половины бюджета папки.
                if (File.Exists(file) && new FileInfo(file).Length > MaxFolderBytes / 2)
                {
                    _limitReached = true;
                    line = $"{DateTime.Now:HH:mm:ss.fff} {level} Лог за сегодня достиг лимита, дальнейшие записи пропускаются.";
                }

                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Запись в лог никогда не должна ронять док.
        }
    }
}
