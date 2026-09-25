using System.Windows;

namespace EdgeDock.Core;

/// <summary>
/// Блок панели (карман, встреча, музыка…). Объект модуля живёт всё время работы дока,
/// а его интерфейс создаётся лениво — при первом разворачивании панели.
/// </summary>
public interface IDockModule
{
    /// <summary>Идентификатор из settings.json → "modules".</summary>
    string Id { get; }

    /// <summary>Подпись вкладки (всплывающая подсказка у значка).</summary>
    string Title { get; }

    /// <summary>Ключ значка вкладки в Tokens.xaml (Glyph.*).</summary>
    string TabGlyph { get; }

    /// <summary>Создать интерфейс блока. Вызывается один раз, при первом открытии его вкладки.</summary>
    FrameworkElement CreateView();

    /// <summary>Вкладка модуля показана (панель развернулась на ней или на неё переключились).</summary>
    void OnExpanded();

    /// <summary>Вкладка скрыта или панель свернулась: освободить превью, остановить анимации.</summary>
    void OnCollapsed();

    /// <summary>Полноэкранное приложение: полностью замереть, включая таймеры.</summary>
    void Suspend();

    void Resume();

    /// <summary>Нужно внимание пользователя — свёрнутая полоска мягко окрашивается в акцент.</summary>
    bool HasAttention { get; }

    event EventHandler? AttentionChanged;
}
