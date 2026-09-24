using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EdgeDock.Core;
using Windows.Foundation;
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
/// Музыка. Два источника, оба только по событиям, без опроса:
/// медиа-сессия Windows (Spotify, Chrome, Edge, плееры) и Яндекс Браузер через наше расширение
/// (он сам Windows о воспроизведении не сообщает). Показывается тот, где играет;
/// если нигде не играет — медиа-сессия Windows, если она есть. Ничего нет — блок скрыт.
/// Id в settings.json — "media".
/// </summary>
internal sealed class MediaModule : IDockModule, IDisposable
{
    /// <summary>Ширина декодирования обложки в пикселях: хватает для 48 DIP при масштабе до 200%.</summary>
    private const int CoverDecodeWidth = 96;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private enum Source { None, System, Browser }

    private sealed record Track(string Title, string Artist, bool IsPlaying, bool CanPrevious, bool CanPlayPause, bool CanNext)
    {
        public static readonly Track Empty = new("", "", false, false, false, false);
    }

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly BrowserMusicBridge _browser;

    // Источник 1: медиа-сессия Windows.
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Track? _system;
    private IRandomAccessStreamReference? _systemCover;
    private int _propertiesVersion;

    // Источник 2: Яндекс Браузер.
    private BrowserMusicBridge.Track? _browserTrack;

    private Source _shown;
    private object? _coverKey; // чья обложка показана: ссылка на поток Windows или адрес картинки из браузера
    private int _coverVersion;
    private bool _expanded, _suspended, _disposed;

    public MediaState State { get; } = new();

    public MediaModule()
    {
        _browser = new BrowserMusicBridge(_ui);
        _browser.Changed += track =>
        {
            _browserTrack = track;
            Publish();
        };
        _browser.Start();
        _ = ConnectAsync();
    }

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
        _ = LoadCoverAsync(++_coverVersion);
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
        _browser.Stop();
    }

    public void Resume()
    {
        _suspended = false;
        _browser.Start();
        if (_manager == null) return;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        AttachSession(_manager.GetCurrentSession());
    }

    public void Previous() => Command("previous", s => s.TrySkipPreviousAsync());

    public void PlayPause() => Command("playPause", s => s.TryTogglePlayPauseAsync());

    public void Next() => Command("next", s => s.TrySkipNextAsync());

    /// <summary>Кнопка идёт туда, чей трек сейчас показан.</summary>
    private async void Command(string browserCommand, Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> systemCommand)
    {
        if (_shown == Source.Browser)
        {
            _browser.Send(browserCommand);
            return;
        }
        if (_shown != Source.System || _session == null) return;
        try
        {
            await systemCommand(_session);
        }
        catch (Exception ex)
        {
            Log.Error("Музыка: команда плееру не прошла.", ex);
        }
    }

    /// <summary>Выбрать источник и показать его трек.</summary>
    private void Publish()
    {
        bool systemPlaying = _system?.IsPlaying == true;
        bool browserPlaying = _browserTrack?.Playing == true;
        var source = browserPlaying && !systemPlaying ? Source.Browser
            : _system != null ? Source.System
            : _browserTrack != null ? Source.Browser
            : Source.None;

        var track = source switch
        {
            Source.System => _system,
            Source.Browser => new Track(_browserTrack!.Title, _browserTrack.Artist, _browserTrack.Playing,
                _browserTrack.CanPrevious, _browserTrack.CanPlayPause, _browserTrack.CanNext),
            _ => null,
        };

        State.HasSession = track != null;
        State.Title = track?.Title ?? "";
        State.Artist = track?.Artist ?? "";
        State.IsPlaying = track?.IsPlaying ?? false;
        State.CanPrevious = track?.CanPrevious ?? false;
        State.CanPlayPause = track?.CanPlayPause ?? false;
        State.CanNext = track?.CanNext ?? false;

        object? coverKey = source switch
        {
            Source.System => _systemCover,
            Source.Browser => _browserTrack!.Artwork,
            _ => null,
        };
        if (source == _shown && Equals(coverKey, _coverKey)) return;
        _shown = source;
        _coverKey = coverKey;
        State.Cover = null;
        _ = LoadCoverAsync(++_coverVersion);
    }

    // ----- Медиа-сессия Windows -----

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
        _system = session == null ? null : Track.Empty;
        _systemCover = null;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            RefreshPlayback();
            _ = RefreshPropertiesAsync();
        }
        Publish();
    }

    private void RefreshPlayback()
    {
        if (_session == null || _system == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            _system = _system with
            {
                IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPrevious = info.Controls.IsPreviousEnabled,
                CanPlayPause = info.Controls.IsPlayPauseToggleEnabled,
                CanNext = info.Controls.IsNextEnabled,
            };
            Publish();
        }
        catch (Exception ex)
        {
            // Сессия могла закрыться прямо сейчас — это не ошибка, следующее событие всё поправит.
            Log.Warn($"Музыка: не удалось прочитать состояние плеера ({ex.Message}).");
        }
    }

    private async Task RefreshPropertiesAsync()
    {
        int version = ++_propertiesVersion;
        var session = _session;
        if (session == null) return;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _propertiesVersion || session != _session || _system == null) return; // пока ждали, всё сменилось
            _system = _system with { Title = properties.Title ?? "", Artist = properties.Artist ?? "" };
            _systemCover = properties.Thumbnail;
            Publish();
        }
        catch (Exception ex)
        {
            Log.Warn($"Музыка: не удалось прочитать название трека ({ex.Message}).");
        }
    }

    // ----- Обложка -----

    /// <summary>Обложка декодируется сразу в маленький размер и только пока панель развёрнута.</summary>
    private async Task LoadCoverAsync(int version)
    {
        if (!_expanded || _coverKey == null || State.Cover != null) return;
        try
        {
            byte[]? bytes = _coverKey switch
            {
                IRandomAccessStreamReference reference => await ReadAsync(reference),
                string address => await DownloadAsync(address),
                _ => null,
            };
            if (bytes == null || bytes.Length == 0 || version != _coverVersion || !_expanded) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = CoverDecodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            State.Cover = image;
        }
        catch (Exception ex)
        {
            Log.Warn($"Музыка: не удалось загрузить обложку ({ex.Message}).");
        }
    }

    private static async Task<byte[]> ReadAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        var memory = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(memory);
        return memory.ToArray();
    }

    /// <summary>Обложка из браузера: https-адрес картинки или data:-адрес. Другие адреса не открываем.</summary>
    private static async Task<byte[]?> DownloadAsync(string address)
    {
        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = address.IndexOf(',');
            return comma > 0 && address[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(address[(comma + 1)..])
                : null;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return await Http.GetByteArrayAsync(uri);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        AttachSession(null);
        _browser.Dispose();
    }
}
