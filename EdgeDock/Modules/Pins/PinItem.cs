using System.ComponentModel;

namespace EdgeDock.Modules.Pins;

/// <summary>Плашка: название, скрытое под ним значение (видно в подсказке), вид значения и отметка «скопировано».</summary>
public sealed class PinItem : INotifyPropertyChanged
{
    private bool _isCopied;

    public PinItem(string label, string value)
    {
        Label = label;
        Value = value;
        Kind = PinKinds.Detect(value);
    }

    public string Label { get; }
    public string Value { get; }
    public PinKind Kind { get; }

    /// <summary>Только что скопировано — на плашке галочка вместо значка.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (_isCopied == value) return;
            _isCopied = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCopied)));
        }
    }

    internal PinEntry ToEntry() => new() { Label = Label, Value = Value };

    public event PropertyChangedEventHandler? PropertyChanged;
}
