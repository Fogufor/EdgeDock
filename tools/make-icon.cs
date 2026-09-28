#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

// Значок EdgeDock «Край экрана»: тёмное стекло, у верхнего края — яркая голубая полоска со свечением,
// под ней — полупрозрачная панель. Рисуется векторно под каждый размер (мелкие — упрощённо, без свечения)
// и собирается в EdgeDock.ico (PNG внутри, как принято с Windows Vista).
//   dotnet run --file tools/make-icon.cs -- EdgeDock/Assets/EdgeDock.ico [лист-превью.png]
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string icoPath = args.Length > 0 ? args[0] : "EdgeDock.ico";
string? previewPath = args.Length > 1 ? args[1] : null;
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

var thread = new Thread(() =>
{
    var frames = sizes.Select(size => (Size: size, Png: Encode(Render(size)))).ToList();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoPath))!);
    using (var file = File.Create(icoPath)) WriteIco(file, frames);
    Console.WriteLine($"{icoPath}: {frames.Count} sizes");
    if (previewPath != null) File.WriteAllBytes(previewPath, Encode(Preview()));
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static BitmapSource Render(int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen()) Draw(dc, size);
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    return bitmap;
}

static void Draw(DrawingContext dc, double s)
{
    bool small = s <= 32;
    double inset = small ? 0.5 : s * 0.04;
    var square = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
    double radius = s * 0.22;
    var outline = new RectangleGeometry(square, radius, radius);

    // Стекло.
    dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(0x46, 0x4C, 0x55), Color.FromRgb(0x1F, 0x23, 0x29), 90), null, outline);

    // Свечение от полоски — мягко на весь значок, без границ (обрезано по форме значка).
    if (!small)
    {
        dc.PushClip(outline);
        var glow = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.18), GradientOrigin = new Point(0.5, 0.18), RadiusX = 0.55, RadiusY = 0.45,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x80, 0x4C, 0xC2, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x28, 0x4C, 0xC2, 0xFF), 0.55),
                new GradientStop(Color.FromArgb(0x00, 0x4C, 0xC2, 0xFF), 1.0),
            },
        };
        dc.DrawRectangle(glow, null, new Rect(0, 0, s, s));
        dc.Pop();
    }

    // Панель под полоской.
    var panel = new Rect(s * 0.20, s * (small ? 0.40 : 0.37), s * 0.60, s * (small ? 0.40 : 0.43));
    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(small ? (byte)0x55 : (byte)0x38, 0xFF, 0xFF, 0xFF)), null, panel, s * 0.08, s * 0.08);

    // Полоска дока.
    double barHeight = small ? Math.Max(2, Math.Round(s * 0.13)) : s * 0.08;
    double barY = small ? Math.Round(s * 0.18) : s * 0.17;
    var bar = new Rect(Math.Round(s * 0.24), barY, s - 2 * Math.Round(s * 0.24), barHeight);
    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)), null, bar, barHeight / 2, barHeight / 2);

    // Тонкая светлая обводка — чтобы значок не сливался с тёмным рабочим столом.
    var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Math.Max(1, s * 0.012));
    dc.DrawRoundedRectangle(null, pen, square, radius, radius);
}

static BitmapSource Preview()
{
    int[] shown = [256, 48, 32, 24, 16];
    const int pad = 24, gap = 24;
    int width = pad * 2 + shown.Sum() + gap * (shown.Length - 1), height = pad * 2 + 256;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        for (int half = 0; half < 2; half++)
        {
            var bg = half == 0 ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, half * height, width, height));
            double x = pad;
            foreach (int size in shown)
            {
                dc.DrawImage(Render(size), new Rect(x, half * height + pad + (256 - size) / 2.0, size, size));
                x += size + gap;
            }
        }
    }
    var bitmap = new RenderTargetBitmap(width, height * 2, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    return bitmap;
}

static byte[] Encode(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var memory = new MemoryStream();
    encoder.Save(memory);
    return memory.ToArray();
}

// ICO: заголовок, по записи на размер (256 пишется как 0), затем PNG подряд.
static void WriteIco(Stream stream, List<(int Size, byte[] Png)> frames)
{
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)frames.Count);
    int offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames) writer.Write(png);
}
