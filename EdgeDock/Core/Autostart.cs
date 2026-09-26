using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EdgeDock.Core;

/// <summary>
/// Автозапуск — запись EdgeDock в HKCU\Software\Microsoft\Windows\CurrentVersion\Run, как у большинства программ в трее.
/// Раньше был ярлык в папке shell:startup, но однажды после входа в Windows виджет так и не запустился, а запуски оттуда
/// Windows нигде не записывает. Запуски из Run происходят раньше и видны в журнале Microsoft-Windows-Shell-Core/Operational.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgeDock";

    /// <summary>Прежний способ — ярлык в папке автозагрузки. Удаляется, чтобы виджет не запускался дважды.</summary>
    private static string LegacyShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "EdgeDock.lnk");

    public static void Sync(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;

            // Отладочная сборка запускается из bin\ — не прописываем её в автозапуск.
            // Рабочую копию ставит окно установки в %LocalAppData%\Programs\EdgeDock.
            if (exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return;

            if (File.Exists(LegacyShortcut)) File.Delete(LegacyShortcut);

            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!enabled)
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
                return;
            }

            // Пишем, только если запись отличается: «оптимизаторы» вроде Advanced SystemCare следят за автозагрузкой.
            string command = $"\"{exe}\"";
            if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, command);
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск: не удалось обновить запись в реестре.", ex);
        }
    }

    /// <summary>Ярлык .lnk на target (им пользуется установщик для меню «Пуск»).</summary>
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
