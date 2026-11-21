using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Windows.Storage.Pickers;

namespace Search.App;

// Bookmarks in the window (Bookmarks.swift's panel and menu): Ctrl+D for the page in front, the panel with its
// folders, the field offering them as you type, and the file every browser imports and exports.
public sealed partial class MainWindow
{
    private readonly Bookmarks _bookmarks = new();

    private void WireBookmarks() => _bookmarks.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_panel == PanelKind.Bookmarks) FillPanel(); });

    /// Ctrl+D: the page in front kept, or let go of if it already was.
    private void ToggleBookmark()
    {
        if (_browser.Selected is not { Url: { } url } tab || tab.Private) return;
        if (_bookmarks.Find(url) is { } kept) { _bookmarks.Remove(kept.Id); Notice("Bookmark removed."); return; }
        _bookmarks.Add(url, tab.Title.Length > 0 ? tab.Title : Search.Core.Address.Pretty(url));
        Notice("Bookmarked.");
    }

    /// The tree, each line under its folder; or, while searching, what matches, with the folder it's in.
    private List<PanelRow> BookmarkRows(string words)
    {
        if (words.Length > 0)
            return _bookmarks.Matches(words).Select(m => BookmarkRow(m.Node, 0, m.Path.Count > 0 ? string.Join(" › ", m.Path) : null)).ToList();
        var rows = new List<PanelRow>();
        void Walk(IEnumerable<Bookmark> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                rows.Add(BookmarkRow(node, depth, null));
                if (node.IsFolder && node.Children is { } children) Walk(children, depth + 1);
            }
        }
        Walk(_bookmarks.Roots, 0);
        return rows;
    }

    private static PanelRow BookmarkRow(Bookmark node, int depth, string? folder) => new()
    {
        Item = node, Glyph = node.IsFolder ? "" : "", Title = new string(' ', depth * 4) + node.Label,
        Detail = node.IsFolder ? $"{Bookmarks.CountOf(node.Children ?? [])} inside" : (folder is null ? "" : folder + " · ") + (node.Url ?? ""),
        ActionGlyph = "", ActionName = "Remove"
    };

    private void TakeBookmarkRow(PanelRow row)
    {
        if (row.Item is not Bookmark node) return;
        if (node.Page is { } page) { Go(page, newTab: false); return; }
        // A folder: all of it, each in a tab of its own.
        foreach (var url in Bookmarks.OpenAll(node)) _browser.Open(url, select: false);
    }

    private async void BookmarkRowAction(PanelRow row)
    {
        if (row.Item is not Bookmark node) return;
        if (Bookmarks.RemovalWarning(node) is { } warning)
        {
            var sure = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Remove this folder?", Content = warning, PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme };
            if (await sure.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _bookmarks.Remove(node.Id);
    }

    /// Bookmarks the field offers for what's typed, after open tabs and before history.
    private IReadOnlyList<Suggestion> BookmarkOffers(string typed) => _bookmarks.Places(typed, 3).Found;

    private async Task ImportBookmarks()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".html");
        picker.FileTypeFilter.Add(".htm");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is not { } file || BookmarksFile.Read(file.Path) is not { } read) return;
        var (added, already) = _bookmarks.Take(read, Path.GetFileNameWithoutExtension(file.Name));
        Notice(added == 0 ? "Nothing new in that file." : $"{added} bookmarks brought in" + (already > 0 ? $", {already} you had already." : "."));
    }

    private async Task ExportBookmarks()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "Bookmarks" };
        picker.FileTypeChoices.Add("Bookmarks file", [".html"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is not { } file) return;
        await File.WriteAllTextAsync(file.Path, BookmarksFile.Export(_bookmarks.Roots));
        Notice($"{_bookmarks.Count} bookmarks written to {file.Name}.");
    }
}
