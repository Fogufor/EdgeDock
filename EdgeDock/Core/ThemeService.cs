using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

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
        Color accent = ReadAccent(dark);

        // Windows присылает несколько уведомлений на одно изменение — реагируем только на настоящие.
        if (_applied && dark == IsDark && taskbarDark == IsTaskbarDark && accent == Accent) return;
        _applied = true;
        IsDark = dark;
        IsTaskbarDark = taskbarDark;
        Accent = accent;

        var resources = Application.Current.Resources;
        string prefix = dark ? "Dark." : "Light.";
        foreach (var dictionary in resources.MergedDictionaries)
        {
            foreach (var key in dictionary.Keys.OfType<string>().Where(k => k.StartsWith(prefix)))
            {
                if (dictionary[key] is not Color color) continue;
                string name = key[prefix.Length..];
                resources["Color." + name] = color;
                resources["Brush." + name] = Frozen(color);
            }
        }
        resources["Color.Accent"] = accent;
        resources["Brush.Accent"] = Frozen(accent);

        Changed?.Invoke();
    }

    /// <summary>Цвет текущей темы, например Color("Panel.Border").</summary>
    public static Color Color(string name) => (Color)Application.Current.Resources["Color." + name];

    private static Color ReadAccent(bool dark)
    {
        // AccentPalette: 8 цветов RGBA от самого светлого к самому тёмному, индекс 3 — основной акцент.
        // Windows 11 рисует акцентные заливки оттенком «Light 2» на тёмном фоне и «Dark 1» на светлом.
        if (Registry.GetValue(AccentKey, "AccentPalette", null) is byte[] palette && palette.Length >= 32)
        {
            int i = (dark ? 1 : 4) * 4;
            return System.Windows.Media.Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
        }

        if (Native.DwmGetColorizationColor(out uint argb, out _) == 0)
            return System.Windows.Media.Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        return (Color)Application.Current.Resources["Accent.Fallback"];
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
