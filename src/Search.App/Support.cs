using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Search.Core;

namespace Search.App;

/// A site's icon for a row, from the address the page gave.
public static class Icons
{
    public static ImageSource? Of(string? uri) => uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var u) ? new BitmapImage(u) : null;
}

/// A row under the field: what it offers, said the way the list shows it.
public sealed class Offer(Suggestion suggestion)
{
    public Suggestion Suggestion { get; } = suggestion;
    public string Title => Suggestion.Kind == SuggestionKind.Search ? Suggestion.Key : Suggestion.Title is { Length: > 0 } t ? t : Suggestion.Key;
    public string Detail => Suggestion.Kind switch { SuggestionKind.Search => Suggestion.Title, SuggestionKind.Open => "Switch to tab · " + Suggestion.Key, _ => Suggestion.Key };
    public string Glyph => Suggestion.Kind switch { SuggestionKind.Search => "", SuggestionKind.Open => "", SuggestionKind.Bookmark => "", _ => "" };
}

/// The window's ordinary tabs, without the bench's, as the column and the strip list them. Kept in step with the
/// browser's row by small edits, so a list never loses its place or its selection to a rebuild, and a drag in the
/// list is handed back to the browser as moves.
public sealed class RowMirror
{
    private readonly Browser _browser;
    private bool _applying;

    public ObservableCollection<Tab> Items { get; } = new();

    public RowMirror(Browser browser)
    {
        _browser = browser;
        Sync();
        browser.Tabs.CollectionChanged += (_, _) => { if (!_applying) Sync(); };
    }

    private void Sync()
    {
        var want = _browser.Ordinary.ToList();
        for (var i = Items.Count - 1; i >= 0; i--) if (!want.Contains(Items[i])) Items.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
        {
            var at = Items.IndexOf(want[i]);
            if (at < 0) Items.Insert(i, want[i]); else if (at != i) Items.Move(at, i);
        }
    }

    /// After a drag in the list: the browser's row takes the list's order.
    public void Reordered()
    {
        _applying = true;
        try { for (var i = 0; i < Items.Count; i++) _browser.Move(Items[i], i); }
        finally { _applying = false; }
    }
}
