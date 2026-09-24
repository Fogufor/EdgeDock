using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using EdgeDock.Core;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace EdgeDock.Modules.Pocket;

/// <summary>
/// Разбирает то, что бросили на полку:
/// файлы из Проводника (путь на диске) или данные без пути — картинки из браузера, вложения Outlook,
/// картинки из буфера программ. Данные без пути сохраняются в %LocalAppData%\EdgeDock\shelf.
/// </summary>
internal static class DropReader
{
    private const string FileGroupDescriptor = "FileGroupDescriptorW";
    private const string FileContents = "FileContents";
    private const string Png = "PNG";

    public static bool CanRead(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop)
        || data.GetDataPresent(FileGroupDescriptor)
        || data.GetDataPresent(Png)
        || data.GetDataPresent(DataFormats.Bitmap);

    /// <summary>Пути брошенных файлов. Owned = файлы — наши копии в папке полки.</summary>
    public static (List<string> Paths, bool Owned) Read(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            return (files.ToList(), false);

        if (data.GetDataPresent(FileGroupDescriptor))
            return (ReadVirtualFiles(data), true);

        if (data.GetData(Png) is MemoryStream png)
        {
            string path = UniquePath(ImageName());
            File.WriteAllBytes(path, png.ToArray());
            return ([path], true);
        }

        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            string path = UniquePath(ImageName());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return ([path], true);
        }

        return ([], false);
    }

    /// <summary>«Виртуальные» файлы: имена в FileGroupDescriptorW, содержимое — FileContents по номеру.</summary>
    private static List<string> ReadVirtualFiles(IDataObject data)
    {
        var result = new List<string>();
        if (data.GetData(FileGroupDescriptor) is not MemoryStream descriptor) return result;

        var names = ParseDescriptor(descriptor.ToArray());
        var com = (ComDataObject)data;
        short format = (short)DataFormats.GetDataFormat(FileContents).Id;

        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] is not string name) continue; // папки пропускаем
            string path = UniquePath(name);
            if (CopyFileContents(com, format, i, path)) result.Add(path);
        }
        return result;
    }

    /// <summary>
    /// FILEGROUPDESCRIPTORW: число элементов, затем FILEDESCRIPTORW по 592 байта.
    /// Атрибуты — со смещения 36, имя (WCHAR[260]) — со смещения 72. null — это папка.
    /// </summary>
    private static List<string?> ParseDescriptor(byte[] bytes)
    {
        const int size = 592, flagsOffset = 0, attributesOffset = 36, nameOffset = 72, nameBytes = 520;
        const int FD_ATTRIBUTES = 0x4, FILE_ATTRIBUTE_DIRECTORY = 0x10;

        var names = new List<string?>();
        int count = BitConverter.ToInt32(bytes, 0);
        for (int i = 0; i < count && 4 + (i + 1) * size <= bytes.Length; i++)
        {
            int start = 4 + i * size;
            int flags = BitConverter.ToInt32(bytes, start + flagsOffset);
            int attributes = BitConverter.ToInt32(bytes, start + attributesOffset);
            bool isFolder = (flags & FD_ATTRIBUTES) != 0 && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

            string raw = Encoding.Unicode.GetString(bytes, start + nameOffset, nameBytes);
            int end = raw.IndexOf('\0');
            string name = SafeName(Path.GetFileName(end >= 0 ? raw[..end] : raw));
            names.Add(isFolder ? null : name);
        }
        return names;
    }

    private static bool CopyFileContents(ComDataObject com, short format, int index, string target)
    {
        var request = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };

        com.GetData(ref request, out STGMEDIUM medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    using var file = File.Create(target);
                    var buffer = new byte[81920];
                    IntPtr read = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        while (true)
                        {
                            stream.Read(buffer, buffer.Length, read);
                            int count = Marshal.ReadInt32(read);
                            if (count <= 0) break;
                            file.Write(buffer, 0, count);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(read);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(stream);
                }
                return true;
            }

            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                IntPtr memory = Native.GlobalLock(medium.unionmember);
                try
                {
                    var bytes = new byte[(int)Native.GlobalSize(medium.unionmember)];
                    Marshal.Copy(memory, bytes, 0, bytes.Length);
                    File.WriteAllBytes(target, bytes);
                }
                finally
                {
                    Native.GlobalUnlock(medium.unionmember);
                }
                return true;
            }

            return false;
        }
        finally
        {
            Native.ReleaseStgMedium(ref medium);
        }
    }

    private static string ImageName() => $"Изображение {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Файл" : name;
    }

    /// <summary>Свободное имя в папке полки: «имя.ext», «имя (2).ext»…</summary>
    private static string UniquePath(string name)
    {
        Directory.CreateDirectory(AppPaths.Shelf);
        string path = Path.Combine(AppPaths.Shelf, name);
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(AppPaths.Shelf, $"{stem} ({n}){extension}");
        return path;
    }
}
