using System.IO;
using System.Windows;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pocket;

/// <summary>Карман: лента снимков экрана + полка. Id в settings.json — "pocket".</summary>
internal sealed class PocketModule : IDockModule, IDisposable
{
    private readonly SettingsService _settings;
    private readonly ScreenshotFeed _feed;
    private readonly Shelf _shelf;

    public PocketModule(SettingsService settings)
    {
        _settings = settings;
        var ui = Dispatcher.CurrentDispatcher;
        _feed = new ScreenshotFeed(ScreenshotsFolder(settings.Settings.Pocket), ui);
        _shelf = new Shelf(settings, ui);

        Shelf.DeleteOrphans(settings);
        Task.Run(Thumbnails.CleanCache);
    }

    public string Id => "pocket";

    public string Title => "Карман";

    public string TabGlyph => "Glyph.Pictures";

    public bool HasAttention => false;

    public event EventHandler? AttentionChanged
    {
        add { }
        remove { }
    }

    public FrameworkElement CreateView() => new PocketView(_feed, _shelf, _settings);

    public void OnExpanded()
    {
        _feed.LoadThumbnails();
        _shelf.LoadIcons();
    }

    public void OnCollapsed()
    {
        _feed.UnloadThumbnails();
        _shelf.UnloadIcons();
    }

    public void Suspend() => _feed.Suspend();

    public void Resume() => _feed.Resume();

    public void Dispose() => _feed.Dispose();

    /// <summary>Папка из настроек или системная «Снимки экрана».</summary>
    private static string ScreenshotsFolder(PocketSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ScreenshotsFolder))
            return Environment.ExpandEnvironmentVariables(settings.ScreenshotsFolder);

        return Native.KnownFolder(Native.FOLDERID_Screenshots)
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }
}
