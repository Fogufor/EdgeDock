using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EdgeDock.Core;
using EdgeDock.Modules.Audio;
using EdgeDock.Modules.Media;
using EdgeDock.Modules.Pins;
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
    private readonly Dictionary<IDockModule, FrameworkElement> _views = [];
    private List<PanelTab> _tabs = [];
    private IDockModule? _active;       // модуль открытой вкладки
    private PixelRect _panelRect;       // где панель сейчас (в том числе пока Show() ещё не сделал её видимой)

    private IntPtr _hwnd;
    private PanelWindow? _panel;
    private SnapHintWindow? _hint;

    // Где и как док показан сейчас (может отличаться от сохранённого, например если монитор отключён).
    private List<MonitorInfo> _monitors = [];
    private MonitorInfo? _monitor;
    private DockMode _shownMode;
    private PixelRect _handleRect;

    private bool _expanded, _attention, _layoutScheduled;
    private bool _fullscreen;      // на мониторе дока что-то на весь экран: полоска невидима, но ловит мышь
    private bool _modulesAsleep;   // модулям сказали Suspend()
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
        "media" => new MediaModule(),
        "audio" => new AudioModule(_settings),
        "pins" => new PinsModule(),
        _ => null,
    };

    /// <summary>Создать модули по списку из settings.json; при перезагрузке настроек — заново.</summary>
    private void BuildModules()
    {
        foreach (var module in _modules) (module as IDisposable)?.Dispose();
        _modules.Clear();
        _views.Clear();
        _active = null;
        _panel?.ClearViews();

        foreach (string id in _settings.Settings.Modules)
        {
            var module = CreateModule(id);
            if (module == null) continue;
            module.AttentionChanged += (_, _) => UpdateAttention();
            _modules.Add(module);
        }
        _tabs = _modules.Select(m => new PanelTab(m.Id, m.Title, (string)FindResource(m.TabGlyph))).ToList();
        _panel?.SetTabs(_tabs);
        _modulesAsleep = false; // новые модули не спят
        if (_fullscreen && !_expanded) SleepModules(true);
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
        // На весь экран полоска невидима, но окно остаётся и ловит мышь (прозрачный фон Root).
        StripView.Visibility = !strip ? Visibility.Collapsed : _fullscreen ? Visibility.Hidden : Visibility.Visible;
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
        if (!_expanded && !_dragging) Restart(_expandTimer);
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
        // На весь экран полоска невидима — тянуть её нельзя, панель открывается наведением.
        if (_fullscreen)
        {
            e.Handled = true;
            return;
        }
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

    // Перетаскивание файла на полоску: разворот сразу, без задержки, и сразу «Карман» — файлы принимает только полка.
    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        _collapseTimer.Stop();
        Expand("pocket");
        ShowTab("pocket");
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
        if (_settings.State.Dock.Locked || _fullscreen) return;
        _pressed = true;
        CaptureMouse(); // кнопка всё ещё нажата — забираем мышь у панели
        BeginDrag(p, grabCenter: true);
        DragTo(p);
        UpdateVisual();
    }

    // ----- Панель -----

    /// <param name="tabId">Какую вкладку открыть; null — последнюю открытую (или первую).</param>
    private void Expand(string? tabId = null)
    {
        if (_expanded || _dragging || _monitor == null) return;
        _expandTimer.Stop();

        var panel = EnsurePanel();
        _expanded = true;
        SleepModules(false); // на весь экран блоки спали — открытой панели они нужны
        _active = _modules.FirstOrDefault(m => m.Id == (tabId ?? _settings.State.Dock.Tab)) ?? _modules.FirstOrDefault();
        if (_active != null)
        {
            panel.ShowView(ViewOf(_active), fade: false);
            _active.OnExpanded();
        }
        MarkActiveTab();

        var (rect, slideX, slideY) = PanelPlacement();
        _panelRect = rect;
        panel.ShowAt(rect, slideX, slideY);
        UpdateVisual();
    }

    /// <summary>Интерфейс модуля: создаётся при первом открытии его вкладки.</summary>
    private FrameworkElement ViewOf(IDockModule module)
    {
        if (_views.TryGetValue(module, out var view)) return view;
        view = module.CreateView();
        AutomationProperties.SetAutomationId(view, "View." + module.Id);
        _views[module] = view;
        return view;
    }

    /// <summary>Открыть вкладку в развёрнутой панели: прежний модуль засыпает, новый просыпается, высота — по новому блоку.</summary>
    private void ShowTab(string id)
    {
        var module = _modules.FirstOrDefault(m => m.Id == id);
        if (!_expanded || module == null || module == _active) return;
        _active?.OnCollapsed();
        _active = module;
        _panel!.ShowView(ViewOf(module), fade: true);
        module.OnExpanded();
        MarkActiveTab();
        ResizePanel();
    }

    /// <summary>Отметить открытую вкладку и запомнить её в state.json (только если сменилась).</summary>
    private void MarkActiveTab()
    {
        foreach (var tab in _tabs) tab.IsActive = tab.Id == _active?.Id;
        if (_active == null || _settings.State.Dock.Tab == _active.Id) return;
        _settings.State.Dock.Tab = _active.Id;
        _settings.SaveState();
    }

    /// <summary>Открытая панель меняет высоту (другая вкладка, выросло содержимое): верхний край на месте.</summary>
    private void ResizePanel()
    {
        if (!_expanded || _dragging || _monitor == null || _panel == null) return;
        _panelRect = _placement.KeepTop(PanelPlacement().Rect, _panelRect.Top, _monitor);
        _panel.MoveTo(_panelRect);
    }

    private (PixelRect Rect, int SlideX, int SlideY) PanelPlacement()
    {
        // Ширина содержимого — из настроек; панель шире на колонку значков вкладок.
        double width = _settings.Settings.Dock.Width + (double)FindResource("Tab.RailWidth");
        return _placement.PanelRect(_shownMode, _settings.State.Dock.Edge, _monitor!, _handleRect,
            width, _panel!.MeasureHeight(width));
    }

    private PanelWindow EnsurePanel()
    {
        if (_panel != null) return _panel;

        _panel = new PanelWindow { IsLocked = () => _settings.State.Dock.Locked };
        _panel.SetTabs(_tabs);
        _panel.TabClicked += ShowTab;
        _panel.FileDragEntered += () => ShowTab("pocket");
        _panel.PointerEntered += () => _collapseTimer.Stop();
        _panel.PointerLeft += () => { if (_expanded) Restart(_collapseTimer); };
        _panel.HeaderDragStarted += OnHeaderDragStarted;
        // Содержимое выросло или уменьшилось (новый снимок, файл на полке) — подгоняем размер окна.
        _panel.ContentResized += ResizePanel;
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

    // Засыпает только открытая вкладка: остальные и так не просыпались. На весь экран — снова спят все.
    private void NotifyCollapsed()
    {
        _active?.OnCollapsed();
        if (_fullscreen) SleepModules(true);
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

    /// <summary>
    /// На мониторе дока что-то открыто на весь экран (игра, видео, презентация). Полоска у края становится невидимой,
    /// но её место по-прежнему ловит мышь — панель открывается поверх, не забирая фокус; блоки спят, пока панель закрыта.
    /// Таблетка в свободном месте прячется совсем: невидимая зона посреди экрана открывала бы панель случайно.
    /// </summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        _expandTimer.Stop();
        _collapseTimer.Stop();

        if (fullscreen)
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
            SleepModules(true);
        }
        else if (!_expanded)
        {
            SleepModules(false);
        }
        Layout(); // заодно заново поднимает полоску поверх всех окон: полноэкранные окна часто сами «поверх всех»
        if (_fullscreen && _shownMode != DockMode.Edge) Hide();
        else if (!IsVisible) Show();
    }

    /// <summary>Усыпить или разбудить все модули (на весь экран — спят, пока панель закрыта).</summary>
    private void SleepModules(bool sleep)
    {
        if (_modulesAsleep == sleep) return;
        _modulesAsleep = sleep;
        foreach (var module in _modules)
        {
            if (sleep) module.Suspend();
            else module.Resume();
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
