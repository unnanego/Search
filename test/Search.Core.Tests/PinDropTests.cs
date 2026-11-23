using Search.Core;

namespace Search.Core.Tests;

/// What letting go over a line of the pinned rows means: before it, after it, or into a folder (PinDrop).
public class PinDropTests
{
    // Work holding Docs, News (live, one post), Shut (folded); pins a and b in Work, c in Docs, e in Shut, d at the top.
    private static readonly Guid Work = Guid.NewGuid(), Docs = Guid.NewGuid(), News = Guid.NewGuid(), Shut = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid(), E = Guid.NewGuid();

    private static IReadOnlyList<PinFolder> Folders(bool shut = true) =>
    [
        new(Work, "Work"), new(Docs, "Docs") { Parent = Work }, new(News, "News") { Feed = "https://news.example/feed", Items = [new(Guid.NewGuid(), "First", "https://news.example/1")] },
        new(Shut, "Shut") { Collapsed = shut },
    ];

    private static IReadOnlyList<PinTree.Line> Lines(IReadOnlyList<PinFolder>? folders = null) =>
        PinTree.Lines([(A, Work), (B, Work), (C, Docs), (D, null), (E, Shut)], folders ?? Folders());

    private static int At(IReadOnlyList<PinTree.Line> lines, Guid id) => lines.ToList().FindIndex(l => l is PinTree.PinLine p && p.Id == id || l is PinTree.FolderLine f && f.Folder.Id == id);

    [Fact]
    public void TheLinesAreAsExpected()
    {
        // Work, Docs, c, a, b, News, First, Shut (folded: e hidden), d.
        var lines = Lines();
        Assert.Equal(9, lines.Count);
        Assert.Equal(Docs, PinDrop.FolderOf(lines, At(lines, C)));
        Assert.Equal(Work, PinDrop.FolderOf(lines, At(lines, A)));
        Assert.Null(PinDrop.FolderOf(lines, At(lines, D)));
        Assert.Null(PinDrop.FolderOf(lines, At(lines, Work)));
    }

    [Fact]
    public void TheMiddleOfAHeadingIsIntoIt()
    {
        Assert.Equal(DropZone.Into, PinDrop.Zone(15, 30, holds: true));
        Assert.Equal(DropZone.Before, PinDrop.Zone(5, 30, holds: true));
        Assert.Equal(DropZone.After, PinDrop.Zone(25, 30, holds: true));
        Assert.Equal(DropZone.Before, PinDrop.Zone(14, 30, holds: false));
        Assert.Equal(DropZone.After, PinDrop.Zone(16, 30, holds: false));
        var lines = Lines();
        Assert.True(PinDrop.Holds(lines[At(lines, Work)]));
        Assert.False(PinDrop.Holds(lines[At(lines, News)]), "a live folder takes nothing");
        Assert.False(PinDrop.Holds(lines[At(lines, A)]));
    }

    [Fact]
    public void APinBesideAPinGoesInItsFolder()
    {
        var lines = Lines();
        Assert.Equal(new PinDrop.PinPlace(Work, A, true), PinDrop.ForPin(lines, D, At(lines, A), DropZone.After));
        Assert.Equal(new PinDrop.PinPlace(Docs, C, false), PinDrop.ForPin(lines, D, At(lines, C), DropZone.Before));
        Assert.Equal(new PinDrop.PinPlace(null, D, false), PinDrop.ForPin(lines, A, At(lines, D), DropZone.Before));
        Assert.Null(PinDrop.ForPin(lines, A, At(lines, A), DropZone.After));
    }

    [Fact]
    public void APinIntoAFolderOrAtTheTopOfOne()
    {
        var lines = Lines();
        Assert.Equal(new PinDrop.PinPlace(Docs, null, false), PinDrop.ForPin(lines, D, At(lines, Docs), DropZone.Into));
        // After an open heading: first in it.
        Assert.Equal(new PinDrop.PinPlace(Work, A, false), PinDrop.ForPin(lines, D, At(lines, Work), DropZone.After));
        // Before a heading: at the top of its level's pins.
        Assert.Equal(new PinDrop.PinPlace(Work, B, false), PinDrop.ForPin(lines, A, At(lines, Docs), DropZone.Before));
        Assert.Equal(new PinDrop.PinPlace(null, D, false), PinDrop.ForPin(lines, A, At(lines, Work), DropZone.Before));
        // After a folded heading: beside it, not into it.
        Assert.Equal(new PinDrop.PinPlace(null, D, false), PinDrop.ForPin(lines, A, At(lines, Shut), DropZone.After));
        Assert.Equal(new PinDrop.PinPlace(Shut, null, false), PinDrop.ForPin(lines, A, At(lines, Shut), DropZone.Into));
    }

    [Fact]
    public void APinNeverIntoALiveFolder()
    {
        var lines = Lines();
        Assert.Equal(new PinDrop.PinPlace(null, D, false), PinDrop.ForPin(lines, A, At(lines, News), DropZone.Into));
        var post = lines.ToList().FindIndex(l => l is PinTree.ItemLine);
        Assert.Equal(new PinDrop.PinPlace(null, D, false), PinDrop.ForPin(lines, A, post, DropZone.Before));
    }

    [Fact]
    public void AFolderBeforeAfterAndIntoAnother()
    {
        var folders = Folders();
        var lines = Lines(folders);
        Assert.Equal(new PinTree.FolderPlace(null, Work), PinDrop.ForFolder(lines, Shut, At(lines, Work), DropZone.Before, folders));
        Assert.Equal(new PinTree.FolderPlace(Work), PinDrop.ForFolder(lines, Shut, At(lines, Work), DropZone.Into, folders));
        // After an open heading: first among the folders in it.
        Assert.Equal(new PinTree.FolderPlace(Work, Docs), PinDrop.ForFolder(lines, Shut, At(lines, Work), DropZone.After, folders));
        // After a live heading, or one of its posts: next after it.
        Assert.Equal(new PinTree.FolderPlace(null, Shut), PinDrop.ForFolder(lines, Work, At(lines, News), DropZone.After, folders));
        Assert.Equal(new PinTree.FolderPlace(null, Shut), PinDrop.ForFolder(lines, Work, lines.ToList().FindIndex(l => l is PinTree.ItemLine), DropZone.Before, folders));
        // Over a pin: at the end of that level's folders.
        Assert.Equal(new PinTree.FolderPlace(Work), PinDrop.ForFolder(lines, Shut, At(lines, A), DropZone.Before, folders));
        Assert.Null(PinDrop.ForFolder(lines, Work, At(lines, Work), DropZone.Into, folders));
    }

    [Fact]
    public void WhatTheFoldersMoveRefuses()
    {
        var folders = Folders();
        var lines = Lines(folders);
        // Into its own folder: refused when applied.
        var into = PinDrop.ForFolder(lines, Work, At(lines, Docs), DropZone.Into, folders)!;
        Assert.Null(PinFolders.MovedTo(folders, Work, into));
        // Last after a folded heading: the end of the top.
        var last = PinDrop.ForFolder(lines, Work, At(lines, Shut), DropZone.After, folders)!;
        Assert.Equal(new PinTree.FolderPlace(null), last);
        Assert.Equal([News, Shut, Work], PinFolders.MovedTo(folders, Work, last)!.Where(f => f.Parent is null).Select(f => f.Id));
    }
}
