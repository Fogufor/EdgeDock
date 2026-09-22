using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EdgeDock.Core;

public enum TrayCommand { ToggleLock = 1, ResetPosition, OpenSettings, OpenLogs, ReloadSettings, Exit }

/// <summary>
/// Иконка в трее на чистом Shell_NotifyIcon и системное меню Win32: без WinForms и сторонних пакетов,
/// меню выглядит как родное меню Windows 11 и следует тёмной теме.
/// Иконка рисуется из глифа Segoe Fluent Icons под цвет панели задач.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;
    private const int IconId = 1;

    private readonly Func<bool> _isLocked;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;

    public event Action<TrayCommand>? Command;

    public TrayIcon(Func<bool> isLocked)
    {
        _isLocked = isLocked;

        // Обычное (не message-only) скрытое окно: только такие получают TaskbarCreated после перезапуска Проводника.
        _window = new HwndSource(new HwndSourceParameters("EdgeDock.Tray")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        try
        {
            Native.SetPreferredAppMode(Native.PreferredAppMode_AllowDark);
            Native.AllowDarkModeForWindow(_window.Handle, true);
            Native.FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Старая сборка Windows: меню будет светлым, это не ошибка.
        }

        Add();
        ThemeService.Changed += OnThemeChanged;
    }

    private void Add()
    {
        _icon = RenderIcon();
        var data = NewData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref data))
        {
            Log.Warn("Не удалось добавить иконку в трей.");
            return;
        }
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    private void OnThemeChanged()
    {
        IntPtr old = _icon;
        _icon = RenderIcon();
        var data = NewData(Native.NIF_ICON);
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        Native.DestroyIcon(old);
        try { Native.FlushMenuThemes(); } catch (EntryPointNotFoundException) { }
    }

    private Native.NOTIFYICONDATA NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "EdgeDock",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: событие в младшем слове lParam, координаты курсора — в wParam.
            int evt = lParam.ToInt32() & 0xFFFF;
            if (evt is Native.WM_CONTEXTMENU or Native.NIN_SELECT or Native.NIN_KEYSELECT)
            {
                int x = (short)(wParam.ToInt64() & 0xFFFF);
                int y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ShowMenu(x, y);
            }
            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            Native.DestroyIcon(_icon);
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = Native.CreatePopupMenu();
        Item(TrayCommand.ToggleLock, "Закрепить положение", _isLocked());
        Item(TrayCommand.ResetPosition, "Вернуть на место");
        Separator();
        Item(TrayCommand.OpenSettings, "Открыть настройки");
        Item(TrayCommand.OpenLogs, "Открыть папку логов");
        Item(TrayCommand.ReloadSettings, "Перезагрузить настройки");
        Separator();
        Item(TrayCommand.Exit, "Выход");

        // Без SetForegroundWindow меню не закроется по клику мимо него.
        Native.SetForegroundWindow(_window.Handle);
        int chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY,
            x, y, _window.Handle, IntPtr.Zero);
        Native.PostMessage(_window.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Native.DestroyMenu(menu);

        if (chosen != 0) Command?.Invoke((TrayCommand)chosen);

        void Item(TrayCommand command, string text, bool isChecked = false) =>
            Native.AppendMenu(menu, Native.MF_STRING | (isChecked ? Native.MF_CHECKED : 0), (UIntPtr)(uint)command, text);

        void Separator() => Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
    }

    /// <summary>Рисует глиф из Tokens.xaml в иконку системного размера, цветом под тему панели задач.</summary>
    private static IntPtr RenderIcon()
    {
        var resources = Application.Current.Resources;
        int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        var color = (Color)resources[ThemeService.IsTaskbarDark ? "Tray.Glyph.OnDark" : "Tray.Glyph.OnLight"];

        var text = new FormattedText((string)resources["Glyph.Tray"], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)resources["Font.Icons"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Центрируем по реальным границам глифа, а не по строке шрифта.
            dc.PushTransform(new TranslateTransform(Math.Round((size - bounds.Width) / 2 - bounds.X), Math.Round((size - bounds.Height) / 2 - bounds.Y)));
            dc.DrawGeometry(new SolidColorBrush(color), null, geometry);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);

        // WPF отдаёт premultiplied alpha, а иконке нужна обычная — иначе края глифа темнеют.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)(pixels[i] * 255 / a);
            pixels[i + 1] = (byte)(pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)(pixels[i + 2] * 255 / a);
        }

        IntPtr colorBitmap = Native.CreateBitmap(size, size, 1, 32, pixels);
        IntPtr maskBitmap = Native.CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = colorBitmap, hbmMask = maskBitmap };
        IntPtr icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(colorBitmap);
        Native.DeleteObject(maskBitmap);
        return icon;
    }

    public void Dispose()
    {
        ThemeService.Changed -= OnThemeChanged;
        var data = NewData(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        Native.DestroyIcon(_icon);
        _window.Dispose();
    }
}
