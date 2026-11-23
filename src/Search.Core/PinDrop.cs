namespace Search.Core;

// A pin or a folder's heading carried in the pinned rows and let go over a line, as Arc's sidebar takes them: before a
// line, after it, or into a folder. Pure: the window says which line and where on it; this says what that means, in
// the terms PinFolders.MovePin and PinFolders.MovedTo already take.
public enum DropZone { Before, Into, After }

public static class PinDrop
{
    /// A pin let go: the folder it goes in (null: the top), and the pin it goes beside, after it or before.
    public sealed record PinPlace(Guid? Folder, Guid? Anchor, bool After);

    /// Where on a line `height` tall a pointer `y` from its top lets go: the middle half of a heading that can hold
    /// what is carried is into it; above the middle is before the line, below it after.
    public static DropZone Zone(double y, double height, bool holds) => holds && y >= height / 4 && y <= height * 3 / 4 ? DropZone.Into : y < height / 2 ? DropZone.Before : DropZone.After;

    /// Whether a line's heading can take what is carried into it: a folder that isn't live.
    public static bool Holds(PinTree.Line line) => line is PinTree.FolderLine { Folder.IsLive: false };

    /// A pin let go over line `at`. Beside a pin, in that pin's folder. Into a folder, at its end. After an open
    /// folder's heading, at the top of what it holds; before any heading, or after a folded or live one, at the top of
    /// the pins of the level it stands in (pins are drawn after a level's folders). Null over its own line.
    public static PinPlace? ForPin(IReadOnlyList<PinTree.Line> lines, Guid pin, int at, DropZone zone)
    {
        if (at < 0 || at >= lines.Count) return null;
        switch (lines[at])
        {
            case PinTree.PinLine line when line.Id == pin: return null;
            case PinTree.PinLine line: return new(FolderOf(lines, at), line.Id, zone == DropZone.After);
            case PinTree.FolderLine { Folder: { IsLive: false } folder } when zone == DropZone.Into: return new(folder.Id, null, false);
            case PinTree.FolderLine { Folder: { IsLive: false, Collapsed: false } folder } when zone == DropZone.After: return new(folder.Id, FirstPin(lines, folder.Id, pin), false);
            case PinTree.FolderLine { Folder: var folder }: return new(folder.Parent, FirstPin(lines, folder.Parent, pin), false);
            case PinTree.ItemLine item when FolderLineOf(lines, item.Folder) is { } live: return new(live.Parent, FirstPin(lines, live.Parent, pin), false);
            default: return null;
        }
    }

    /// A folder's heading let go over line `at`: into a folder, at the end of its folders; before a heading, before that
    /// folder; after an open folder's heading, first in it; after a folded or live one (or one of its posts), next after
    /// it. Over a pin, at the end of that level's folders. PinFolders.MovedTo then refuses what doesn't fit.
    public static PinTree.FolderPlace? ForFolder(IReadOnlyList<PinTree.Line> lines, Guid moving, int at, DropZone zone, IReadOnlyList<PinFolder> folders)
    {
        if (at < 0 || at >= lines.Count) return null;
        switch (lines[at])
        {
            case PinTree.FolderLine { Folder: var folder } when folder.Id == moving: return null;
            case PinTree.FolderLine { Folder: { IsLive: false } folder } when zone == DropZone.Into: return new(folder.Id);
            case PinTree.FolderLine { Folder: var folder } when zone == DropZone.Before: return new(folder.Parent, folder.Id);
            case PinTree.FolderLine { Folder: { IsLive: false, Collapsed: false } folder }: return new(folder.Id, folders.FirstOrDefault(f => f.Parent == folder.Id && f.Id != moving)?.Id);
            case PinTree.FolderLine { Folder: var folder }: return new(folder.Parent, NextBeside(folder, moving, folders));
            case PinTree.ItemLine item when folders.FirstOrDefault(f => f.Id == item.Folder) is { } live: return new(live.Parent, NextBeside(live, moving, folders));
            case PinTree.PinLine: return new(FolderOf(lines, at));
            default: return null;
        }
    }

    /// The folder a line stands in, by the heading above it one level out; null at the top.
    public static Guid? FolderOf(IReadOnlyList<PinTree.Line> lines, int at)
    {
        for (var i = at - 1; i >= 0; i--) if (lines[i].Depth < lines[at].Depth) return lines[i] is PinTree.FolderLine heading ? heading.Folder.Id : null;
        return null;
    }

    private static PinFolder? FolderLineOf(IReadOnlyList<PinTree.Line> lines, Guid id) => lines.OfType<PinTree.FolderLine>().FirstOrDefault(l => l.Folder.Id == id)?.Folder;

    /// The first pin drawn directly in a folder (null: the top), other than the one carried.
    private static Guid? FirstPin(IReadOnlyList<PinTree.Line> lines, Guid? folder, Guid carried)
    {
        for (var i = 0; i < lines.Count; i++) if (lines[i] is PinTree.PinLine pin && pin.Id != carried && FolderOf(lines, i) == folder) return pin.Id;
        return null;
    }

    /// The folder after this one among its level's, other than the one carried; null when it is the last.
    private static Guid? NextBeside(PinFolder folder, Guid moving, IReadOnlyList<PinFolder> folders)
    {
        var level = folders.Where(f => f.Parent == folder.Parent && f.Id != moving).ToList();
        var at = level.FindIndex(f => f.Id == folder.Id);
        return at >= 0 && at + 1 < level.Count ? level[at + 1].Id : null;
    }
}
