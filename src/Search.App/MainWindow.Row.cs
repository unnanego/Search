using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Search.Core;

namespace Search.App;

// What a right-click on a tab offers (TabBar.swift's menu): pin it, name it, put it in a group or take it out, put it
// beside the page in front, keep two pages apart again, duplicate it, close it.
public sealed partial class MainWindow
{
    private void WireRow()
    {
        Row.RightTapped += OnRowMenu;
        PinGrid.RightTapped += OnRowMenu;
        Strip.RightTapped += OnRowMenu;
    }

    private void OnRowMenu(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: var item } element) return;
        var menu = item switch
        {
            Tab tab => TabMenu(tab), GroupRow heading => GroupMenu(heading.Group),
            PinLine { Kind: PinLineKind.Pin, Tab: { } pin } => TabMenu(pin), PinLine { Kind: PinLineKind.Folder, Folder: { } folder } => FolderMenu(folder), PinLine { Kind: PinLineKind.Post, Post: { } post } => PostMenu(post),
            null when ReferenceEquals(sender, Row) || ReferenceEquals(sender, PinRows) => ColumnMenu(),
            _ => null
        };
        if (menu is null) return;
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private MenuFlyout TabMenu(Tab tab)
    {
        var menu = new MenuFlyout();
        void Item(string text, Action action, bool enabled = true) { var i = new MenuFlyoutItem { Text = text, IsEnabled = enabled }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        Item(tab.Pinned ? "Unpin" : _browser.PinnableSplit(tab) is not null ? "Pin split view" : "Pin", () => { if (tab.Pinned) _browser.Unpin(tab); else _browser.Pin(tab); }, !tab.Private);
        Item("Name…", () => _ = NameTab(tab));
        PinItems(menu, tab);
        if (!tab.Pinned)
        {
            if (tab.GroupId is null)
            {
                var into = new MenuFlyoutSubItem { Text = "Add to group" };
                var fresh = new MenuFlyoutItem { Text = "New group" };
                fresh.Click += (_, _) => _browser.Group(tab);
                into.Items.Add(fresh);
                foreach (var group in _browser.Groups) { var g = group; var i = new MenuFlyoutItem { Text = g.Name }; i.Click += (_, _) => _browser.Group(tab, g); into.Items.Add(i); }
                menu.Items.Add(into);
            }
            else Item("Remove from group", () => _browser.Ungroup(tab));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        if (_browser.SplitOf(tab) is { } split) Item("Separate split tabs", () => _browser.Separate(split));
        else Item("Split with this page", () => { if (_browser.Selected is { } front) _browser.Split(front, tab); }, _browser.Selected is { } f && f != tab);
        Item("Duplicate", () => _browser.Duplicate(tab));
        Item("Close", () => Close(tab));
        return menu;
    }

    private MenuFlyout GroupMenu(TabGroup group)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += (_, _) => _ = RenameGroup(group);
        menu.Items.Add(rename);
        var fold = new MenuFlyoutItem { Text = group.Collapsed ? "Open" : "Fold" };
        fold.Click += (_, _) => _browser.SetGroup(group with { Collapsed = !group.Collapsed });
        menu.Items.Add(fold);
        var ungroup = new MenuFlyoutItem { Text = "Ungroup" };
        ungroup.Click += (_, _) => { foreach (var tab in _browser.Ordinary.Where(t => t.GroupId == group.Id).ToList()) _browser.Ungroup(tab); };
        menu.Items.Add(ungroup);
        return menu;
    }

    private async Task NameTab(Tab tab)
    {
        var box = new TextBox { Text = tab.Name ?? tab.Title, PlaceholderText = "Leave empty for the page's own title" };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Name this tab", Content = box, PrimaryButtonText = "Name", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) tab.Name = box.Text.Trim() is { Length: > 0 } name && name != tab.Title ? name : null;
    }

    private async Task RenameGroup(TabGroup group)
    {
        var box = new TextBox { Text = group.Name };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Rename group", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim() is { Length: > 0 } name) _browser.SetGroup(group with { Name = name });
    }
}
