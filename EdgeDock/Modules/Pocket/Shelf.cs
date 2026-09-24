using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pocket;

/// <summary>
/// Полка: файлы, которые положили «на потом». Для обычных файлов хранится ссылка,
/// для данных без пути — наша копия в %LocalAppData%\EdgeDock\shelf. Содержимое живёт в state.json.
/// </summary>
internal sealed class Shelf
{
    private readonly SettingsService _settings;
    private readonly Dispatcher _ui;
    private bool _iconsWanted;

    /// <summary>Элементы, новые сверху. Меняется только в потоке интерфейса.</summary>
    public ObservableCollection<ShelfItem> Items { get; } = [];

    public Shelf(SettingsService settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        foreach (var entry in settings.State.Shelf) Items.Add(new ShelfItem(entry));
    }

    public void Add(IEnumerable<string> paths, bool owned)
    {
        int index = 0;
        foreach (string path in paths)
        {
            if (Items.Any(i => string.Equals(i.Entry.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ShelfItem(new ShelfEntry { Path = path, Owned = owned });
            Items.Insert(index++, item);
            if (_iconsWanted) LoadIcon(item);
        }
        Save();
    }

    /// <summary>
    /// Убрать с полки. Наши копии файлов не удаляются сразу: получатель (например, Telegram)
    /// может читать файл уже после того, как его бросили. Их подчищает <see cref="DeleteOrphans"/> при запуске.
    /// </summary>
    public void Remove(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.ToList()) Items.Remove(item);
        Save();
    }

    /// <summary>Панель развернулась: проверить, живы ли файлы, и загрузить значки.</summary>
    public void LoadIcons()
    {
        _iconsWanted = true;
        foreach (var item in Items) LoadIcon(item);
    }

    /// <summary>Панель свернулась: значки выгружаются, выделение снимается.</summary>
    public void UnloadIcons()
    {
        _iconsWanted = false;
        foreach (var item in Items)
        {
            item.Icon = null;
            item.IsSelected = false;
        }
    }

    private void LoadIcon(ShelfItem item)
    {
        item.IsMissing = !item.Exists;
        if (item.IsMissing || !Thumbnails.IsImage(item.Entry.Path))
        {
            item.Icon = Thumbnails.ShellIcon(item.Entry.Path, exists: !item.IsMissing);
            return;
        }

        // Картинки — миниатюрой, в фоне.
        Task.Run(() =>
        {
            var thumbnail = Thumbnails.Get(item.Entry.Path);
            _ui.BeginInvoke(() =>
            {
                if (_iconsWanted) item.Icon = thumbnail;
            });
        });
    }

    /// <summary>
    /// При запуске: удалить из папки полки наши копии, которых на полке уже нет.
    /// Если state.json не прочитался, ничего не трогаем — иначе удалили бы всё.
    /// </summary>
    public static void DeleteOrphans(SettingsService settings)
    {
        if (!settings.StateIsTrusted || !Directory.Exists(AppPaths.Shelf)) return;
        try
        {
            var kept = settings.State.Shelf
                .Where(e => e.Owned)
                .Select(e => Path.GetFullPath(e.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(AppPaths.Shelf))
            {
                if (!kept.Contains(Path.GetFullPath(file))) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось почистить папку полки.", ex);
        }
    }

    private void Save()
    {
        _settings.State.Shelf = Items.Select(i => i.Entry).ToList();
        _settings.SaveState();
    }
}
