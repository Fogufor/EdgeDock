using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace EdgeDock.Core;

/// <summary>Оформление панели, выбирается в меню трея. Хранится в state.json → "look".</summary>
public enum PanelLook
{
    /// <summary>Системный акрил, светлая или тёмная — как Windows.</summary>
    Standard,
    /// <summary>Всегда тёмное: почти непрозрачная графитовая подложка поверх акрила.</summary>
    Dark,
    /// <summary>Всегда тёмное дымчатое стекло: размытие без системного оттенка, блик и отсвет акцента.</summary>
    Glass,
}

/// <summary>
/// Следит за светлой/тёмной темой Windows и цветом акцента.
/// Копирует цвета "Dark.*" или "Light.*" из Tokens.xaml в ресурсы "Brush.*" и "Color.*",
/// для оформлений «Тёмное» и «Стекло» — поверх ещё "Deep.*" или "Glass.*".
/// Вызывается при запуске, при смене оформления и каждый раз, когда Windows сообщает о смене темы или акцента.
/// </summary>
internal static class ThemeService
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private static bool _applied;
    private static PanelLook _appliedLook;

    /// <summary>Выбранное оформление.</summary>
    public static PanelLook Look { get; private set; }

    /// <summary>Интерфейс в тёмных цветах: тёмная тема Windows или оформление «Тёмное»/«Стекло».</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Панель задач (а значит, и трей) в тёмной теме. Может отличаться от <see cref="IsDark"/>.</summary>
    public static bool IsTaskbarDark { get; private set; }

    public static Color Accent { get; private set; }

    public static event Action? Changed;

    /// <summary>Сменить оформление: всё перекрашивается на лету.</summary>
    public static void SetLook(PanelLook look)
    {
        Look = look;
        Apply();
    }

    public static void Apply()
    {
        bool dark = Look != PanelLook.Standard || ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
        bool taskbarDark = ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) == 0;
        Color[] palette = ReadAccentPalette();
        // Windows 11 рисует акцентные заливки оттенком «Light 2» на тёмном фоне и «Dark 1» на светлом.
        Color accent = palette[dark ? 1 : 4];

        // Windows присылает несколько уведомлений на одно изменение — реагируем только на настоящие.
        if (_applied && dark == IsDark && taskbarDark == IsTaskbarDark && accent == Accent && Look == _appliedLook) return;
        bool themeChanged = !_applied || dark != IsDark;
        _applied = true;
        _appliedLook = Look;
        IsDark = dark;
        IsTaskbarDark = taskbarDark;
        Accent = accent;

        var resources = Application.Current.Resources;
        var merged = resources.MergedDictionaries;

        // Наши токены: набор темы, поверх — отличия оформления.
        var tokens = merged.First(d => d.Source?.OriginalString.EndsWith("Tokens.xaml") == true);
        CopyColors(tokens, resources, dark ? "Dark." : "Light.");
        if (Look == PanelLook.Dark) CopyColors(tokens, resources, "Deep.");
        if (Look == PanelLook.Glass) CopyColors(tokens, resources, "Glass.");
        resources["Color.Accent"] = accent;
        resources["Brush.Accent"] = Frozen(accent);
        resources["Brush.Accent.Hover"] = Frozen(WithOpacity(accent, (double)tokens["Opacity.AccentHover"]));
        resources["Brush.Accent.Pressed"] = Frozen(WithOpacity(accent, (double)tokens["Opacity.AccentPressed"]));

        // Блик сверху (гаснет к середине панели) и отсвет акцента у верхнего края — только у «Стекла».
        Color sheen = (Color)resources["Color.Panel.Sheen"];
        resources["Brush.Panel.Sheen"] = sheen.A == 0 ? Brushes.Transparent : Frozen(new LinearGradientBrush(
            [new GradientStop(sheen, 0), new GradientStop(WithOpacity(sheen, 0), (double)tokens["Sheen.Fade"])],
            new Point(0, 0), new Point(0, 1)));
        resources["Brush.Panel.Glow"] = Look != PanelLook.Glass ? Brushes.Transparent : Frozen(new RadialGradientBrush(
            [new GradientStop(WithOpacity(accent, (double)tokens["Opacity.GlassGlow"]), 0), new GradientStop(WithOpacity(accent, 0), 1)])
        {
            Center = new Point(0.5, 0),
            GradientOrigin = new Point(0.5, 0),
            RadiusX = (double)tokens["Glow.RadiusX"],
            RadiusY = (double)tokens["Glow.RadiusY"],
        });

        // Словарь темы WPF UI меняем сами: её ApplicationThemeManager заодно перекрашивает фон
        // главного окна приложения, а у нас это прозрачная полоска дока.
        if (themeChanged)
        {
            int index = merged.IndexOf(merged.OfType<ThemesDictionary>().First());
            merged[index] = new ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light };
        }
        // Акцент WPF UI: те же оттенки системной палитры, что использует Windows.
        ApplicationAccentColorManager.Apply(palette[3],
            dark ? palette[2] : palette[4], dark ? palette[1] : palette[5], dark ? palette[0] : palette[6]);

        Changed?.Invoke();
    }

    /// <summary>Цвет текущей темы, например Color("Panel.Border").</summary>
    public static Color Color(string name) => (Color)Application.Current.Resources["Color." + name];

    /// <summary>
    /// Системная палитра акцента: 7 оттенков от самого светлого (Light 3) до самого тёмного (Dark 3),
    /// индекс 3 — основной цвет.
    /// </summary>
    private static Color[] ReadAccentPalette()
    {
        // AccentPalette в реестре: 8 цветов RGBA, последний не используется.
        if (Registry.GetValue(AccentKey, "AccentPalette", null) is byte[] bytes && bytes.Length >= 28)
        {
            return Enumerable.Range(0, 7)
                .Select(i => System.Windows.Media.Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]))
                .ToArray();
        }

        Color baseColor = Native.DwmGetColorizationColor(out uint argb, out _) == 0
            ? System.Windows.Media.Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : (Color)Application.Current.Resources["Accent.Fallback"];
        return Enumerable.Repeat(baseColor, 7).ToArray();
    }

    /// <summary>Цвета набора prefix ("Dark.", "Glass." …) — в ресурсы "Color.*" и "Brush.*".</summary>
    private static void CopyColors(ResourceDictionary tokens, ResourceDictionary resources, string prefix)
    {
        foreach (var key in tokens.Keys.OfType<string>().Where(k => k.StartsWith(prefix)))
        {
            if (tokens[key] is not Color color) continue;
            string name = key[prefix.Length..];
            resources["Color." + name] = color;
            resources["Brush." + name] = Frozen(color);
        }
    }

    private static int ReadDword(string key, string name, int fallback) =>
        Registry.GetValue(key, name, fallback) is int value ? value : fallback;

    private static Color WithOpacity(Color color, double opacity) =>
        System.Windows.Media.Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);

    private static SolidColorBrush Frozen(Color color) => Frozen(new SolidColorBrush(color));

    private static T Frozen<T>(T brush) where T : Brush
    {
        brush.Freeze();
        return brush;
    }
}
