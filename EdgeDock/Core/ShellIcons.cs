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
        var info = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        if (!exists) flags |= Native.SHGFI_USEFILEATTRIBUTES;

        Native.SHGetFileInfo(path, Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), flags);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        finally
        {
            Native.DestroyIcon(info.hIcon);
        }
    }
}
