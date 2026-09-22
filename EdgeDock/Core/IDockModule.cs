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

    /// <summary>Создать интерфейс блока. Вызывается один раз, при первом разворачивании.</summary>
    FrameworkElement CreateView();

    /// <summary>Панель развернулась.</summary>
    void OnExpanded();

    /// <summary>Панель свернулась: освободить превью, остановить анимации.</summary>
    void OnCollapsed();

    /// <summary>Полноэкранное приложение: полностью замереть, включая таймеры.</summary>
    void Suspend();

    void Resume();

    /// <summary>Нужно внимание пользователя — свёрнутая полоска мягко окрашивается в акцент.</summary>
    bool HasAttention { get; }

    event EventHandler? AttentionChanged;
}
