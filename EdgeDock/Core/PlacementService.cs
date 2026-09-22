using System.Runtime.InteropServices;
using System.Windows;

namespace EdgeDock.Core;

public enum DockMode { Edge, Free }

public enum DockEdge { Left, Top, Right }

/// <summary>
/// Где стоит док. Хранится в state.json → "dock".
/// X и Y — доли рабочей области монитора (0…1): у края это положение центра полоски вдоль края,
/// в свободном месте — центр таблетки. Так положение переживает смену разрешения и масштаба.
/// </summary>
public sealed class DockState
{
    public DockMode Mode { get; set; } = DockMode.Edge;
    public DockEdge Edge { get; set; } = DockEdge.Top;

    /// <summary>Имя устройства монитора (\\.\DISPLAY1). null — основной монитор.</summary>
    public string? Monitor { get; set; }

    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.0;
    public bool Locked { get; set; }
}

/// <summary>Прямоугольник в физических пикселях экрана.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static PixelRect From(Native.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, PixelRect Bounds, PixelRect Work, double Scale, bool IsPrimary);

/// <summary>Край, к которому прилипнет док, если отпустить его сейчас.</summary>
public sealed record SnapTarget(MonitorInfo Monitor, DockEdge Edge, double Along);

/// <summary>
/// Геометрия дока: мониторы, внешние края, магнит, куда раскрывается панель.
/// Все размеры в Tokens.xaml заданы в DIP; здесь они переводятся в пиксели конкретного монитора.
/// </summary>
public sealed class PlacementService
{
    private readonly double _stripLength, _stripHit, _pillWidth, _pillHeight, _panelGap;

    public PlacementService(ResourceDictionary tokens)
    {
        _stripLength = (double)tokens["Strip.Length"];
        _stripHit = (double)tokens["Strip.HitThickness"];
        _pillWidth = (double)tokens["Pill.Width"];
        _pillHeight = (double)tokens["Pill.Height"];
        _panelGap = (double)tokens["Panel.EdgeGap"];
    }

    public static int Px(double dip, MonitorInfo monitor) => (int)Math.Round(dip * monitor.Scale);

    // ----- Мониторы -----

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(handle, ref info))
            {
                double scale = Native.GetDpiForMonitor(handle, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
                list.Add(new MonitorInfo(handle, info.szDevice, PixelRect.From(info.rcMonitor), PixelRect.From(info.rcWork),
                    scale, (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo Primary(List<MonitorInfo> all) => all.FirstOrDefault(m => m.IsPrimary) ?? all[0];

    public static MonitorInfo MonitorAt(List<MonitorInfo> all, int x, int y)
    {
        IntPtr handle = Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, Native.MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == handle) ?? Primary(all);
    }

    /// <summary>
    /// Внешний край — тот, в который упирается курсор. Край, к которому примыкает другой монитор,
    /// курсор проходит насквозь, поэтому прилипать к нему нельзя.
    /// </summary>
    public static bool IsOuterEdge(MonitorInfo m, DockEdge edge, List<MonitorInfo> all)
    {
        const int tolerance = 2;
        foreach (var other in all)
        {
            if (other.Handle == m.Handle) continue;
            var a = m.Bounds;
            var b = other.Bounds;
            bool touches = edge switch
            {
                DockEdge.Left => Math.Abs(b.Right - a.Left) <= tolerance && b.Top < a.Bottom && b.Bottom > a.Top,
                DockEdge.Right => Math.Abs(b.Left - a.Right) <= tolerance && b.Top < a.Bottom && b.Bottom > a.Top,
                DockEdge.Top => Math.Abs(b.Bottom - a.Top) <= tolerance && b.Left < a.Right && b.Right > a.Left,
                _ => false,
            };
            if (touches) return false;
        }
        return true;
    }

    // ----- Свёрнутый док (полоска или таблетка) -----

    /// <summary>
    /// Прямоугольник свёрнутого дока для сохранённого положения. Сохранённое состояние не меняет:
    /// если монитор отключён — показывает на основном в том же относительном месте,
    /// если край перестал быть внешним — показывает таблеткой рядом.
    /// </summary>
    public (MonitorInfo Monitor, DockMode Mode, PixelRect Rect) HandleRect(DockState s, List<MonitorInfo> all)
    {
        var mon = all.FirstOrDefault(m => m.DeviceName == s.Monitor) ?? Primary(all);
        var work = mon.Work;
        double x = Math.Clamp(s.X, 0, 1), y = Math.Clamp(s.Y, 0, 1);

        if (s.Mode == DockMode.Edge && IsOuterEdge(mon, s.Edge, all))
        {
            int length = Px(_stripLength, mon), hit = Px(_stripHit, mon);
            if (s.Edge == DockEdge.Top)
            {
                int left = Math.Clamp(Along(work.Left, work.Width, x) - length / 2, work.Left, work.Right - length);
                return (mon, DockMode.Edge, new PixelRect(left, work.Top, length, hit));
            }

            int top = Math.Clamp(Along(work.Top, work.Height, y) - length / 2, work.Top, work.Bottom - length);
            int stripLeft = s.Edge == DockEdge.Left ? work.Left : work.Right - hit;
            return (mon, DockMode.Edge, new PixelRect(stripLeft, top, hit, length));
        }

        var (w, h) = PillSize(mon);
        var pill = new PixelRect(Along(work.Left, work.Width, x) - w / 2, Along(work.Top, work.Height, y) - h / 2, w, h);
        return (mon, DockMode.Free, ClampInto(pill, work));
    }

    public (int Width, int Height) PillSize(MonitorInfo mon) => (Px(_pillWidth, mon), Px(_pillHeight, mon));

    public static PixelRect ClampInto(PixelRect r, PixelRect area) => r with
    {
        Left = Math.Clamp(r.Left, area.Left, Math.Max(area.Left, area.Right - r.Width)),
        Top = Math.Clamp(r.Top, area.Top, Math.Max(area.Top, area.Bottom - r.Height)),
    };

    // ----- Магнит -----

    /// <summary>Ближайший внешний край в пределах snapDistance от таблетки, или null.</summary>
    public SnapTarget? FindSnap(PixelRect pill, List<MonitorInfo> all, int snapDistanceDip)
    {
        var mon = MonitorAt(all, pill.CenterX, pill.CenterY);
        var work = mon.Work;
        int threshold = Px(snapDistanceDip, mon);

        SnapTarget? best = null;
        int bestDistance = int.MaxValue;
        void Consider(DockEdge edge, int distance, double along)
        {
            if (distance > threshold || distance >= bestDistance || !IsOuterEdge(mon, edge, all)) return;
            best = new SnapTarget(mon, edge, along);
            bestDistance = distance;
        }

        Consider(DockEdge.Left, pill.Left - work.Left, Fraction(pill.CenterY, work.Top, work.Height));
        Consider(DockEdge.Top, pill.Top - work.Top, Fraction(pill.CenterX, work.Left, work.Width));
        Consider(DockEdge.Right, work.Right - pill.Right, Fraction(pill.CenterY, work.Top, work.Height));
        return best;
    }

    public static DockState EdgeState(SnapTarget t) => new()
    {
        Mode = DockMode.Edge,
        Edge = t.Edge,
        Monitor = t.Monitor.DeviceName,
        X = t.Edge switch { DockEdge.Top => t.Along, DockEdge.Left => 0, _ => 1 },
        Y = t.Edge == DockEdge.Top ? 0 : t.Along,
    };

    public static DockState FreeState(PixelRect pill, MonitorInfo mon) => new()
    {
        Mode = DockMode.Free,
        Edge = DockEdge.Top,
        Monitor = mon.DeviceName,
        X = Fraction(pill.CenterX, mon.Work.Left, mon.Work.Width),
        Y = Fraction(pill.CenterY, mon.Work.Top, mon.Work.Height),
    };

    // ----- Панель -----

    /// <summary>
    /// Где встанет панель и откуда она выезжает (slideX/slideY: -1, 0 или 1).
    /// У края — от края внутрь экрана; у таблетки — в сторону, где больше места.
    /// Панель никогда не выходит за рабочую область (не залезает под панель задач).
    /// </summary>
    public (PixelRect Rect, int SlideX, int SlideY) PanelRect(DockMode mode, DockEdge edge, MonitorInfo mon, PixelRect handle,
        double widthDip, double heightDip)
    {
        var work = mon.Work;
        int gap = Px(_panelGap, mon);
        int width = Math.Min(Px(widthDip, mon), work.Width - 2 * gap);
        int height = Math.Min(Px(heightDip, mon), work.Height - 2 * gap);
        int x, y, slideX = 0, slideY = 0;

        if (mode == DockMode.Edge)
        {
            switch (edge)
            {
                case DockEdge.Top:
                    x = handle.CenterX - width / 2;
                    y = work.Top + gap;
                    slideY = -1;
                    break;
                case DockEdge.Left:
                    x = work.Left + gap;
                    y = handle.CenterY - height / 2;
                    slideX = -1;
                    break;
                default:
                    x = work.Right - gap - width;
                    y = handle.CenterY - height / 2;
                    slideX = 1;
                    break;
            }
        }
        else
        {
            // Панель накрывает таблетку и раскрывается в сторону большего свободного места.
            bool toRight = work.Right - handle.Right >= handle.Left - work.Left;
            bool down = work.Bottom - handle.Bottom >= handle.Top - work.Top;
            x = toRight ? handle.Left : handle.Right - width;
            y = down ? handle.Top : handle.Bottom - height;
            slideX = toRight ? -1 : 1;
        }

        x = Math.Clamp(x, work.Left + gap, work.Right - gap - width);
        y = Math.Clamp(y, work.Top + gap, work.Bottom - gap - height);
        return (new PixelRect(x, y, width, height), slideX, slideY);
    }

    private static int Along(int start, int length, double fraction) => start + (int)Math.Round(fraction * length);

    private static double Fraction(int value, int start, int length) =>
        length <= 0 ? 0.5 : Math.Round(Math.Clamp((value - start) / (double)length, 0, 1), 4);
}
