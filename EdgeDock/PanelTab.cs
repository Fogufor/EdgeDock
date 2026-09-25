using System.ComponentModel;

namespace EdgeDock;

/// <summary>Значок вкладки в колонке слева: модуль, подпись (подсказка), глиф и отметка «открыта».</summary>
public sealed class PanelTab : INotifyPropertyChanged
{
    private bool _isActive;

    public PanelTab(string id, string title, string glyph)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }

    /// <summary>Для UI Automation: «Tab.pins».</summary>
    public string AutomationId => "Tab." + Id;

    /// <summary>Вкладка открыта: заливка и акцентная черта слева.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
