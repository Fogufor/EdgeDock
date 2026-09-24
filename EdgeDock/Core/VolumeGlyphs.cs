using System.Windows;

namespace EdgeDock.Core;

/// <summary>Значок динамика по уровню громкости — как в Windows 11 (общая громкость и микшер).</summary>
internal static class VolumeGlyphs
{
    public static string For(double level, bool muted) => (string)Application.Current.Resources[
        muted ? "Glyph.Mute"
        : level <= 0 ? "Glyph.Volume0"
        : level < 34 ? "Glyph.Volume1"
        : level < 67 ? "Glyph.Volume2"
        : "Glyph.Volume3"];
}
