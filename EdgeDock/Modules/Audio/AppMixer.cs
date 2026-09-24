using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using EdgeDock.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EdgeDock.Modules.Audio;

/// <summary>Строка микшера: программа, которая сейчас звучит.</summary>
public sealed class AppVolume : INotifyPropertyChanged
{
    private readonly Action<AppVolume> _apply;
    private double _level;
    private bool _isMuted;

    internal AppVolume(int processId, string name, ImageSource? icon, Action<AppVolume> apply)
    {
        ProcessId = processId;
        Name = name;
        Icon = icon;
        _apply = apply;
    }

    public int ProcessId { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }

    /// <summary>Громкость программы 0…100. Установка из интерфейса сразу меняет её в Windows.</summary>
    public double Level
    {
        get => _level;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (value == _level) return;
            _level = value;
            Changed();
            Changed(nameof(Glyph));
            if (!Syncing) _apply(this);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (value == _isMuted) return;
            _isMuted = value;
            Changed();
            Changed(nameof(Glyph));
            if (!Syncing) _apply(this);
        }
    }

    public string Glyph => VolumeGlyphs.For(Level, IsMuted);

    /// <summary>Значения пришли из Windows — применять их обратно не нужно.</summary>
    internal bool Syncing { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// Микшер: громкость программ, которые звучат прямо сейчас, на текущем устройстве вывода.
/// Только события Core Audio (новая сессия, звук начался или закончился, громкость поменяли где-то ещё), без опроса.
/// Подключается только пока панель развёрнута. Несколько сессий одной программы (например, вкладки браузера) — одна строка.
/// </summary>
internal sealed class AppMixer : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly List<Tracked> _tracked = [];
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private MMDevice? _device;
    private bool _rebuildScheduled, _syncScheduled;

    private sealed record Tracked(AudioSessionControl Control, SessionEvents Events, int ProcessId);

    public AppMixer(Dispatcher ui) => _ui = ui;

    /// <summary>Звучащие программы. Меняется только в потоке интерфейса.</summary>
    public ObservableCollection<AppVolume> Apps { get; } = [];

    public void Attach()
    {
        if (_enumerator != null) return;
        Run(() =>
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: true);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) BindDevice();
            };
            BindDevice();
        });
    }

    public void Detach()
    {
        ReleaseDevice();
        Apps.Clear();
        _notifications?.Dispose();
        _notifications = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void BindDevice()
    {
        ReleaseDevice();
        if (_enumerator == null || !_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device == null)
        {
            Apps.Clear();
            return;
        }
        _device = device;
        _device.AudioSessionManager.OnSessionCreated += OnSessionCreated;
        Rebuild();
    }

    private void ReleaseDevice()
    {
        UntrackAll();
        if (_device == null) return;
        Run(() => _device.AudioSessionManager.OnSessionCreated -= OnSessionCreated);
        _device.Dispose();
        _device = null;
    }

    // Уведомления Core Audio приходят из его потоков — перечитываем всё в потоке интерфейса, одной пачкой.
    private void OnSessionCreated(object sender, AudioSessionControl newSession) => ScheduleRebuild();

    /// <summary>Сессия появилась, зазвучала, замолчала или закрылась — перечитать список.</summary>
    internal void ScheduleRebuild()
    {
        if (_rebuildScheduled) return;
        _rebuildScheduled = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _rebuildScheduled = false;
            Rebuild();
        });
    }

    /// <summary>Громкость поменяли (в том числе мы сами) — достаточно обновить числа в строках.</summary>
    internal void ScheduleSync()
    {
        if (_syncScheduled) return;
        _syncScheduled = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _syncScheduled = false;
            Run(SyncApps);
        });
    }

    /// <summary>Перечитать сессии устройства и обновить список: строки, которые остались, не пересоздаются.</summary>
    private void Rebuild()
    {
        if (_device == null) return;
        Run(() =>
        {
            UntrackAll();
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions(); // заодно (пере)подписывает на новые сессии
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var control = sessions[i]; // новый объект при каждом обращении — освобождаем сами
                var events = new SessionEvents(this);
                control.RegisterEventClient(events);
                _tracked.Add(new Tracked(control, events, (int)control.GetProcessID));
            }
            SyncApps();
        });
    }

    /// <summary>Строки — по программам, у которых есть активная (звучащая) сессия; громкость — из их сессий.</summary>
    private void SyncApps()
    {
        var active = _tracked
            .Where(IsActive)
            .GroupBy(t => t.ProcessId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var app in Apps.Where(a => !active.ContainsKey(a.ProcessId)).ToList()) Apps.Remove(app);

        foreach (var (processId, sessions) in active)
        {
            var app = Apps.FirstOrDefault(a => a.ProcessId == processId);
            if (app == null)
            {
                var (name, icon) = Describe(sessions[0].Control, processId);
                app = new AppVolume(processId, name, icon, Apply);
                Apps.Add(app);
            }

            var volume = sessions[0].Control.SimpleAudioVolume;
            app.Syncing = true;
            app.Level = volume.Volume * 100;
            app.IsMuted = volume.Mute;
            app.Syncing = false;
        }
    }

    private static bool IsActive(Tracked t)
    {
        try
        {
            return t.Control.State == AudioSessionState.AudioSessionStateActive;
        }
        catch (Exception)
        {
            return false; // сессия закрылась прямо сейчас — следующее уведомление уберёт её из списка
        }
    }

    /// <summary>Громкость или «без звука» из интерфейса — во все сессии программы.</summary>
    private void Apply(AppVolume app) => Run(() =>
    {
        foreach (var t in _tracked.Where(t => t.ProcessId == app.ProcessId))
        {
            t.Control.SimpleAudioVolume.Volume = (float)(app.Level / 100);
            t.Control.SimpleAudioVolume.Mute = app.IsMuted;
        }
    });

    /// <summary>Название и значок: «Системные звуки», описание exe («Yandex», «Discord») или имя процесса.</summary>
    private static (string Name, ImageSource? Icon) Describe(AudioSessionControl control, int processId)
    {
        if (control.IsSystemSoundsSession) return ("Системные звуки", null);
        try
        {
            using var process = Process.GetProcessById(processId);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Процесс другого пользователя или защищённый — обойдёмся без значка.
            }

            string name = path != null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description
                ? description
                : process.ProcessName;
            return (name, path != null ? ShellIcons.Get(path) : null);
        }
        catch (ArgumentException)
        {
            return (string.IsNullOrWhiteSpace(control.DisplayName) ? "Программа" : control.DisplayName, null);
        }
    }

    private void UntrackAll()
    {
        foreach (var t in _tracked)
        {
            Run(() => t.Control.UnRegisterEventClient(t.Events));
            t.Control.Dispose();
        }
        _tracked.Clear();
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Микшер: не удалось прочитать или изменить громкость программ.", ex);
        }
    }

    public void Dispose() => Detach();

    /// <summary>События одной сессии (приходят из потоков Core Audio).</summary>
    private sealed class SessionEvents(AppMixer mixer) : IAudioSessionEventsHandler
    {
        public void OnVolumeChanged(float volume, bool isMuted) => mixer.ScheduleSync();
        public void OnStateChanged(AudioSessionState state) => mixer.ScheduleRebuild();
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => mixer.ScheduleRebuild();
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
    }
}
