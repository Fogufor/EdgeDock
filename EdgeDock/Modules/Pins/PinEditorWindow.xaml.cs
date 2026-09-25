using System.Windows;
using System.Windows.Controls;
using EdgeDock.Core;

namespace EdgeDock.Modules.Pins;

/// <summary>Окно «Новый элемент» / «Изменить»: название и то, что копировать.</summary>
public partial class PinEditorWindow : Window
{
    private readonly PinsModule _module;
    private readonly PinItem? _item;

    internal PinEditorWindow(PinsModule module, PinItem? item)
    {
        _module = module;
        _item = item;
        InitializeComponent();
        if (item != null)
        {
            Title = "Изменить";
            LabelBox.Text = item.Label;
            ValueBox.Text = item.Value;
            DeleteButton.Visibility = Visibility.Visible;
        }
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        Loaded += (_, _) =>
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        };
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = LabelBox.Text.Trim().Length > 0 && ValueBox.Text.Trim().Length > 0;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string label = LabelBox.Text.Trim(), value = ValueBox.Text.Trim();
        if (label.Length == 0 || value.Length == 0) return;
        if (_item == null) _module.Add(label, value);
        else _module.Update(_item, label, value);
        Close();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_item != null) _module.Remove(_item);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
