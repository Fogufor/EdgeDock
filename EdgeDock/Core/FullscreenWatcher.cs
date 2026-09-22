using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EdgeDock.Core;

/// <summary>
/// Сообщает, когда на мониторе дока активно полноэкранное приложение (видео, игра, презентация).
/// Без опроса: проверка идёт только по событиям Windows — смена активного окна
/// и уведомление оболочки о полноэкранном приложении (ABN_FULLSCREENAPP).
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private const int AppBarMessage = Native.WM_APP + 2;

    private readonly Func<IntPtr> _dockMonitor;
    private readonly HwndSource _window;
    private readonly Native.WinEventProc _onForeground; // держим ссылку, иначе сборщик мусора удалит делегат
    private readonly IntPtr _hook;

    public bool IsActive { get; private set; }

    public event Action<bool>? Changed;

    /// <param name="dockMonitor">Возвращает монитор (HMONITOR), на котором сейчас стоит док.</param>
    public FullscreenWatcher(Func<IntPtr> dockMonitor)
    {
        _dockMonitor = dockMonitor;

        // Скрытое окно для уведомлений оболочки. Регистрация как appbar место на экране не резервирует.
        _window = new HwndSource(new HwndSourceParameters("EdgeDock.Fullscreen")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        var appBar = NewAppBarData();
        Native.SHAppBarMessage(Native.ABM_NEW, ref appBar);

        _onForeground = (_, _, _, _, _, _, _) => Check();
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _onForeground, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        IsActive = Detect();
    }

    /// <summary>Перепроверить вручную, например после переноса дока на другой монитор.</summary>
    public void Check()
    {
        bool active = Detect();
        if (active == IsActive) return;
        IsActive = active;
        Changed?.Invoke(active);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((msg == AppBarMessage && wParam.ToInt32() == Native.ABN_FULLSCREENAPP) || msg == Native.WM_DISPLAYCHANGE)
            Check();
        return IntPtr.Zero;
    }

    private bool Detect()
    {
        if (Native.SHQueryUserNotificationState(out int state) != 0) return false;

        // Эксклюзивный полноэкранный режим игр и режим презентации — прячемся на всех мониторах.
        if (state is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE) return true;
        if (state != Native.QUNS_BUSY) return false;

        // Обычное полноэкранное окно — прячемся, только если оно на мониторе дока.
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsShellWindow(foreground)) return false;

        IntPtr monitor = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor != _dockMonitor()) return false;

        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(foreground, out var r)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new char[64];
        int length = Native.GetClassName(hwnd, buffer, buffer.Length);
        string name = new(buffer, 0, length);
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private Native.APPBARDATA NewAppBarData() => new()
    {
        cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = _window.Handle,
        uCallbackMessage = AppBarMessage,
    };

    public void Dispose()
    {
        Native.UnhookWinEvent(_hook);
        var appBar = NewAppBarData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref appBar);
        _window.Dispose();
    }
}
