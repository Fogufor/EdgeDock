using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EdgeDock.Core;

/// <summary>Системные значки файлов, папок и программ — как в Проводнике.</summary>
internal static class ShellIcons
{
    /// <summary>Значок файла, папки или exe. exists = false — значок по расширению. Только из потока интерфейса.</summary>
    public static ImageSource? Get(string path, bool exists = true)
    {
        // Значок ярлыка у оболочки не спрашиваем — см. FromShortcut.
        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            return exists ? FromShortcut(path) : null;

        var info = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        if (!exists) flags |= Native.SHGFI_USEFILEATTRIBUTES;

        Native.SHGetFileInfo(path, Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), flags);
        return FromHIcon(info.hIcon);
    }

    /// <summary>
    /// Значок ярлыка — его собственный, если задан, иначе значок того, на что он указывает. Спрашивать значок .lnk
    /// у оболочки нельзя: чтобы найти цель, она подгружает в процесс сетевые компоненты, автономные файлы и сторонние
    /// расширения Проводника (OneDrive, IObit…) — это +200 МБ памяти до закрытия виджета. Прочитать ярлык — несколько МБ.
    /// </summary>
    private static ImageSource? FromShortcut(string lnk)
    {
        try
        {
            var (target, iconFile, iconIndex) = ReadShortcut(lnk);
            if (iconFile.Length > 0 && File.Exists(iconFile)) return FromResource(iconFile, iconIndex);
            if (target.Length > 0 && (File.Exists(target) || Directory.Exists(target))) return Get(target);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            // Ярлык не читается — без значка.
        }
        return null;
    }

    private static (string Target, string IconFile, int IconIndex) ReadShortcut(string lnk)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return ("", "", 0);
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnk); // существующий ярлык только читается
            try
            {
                string target = Environment.ExpandEnvironmentVariables((string?)link.TargetPath ?? "");
                string icon = (string?)link.IconLocation ?? ""; // «C:\путь\файл.ico,0» или «,0», если свой значок не задан
                int comma = icon.LastIndexOf(',');
                string iconFile = Environment.ExpandEnvironmentVariables(comma >= 0 ? icon[..comma] : icon);
                int index = comma >= 0 && int.TryParse(icon[(comma + 1)..], out int number) ? number : 0;
                return (target, iconFile, index);
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

    private static ImageSource? FromResource(string file, int index)
    {
        var large = new IntPtr[1];
        Native.ExtractIconEx(file, index, large, null, 1);
        return FromHIcon(large[0]);
    }

    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }
}
