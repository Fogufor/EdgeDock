using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pocket;

/// <summary>Интерфейс кармана: лента снимков, полка, перетаскивание внутрь и наружу.</summary>
public partial class PocketView : UserControl
{
    /// <summary>Метка «это перетаскивают с самой полки» — такое на полку обратно не кладём.</summary>
    private const string ShelfDragFormat = "EdgeDock.Shelf";

    private readonly Shelf _shelf;
    private readonly SettingsService _settings;
    private Point? _pressPoint;
    private ShelfItem? _anchor;

    internal PocketView(ScreenshotFeed feed, Shelf shelf, SettingsService settings)
    {
        InitializeComponent();
        _shelf = shelf;
        _settings = settings;
        Shots.ItemsSource = feed.Items;
        ShelfList.ItemsSource = shelf.Items;
        feed.Items.CollectionChanged += (_, _) => UpdateHints();
        shelf.Items.CollectionChanged += (_, _) => UpdateHints();
        UpdateHints();
    }

    private void UpdateHints()
    {
        ShotsEmpty.Visibility = Shots.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfEmpty.Visibility = ShelfList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Лента снимков -----

    /// <summary>Колесо мыши листает ленту по одному снимку.</summary>
    private void OnRibbonWheel(object sender, MouseWheelEventArgs e)
    {
        double step = (double)FindResource("Thumb.Width") + (double)FindResource("Space.S");
        double target = Math.Round(Ribbon.HorizontalOffset / step) * step - Math.Sign(e.Delta) * step;
        Ribbon.ScrollToHorizontalOffset(Math.Clamp(target, 0, Ribbon.ScrollableWidth));
        e.Handled = true;
    }

    private void OnShotDown(object sender, MouseButtonEventArgs e) => BeginPress(sender, e);

    private void OnShotMove(object sender, MouseEventArgs e)
    {
        if (!DragStarted(sender, e)) return;
        DragFiles((DependencyObject)sender, [ShotOf(sender).Path], fromShelf: false);
    }

    private void OnShotUp(object sender, MouseButtonEventArgs e)
    {
        if (EndPress(sender, e)) Start(ShotOf(sender).Path);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e) =>
        Start("explorer.exe", $"/select,\"{ShotOf(sender).Path}\"");

    // ----- Полка -----

    private void OnRowDown(object sender, MouseButtonEventArgs e)
    {
        var item = ShelfItemOf(sender);
        if (e.ClickCount == 2 && !item.IsMissing)
        {
            _pressPoint = null;
            Start(item.Entry.Path);
            e.Handled = true;
            return;
        }
        BeginPress(sender, e);
    }

    private void OnRowMove(object sender, MouseEventArgs e)
    {
        if (!DragStarted(sender, e)) return;
        var item = ShelfItemOf(sender);
        if (item.IsMissing) return;

        // Тянут невыделенный элемент — тянется он один; выделенный — все выделенные разом.
        if (!item.IsSelected) SelectOnly(item);
        var dragged = _shelf.Items.Where(i => i.IsSelected && !i.IsMissing).ToList();
        var effect = DragFiles((DependencyObject)sender, dragged.Select(i => i.Entry.Path).ToList(), fromShelf: true);
        if (effect != DragDropEffects.None && _settings.Settings.Pocket.ShelfRemoveAfterDrag) _shelf.Remove(dragged);
    }

    private void OnRowUp(object sender, MouseButtonEventArgs e)
    {
        if (!EndPress(sender, e)) return;
        var item = ShelfItemOf(sender);
        if (item.IsMissing)
        {
            _shelf.Remove([item]);
            return;
        }

        // Выделение как в Проводнике: клик — один, Ctrl — добавить/убрать, Shift — диапазон.
        bool ctrl = Native.IsKeyDown(Native.VK_CONTROL);
        if (Native.IsKeyDown(Native.VK_SHIFT) && _anchor != null && _shelf.Items.Contains(_anchor))
        {
            int from = _shelf.Items.IndexOf(_anchor), to = _shelf.Items.IndexOf(item);
            for (int i = 0; i < _shelf.Items.Count; i++)
            {
                bool inRange = i >= Math.Min(from, to) && i <= Math.Max(from, to);
                if (inRange) _shelf.Items[i].IsSelected = true;
                else if (!ctrl) _shelf.Items[i].IsSelected = false;
            }
            return;
        }

        if (ctrl)
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
        }
        else
        {
            SelectOnly(item);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => _shelf.Remove([ShelfItemOf(sender)]);

    private void SelectOnly(ShelfItem item)
    {
        foreach (var i in _shelf.Items) i.IsSelected = i == item;
        _anchor = item;
    }

    // ----- Бросили на карман — кладём на полку -----

    protected override void OnDragEnter(DragEventArgs e) => ShowDropTarget(e);

    protected override void OnDragOver(DragEventArgs e) => ShowDropTarget(e);

    protected override void OnDragLeave(DragEventArgs e) => SetDropHighlight(false);

    protected override void OnDrop(DragEventArgs e)
    {
        SetDropHighlight(false);
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!Accepts(e.Data)) return;

        try
        {
            var (paths, owned) = DropReader.Read(e.Data);
            _shelf.Add(paths, owned);
            if (paths.Count > 0) e.Effects = DragDropEffects.Copy;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось положить на полку то, что перетащили.", ex);
        }
    }

    private void ShowDropTarget(DragEventArgs e)
    {
        bool accepted = Accepts(e.Data);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetDropHighlight(accepted);
    }

    private static bool Accepts(IDataObject data) => !data.GetDataPresent(ShelfDragFormat) && DropReader.CanRead(data);

    private void SetDropHighlight(bool on) =>
        Card.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Card.Border");

    // ----- Нажатие, клик, перетаскивание наружу -----

    private void BeginPress(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Зажатую мышь сдвинули дальше порога — начинается перетаскивание.</summary>
    private bool DragStarted(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not Point start || e.LeftButton != MouseButtonState.Pressed) return false;
        var now = e.GetPosition(this);
        double threshold = (double)FindResource("Drag.Threshold");
        if (Math.Abs(now.X - start.X) < threshold && Math.Abs(now.Y - start.Y) < threshold) return false;

        _pressPoint = null;
        ((UIElement)sender).ReleaseMouseCapture();
        return true;
    }

    /// <summary>Отпустили без перетаскивания и над тем же элементом — это клик.</summary>
    private bool EndPress(object sender, MouseButtonEventArgs e)
    {
        var element = (FrameworkElement)sender;
        var p = e.GetPosition(element);
        bool click = _pressPoint != null
                     && p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
        _pressPoint = null;
        element.ReleaseMouseCapture();
        e.Handled = true;
        return click;
    }

    private void OnPressLost(object sender, MouseEventArgs e) => _pressPoint = null;

    /// <summary>Отдать файлы наружу: в Проводник, Telegram, Outlook — куда угодно, что принимает файлы.</summary>
    private static DragDropEffects DragFiles(DependencyObject source, IList<string> paths, bool fromShelf)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (existing.Length == 0) return DragDropEffects.None;

        var files = new StringCollection();
        files.AddRange(existing);
        var data = new DataObject();
        data.SetFileDropList(files);
        // Проводник на том же диске по умолчанию перемещает файл — просим копировать.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        if (fromShelf) data.SetData(ShelfDragFormat, true);

        try
        {
            return DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перетащить файлы.", ex);
            return DragDropEffects.None;
        }
    }

    private static void Start(string file, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { Arguments = arguments, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {file}.", ex);
        }
    }

    private static ShotItem ShotOf(object sender) => (ShotItem)((FrameworkElement)sender).DataContext;

    private static ShelfItem ShelfItemOf(object sender) => (ShelfItem)((FrameworkElement)sender).DataContext;
}
