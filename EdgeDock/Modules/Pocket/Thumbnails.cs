using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pocket;

/// <summary>
/// Миниатюры и значки. Картинки декодируются сразу в размер миниатюры и кэшируются на диске
/// маленькими PNG в %LocalAppData%\EdgeDock\cache, поэтому полноразмерные изображения в памяти не держатся.
/// </summary>
internal static class Thumbnails
{
    /// <summary>Ширина миниатюры в пикселях: хватает для превью при масштабе до 200%.</summary>
    private const int DecodeWidth = 200;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Миниатюра картинки (можно вызывать из фонового потока). null — файл не читается.</summary>
    public static BitmapSource? Get(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            string key = Convert.ToHexString(SHA1.HashData(
                Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{DecodeWidth}")));
            string cached = Path.Combine(AppPaths.Cache, key + ".png");
            if (File.Exists(cached)) return Decode(cached, 0);

            var thumbnail = Decode(path, DecodeWidth);
            Directory.CreateDirectory(AppPaths.Cache);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumbnail));
            string temp = cached + ".tmp";
            using (var file = File.Create(temp)) encoder.Save(file);
            File.Move(temp, cached, overwrite: true);
            return thumbnail;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось сделать миниатюру: {path}", ex);
            return null;
        }
    }

    private static BitmapSource Decode(string path, int width)
    {
        // Файл не блокируем: его могут переименовать или удалить, пока мы читаем.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width > 0) image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Системный значок файла или папки. Только из потока интерфейса.</summary>
    public static ImageSource? ShellIcon(string path, bool exists)
    {
        var info = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        // Для исчезнувшего файла — значок по расширению.
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

    /// <summary>Удалить миниатюры, которые давно не пересоздавались. Вызывается при запуске, в фоне.</summary>
    public static void CleanCache()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Cache)) return;
            foreach (var file in new DirectoryInfo(AppPaths.Cache).GetFiles())
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > CacheLifetime) file.Delete();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось почистить кэш миниатюр.", ex);
        }
    }
}
