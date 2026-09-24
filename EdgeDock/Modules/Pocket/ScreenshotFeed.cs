using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pocket;

/// <summary>
/// Последние снимки экрана, новые первыми. Новые файлы приходят через FileSystemWatcher — без опроса.
/// Миниатюры загружаются только пока панель развёрнута.
/// </summary>
internal sealed class ScreenshotFeed : IDisposable
{
    private const int MaxItems = 20;

    // Недописанный файл: повторяем попытку прочитать несколько раз с паузой.
    private const int ReadAttempts = 15;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Dispatcher _ui;
    private readonly FileSystemWatcher? _watcher;
    private bool _thumbnailsWanted, _disposed;

    public string Folder { get; }

    /// <summary>Снимки, новые первыми. Меняется только в потоке интерфейса.</summary>
    public ObservableCollection<ShotItem> Items { get; } = [];

    public ScreenshotFeed(string folder, Dispatcher ui)
    {
        _ui = ui;
        Folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName };
            _watcher.Created += (_, e) => OnAdded(e.FullPath);
            _watcher.Deleted += (_, e) => OnRemoved(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                OnRemoved(e.OldFullPath);
                OnAdded(e.FullPath);
            };
            _watcher.Error += (_, e) =>
            {
                Log.Error("Слежение за папкой снимков прервалось, перечитываю папку.", e.GetException());
                Rescan();
            };
            Rescan();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Папка снимков недоступна: {folder}", ex);
        }
    }

    /// <summary>Полноэкранный режим: не следить за папкой.</summary>
    public void Suspend()
    {
        if (_watcher != null) _watcher.EnableRaisingEvents = false;
    }

    /// <summary>После полноэкранного режима: подхватить то, что появилось за это время.</summary>
    public void Resume()
    {
        if (_watcher == null) return;
        Rescan();
        _watcher.EnableRaisingEvents = true;
    }

    public void LoadThumbnails()
    {
        _thumbnailsWanted = true;
        var pending = Items.Where(i => i.Thumbnail == null).ToList();
        if (pending.Count > 0) Task.Run(() => pending.ForEach(LoadThumbnail));
    }

    /// <summary>Панель свернулась: превью выгружаются из памяти.</summary>
    public void UnloadThumbnails()
    {
        _thumbnailsWanted = false;
        foreach (var item in Items) item.Thumbnail = null;
    }

    /// <summary>Фоновый поток: миниатюра (из кэша или новая) и показ, если панель всё ещё развёрнута.</summary>
    private void LoadThumbnail(ShotItem item)
    {
        var thumbnail = Thumbnails.Get(item.Path);
        _ui.BeginInvoke(() =>
        {
            if (_thumbnailsWanted && Items.Contains(item)) item.Thumbnail = thumbnail;
        });
    }

    private void Rescan()
    {
        Task.Run(() =>
        {
            List<string> latest;
            try
            {
                latest = new DirectoryInfo(Folder).EnumerateFiles()
                    .Where(f => Thumbnails.IsImage(f.Name))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Take(MaxItems)
                    .Select(f => f.FullName)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось прочитать папку снимков: {Folder}", ex);
                return;
            }

            _ui.BeginInvoke(() =>
            {
                Items.Clear();
                foreach (string path in latest) Items.Add(new ShotItem(path));
                if (_thumbnailsWanted) LoadThumbnails();
            });
        });
    }

    private void OnAdded(string path)
    {
        if (!Thumbnails.IsImage(path)) return;

        Task.Run(async () =>
        {
            // Снимок мог ещё записываться, а некоторые программы сначала создают пустой файл
            // и дописывают его позже. Ждём, пока файл читается и его размер перестал меняться.
            long lastSize = -1;
            for (int attempt = 0; attempt < ReadAttempts; attempt++)
            {
                long size = ReadableSize(path);
                if (size > 0 && size == lastSize) break;
                lastSize = size;
                await Task.Delay(ReadRetryDelay);
            }
            if (_disposed || ReadableSize(path) <= 0) return;

            // Миниатюру готовим сразу, чтобы при следующем разворачивании она просто читалась из кэша.
            // Если файл всё же был недописан — одна повторная попытка, и только потом ошибка в лог.
            var thumbnail = Thumbnails.Get(path, logErrors: false);
            if (thumbnail == null)
            {
                await Task.Delay(ReadRetryDelay * 3);
                thumbnail = Thumbnails.Get(path);
            }

            await _ui.BeginInvoke(() =>
            {
                if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;
                var item = new ShotItem(path) { Thumbnail = _thumbnailsWanted ? thumbnail : null };
                Items.Insert(0, item);
                while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
            });
        });
    }

    private void OnRemoved(string path)
    {
        _ui.BeginInvoke(() =>
        {
            var item = Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item != null) Items.Remove(item);
        });
    }

    /// <summary>Размер файла, если его уже можно прочитать целиком; иначе -1.</summary>
    private static long ReadableSize(string path)
    {
        try
        {
            // Открыть без разделения записи получится, только когда программа-скриншотер закрыла файл.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return file.Length;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
    }
}
