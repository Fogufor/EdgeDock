using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pins;

/// <summary>Закреплённое: ссылки, теги, телефоны под названиями; клик копирует. Id в settings.json — "pins".</summary>
internal sealed class PinsModule : IDockModule, INotifyPropertyChanged, IDisposable
{
    private readonly PinStore _store = new(AppPaths.PinsFile);
    private readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private PinItem? _copied;
    private PinEditorWindow? _editor;
    private bool _saveFailed;
    private string _status = "";

    public PinsModule()
    {
        foreach (var entry in _store.Load()) Items.Add(new PinItem(entry.Label, entry.Value));
        _confirmTimer.Tick += (_, _) => EndConfirmation();
    }

    public ObservableCollection<PinItem> Items { get; } = [];

    /// <summary>pins.json не читается: блок показывает ошибку, добавлять нельзя.</summary>
    public bool IsBroken => !_store.IsReadable;

    /// <summary>Надпись в заголовке блока: «Скопировано», «Не удалось скопировать», «Не удалось сохранить» или пусто.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }

    public string Id => "pins";

    public bool HasAttention => false;

    public event EventHandler? AttentionChanged
    {
        add { }
        remove { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FrameworkElement CreateView() => new PinsView(this);

    public void OnExpanded() { }

    public void OnCollapsed() => EndConfirmation();

    public void Suspend() => EndConfirmation();

    public void Resume() { }

    /// <summary>Скопировать значение; на 1,5 с — галочка на плашке и «Скопировано» в заголовке.</summary>
    public void Copy(PinItem item)
    {
        EndConfirmation();
        try
        {
            Clipboard.SetText(item.Value);
            _copied = item;
            item.IsCopied = true;
            Status = "Скопировано";
        }
        catch (ExternalException ex) // буфер занят другой программой, WPF уже повторил попытки
        {
            Log.Error("Закреплённое: не удалось скопировать в буфер обмена.", ex);
            Status = "Не удалось скопировать";
        }
        _confirmTimer.Start();
    }

    /// <summary>Окно добавления (item == null) или правки. Окно одно: если уже открыто — показать его.</summary>
    public void OpenEditor(PinItem? item)
    {
        if (IsBroken) return;
        if (_editor != null)
        {
            _editor.Activate();
            return;
        }
        _editor = new PinEditorWindow(this, item);
        _editor.Closed += (_, _) => _editor = null;
        _editor.Show();
        _editor.Activate();
    }

    public void Add(string label, string value)
    {
        Items.Add(new PinItem(label, value));
        Save();
    }

    public void Update(PinItem item, string label, string value)
    {
        int index = Items.IndexOf(item);
        if (index < 0) return; // элемент уже удалили
        Items[index] = new PinItem(label, value);
        Save();
    }

    public void Remove(PinItem item)
    {
        if (Items.Remove(item)) Save();
    }

    private void Save()
    {
        _saveFailed = !_store.Save(Items.Select(i => i.ToEntry()));
        if (!_confirmTimer.IsEnabled) Status = IdleStatus;
    }

    private string IdleStatus => _saveFailed ? "Не удалось сохранить" : "";

    private void EndConfirmation()
    {
        _confirmTimer.Stop();
        if (_copied != null)
        {
            _copied.IsCopied = false;
            _copied = null;
        }
        Status = IdleStatus;
    }

    public void Dispose()
    {
        EndConfirmation();
        _editor?.Close();
    }
}
