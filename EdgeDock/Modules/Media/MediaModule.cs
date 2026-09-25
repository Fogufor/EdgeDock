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
    private bool _hasSession, _isPlaying, _canPrevious, _canPlayPause, _canNext, _canSeek, _canLike, _isLiked;
    private string _title = "", _artist = "";
    private double _position, _duration;
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

    /// <summary>Позиция в секундах. Меняет и интерфейс перемотки (ползунок), и виджет раз в секунду.</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (!Set(ref _position, value)) return;
            PropertyChanged?.Invoke(this, new(nameof(ElapsedText)));
            PropertyChanged?.Invoke(this, new(nameof(RemainingText)));
        }
    }

    /// <summary>Длина трека в секундах; 0 — плеер её не сообщает, полоски времени нет.</summary>
    public double Duration
    {
        get => _duration;
        set
        {
            if (!Set(ref _duration, value)) return;
            PropertyChanged?.Invoke(this, new(nameof(HasTimeline)));
            PropertyChanged?.Invoke(this, new(nameof(RemainingText)));
        }
    }

    public bool HasTimeline => Duration > 0;
    public string ElapsedText => Format(Position);
    public string RemainingText => "−" + Format(Math.Max(0, Duration - Position));

    public bool CanSeek { get => _canSeek; set => Set(ref _canSeek, value); }

    /// <summary>Лайк есть только у Яндекс Музыки: в системной медиа-сессии Windows оценок нет.</summary>
    public bool CanLike { get => _canLike; set => Set(ref _canLike, value); }

    public bool IsLiked { get => _isLiked; set => Set(ref _isLiked, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }
}

/// <summary>
/// Музыка. Два источника, оба только по событиям, без опроса:
/// медиа-сессия Windows (Spotify, Chrome, Edge, плееры) и Яндекс Браузер через наше расширение
/// (он сам Windows о воспроизведении не сообщает). Показывается тот, где играет;
/// если нигде не играет — медиа-сессия Windows, если она есть. Ничего нет — блок скрыт.
/// Позицию трека источники сообщают только при перемотке, паузе и смене трека — между этим
/// виджет досчитывает её сам, раз в секунду и только пока панель развёрнута и трек играет.
/// Id в settings.json — "media".
/// </summary>
internal sealed class MediaModule : IDockModule, IDisposable
{
    /// <summary>Ширина декодирования обложки в пикселях: хватает для 48 DIP при масштабе до 200%.</summary>
    private const int CoverDecodeWidth = 96;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private enum Source { None, System, Browser }

    /// <summary>Трек источника. Position — в секундах на момент PositionAt (UTC).</summary>
    private sealed record Track(string Title, string Artist, bool IsPlaying, bool CanPrevious, bool CanPlayPause, bool CanNext,
        double Position, DateTime PositionAt, double Duration, bool CanSeek, bool Liked, bool CanLike)
    {
        public static readonly Track Empty = new("", "", false, false, false, false, 0, DateTime.UtcNow, 0, false, false, false);

        /// <summary>Где трек сейчас: последняя известная позиция плюс сколько прошло, если играет.</summary>
        public double PositionNow()
        {
            double position = Position + (IsPlaying ? (DateTime.UtcNow - PositionAt).TotalSeconds : 0);
            return Duration > 0 ? Math.Clamp(position, 0, Duration) : Math.Max(0, position);
        }
    }

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly BrowserMusicBridge _browser;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };

    // Источник 1: медиа-сессия Windows.
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Track? _system;
    private TimeSpan _systemStart; // начало шкалы времени плеера — позиции у него считаются от него
    private bool _systemCanSeek;
    private IRandomAccessStreamReference? _systemCover;
    private int _propertiesVersion;

    // Источник 2: Яндекс Браузер.
    private Track? _browserTrack;
    private string _browserArtwork = "";

    private Source _shown;
    private Track? _shownTrack;
    private object? _coverKey; // чья обложка показана: ссылка на поток Windows или адрес картинки из браузера
    private int _coverVersion;
    private bool _expanded, _suspended, _disposed, _seeking;

    public MediaState State { get; } = new();

    /// <summary>Общая громкость Windows — полоска под треком.</summary>
    public SystemVolume Volume { get; }

    public MediaModule()
    {
        Volume = new SystemVolume(_ui);
        _ticker.Tick += (_, _) => UpdatePosition();
        _browser = new BrowserMusicBridge(_ui);
        _browser.Changed += OnBrowserTrack;
        _browser.Start();
        _ = ConnectAsync();
    }

    public string Id => "media";

    public string Title => "Музыка";

    public string TabGlyph => "Glyph.Music";

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
        Volume.Attach();
        UpdatePosition();
        UpdateTicker();
        _ = LoadCoverAsync(++_coverVersion);
    }

    /// <summary>Панель свернулась — обложка выгружается, громкость отключается, секундомер стоит.</summary>
    public void OnCollapsed()
    {
        _expanded = false;
        State.Cover = null;
        Volume.Detach();
        UpdateTicker();
    }

    public void Suspend()
    {
        _suspended = true;
        Volume.Detach();
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        AttachSession(null);
        _browser.Stop();
        UpdateTicker();
    }

    public void Resume()
    {
        _suspended = false;
        _browser.Start();
        if (_manager == null) return;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        AttachSession(_manager.GetCurrentSession());
    }

    // ----- Кнопки: уходят туда, чей трек сейчас показан -----

    public void Previous() => Command("previous", s => s.TrySkipPreviousAsync());

    public void PlayPause() => Command("playPause", s => s.TryTogglePlayPauseAsync());

    public void Next() => Command("next", s => s.TrySkipNextAsync());

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

    /// <summary>Пользователь взялся за ползунок времени — не двигаем его, пока не отпустит.</summary>
    public void BeginSeek() => _seeking = true;

    /// <summary>Перемотать на seconds от начала трека.</summary>
    public async void Seek(double seconds)
    {
        _seeking = false;
        if (_shown == Source.Browser && _browserTrack != null)
        {
            _browser.Send("seek", seconds);
            _browserTrack = _browserTrack with { Position = seconds, PositionAt = DateTime.UtcNow };
            Publish();
            return;
        }
        if (_shown != Source.System || _session == null || _system == null) return;

        _system = _system with { Position = seconds, PositionAt = DateTime.UtcNow };
        Publish();
        try
        {
            await _session.TryChangePlaybackPositionAsync((_systemStart + TimeSpan.FromSeconds(seconds)).Ticks);
        }
        catch (Exception ex)
        {
            Log.Error("Музыка: плеер не дал перемотать.", ex);
        }
    }

    /// <summary>Лайк в Яндекс Музыке: расширение нажимает «Нравится» на странице — лайк сохраняется в аккаунте.</summary>
    public void ToggleLike()
    {
        if (_shown != Source.Browser || _browserTrack == null) return;
        _browser.Send("like");
        _browserTrack = _browserTrack with { Liked = !_browserTrack.Liked }; // сразу, не дожидаясь ответа страницы
        Publish();
    }

    // ----- Выбор источника и показ -----

    private void Publish()
    {
        bool systemPlaying = _system?.IsPlaying == true;
        bool browserPlaying = _browserTrack?.IsPlaying == true;
        var source = browserPlaying && !systemPlaying ? Source.Browser
            : _system != null ? Source.System
            : _browserTrack != null ? Source.Browser
            : Source.None;
        var track = source switch
        {
            Source.System => _system,
            Source.Browser => _browserTrack,
            _ => null,
        };

        _shownTrack = track;
        State.HasSession = track != null;
        State.Title = track?.Title ?? "";
        State.Artist = track?.Artist ?? "";
        State.IsPlaying = track?.IsPlaying ?? false;
        State.CanPrevious = track?.CanPrevious ?? false;
        State.CanPlayPause = track?.CanPlayPause ?? false;
        State.CanNext = track?.CanNext ?? false;
        State.Duration = track?.Duration ?? 0;
        State.CanSeek = track?.CanSeek ?? false;
        State.CanLike = track?.CanLike ?? false;
        State.IsLiked = track?.Liked ?? false;
        UpdatePosition();
        UpdateTicker();

        object? coverKey = source switch
        {
            Source.System => _systemCover,
            Source.Browser => _browserArtwork,
            _ => null,
        };
        if (source == _shown && Equals(coverKey, _coverKey)) return;
        _shown = source;
        _coverKey = coverKey;
        State.Cover = null;
        _ = LoadCoverAsync(++_coverVersion);
    }

    private void UpdatePosition()
    {
        if (_seeking) return;
        State.Position = _shownTrack?.PositionNow() ?? 0;
    }

    /// <summary>Секундомер идёт, только когда его видно и есть что отсчитывать.</summary>
    private void UpdateTicker()
    {
        bool needed = _expanded && !_suspended && State.IsPlaying && State.HasTimeline;
        if (needed && !_ticker.IsEnabled) _ticker.Start();
        else if (!needed && _ticker.IsEnabled) _ticker.Stop();
    }

    // ----- Яндекс Браузер -----

    private void OnBrowserTrack(BrowserMusicBridge.Track? track)
    {
        if (track == null)
        {
            _browserTrack = null;
            _browserArtwork = "";
        }
        else
        {
            var at = track.PositionAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(track.PositionAt).UtcDateTime : DateTime.UtcNow;
            _browserTrack = new Track(track.Title, track.Artist, track.Playing, track.CanPrevious, track.CanPlayPause, track.CanNext,
                track.Position, at, track.Duration, track.CanSeek && track.Duration > 0, track.Liked, track.CanLike);
            _browserArtwork = track.Artwork;
        }
        Publish();
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

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ui.BeginInvoke(() => { if (sender == _session) RefreshTimeline(); });

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = session;
        _system = session == null ? null : Track.Empty with { PositionAt = DateTime.UtcNow };
        _systemCover = null;
        _systemCanSeek = false;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            RefreshPlayback();
            RefreshTimeline();
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
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _systemCanSeek = info.Controls.IsPlaybackPositionEnabled;
            _system = _system with
            {
                // Пауза или старт: запоминаем, где трек сейчас, чтобы дальше считать от этого места.
                Position = _system.PositionNow(),
                PositionAt = DateTime.UtcNow,
                IsPlaying = playing,
                CanPrevious = info.Controls.IsPreviousEnabled,
                CanPlayPause = info.Controls.IsPlayPauseToggleEnabled,
                CanNext = info.Controls.IsNextEnabled,
                CanSeek = _systemCanSeek && _system.Duration > 0,
            };
            Publish();
        }
        catch (Exception ex)
        {
            // Сессия могла закрыться прямо сейчас — это не ошибка, следующее событие всё поправит.
            Log.Warn($"Музыка: не удалось прочитать состояние плеера ({ex.Message}).");
        }
    }

    /// <summary>Шкала времени плеера: длина трека и позиция на момент, когда плеер её сообщил.</summary>
    private void RefreshTimeline()
    {
        if (_session == null || _system == null) return;
        try
        {
            var timeline = _session.GetTimelineProperties();
            _systemStart = timeline.StartTime;
            double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
            var updated = timeline.LastUpdatedTime.UtcDateTime;
            _system = _system with
            {
                Duration = duration > 0 ? duration : 0,
                Position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds),
                // Некоторые плееры не заполняют время обновления — тогда считаем, что позиция свежая.
                PositionAt = updated.Year > 2000 && updated <= DateTime.UtcNow ? updated : DateTime.UtcNow,
                CanSeek = _systemCanSeek && duration > 0,
            };
            Publish();
        }
        catch (Exception ex)
        {
            Log.Warn($"Музыка: не удалось прочитать позицию трека ({ex.Message}).");
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
        if (address.Length == 0) return null;
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
        _ticker.Stop();
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        AttachSession(null);
        _browser.Dispose();
        Volume.Dispose();
    }
}
