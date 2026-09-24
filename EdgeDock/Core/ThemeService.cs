using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace EdgeDock.Core;

/// <summary>
/// Следит за светлой/тёмной темой Windows и цветом акцента.
/// Копирует цвета "Dark.*" или "Light.*" из Tokens.xaml в ресурсы "Brush.*" и "Color.*".
/// Вызывается при запуске и каждый раз, когда Windows сообщает о смене темы или акцента.
/// </summary>
internal static class ThemeService
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private static bool _applied;

    /// <summary>Приложения в тёмной теме.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Панель задач (а значит, и трей) в тёмной теме. Может отличаться от <see cref="IsDark"/>.</summary>
    public static bool IsTaskbarDark { get; private set; }

    public static Color Accent { get; private set; }

    public static event Action? Changed;

    public static void Apply()
    {
        bool dark = ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
        bool taskbarDark = ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) == 0;
        Color[] palette = ReadAccentPalette();
        // Windows 11 рисует акцентные заливки оттенком «Light 2» на тёмном фоне и «Dark 1» на светлом.
        Color accent = palette[dark ? 1 : 4];

        // Windows присылает несколько уведомлений на одно изменение — реагируем только на настоящие.
        if (_applied && dark == IsDark && taskbarDark == IsTaskbarDark && accent == Accent) return;
        bool themeChanged = !_applied || dark != IsDark;
        _applied = true;
        IsDark = dark;
        IsTaskbarDark = taskbarDark;
        Accent = accent;

        var resources = Application.Current.Resources;
        var merged = resources.MergedDictionaries;

        // Наши токены.
        var tokens = merged.First(d => d.Source?.OriginalString.EndsWith("Tokens.xaml") == true);
        string prefix = dark ? "Dark." : "Light.";
        foreach (var key in tokens.Keys.OfType<string>().Where(k => k.StartsWith(prefix)))
        {
            if (tokens[key] is not Color color) continue;
            string name = key[prefix.Length..];
            resources["Color." + name] = color;
            resources["Brush." + name] = Frozen(color);
        }
        resources["Color.Accent"] = accent;
        resources["Brush.Accent"] = Frozen(accent);

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

    private static int ReadDword(string key, string name, int fallback) =>
        Registry.GetValue(key, name, fallback) is int value ? value : fallback;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
