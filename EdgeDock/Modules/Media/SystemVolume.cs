using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using EdgeDock.Core;
using NAudio.CoreAudioApi;

namespace EdgeDock.Modules.Media;

/// <summary>
/// Общая громкость Windows на текущем устройстве вывода — для полоски в блоке музыки.
/// Подключается только пока панель развёрнута. Изменения извне (клавиши громкости, панель Windows)
/// приходят событием, без опроса; при смене устройства по умолчанию полоска переключается на новое.
/// </summary>
public sealed class SystemVolume : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _ui;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private MMDevice? _device;
    private double _level;
    private bool _isMuted, _isAvailable, _syncing;

    public SystemVolume(Dispatcher ui) => _ui = ui;

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => Set(ref _isAvailable, value);
    }

    /// <summary>Громкость 0…100. Установка из интерфейса сразу меняет громкость Windows.</summary>
    public double Level
    {
        get => _level;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _level, value)) return;
            OnPropertyChanged(nameof(Glyph));
            if (_syncing || _device == null) return;
            Run(() => _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(value / 100));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (Set(ref _isMuted, value)) OnPropertyChanged(nameof(Glyph));
        }
    }

    /// <summary>Значок динамика по уровню громкости, как в Windows 11.</summary>
    public string Glyph => VolumeGlyphs.For(Level, IsMuted);

    public void ToggleMute()
    {
        if (_device == null) return;
        Run(() => _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute);
    }

    /// <summary>Панель развернулась — подключиться к текущему устройству вывода.</summary>
    public void Attach()
    {
        if (_enumerator != null) return;
        Run(() =>
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: true);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) BindDefaultDevice();
            };
            BindDefaultDevice();
        });
    }

    /// <summary>Панель свернулась — отпустить устройство и уведомления.</summary>
    public void Detach()
    {
        ReleaseDevice();
        _notifications?.Dispose();
        _notifications = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void BindDefaultDevice()
    {
        ReleaseDevice();
        if (_enumerator == null || !_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device == null)
        {
            IsAvailable = false;
            return;
        }

        _device = device;
        _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        Sync(_device.AudioEndpointVolume.MasterVolumeLevelScalar, _device.AudioEndpointVolume.Mute);
        IsAvailable = true;
    }

    private void ReleaseDevice()
    {
        if (_device == null) return;
        Run(() => _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification);
        _device.Dispose();
        _device = null;
    }

    // Громкость поменяли где угодно (в том числе мы сами) — событие приходит из потока Core Audio.
    private void OnVolumeNotification(AudioVolumeNotificationData data) =>
        _ui.BeginInvoke(() => Sync(data.MasterVolume, data.Muted));

    private void Sync(float scalar, bool muted)
    {
        _syncing = true;
        Level = scalar * 100;
        IsMuted = muted;
        _syncing = false;
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Громкость: не удалось прочитать или изменить громкость Windows.", ex);
        }
    }

    public void Dispose() => Detach();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
}
