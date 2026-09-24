using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EdgeDock.Core;
using EdgeDock.Modules.Pocket;

namespace EdgeDock;

/// <summary>
/// Свёрнутый док и вся логика поведения: наведение с задержками, клик, перетаскивание с магнитом,
/// разворот и сворачивание панели, скрытие при полноэкранных приложениях.
/// </summary>
public partial class DockWindow : Window
{
    private readonly SettingsService _settings;
    private readonly PlacementService _placement;
    private readonly DispatcherTimer _expandTimer = new();
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly List<IDockModule> _modules = [];

    private IntPtr _hwnd;
    private PanelWindow? _panel;
    private SnapHintWindow? _hint;

    // Где и как док показан сейчас (может отличаться от сохранённого, например если монитор отключён).
    private List<MonitorInfo> _monitors = [];
    private MonitorInfo? _monitor;
    private DockMode _shownMode;
    private PixelRect _handleRect;

    private bool _expanded, _suspended, _attention, _layoutScheduled, _viewsCreated;
    private bool _hover, _pressed, _dragging;
    private Native.POINT _pressPoint;
    private int _grabX, _grabY;
    private SnapTarget? _snap;

    /// <summary>Док переехал (перетащили, вернули на место, сменилась конфигурация мониторов).</summary>
    public event Action? Moved;

    public DockWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        _placement = new PlacementService(Application.Current.Resources);

        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); Expand(); };
        _collapseTimer.Tick += (_, _) => OnCollapseTimer();
        ApplySettings();

        SourceInitialized += OnSourceInitialized;
        UpdateVisual();
    }

    /// <summary>Модуль по id из settings.json. Модули добавляются по этапам, неизвестные id пропускаются.</summary>
    private IDockModule? CreateModule(string id) => id switch
    {
        "pocket" => new PocketModule(_settings),
        _ => null,
    };

    /// <summary>Создать модули по списку из settings.json; при перезагрузке настроек — заново.</summary>
    private void BuildModules()
    {
        foreach (var module in _modules) (module as IDisposable)?.Dispose();
        _modules.Clear();
        _panel?.ModuleViews.Clear();
        _viewsCreated = false;

        foreach (string id in _settings.Settings.Modules)
        {
            var module = CreateModule(id);
            if (module == null) continue;
            module.AttentionChanged += (_, _) => UpdateAttention();
            _modules.Add(module);
        }
        UpdateAttention();
    }

    /// <summary>Монитор (HMONITOR), на котором сейчас стоит док.</summary>
    public IntPtr CurrentMonitor =>
        _hwnd == IntPtr.Zero ? IntPtr.Zero : Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);

    /// <summary>Применить settings.json (при запуске и по «Перезагрузить настройки»).</summary>
    public void ApplySettings()
    {
        var dock = _settings.Settings.Dock;
        _expandTimer.Interval = TimeSpan.FromMilliseconds(dock.ExpandDelayMs);
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(dock.CollapseDelayMs);
        CollapseNow();
        BuildModules();
        Layout();
    }

    // ----- Окно -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        Native.PrepareToolWindow(source);
        Native.SetDwm(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        Native.SetDwm(_hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);
        source.AddHook(WndProc);
        Layout();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                // Клик по доку не активирует окно — фокус остаётся там, где вы работаете.
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_SETTINGCHANGE:
                if (wParam.ToInt64() == Native.SPI_SETWORKAREA) ScheduleLayout();
                else if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") ThemeService.Apply();
                break;
            case Native.WM_DWMCOLORIZATIONCOLORCHANGED:
                ThemeService.Apply();
                break;
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                ScheduleLayout();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Мониторы, масштаб или рабочая область поменялись — перестроиться, когда Windows закончит.</summary>
    private void ScheduleLayout()
    {
        if (_layoutScheduled || _dragging) return;
        _layoutScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _layoutScheduled = false;
            if (_dragging) return;
            CollapseNow();
            Layout();
        });
    }

    /// <summary>Поставить свёрнутый док по сохранённому положению.</summary>
    private void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        _monitors = PlacementService.GetMonitors();
        if (_monitors.Count == 0) return;

        var state = _settings.State.Dock;
        (_monitor, _shownMode, _handleRect) = _placement.HandleRect(state, _monitors);
        ApplyShape(_shownMode, state.Edge);
        MoveTo(_handleRect);
        Moved?.Invoke();
    }

    private void MoveTo(PixelRect r) =>
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);

    private void ApplyShape(DockMode mode, DockEdge edge)
    {
        bool strip = mode == DockMode.Edge;
        StripView.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        PillView.Visibility = strip ? Visibility.Collapsed : Visibility.Visible;
        if (!strip) return;

        double thickness = (double)FindResource("Strip.Thickness");
        bool horizontal = edge == DockEdge.Top;
        foreach (var bar in new[] { StripBar, StripAttention })
        {
            bar.Width = horizontal ? double.NaN : thickness;
            bar.Height = horizontal ? thickness : double.NaN;
            bar.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            bar.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }
    }

    private void UpdateVisual()
    {
        string state = _pressed ? "Pressed" : _hover || _expanded ? "Hover" : "Rest";
        StripBar.SetResourceReference(Border.BackgroundProperty, "Brush.Handle." + state);
        PillBar.SetResourceReference(Border.BackgroundProperty, "Brush.Handle." + state);
        PillView.SetResourceReference(Border.BackgroundProperty, "Brush.Pill." + state);
    }

    // ----- Мышь -----

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        _hover = true;
        UpdateVisual();
        _collapseTimer.Stop();
        // Разворот только после непрерывного наведения — случайное касание не срабатывает.
        if (!_expanded && !_dragging && !_suspended) Restart(_expandTimer);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = false;
        UpdateVisual();
        _expandTimer.Stop();
        if (_expanded) Restart(_collapseTimer);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _expandTimer.Stop();
        _pressed = true;
        Native.GetCursorPos(out _pressPoint);
        CaptureMouse();
        UpdateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Native.GetCursorPos(out var p);
        if (!_pressed || _monitor == null) return;

        if (!_dragging)
        {
            if (_settings.State.Dock.Locked) return;
            // Сдвиг меньше порога — это ещё клик, а не перетаскивание.
            int threshold = PlacementService.Px((double)FindResource("Drag.Threshold"), _monitor);
            if (Math.Abs(p.X - _pressPoint.X) < threshold && Math.Abs(p.Y - _pressPoint.Y) < threshold) return;
            BeginDrag(p, grabCenter: _shownMode == DockMode.Edge);
        }
        DragTo(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (_dragging)
        {
            // Отпускание может прийти раньше последнего движения мыши — берём итоговое положение курсора.
            Native.GetCursorPos(out var p);
            DragTo(p);
            EndDrag();
        }
        else
        {
            Expand(); // клик — развернуть сразу
        }
        ReleaseMouseCapture();
        UpdateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // Мышь отобрали посреди перетаскивания — оставляем док там, где он сейчас.
        if (_dragging) EndDrag();
        _pressed = false;
        UpdateVisual();
    }

    // Перетаскивание файла на полоску: разворот сразу, без задержки.
    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        _collapseTimer.Stop();
        Expand();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        if (_expanded) Restart(_collapseTimer);
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    // ----- Перетаскивание -----

    /// <param name="grabCenter">Держать таблетку за центр (тянули полоску или заголовок панели).</param>
    private void BeginDrag(Native.POINT p, bool grabCenter)
    {
        _dragging = true;
        _expandTimer.Stop();
        _collapseTimer.Stop();
        CollapseNow();

        _monitors = PlacementService.GetMonitors();
        var (width, height) = _placement.PillSize(PlacementService.MonitorAt(_monitors, p.X, p.Y));
        if (grabCenter)
        {
            _grabX = width / 2;
            _grabY = height / 2;
        }
        else
        {
            // Держим таблетку за ту точку, где нажали, а не где сработал порог перетаскивания.
            _grabX = _pressPoint.X - _handleRect.Left;
            _grabY = _pressPoint.Y - _handleRect.Top;
        }

        // Пока тянем, док — таблетка под курсором.
        _shownMode = DockMode.Free;
        ApplyShape(DockMode.Free, _settings.State.Dock.Edge);

        // Окно подсказки создаём заранее, чтобы первое создание не притормозило перетаскивание.
        if (_hint == null)
        {
            _hint = new SnapHintWindow();
            new WindowInteropHelper(_hint).EnsureHandle();
        }
    }

    private void DragTo(Native.POINT p)
    {
        var mon = PlacementService.MonitorAt(_monitors, p.X, p.Y);
        var (width, height) = _placement.PillSize(mon);
        var pill = PlacementService.ClampInto(new PixelRect(p.X - _grabX, p.Y - _grabY, width, height), mon.Work);
        _monitor = mon;
        _handleRect = pill;
        MoveTo(pill);

        // Подсказка: где док прилипнет, если отпустить сейчас.
        _snap = _placement.FindSnap(pill, _monitors, _settings.Settings.Dock.SnapDistancePx);
        if (_snap == null)
        {
            _hint?.Hide();
            return;
        }
        var (_, _, stripRect) = _placement.HandleRect(PlacementService.EdgeState(_snap), _monitors);
        _hint?.ShowAt(stripRect, _snap.Edge);
    }

    private void EndDrag()
    {
        _dragging = false;
        _hint?.Hide();

        var next = _snap != null ? PlacementService.EdgeState(_snap) : PlacementService.FreeState(_handleRect, _monitor!);
        next.Locked = _settings.State.Dock.Locked;
        _snap = null;
        _settings.State.Dock = next;
        _settings.SaveState();
        Layout();
    }

    /// <summary>Потянули за заголовок развёрнутой панели: панель сворачивается, дальше тянется таблетка.</summary>
    private void OnHeaderDragStarted(Native.POINT p)
    {
        if (_settings.State.Dock.Locked || _suspended) return;
        _pressed = true;
        CaptureMouse(); // кнопка всё ещё нажата — забираем мышь у панели
        BeginDrag(p, grabCenter: true);
        DragTo(p);
        UpdateVisual();
    }

    // ----- Панель -----

    private void Expand()
    {
        if (_expanded || _suspended || _dragging || _monitor == null) return;
        _expandTimer.Stop();

        var panel = EnsurePanel();
        if (!_viewsCreated)
        {
            // Интерфейс модулей создаётся один раз — при первом разворачивании.
            foreach (var module in _modules) panel.AddModuleView(module.CreateView());
            _viewsCreated = true;
        }

        _expanded = true;
        foreach (var module in _modules) module.OnExpanded();

        var (rect, slideX, slideY) = PanelPlacement();
        panel.ShowAt(rect, slideX, slideY);
        UpdateVisual();
    }

    private (PixelRect Rect, int SlideX, int SlideY) PanelPlacement()
    {
        double width = _settings.Settings.Dock.Width;
        return _placement.PanelRect(_shownMode, _settings.State.Dock.Edge, _monitor!, _handleRect,
            width, _panel!.MeasureHeight(width));
    }

    private PanelWindow EnsurePanel()
    {
        if (_panel != null) return _panel;

        _panel = new PanelWindow { IsLocked = () => _settings.State.Dock.Locked };
        _panel.PointerEntered += () => _collapseTimer.Stop();
        _panel.PointerLeft += () => { if (_expanded) Restart(_collapseTimer); };
        _panel.HeaderDragStarted += OnHeaderDragStarted;
        _panel.ContentResized += () =>
        {
            // Содержимое выросло или уменьшилось (новый снимок, файл на полке) — подгоняем размер окна.
            if (_expanded && !_dragging && _monitor != null) _panel.MoveTo(PanelPlacement().Rect);
        };
        return _panel;
    }

    private void OnCollapseTimer()
    {
        _collapseTimer.Stop();
        if (!_expanded || _dragging) return;

        // Мышь могла вернуться на полоску или панель, не вызвав событий (например, после перетаскивания файла).
        Native.GetCursorPos(out var p);
        if (_handleRect.Contains(p.X, p.Y) || (_panel?.ScreenRect.Contains(p.X, p.Y) ?? false)) return;
        Collapse();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _collapseTimer.Stop();
        UpdateVisual();
        _panel?.HideAnimated(NotifyCollapsed);
    }

    private void CollapseNow()
    {
        if (!_expanded) return;
        _expanded = false;
        _collapseTimer.Stop();
        _panel?.HideNow();
        NotifyCollapsed();
        UpdateVisual();
    }

    private void NotifyCollapsed()
    {
        foreach (var module in _modules) module.OnCollapsed();
    }

    // ----- Команды из трея -----

    public void ToggleLock()
    {
        _settings.State.Dock.Locked = !_settings.State.Dock.Locked;
        _settings.SaveState();
    }

    /// <summary>«Вернуть на место»: верхний край основного монитора, по центру.</summary>
    public void ResetPosition()
    {
        _settings.State.Dock = new DockState { Locked = _settings.State.Dock.Locked };
        _settings.SaveState();
        CollapseNow();
        Layout();
    }

    // ----- Полноэкранный режим -----

    /// <summary>Полноэкранное приложение на мониторе дока: спрятаться и полностью замереть.</summary>
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        _expandTimer.Stop();
        _collapseTimer.Stop();

        if (suspended)
        {
            if (_dragging)
            {
                _dragging = false;
                _snap = null;
                _hint?.Hide();
            }
            if (_pressed)
            {
                _pressed = false;
                ReleaseMouseCapture();
            }
            CollapseNow();
            foreach (var module in _modules) module.Suspend();
            Hide();
        }
        else
        {
            foreach (var module in _modules) module.Resume();
            Show();
            Layout();
        }
    }

    // ----- Напоминание -----

    /// <summary>Какой-то модуль просит внимания — полоска один раз плавно окрашивается в акцент, без пульсации.</summary>
    private void UpdateAttention()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateAttention);
            return;
        }

        bool attention = _modules.Any(m => m.HasAttention);
        if (attention == _attention) return;
        _attention = attention;

        var duration = (Duration)FindResource("Motion.Attention");
        foreach (var layer in new UIElement[] { StripAttention, PillAttention })
        {
            double from = layer.Opacity, to = attention ? 1 : 0;
            layer.Opacity = to;
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, duration) { FillBehavior = FillBehavior.Stop });
        }
    }
}
