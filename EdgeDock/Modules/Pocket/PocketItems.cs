using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pocket;

/// <summary>Снимок в ленте.</summary>
public sealed class ShotItem(string path) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public string Path { get; } = path;

    /// <summary>Миниатюра; есть только пока панель развёрнута.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Changed(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Элемент полки.</summary>
public sealed class ShelfItem(ShelfEntry entry) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isMissing, _isSelected;

    public ShelfEntry Entry { get; } = entry;

    public string Name => System.IO.Path.GetFileName(Entry.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    public bool Exists => File.Exists(Entry.Path) || Directory.Exists(Entry.Path);

    /// <summary>Значок или миниатюра; есть только пока панель развёрнута.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Changed(); }
    }

    /// <summary>Исходный файл исчез — элемент показывается приглушённым, по клику убирается.</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set { _isMissing = value; Changed(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Changed(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
