using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using EdgeDock.Core;
using NAudio.CoreAudioApi;

namespace EdgeDock.Modules.Audio;

/// <summary>Строка устройства в списке.</summary>
public sealed class DeviceItem(string label, string glyph, string? id, bool isCurrent)
{
    /// <summary>Как устройство записано в settings.json.</summary>
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;

    /// <summary>id устройства; null — не подключено.</summary>
    public string? Id { get; } = id;
    public bool IsAvailable => Id != null;
    public bool IsCurrent { get; } = isCurrent;
}

public sealed class AudioState : INotifyPropertyChanged
{
    private bool _hasFavorites, _hasCallMode, _callModeAvailable, _callModeActive;

    public bool HasFavorites { get => _hasFavorites; set => Set(ref _hasFavorites, value); }

    /// <summary>В настройках задан callMode — есть кнопка «Созвон».</summary>
    public bool HasCallMode { get => _hasCallMode; set => Set(ref _hasCallMode, value); }

    /// <summary>Устройства для созвона подключены — кнопку можно нажать.</summary>
    public bool CallModeAvailable { get => _callModeAvailable; set => Set(ref _callModeAvailable, value); }

    public bool CallModeActive { get => _callModeActive; set => Set(ref _callModeActive, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>
/// Звук: избранные выходы и микрофоны, переключение в один клик, режим созвона.
/// За устройствами следит только пока панель развёрнута. Id в settings.json — "audio".
/// </summary>
internal sealed class AudioModule : IDockModule, IDisposable
{
    private readonly SettingsService _settings;
    private readonly AudioDevices _devices = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    // Что было выбрано до режима созвона — для каждой роли отдельно, чтобы вернуть в точности как было.
    private readonly Dictionary<(DataFlow Flow, Role Role), string> _beforeCall = [];
    private bool _expanded, _refreshScheduled;

    public ObservableCollection<DeviceItem> Outputs { get; } = [];
    public ObservableCollection<DeviceItem> Inputs { get; } = [];
    public AudioState State { get; } = new();

    /// <summary>Громкость программ, которые звучат прямо сейчас.</summary>
    public AppMixer Mixer { get; }

    public AudioModule(SettingsService settings)
    {
        _settings = settings;
        Mixer = new AppMixer(_ui);
        _devices.Changed += ScheduleRefresh;
        var audio = settings.Settings.Audio;
        State.HasFavorites = audio.FavoriteOutputs.Count + audio.FavoriteInputs.Count > 0;
        State.HasCallMode = !string.IsNullOrWhiteSpace(audio.CallMode?.Output) || !string.IsNullOrWhiteSpace(audio.CallMode?.Input);
    }

    public string Id => "audio";

    public string Title => "Звук";

    public string TabGlyph => "Glyph.Speaker";

    public bool HasAttention => false;

    public event EventHandler? AttentionChanged
    {
        add { }
        remove { }
    }

    public FrameworkElement CreateView() => new AudioView(this);

    public void OnExpanded()
    {
        _expanded = true;
        _devices.Watch();
        Mixer.Attach();
        Refresh();
    }

    public void OnCollapsed()
    {
        _expanded = false;
        _devices.StopWatching();
        Mixer.Detach();
    }

    public void Suspend()
    {
        _devices.StopWatching();
        Mixer.Detach();
    }

    public void Resume()
    {
        if (!_expanded) return;
        _devices.Watch();
        Mixer.Attach();
    }

    /// <summary>Клик по устройству: сделать его устройством по умолчанию для всех ролей.</summary>
    public void Select(DeviceItem item)
    {
        if (item.Id == null || item.IsCurrent) return;
        Run(() => _devices.SetDefault(item.Id));
        Refresh();
    }

    /// <summary>
    /// Режим созвона: первое нажатие переключает и выход, и микрофон на callMode,
    /// второе — возвращает те устройства, что были до этого.
    /// </summary>
    public void ToggleCallMode()
    {
        if (State.CallModeActive)
        {
            Run(() =>
            {
                foreach (var ((flow, role), id) in _beforeCall)
                {
                    if (_devices.Active(flow).Any(d => d.Id == id)) _devices.SetDefault(id, role);
                }
            });
            _beforeCall.Clear();
            State.CallModeActive = false;
        }
        else
        {
            var callMode = _settings.Settings.Audio.CallMode;
            string? output = _devices.Find(DataFlow.Render, callMode?.Output);
            string? input = _devices.Find(DataFlow.Capture, callMode?.Input);
            if (output == null && input == null) return;

            Run(() =>
            {
                _beforeCall.Clear();
                foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
                {
                    foreach (var role in AudioDevices.Roles)
                    {
                        if (_devices.DefaultId(flow, role) is string id) _beforeCall[(flow, role)] = id;
                    }
                }
                if (output != null) _devices.SetDefault(output);
                if (input != null) _devices.SetDefault(input);
            });
            State.CallModeActive = true;
        }
        Refresh();
    }

    private void ScheduleRefresh()
    {
        // Windows присылает пачку уведомлений на одно изменение — перечитываем один раз.
        if (_refreshScheduled || !_expanded) return;
        _refreshScheduled = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshScheduled = false;
            Refresh();
        });
    }

    private void Refresh() => Run(() =>
    {
        var audio = _settings.Settings.Audio;
        Fill(Outputs, DataFlow.Render, audio.FavoriteOutputs, "Glyph.Speaker");
        Fill(Inputs, DataFlow.Capture, audio.FavoriteInputs, "Glyph.Microphone");
        State.CallModeAvailable = _devices.Find(DataFlow.Render, audio.CallMode?.Output) != null
                                  || _devices.Find(DataFlow.Capture, audio.CallMode?.Input) != null;
    });

    /// <summary>Строки избранных устройств; текущее отмечено, отключённые — неактивны.</summary>
    private void Fill(ObservableCollection<DeviceItem> items, DataFlow flow, List<string> favorites, string glyphKey)
    {
        var active = _devices.Active(flow);
        string? current = _devices.DefaultId(flow, Role.Console);
        string glyph = (string)Application.Current.Resources[glyphKey];

        items.Clear();
        foreach (string favorite in favorites)
        {
            string? id = active.FirstOrDefault(d => d.Name.Contains(favorite, StringComparison.OrdinalIgnoreCase)).Id;
            items.Add(new DeviceItem(favorite, glyph, id, id != null && id == current));
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Звук: не удалось прочитать или переключить устройства.", ex);
        }
    }

    public void Dispose()
    {
        _devices.Dispose();
        Mixer.Dispose();
    }
}
