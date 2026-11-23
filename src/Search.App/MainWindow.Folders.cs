using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;

namespace Search.App;

// The pinned rows and their folders, in the window (PinFolderViews.swift, BrowserPinFolders.swift, LiveFolders.swift):
// pins kept as rows under the squares (Settings › Pinned rows), in folders that fold and open (Folders in pinned rows),
// and live folders filled from a web feed, whose posts open as a bookmark would, in a new tab, never as a pin. Which
// folder a pin is in is the pin's own, in pins.json; the folders are pin-folders.json's, the same in every window.
// A pin goes into a folder from its menu (Move to Folder) or carried there (MainWindow.PinDrag.cs).
public sealed partial class MainWindow
{
    private PinFolders FolderStore => App.Shared.Folders;
    private IReadOnlyList<PinFolder> SpaceFolders => FolderStore.Folders(_space);

    private RowMirror MakeRow() => new(_browser, () => _prefs.ShowsPinRows, () => _prefs.ShowsPinFolders, () => SpaceFolders, f => App.Live?.Tip(f) ?? "");

    private void WireFolders()
    {
        PinRows.ItemsSource = _row.PinRows;
        _row.PinRows.CollectionChanged += (_, _) => PinRows.Visibility = _row.PinRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PinRows.SelectionChanged += OnPinRowChosen;
        PinRows.RightTapped += OnRowMenu;
        WirePinDrag();
        // Another window, or a live folder's fetch, changed this space's folders.
        Hook<Action<PinsChange>>(h => FolderStore.Changed += h, h => FolderStore.Changed -= h, change =>
        {
            if (change.Space != _space || ReferenceEquals(change.From, this)) return;
            DispatcherQueue.TryEnqueue(() => { _row.Sync(); MarkPinRow(); });
        });
        Hook<System.ComponentModel.PropertyChangedEventHandler>(h => _prefs.PropertyChanged += h, h => _prefs.PropertyChanged -= h, (_, e) =>
        {
            if (e.PropertyName is not (nameof(Prefs.PinRows) or nameof(Prefs.PinFolders) or nameof(Prefs.TabsOnSide))) return;
            _row.Sync();
            MarkPinRow();
            // Folders on now: a fetch for the live ones, as at launch.
            if (e.PropertyName == nameof(Prefs.PinFolders) && _prefs.ShowsPinFolders) _ = App.Live?.RefreshAll(true);
        });
    }

    /// The tab in front's line among the pinned rows, selected; none when it isn't one.
    private void MarkPinRow()
    {
        var line = _browser.Selected is { } tab ? _row.LineOf(tab) : null;
        if (!ReferenceEquals(PinRows.SelectedItem, line)) PinRows.SelectedItem = line;
    }

    private void OnPinRowChosen(object sender, SelectionChangedEventArgs e)
    {
        if (PinRows.SelectedItem is not PinLine line) return;
        switch (line.Kind)
        {
            case PinLineKind.Pin when line.Tab is { } tab && tab != _browser.Selected: _browser.Selected = tab; return;
            case PinLineKind.Pin: return;
            // A heading folds or opens its folder, a post opens in a new tab; the tab in front stays marked.
            case PinLineKind.Folder when line.Folder is { } folder: ToggleFolder(folder.Id); break;
            case PinLineKind.Post when line.Post is { } post: OpenPost(post); break;
        }
        MarkPinRow();
    }

    /// The pins changed in this window (a row moved into a folder, shown as a row or a square): every window told,
    /// the session written, the column redrawn.
    private void PinsEdited() { TellPins(); SaveSession(now: false); _row.Sync(); MarkPinRow(); }

    private void SetFolders(IReadOnlyList<PinFolder>? folders)
    {
        if (folders is null) return;
        FolderStore.Set(_space, folders, from: this);
        _row.Sync();
        MarkPinRow();
    }

    private void ToggleFolder(Guid id) => SetFolders(PinFolders.Toggled(SpaceFolders, id));

    /// New Folder: inside `parent`, or holding `pin` (which goes in it), named as asked.
    private async Task NewFolder(Guid? parent = null, Tab? pin = null, string? name = null)
    {
        if (pin is not null && _browser.PinnedPair(pin) is not null) return;
        name ??= await Ask("New folder", PinFolders.DefaultName, "Make", "Folder name");
        if (name is null || PinFolders.Made(SpaceFolders, name, parent, pin is null ? null : Facts(pin)) is not { } made) return;
        SetFolders(made.Folders);
        if (pin is null) return;
        pin.Folder = made.Made;
        PinsEdited();
    }

    /// A pinned row into a folder, or out of every one (null).
    private void MovePin(Tab pin, Guid? folder)
    {
        if (!pin.Listed || pin.Folder == folder || _browser.PinnedPair(pin) is not null || (folder is { } id && SpaceFolders.FirstOrDefault(f => f.Id == id) is not { IsLive: false })) return;
        pin.Folder = folder;
        PinsEdited();
    }

    private async Task RenameFolder(Guid id)
    {
        if (SpaceFolders.FirstOrDefault(f => f.Id == id) is not { } folder || await Ask("Rename folder", folder.Name, "Rename", "Folder name") is not { } name) return;
        SetFolders(PinFolders.Renamed(SpaceFolders, id, name));
    }

    /// Delete Folder: what it held goes up into the folder that held it; no pin is closed.
    private void DeleteFolder(Guid id)
    {
        if (PinFolders.Deleted(SpaceFolders, id) is not { } deleted) return;
        App.Live?.Forget(id);
        var moved = false;
        foreach (var pin in _browser.Ordinary.Where(t => t.Pinned && t.Folder == id)) { pin.Folder = deleted.Up; moved = true; }
        SetFolders(deleted.Folders);
        if (moved) PinsEdited();
    }

    private void MoveFolder(Guid id, Guid? parent) => SetFolders(PinFolders.MovedInto(SpaceFolders, id, parent));

    /// New Live Folder: a feed's address (or a page that names its feed), fetched, and a folder of its posts made.
    private async Task NewLiveFolder(Guid? parent = null, string? typed = null)
    {
        typed ??= await Ask(LiveFeed.AskTitle, "", LiveFeed.AskConfirm, LiveFeed.AskPlaceholder, LiveFeed.AskDetail);
        if (typed is null || App.Live is not { } live) return;
        if (LiveFeed.Address(typed) is not { } url) { await Say(LiveFeed.NotAddressTitle, LiveFeed.NotAddressDetail); return; }
        var fetched = await live.Load(url);
        if (fetched.Read is not { } read) { await Say(LiveFeed.NoFeedTitle, LiveFeed.Said(fetched.Problem ?? LiveFeed.Problem.NotAFeed, url)); return; }
        SetFolders(LiveFeed.Made(SpaceFolders, read, parent).Folders);
    }

    private void RefreshFolder(Guid id) => _ = App.Live?.Refresh(id, _space);

    /// A live folder's post, opened as a bookmark is: in a new tab, in front.
    private void OpenPost(FeedItem post)
    {
        if (LiveFeed.Web(post.Link) is not { } url) return;
        var tab = _browser.Open(url);
        _ = Front(tab, givePage: true);
    }

    /// What a folder heading's menu offers. A live folder holds only its posts: no folder or pin goes in it.
    private MenuFlyout FolderMenu(PinFolder folder)
    {
        var menu = new MenuFlyout();
        void Item(string text, Action action, bool enabled = true) { var i = new MenuFlyoutItem { Text = text, IsEnabled = enabled }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        Item("Rename Folder…", () => _ = RenameFolder(folder.Id));
        Item(folder.Collapsed ? "Expand Folder" : "Collapse Folder", () => ToggleFolder(folder.Id));
        if (folder.IsLive) Item("Refresh", () => RefreshFolder(folder.Id), App.Live?.IsFetching(folder.Id) != true);
        else
        {
            Item("New Folder Inside…", () => _ = NewFolder(folder.Id), PinTree.CanMake(folder.Id, SpaceFolders));
            Item("New Live Folder Inside…", () => _ = NewLiveFolder(folder.Id), PinTree.CanMake(folder.Id, SpaceFolders));
        }
        var into = new MenuFlyoutSubItem { Text = "Move Folder To" };
        var top = new MenuFlyoutItem { Text = "Top", IsEnabled = folder.Parent is not null };
        top.Click += (_, _) => MoveFolder(folder.Id, null);
        into.Items.Add(top);
        foreach (var other in Listed(SpaceFolders).Where(o => !o.Folder.IsLive && o.Folder.Id != folder.Id))
        {
            var target = other.Folder;
            var item = new MenuFlyoutItem { Text = new string(' ', other.Depth * 3) + target.Name, IsEnabled = target.Id != folder.Parent && PinTree.CanMove(folder.Id, target.Id, SpaceFolders) };
            item.Click += (_, _) => MoveFolder(folder.Id, target.Id);
            into.Items.Add(item);
        }
        menu.Items.Add(into);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Delete Folder", () => DeleteFolder(folder.Id));
        return menu;
    }

    private MenuFlyout PostMenu(FeedItem post)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open in New Tab" };
        open.Click += (_, _) => OpenPost(post);
        menu.Items.Add(open);
        var copy = new MenuFlyoutItem { Text = "Copy Link" };
        copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(post.Link); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); };
        menu.Items.Add(copy);
        return menu;
    }

    /// The pin's own entries in its menu, while pinned rows show: a row or a square, and, with folders, which one.
    private void PinItems(MenuFlyout menu, Tab tab)
    {
        // A split pinned whole is always rows, and in no folder.
        if (!tab.Pinned || !_prefs.ShowsPinRows || _browser.PinnedPair(tab) is not null) return;
        var listed = new MenuFlyoutItem { Text = tab.Listed ? "Show as Square" : "Show as Row" };
        listed.Click += (_, _) => { _browser.SetListed(tab, !tab.Listed); PinsEdited(); };
        menu.Items.Add(listed);
        if (!tab.Listed || !_prefs.ShowsPinFolders) return;
        var into = new MenuFlyoutSubItem { Text = "Move to Folder" };
        var top = new MenuFlyoutItem { Text = "Top", IsEnabled = tab.Folder is not null };
        top.Click += (_, _) => MovePin(tab, null);
        into.Items.Add(top);
        foreach (var line in Listed(SpaceFolders).Where(l => !l.Folder.IsLive))
        {
            var folder = line.Folder;
            var item = new MenuFlyoutItem { Text = new string(' ', line.Depth * 3) + folder.Name, IsEnabled = folder.Id != tab.Folder };
            item.Click += (_, _) => MovePin(tab, folder.Id);
            into.Items.Add(item);
        }
        menu.Items.Add(into);
        var fresh = new MenuFlyoutItem { Text = "New Folder with This Pin…" };
        fresh.Click += (_, _) => _ = NewFolder(pin: tab);
        menu.Items.Add(fresh);
    }

    /// What a right-click on the column where there is no tab offers, with folders on: a folder, or a live one.
    private MenuFlyout? ColumnMenu()
    {
        if (!_prefs.ShowsPinFolders) return null;
        var menu = new MenuFlyout();
        var folder = new MenuFlyoutItem { Text = "New Folder…" };
        folder.Click += (_, _) => _ = NewFolder();
        menu.Items.Add(folder);
        var live = new MenuFlyoutItem { Text = "New Live Folder…" };
        live.Click += (_, _) => _ = NewLiveFolder();
        menu.Items.Add(live);
        return menu;
    }

    /// The folders in drawn order, each with how deep it is, as the menus list them.
    private static IEnumerable<(PinFolder Folder, int Depth)> Listed(IReadOnlyList<PinFolder> folders)
    {
        IEnumerable<(PinFolder, int)> Under(Guid? parent, int depth) => folders.Where(f => f.Parent == parent).SelectMany(f => new[] { (f, depth) }.Concat(Under(f.Id, depth + 1)));
        return Under(null, 0);
    }

    private async Task<string?> Ask(string title, string text, string action, string placeholder, string? detail = null)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder, MinWidth = 320 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var content = new StackPanel { Spacing = 8 };
        if (detail is not null) content.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        content.Children.Add(box);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = content, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.ActualTheme };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim() is { Length: > 0 } said ? said : null;
    }

    private async Task Say(string title, string detail)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", RequestedTheme = Root.ActualTheme };
        await dialog.ShowAsync();
    }
}
