using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Search.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Search.App;

// Pins and folders carried in the column (Side.swift's pin drags): a square among the squares, a row or a folder's
// heading among the pinned rows, before a line, after it or into a folder (PinDrop says which), a square onto the rows
// to become one, a row onto the squares to become a square. A split pinned whole goes whole, and never into a folder.
public sealed partial class MainWindow
{
    /// What is carried, in the drag's data: "pin:" or "folder:" and its id.
    private const string Carrying = "search-pin-line";

    private void WirePinDrag()
    {
        PinRows.CanDragItems = PinRows.AllowDrop = true;
        PinRows.DragItemsStarting += (_, e) => Carry(e, e.Items.FirstOrDefault() switch
        {
            PinLine { Kind: PinLineKind.Pin, Tab: { } tab } => "pin:" + tab.Id,
            PinLine { Kind: PinLineKind.Folder } line => "folder:" + line.Id,
            _ => null
        });
        PinRows.DragOver += (_, e) =>
        {
            var drop = RowsDrop(e);
            e.AcceptedOperation = drop is null ? DataPackageOperation.None : DataPackageOperation.Move;
            if (drop is { } d) e.DragUIOverride.Caption = d.Caption;
        };
        PinRows.Drop += (_, e) => RowsDrop(e)?.Apply();
        // The squares reorder themselves; what's let go among them is then the browser's order.
        PinGrid.CanDragItems = PinGrid.CanReorderItems = PinGrid.AllowDrop = true;
        PinGrid.DragItemsStarting += (_, e) => Carry(e, e.Items.FirstOrDefault() is Tab tab ? "pin:" + tab.Id : null);
        PinGrid.DragItemsCompleted += (_, e) => { if (e.DropResult == DataPackageOperation.Move) SquaresReordered(); };
        PinGrid.DragOver += (_, e) => { if (CarriedRow(e.DataView) is not null) { e.AcceptedOperation = DataPackageOperation.Move; e.DragUIOverride.Caption = "Show as Square"; } };
        PinGrid.Drop += (_, e) => { if (CarriedRow(e.DataView) is { } row) { _browser.SetListed(row, false); PinsEdited(); } };
    }

    private static void Carry(DragItemsStartingEventArgs e, string? what)
    {
        if (what is null) { e.Cancel = true; return; }
        e.Data.Properties[Carrying] = what;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private static string? Carried(DataPackageView data) => data.Properties.TryGetValue(Carrying, out var what) ? what as string : null;

    /// A pinned row carried onto the squares, when it may become one (not a page of a split pinned whole).
    private Tab? CarriedRow(DataPackageView data) => Carried(data) is { } what && what.StartsWith("pin:", StringComparison.Ordinal) && Guid.TryParse(what[4..], out var id)
        && _browser.Ordinary.FirstOrDefault(t => t.Id == id) is { Pinned: true, Listed: true } row && _row.PinRows.Any(l => l.Tab == row) && _browser.PinnedPair(row) is null ? row : null;

    /// Where over the pinned rows the pointer is: the line, and where on it.
    private (string Caption, Action Apply)? RowsDrop(DragEventArgs e)
    {
        if (Carried(e.DataView) is not { } what || _row.PinRows.Count == 0) return null;
        var y = e.GetPosition(PinRows).Y;
        var lines = _row.PinRows.ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (PinRows.ContainerFromIndex(i) is not FrameworkElement line) continue;
            var top = line.TransformToVisual(PinRows).TransformPoint(default).Y;
            if (y < top + line.ActualHeight || i == lines.Count - 1)
                return PinDropOn(what, i, PinDrop.Zone(Math.Clamp(y - top, 0, line.ActualHeight), line.ActualHeight, PinDrop.Holds(Tree(lines)[i])));
        }
        return null;
    }

    /// The pinned rows as PinTree drew them.
    private static List<PinTree.Line> Tree(IEnumerable<PinLine> lines) => lines.Select(l => l.Kind switch
    {
        PinLineKind.Folder => (PinTree.Line)new PinTree.FolderLine(l.Folder!, l.Depth),
        PinLineKind.Post => new PinTree.ItemLine(l.Post!, l.PostFolder ?? Guid.Empty, l.Depth),
        _ => new PinTree.PinLine(l.Tab?.Id ?? l.Id, l.Depth)
    }).ToList();

    /// What letting go of `what` over line `at` does, and its caption; null where it can't go. The bench asks this too.
    private (string Caption, Action Apply)? PinDropOn(string what, int at, DropZone zone)
    {
        var tree = Tree(_row.PinRows);
        if (!Guid.TryParse(what[(what.IndexOf(':') + 1)..], out var id)) return null;
        string Into(Guid? folder) => folder is { } f && SpaceFolders.FirstOrDefault(x => x.Id == f) is { } named ? "Move to " + named.Name : "Move here";
        if (what.StartsWith("folder:", StringComparison.Ordinal))
        {
            if (PinDrop.ForFolder(tree, id, at, zone, SpaceFolders) is not { } place || PinFolders.MovedTo(SpaceFolders, id, place) is not { } moved) return null;
            return (Into(place.Parent), () => SetFolders(moved));
        }
        if (_browser.Ordinary.FirstOrDefault(t => t.Id == id) is not { Pinned: true } pin || PinDrop.ForPin(tree, id, at, zone) is not { } spot) return null;
        // Folders off, a pin keeps the folder it has; a split pinned whole stays out of them.
        if (!_prefs.ShowsPinFolders) spot = spot with { Folder = pin.Folder };
        if (_browser.PinnedPair(pin) is not null && spot.Folder is not null) return null;
        return (Into(spot.Folder), () => DropPin(pin, spot));
    }

    /// A pin let go among the rows: a square becomes one, and it goes where it was let go, a split pinned whole with
    /// every page of it, in their order, and never between two pages of another.
    private void DropPin(Tab pin, PinDrop.PinPlace place)
    {
        if (!pin.Pinned) return;
        if (!pin.Listed) _browser.SetListed(pin, true);
        var moving = _browser.PinnedPair(pin)?.Tabs.ToList() ?? [pin];
        var anchor = place.Anchor is { } a ? _browser.Ordinary.FirstOrDefault(t => t.Id == a) : null;
        if (anchor is not null && _browser.PinnedPair(anchor) is { } beside && !beside.Tabs.Contains(pin)) anchor = place.After ? beside.Tabs[^1] : beside.Tabs[0];
        Tab? previous = null;
        foreach (var page in moving)
        {
            var (at, after) = previous is null ? (anchor, place.After) : (previous, true);
            previous = page;
            // Folders off, the rows are drawn in the row's order: beside the pin, nothing else moved.
            if (!_prefs.ShowsPinFolders) { var from = _browser.Tabs.IndexOf(page); var to = at is null ? _browser.Ordinary.Count(t => t.Pinned) - 1 : _browser.Tabs.IndexOf(at) + (after ? 1 : 0); _browser.Move(page, from < to ? to - 1 : to); continue; }
            if (PinFolders.MovePin(_browser.Ordinary.Select(Facts).ToList(), page.Id, place.Folder, SpaceFolders, at?.Id, after) is not { } arranged) break;
            page.Folder = arranged.First(t => t.Id == page.Id).Folder;
            for (var i = 0; i < arranged.Count; i++) if (_browser.Ordinary.FirstOrDefault(t => t.Id == arranged[i].Id) is { } tab && _browser.Tabs.IndexOf(tab) != i) _browser.Move(tab, i);
        }
        PinsEdited();
    }

    /// The squares as the grid now has them, carried into that order in the row (they're its head).
    private void SquaresReordered()
    {
        var squares = _row.Pins.Where(t => t.Pinned).ToList();
        for (var i = 0; i < squares.Count; i++) if (_browser.Tabs.IndexOf(squares[i]) != i) _browser.Move(squares[i], i);
        PinsEdited();
    }
}
