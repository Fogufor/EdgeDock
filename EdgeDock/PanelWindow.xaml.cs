using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EdgeDock.Core;

namespace EdgeDock;

/// <summary>
/// Развёрнутая панель. Положением и моментом показа управляет DockWindow;
/// панель умеет только появиться/исчезнуть с анимацией и сообщить о мыши.
/// </summary>
public partial class PanelWindow : Window
{
    private IntPtr _hwnd;
    private int _slideX, _slideY;
    private bool _hiding;
    private bool _headerPressed, _headerHover;
    private Native.POINT _headerPressPoint;

    public event Action? PointerEntered;
    public event Action? PointerLeft;

    /// <summary>Высота содержимого изменилась, пока панель открыта.</summary>
    public event Action? ContentResized;

    /// <summary>Нажали значок вкладки (id модуля).</summary>
    public event Action<string>? TabClicked;

    /// <summary>Над панелью тащат файл или картинку — DockWindow открывает «Карман».</summary>
    public event Action? FileDragEntered;

    /// <summary>Нажали на заголовок и потянули: DockWindow забирает мышь и продолжает перетаскивание.</summary>
    public event Action<Native.POINT>? HeaderDragStarted;

    public Func<bool> IsLocked { get; set; } = () => false;

    public PanelWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        ThemeService.Changed += ApplyDwmTheme;

        Header.MouseEnter += (_, _) => { _headerHover = true; UpdateHeaderVisual(); };
        Header.MouseLeave += (_, _) => { _headerHover = false; UpdateHeaderVisual(); };
        Header.MouseLeftButtonDown += OnHeaderDown;
        Header.MouseMove += OnHeaderMove;
        Header.MouseLeftButtonUp += (_, _) => EndHeaderPress();
        Header.LostMouseCapture += (_, _) => { _headerPressed = false; UpdateHeaderVisual(); };

        Body.SizeChanged += (_, e) =>
        {
            // Без проверки IsVisible: Show() раскладывает окно ещё до того, как оно станет видимым, и если
            // содержимое выросло именно тогда (микшер заполнился уже после замера), рост терялся — низ панели
            // обрезался. Свёрнутую панель обработчик в доке и так не двигает.
            if (e.HeightChanged && !_hiding) ContentResized?.Invoke();
        };
    }

    /// <summary>Значки вкладок слева, в порядке модулей.</summary>
    public void SetTabs(IReadOnlyList<PanelTab> tabs) => Tabs.ItemsSource = tabs;

    /// <summary>Показать блок вкладки, остальные скрыть. Новый блок добавляется при первом показе. fade — содержимое проявляется.</summary>
    public void ShowView(FrameworkElement view, bool fade)
    {
        if (!ModuleHost.Children.Contains(view)) ModuleHost.Children.Add(view);
        foreach (UIElement child in ModuleHost.Children)
            child.Visibility = child == view ? Visibility.Visible : Visibility.Collapsed;
        if (fade)
            Animate(view, OpacityProperty, 0, 1, (Duration)FindResource("Motion.TabSwitch"), new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>Убрать все блоки (модули пересоздаются по «Перезагрузить настройки»).</summary>
    public void ClearViews() => ModuleHost.Children.Clear();

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PanelTab tab) TabClicked?.Invoke(tab.Id);
    }

    /// <summary>Переставить открытую панель (без анимации) — например, когда выросло содержимое.</summary>
    public void MoveTo(PixelRect rect) =>
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, Native.SWP_NOACTIVATE);

    /// <summary>Экранный прямоугольник панели в пикселях.</summary>
    public PixelRect ScreenRect =>
        _hwnd != IntPtr.Zero && IsVisible && Native.GetWindowRect(_hwnd, out var r) ? PixelRect.From(r) : default;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        Native.PrepareToolWindow(source);
        Native.SetDwm(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        ApplyDwmTheme();

        // Акрил. DWM рисует его только у активного окна, а это окно не активируется никогда (WS_EX_NOACTIVATE).
        // Поэтому держим рамку окна в «активном» состоянии сами — см. WM_NCACTIVATE ниже.
        if (Native.SetDwm(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_TRANSIENTWINDOW) != 0)
            Root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Brush.Panel.Fallback");

        source.AddHook(WndProc);
        Native.SendMessage(_hwnd, Native.WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_NCACTIVATE:
                handled = true;
                return Native.DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
        }
        return IntPtr.Zero;
    }

    private void ApplyDwmTheme()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetDwm(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ThemeService.IsDark ? 1 : 0);
        Native.SetDwm(_hwnd, Native.DWMWA_BORDER_COLOR, Native.ToColorRef(ThemeService.Color("Panel.Border")));
    }

    /// <summary>Нужная высота содержимого (DIP) при заданной ширине.</summary>
    public double MeasureHeight(double width)
    {
        EmptyText.Visibility = ModuleHost.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Body.Measure(new Size(width, double.PositiveInfinity));
        return Math.Ceiling(Body.DesiredSize.Height);
    }

    /// <summary>Показать панель в прямоугольнике rect; содержимое выезжает со стороны (slideX, slideY).</summary>
    public void ShowAt(PixelRect rect, int slideX, int slideY)
    {
        _hiding = false;
        _slideX = slideX;
        _slideY = slideY;
        new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, Native.SWP_NOACTIVATE);

        // Фон DWM появляется сразу; содержимое проявляется и выезжает.
        double distance = (double)FindResource("Motion.SlideDistance");
        var duration = (Duration)FindResource("Motion.Expand");
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Animate(Body, OpacityProperty, 0, 1, duration, ease);
        Animate(Slide, TranslateTransform.XProperty, slideX * distance, 0, duration, ease);
        Animate(Slide, TranslateTransform.YProperty, slideY * distance, 0, duration, ease);

        if (!IsVisible) Show();
    }

    /// <summary>Спрятать с анимацией; onHidden — когда окно действительно скрыто.</summary>
    public void HideAnimated(Action onHidden)
    {
        if (!IsVisible) { onHidden(); return; }
        _hiding = true;

        double distance = (double)FindResource("Motion.SlideDistance");
        var duration = (Duration)FindResource("Motion.Collapse");
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Animate(Slide, TranslateTransform.XProperty, Slide.X, _slideX * distance / 2, duration, ease);
        Animate(Slide, TranslateTransform.YProperty, Slide.Y, _slideY * distance / 2, duration, ease);
        Animate(Body, OpacityProperty, Body.Opacity, 0, duration, ease, completed: () =>
        {
            // Если за время анимации панель снова попросили показать — не прячем.
            if (!_hiding) return;
            _hiding = false;
            Hide();
            onHidden();
        });
    }

    /// <summary>Спрятать мгновенно (перетаскивание, полноэкранный режим).</summary>
    public void HideNow()
    {
        _hiding = false;
        Body.BeginAnimation(OpacityProperty, null);
        Body.Opacity = 0;
        Hide();
    }

    /// <summary>
    /// Анимация без «вечных» часов: итоговое значение ставится как обычное, анимация идёт from→to
    /// и по окончании снимается (FillBehavior.Stop). В покое ничего не тикает.
    /// </summary>
    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to,
        Duration duration, IEasingFunction ease, Action? completed = null)
    {
        ((DependencyObject)target).SetValue(property, to);
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        // Подписываться нужно до BeginAnimation: подписка после запуска до часов анимации уже не доходит.
        if (completed != null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
    }

    // ----- Мышь -----

    protected override void OnMouseEnter(MouseEventArgs e) => PointerEntered?.Invoke();

    protected override void OnMouseLeave(MouseEventArgs e) => PointerLeft?.Invoke();

    // Перетаскивание над панелью: следим через Preview-события — они доходят до окна раньше,
    // чем модуль (например, карман) обработает само перетаскивание.
    protected override void OnPreviewDragEnter(DragEventArgs e)
    {
        PointerEntered?.Invoke();
        FileDragEntered?.Invoke();
    }

    protected override void OnPreviewDragLeave(DragEventArgs e) => PointerLeft?.Invoke();

    protected override void OnDragOver(DragEventArgs e)
    {
        // Над местом, которое ничего не принимает (заголовок, пустые края), — «нельзя».
        if (e.Handled) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        _headerPressed = true;
        Native.GetCursorPos(out _headerPressPoint);
        Header.CaptureMouse();
        UpdateHeaderVisual();
        e.Handled = true;
    }

    private void OnHeaderMove(object sender, MouseEventArgs e)
    {
        if (!_headerPressed || IsLocked()) return;
        Native.GetCursorPos(out var p);
        double threshold = (double)FindResource("Drag.Threshold") * VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(p.X - _headerPressPoint.X) < threshold && Math.Abs(p.Y - _headerPressPoint.Y) < threshold) return;

        _headerPressed = false;
        UpdateHeaderVisual();
        HeaderDragStarted?.Invoke(p);
    }

    private void EndHeaderPress()
    {
        _headerPressed = false;
        Header.ReleaseMouseCapture();
        UpdateHeaderVisual();
    }

    private void UpdateHeaderVisual()
    {
        string key = _headerPressed ? "Brush.Handle.Pressed" : _headerHover ? "Brush.Handle.Hover" : "Brush.Handle.Rest";
        HeaderGrip.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, key);
    }
}
