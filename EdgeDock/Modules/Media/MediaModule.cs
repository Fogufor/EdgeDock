using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EdgeDock.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace EdgeDock.Modules.Media;

/// <summary>Что сейчас играет — для привязки в интерфейсе.</summary>
public sealed class MediaState : INotifyPropertyChanged
{
    private bool _hasSession, _isPlaying, _canPrevious, _canPlayPause, _canNext;
    private string _title = "", _artist = "";
    private ImageSource? _cover;

    public bool HasSession { get => _hasSession; set => Set(ref _hasSession, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }

    /// <summary>Маленькая обложка; есть только пока панель развёрнута.</summary>
    public ImageSource? Cover { get => _cover; set => Set(ref _cover, value); }

    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public bool CanPrevious { get => _canPrevious; set => Set(ref _canPrevious, value); }
    public bool CanPlayPause { get => _canPlayPause; set => Set(ref _canPlayPause, value); }
    public bool CanNext { get => _canNext; set => Set(ref _canNext, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>
/// Музыка: текущая медиа-сессия Windows (Spotify, браузер, плеер). Только события, без опроса.
/// Нет сессии — блок скрыт. Id в settings.json — "media".
/// </summary>
internal sealed class MediaModule : IDockModule, IDisposable
{
    /// <summary>Ширина декодирования обложки в пикселях: хватает для 48 DIP при масштабе до 200%.</summary>
    private const int CoverDecodeWidth = 96;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private IRandomAccessStreamReference? _thumbnail;
    private bool _expanded, _suspended, _disposed;
    private int _version;

    public MediaState State { get; } = new();

    public MediaModule() => _ = ConnectAsync();

    public string Id => "media";

    public bool HasAttention => false;

    public event EventHandler? AttentionChanged
    {
        add { }
        remove { }
    }

    public FrameworkElement CreateView() => new MediaView(this);

    public void OnExpanded()
    {
        _expanded = true;
        _ = LoadCoverAsync(_version);
    }

    /// <summary>Панель свернулась — обложка выгружается.</summary>
    public void OnCollapsed()
    {
        _expanded = false;
        State.Cover = null;
    }

    public void Suspend()
    {
        _suspended = true;
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        AttachSession(null);
    }

    public void Resume()
    {
        _suspended = false;
        if (_manager == null) return;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        AttachSession(_manager.GetCurrentSession());
    }

    public async void Previous() => await Control(s => s.TrySkipPreviousAsync().AsTask());

    public async void PlayPause() => await Control(s => s.TryTogglePlayPauseAsync().AsTask());

    public async void Next() => await Control(s => s.TrySkipNextAsync().AsTask());

    private async Task Control(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        if (_session == null) return;
        try
        {
            await command(_session);
        }
        catch (Exception ex)
        {
            Log.Error("Музыка: команда плееру не прошла.", ex);
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed || _suspended) return;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSession(_manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            Log.Error("Музыка: не удалось подключиться к медиа-сессиям Windows.", ex);
        }
    }

    // События WinRT приходят из фоновых потоков — всё переносим в поток интерфейса.
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _ui.BeginInvoke(() => AttachSession(_suspended ? null : sender.GetCurrentSession()));

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ui.BeginInvoke(() => { if (sender == _session) _ = RefreshPropertiesAsync(); });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ui.BeginInvoke(() => { if (sender == _session) RefreshPlayback(); });

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;
        State.HasSession = session != null;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        RefreshPlayback();
        _ = RefreshPropertiesAsync();
    }

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            State.IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            State.CanPrevious = info?.Controls.IsPreviousEnabled ?? false;
            State.CanPlayPause = info?.Controls.IsPlayPauseToggleEnabled ?? false;
            State.CanNext = info?.Controls.IsNextEnabled ?? false;
        }
        catch (Exception ex)
        {
            // Сессия могла закрыться прямо сейчас — это не ошибка, следующее событие всё поправит.
            Log.Warn($"Музыка: не удалось прочитать состояние плеера ({ex.Message}).");
        }
    }

    private async Task RefreshPropertiesAsync()
    {
        int version = ++_version;
        var session = _session;
        if (session == null)
        {
            State.Title = State.Artist = "";
            State.Cover = null;
            _thumbnail = null;
            return;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return; // пока ждали, трек сменился ещё раз
            State.Title = properties.Title ?? "";
            State.Artist = properties.Artist ?? "";
            _thumbnail = properties.Thumbnail;
            State.Cover = null;
            await LoadCoverAsync(version);
        }
        catch (Exception ex)
        {
            Log.Warn($"Музыка: не удалось прочитать название трека ({ex.Message}).");
        }
    }

    /// <summary>Обложка декодируется сразу в маленький размер и только пока панель развёрнута.</summary>
    private async Task LoadCoverAsync(int version)
    {
        if (!_expanded || _thumbnail == null || State.Cover != null) return;
        try
        {
            using var stream = await _thumbnail.OpenReadAsync();
            var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = CoverDecodeWidth;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();

            if (_expanded && version == _version) State.Cover = image;
        }
        catch (Exception ex)
        {
            Log.Warn($"Музыка: не удалось загрузить обложку ({ex.Message}).");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        AttachSession(null);
    }
}
