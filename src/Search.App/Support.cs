using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Search.Core;

namespace Search.App;

/// A site's icon for a row, from the address the page gave; and the few small conversions the rows' templates need.
public static class Icons
{
    public static ImageSource? Of(string? uri) => uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var u) ? new BitmapImage(u) : null;
    /// A pin without an icon wears its letter.
    public static Visibility NoIcon(string? uri) => uri is null ? Visibility.Visible : Visibility.Collapsed;
    /// A tab in a group sits in a little from the group's heading.
    public static Thickness Indent(Guid? group) => new(group is null ? 0 : 14, 0, 0, 0);
}

/// A row under the field: what it offers, said the way the list shows it.
public sealed class Offer(Suggestion suggestion)
{
    public Suggestion Suggestion { get; } = suggestion;
    public string Title => Suggestion.Kind == SuggestionKind.Search ? Suggestion.Key : Suggestion.Title is { Length: > 0 } t ? t : Suggestion.Key;
    public string Detail => Suggestion.Kind switch { SuggestionKind.Search => Suggestion.Title, SuggestionKind.Open => "Switch to tab · " + Suggestion.Key, _ => Suggestion.Key };
    public string Glyph => Suggestion.Kind switch { SuggestionKind.Search => "", SuggestionKind.Open => "", SuggestionKind.Bookmark => "", _ => "" };
}

/// A group's heading in the column (TabGroup.swift): its name, how many tabs, and whether it's folded.
public sealed class GroupRow(TabGroup group) : Observable
{
    private TabGroup _group = group;
    private int _count;
    public TabGroup Group { get => _group; set { if (Set(ref _group, value)) { Changed(nameof(Name)); Changed(nameof(Chevron)); } } }
    public string Name => _group.Name;
    public string Chevron => _group.Collapsed ? "" : "";
    public string Count { get => _count.ToString(); }
    public void Counted(int count) { if (_count == count) return; _count = count; Changed(nameof(Count)); }
}

/// Which template draws a row of the column: a tab, or a group's heading.
public sealed partial class RowTemplates : DataTemplateSelector
{
    public DataTemplate? Tab { get; set; }
    public DataTemplate? Group { get; set; }
    protected override DataTemplate? SelectTemplateCore(object item) => item is GroupRow ? Group : Tab;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// The window's tabs as the column and the strip list them, without the bench's: the pins in their grid, the rest in the
/// column under their groups' headings (a folded group's tabs left out), and every tab in a row for the strip. Kept in
/// step with the browser by small edits, so a list never loses its place or its selection to a rebuild; a drag in the
/// column is handed back to the browser as moves.
public sealed class RowMirror
{
    private readonly Browser _browser;
    private readonly Dictionary<Guid, GroupRow> _headings = new();
    private bool _applying;

    public ObservableCollection<object> Items { get; } = new();
    public ObservableCollection<Tab> Pins { get; } = new();
    public ObservableCollection<Tab> Flat { get; } = new();

    public RowMirror(Browser browser)
    {
        _browser = browser;
        Sync();
        browser.Tabs.CollectionChanged += (_, _) => { if (!_applying) Sync(); };
        browser.RowShapeChanged += () => { if (!_applying) Sync(); };
    }

    public void Sync()
    {
        var all = _browser.Ordinary.ToList();
        var groups = _browser.Groups.ToDictionary(g => g.Id);
        var rows = new List<object>();
        foreach (var tab in all.Where(t => !t.Pinned))
        {
            if (tab.GroupId is { } id && groups.TryGetValue(id, out var group))
            {
                if (!_headings.TryGetValue(id, out var heading)) _headings[id] = heading = new GroupRow(group);
                heading.Group = group;
                if (!rows.Contains(heading)) { rows.Add(heading); heading.Counted(all.Count(t => t.GroupId == id)); }
                if (!group.Collapsed) rows.Add(tab);
            }
            else rows.Add(tab);
        }
        foreach (var gone in _headings.Keys.Where(id => !groups.ContainsKey(id)).ToList()) _headings.Remove(gone);
        Follow(Items, rows);
        Follow(Pins, all.Where(t => t.Pinned).ToList());
        Follow(Flat, all);
    }

    private static void Follow<T>(ObservableCollection<T> items, List<T> want)
    {
        for (var i = items.Count - 1; i >= 0; i--) if (!want.Contains(items[i])) items.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
        {
            var at = items.IndexOf(want[i]);
            if (at < 0) items.Insert(i, want[i]); else if (at != i) items.Move(at, i);
        }
    }

    /// After a drag in the column: the browser's row takes the column's order, after the pins. The tab carried joins
    /// the group it was let go in (under its heading, among its tabs) or, let go outside every group, leaves its own.
    public void Reordered(IReadOnlyCollection<object> carried)
    {
        _applying = true;
        try
        {
            var pins = _browser.Ordinary.Count(t => t.Pinned);
            Guid? under = null;
            var place = pins;
            var items = Items.ToList();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is GroupRow heading) { under = heading.Group.Id; continue; }
                if (items[i] is not Tab tab) continue;
                if (carried.Contains(tab))
                {
                    // In the group if what is right after it, or right before it, is that group's heading or one of its tabs.
                    var next = i + 1 < items.Count ? items[i + 1] as Tab : null;
                    var previous = i > 0 ? items[i - 1] : null;
                    var inside = under is { } g && (previous is GroupRow { Group.Id: var h } && h == g || previous is Tab { GroupId: var p } && p == g || next?.GroupId == g);
                    tab.GroupId = inside ? under : null;
                }
                else if (tab.GroupId is null) under = null;
                _browser.Move(tab, place++);
            }
        }
        finally { _applying = false; }
        Sync();
    }
}
