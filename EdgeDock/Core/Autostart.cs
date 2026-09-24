using System.IO;
using System.Runtime.InteropServices;

namespace EdgeDock.Core;

/// <summary>Автозапуск — ярлык EdgeDock.lnk в папке shell:startup.</summary>
internal static class Autostart
{
    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "EdgeDock.lnk");

    public static void Sync(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;

            // Отладочная сборка запускается из bin\ — не делаем на неё ярлык автозапуска.
            // Рабочую копию ставит publish.ps1 в %LocalAppData%\Programs\EdgeDock.
            if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return;

            if (!enabled)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return;
            }

            CreateShortcut(ShortcutPath, exe);
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск: не удалось обновить ярлык.", ex);
        }
    }

    /// <summary>Ярлык .lnk на target (им же пользуется установщик для меню «Пуск»).</summary>
    public static void CreateShortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try
            {
                link.TargetPath = target;
                link.WorkingDirectory = Path.GetDirectoryName(target);
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
