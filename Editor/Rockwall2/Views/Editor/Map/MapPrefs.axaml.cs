using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Rockwall2.Editor.Common.Utils;
using Rockwall2.Editor.Mapper;
using Rockwall2.Editor.Mapper.Toolbar;
using System.Linq;

namespace Rockwall2;

public partial class MapPrefs : Window
{
    private bool isWindowDragInEffect = false;
    private Point cursorDragStart = new(0, 0);
    public MapPrefs()
    {
        InitializeComponent();

        selectionFlip.IsChecked = Toolbelt.FlipSelectShift;
        scrollFlip.IsChecked = Toolbelt.FlipScrollShift;
        autoSave.IsChecked = Toolbelt.AutoSaveBeforeCompile;

        var items = Hotbar.AllAvailable.Select(h => h.Id).ToList();
        items.Insert(0, "none");
        key1.ItemsSource = items;
        key2.ItemsSource = items;
        key3.ItemsSource = items;
        key4.ItemsSource = items;
        key5.ItemsSource = items;
        key6.ItemsSource = items;
        key7.ItemsSource = items;
        key8.ItemsSource = items;
        key9.ItemsSource = items;

        key1.SelectedItem = Hotbar.GetSlot(1)?.Id ?? "none";
        key2.SelectedItem = Hotbar.GetSlot(2)?.Id ?? "none";
        key3.SelectedItem = Hotbar.GetSlot(3)?.Id ?? "none";
        key4.SelectedItem = Hotbar.GetSlot(4)?.Id ?? "none";
        key5.SelectedItem = Hotbar.GetSlot(5)?.Id ?? "none";
        key6.SelectedItem = Hotbar.GetSlot(6)?.Id ?? "none";
        key7.SelectedItem = Hotbar.GetSlot(7)?.Id ?? "none";
        key8.SelectedItem = Hotbar.GetSlot(8)?.Id ?? "none";
        key9.SelectedItem = Hotbar.GetSlot(9)?.Id ?? "none";

        Closing += (s, arg) =>
        {
            Save();
        };
    }

    public void Save()
    {
        Toolbelt.FlipSelectShift = selectionFlip.IsChecked ?? false;
        Toolbelt.FlipScrollShift = scrollFlip.IsChecked ?? false;
        Toolbelt.AutoSaveBeforeCompile = autoSave.IsChecked ?? false;

        Hotbar.AssignSlot(1, (key1.SelectedItem as string) != "none" ? (key1.SelectedItem as string) : null);
        Hotbar.AssignSlot(2, (key2.SelectedItem as string) != "none" ? (key2.SelectedItem as string) : null);
        Hotbar.AssignSlot(3, (key3.SelectedItem as string) != "none" ? (key3.SelectedItem as string) : null);
        Hotbar.AssignSlot(4, (key4.SelectedItem as string) != "none" ? (key4.SelectedItem as string) : null);
        Hotbar.AssignSlot(5, (key5.SelectedItem as string) != "none" ? (key5.SelectedItem as string) : null);
        Hotbar.AssignSlot(6, (key6.SelectedItem as string) != "none" ? (key6.SelectedItem as string) : null);
        Hotbar.AssignSlot(7, (key7.SelectedItem as string) != "none" ? (key7.SelectedItem as string) : null);
        Hotbar.AssignSlot(8, (key8.SelectedItem as string) != "none" ? (key8.SelectedItem as string) : null);
        Hotbar.AssignSlot(9, (key9.SelectedItem as string) != "none" ? (key9.SelectedItem as string) : null);
        EditorPrefs.SaveEditorPrefs();
    }

    private void TitleBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (isWindowDragInEffect)
        {
            Point currentCursorPosition = e.GetPosition(this);
            Point delta = currentCursorPosition - cursorDragStart;
            Position = this.PointToScreen(delta);
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen) return;
        isWindowDragInEffect = true;
        cursorDragStart = e.GetPosition(this);
    }

    private void TitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => isWindowDragInEffect = false;

}