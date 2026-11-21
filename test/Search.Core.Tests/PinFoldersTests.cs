using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class PinFoldersTests
{
    private const string Space = "5C2C3B2E-4D5E-4F60-8A1B-2C3D4E5F6A7B";
    private const string Other = "C9A646D3-9C61-4CB7-BFCD-EE2522C8F633";
    private const string Work = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";
    private const string Inner = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string Live = "1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB";
    private const string Post = "A1B2C3D4-0000-4000-8000-000000000001";

    public PinFoldersTests() => TempStore.Ready();

    /// As a Mac writes it: a folder in a folder, one with no folded state and a parent that doesn't read, a live one
    /// with its posts, and a space whose list doesn't read (a folder without a name), which is that space's alone.
    private const string MacFile = $$"""
        {"{{Space}}":[{"id":"{{Work}}","name":"Work","collapsed":true},{"id":"{{Inner}}","name":"Inner","parent":"{{Work}}","collapsed":false},
                      {"id":"{{Live}}","name":"Blog","parent":"not-a-uuid","feed":"https:\/\/blog.example\/feed","items":[{"id":"{{Post}}","title":"Hello","link":"https:\/\/blog.example\/hello"}]}],
         "{{Other}}":[{"id":"{{Work}}"}],
         "not-a-space":[]}
        """;

    private static PinFolder F(string name, Guid? parent = null, bool collapsed = false) => new(Guid.NewGuid(), name) { Parent = parent, Collapsed = collapsed };
    private static PinnedTab Row(Guid? folder = null, bool everywhere = false) => new(Guid.NewGuid()) { Pin = "R", Listed = true, Folder = folder, Everywhere = everywhere };

    [Fact]
    public void AMacFileReads()
    {
        var file = TempStore.Unique("pin-folders");
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File(file), MacFile);
        var folders = new PinFolders(file);
        var list = folders.Folders(Guid.Parse(Space));
        Assert.Equal(3, list.Count);
        Assert.Equal(new PinFolder(Guid.Parse(Work), "Work") { Collapsed = true }, list[0]);
        Assert.Equal(Guid.Parse(Work), list[1].Parent);
        Assert.Null(list[2].Parent);
        Assert.True(list[2].IsLive);
        Assert.Equal(new FeedItem(Guid.Parse(Post), "Hello", "https://blog.example/hello"), Assert.Single(list[2].Items!));
        Assert.Empty(folders.Folders(Guid.Parse(Other)));
        Assert.True(File.Exists(Store.File(file)));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var space = Guid.Parse(Space);
        var json = Encoding.UTF8.GetString(PinFolders.Encode(new Dictionary<Guid, IReadOnlyList<PinFolder>>
        {
            [space] = [new PinFolder(Guid.Parse(Work), "Work"), new PinFolder(Guid.Parse(Inner), "Inner") { Parent = Guid.Parse(Work), Collapsed = true },
                       new PinFolder(Guid.Parse(Live), "Blog") { Feed = "https://blog.example/feed", Items = [new FeedItem(Guid.Parse(Post), "Hello", "https://blog.example/hello")] }],
            [Guid.NewGuid()] = [],
        }));
        Assert.Equal($$"""{"{{Space}}":[{"id":"{{Work}}","name":"Work","collapsed":false},{"id":"{{Inner}}","name":"Inner","parent":"{{Work}}","collapsed":true},{"id":"{{Live}}","name":"Blog","collapsed":false,"feed":"https:\/\/blog.example\/feed","items":[{"id":"{{Post}}","title":"Hello","link":"https:\/\/blog.example\/hello"}]}]}""", json);
        Assert.Equal(3, PinFolders.Decode(Encoding.UTF8.GetBytes(json))![space].Count);
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("[1,2]")]
    public void AFileThatIsntAnObjectIsSetAside(string contents)
    {
        var file = TempStore.Unique("pin-folders");
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.File(file), contents);
        Assert.Empty(new PinFolders(file).BySpace);
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, Path.GetFileNameWithoutExtension(file) + ".unreadable-*.json"));
    }

    [Fact]
    public async Task SetSanitizesKeepsAndTells()
    {
        var file = TempStore.Unique("pin-folders");
        var folders = new PinFolders(file);
        var space = Guid.NewGuid();
        var heard = 0;
        folders.Changed += _ => heard++;
        var a = F("A");
        folders.Set(space, [a, a with { Name = "dup" }, F("Orphan", Guid.NewGuid())]);
        Assert.Equal(new[] { "A", "Orphan" }, folders.Folders(space).Select(f => f.Name));
        Assert.Null(folders.Folders(space)[1].Parent);
        folders.Set(space, folders.Folders(space).ToList());
        Assert.Equal(1, heard);
        folders.Set(space, folders.Folders(space).ToList(), always: true);
        Assert.Equal(2, heard);
        await Disk.Flush();
        Assert.Equal(folders.Folders(space), PinFolders.Decode(await File.ReadAllBytesAsync(Store.File(file)))![space]);
        folders.Forget(space);
        Assert.Empty(folders.Folders(space));
    }

    [Fact]
    public void SanitizedCutsCyclesAndDepth()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var cycle = PinTree.Sanitized([new PinFolder(a, "A") { Parent = b }, new PinFolder(b, "B") { Parent = a }]);
        Assert.Contains(cycle, f => f.Parent is null);
        var self = PinTree.Sanitized([new PinFolder(a, "A") { Parent = a }]);
        Assert.Null(self[0].Parent);
        var one = F("1");
        var two = F("2", one.Id);
        var three = F("3", two.Id);
        var four = F("4", three.Id);
        var deep = PinTree.Sanitized([one, two, three, four]);
        Assert.Equal(two.Id, deep[3].Parent);
        var live = new PinFolder(Guid.NewGuid(), "Feed") { Feed = "https://f/" };
        Assert.Null(PinTree.Sanitized([live, F("In", live.Id)])[1].Parent);
    }

    [Fact]
    public void WhatMayGoWhere()
    {
        var one = F("1");
        var two = F("2", one.Id);
        var three = F("3", two.Id);
        var loose = F("Loose");
        var live = new PinFolder(Guid.NewGuid(), "Feed") { Feed = "https://f/" };
        IReadOnlyList<PinFolder> all = [one, two, three, loose, live];
        Assert.Equal(3, PinTree.Depth(three.Id, all));
        Assert.Equal(3, PinTree.Height(one.Id, all));
        Assert.Equal(new[] { two.Id, one.Id }, PinTree.Ancestors(three.Id, all));
        Assert.True(PinTree.CanMake(two.Id, all));
        Assert.False(PinTree.CanMake(three.Id, all));
        Assert.False(PinTree.CanMake(live.Id, all));
        Assert.False(PinTree.CanMake(Guid.NewGuid(), all));
        Assert.True(PinTree.CanMove(loose.Id, two.Id, all));
        Assert.False(PinTree.CanMove(loose.Id, three.Id, all));
        Assert.False(PinTree.CanMove(one.Id, three.Id, all));
        Assert.True(PinTree.CanMove(two.Id, loose.Id, all));
        Assert.False(PinTree.CanMove(one.Id, loose.Id, all));
        Assert.False(PinTree.CanMove(loose.Id, live.Id, all));
        Assert.True(PinTree.CanMove(three.Id, null, all));
    }

    [Fact]
    public void TheLinesAsTheColumnDrawsThem()
    {
        var work = F("Work");
        var inner = F("Inner", work.Id, collapsed: true);
        var live = new PinFolder(Guid.NewGuid(), "Blog") { Feed = "https://f/", Items = [new FeedItem(Guid.NewGuid(), "Post", "https://f/1")] };
        var (top, inWork, inInner, lost, shown, shared) = (Row(), Row(work.Id), Row(inner.Id), Row(Guid.NewGuid()), Row(inner.Id), Row(work.Id, everywhere: true));
        var lines = PinFolders.RowLines([top, inWork, inInner, lost, shown, shared], [work, inner, live], showing: shown.Id);
        Assert.Equal(new PinTree.Line[]
        {
            new PinTree.FolderLine(work, 0), new PinTree.FolderLine(inner, 1), new PinTree.PinLine(shown.Id, 2), new PinTree.PinLine(inWork.Id, 1),
            new PinTree.FolderLine(live, 0), new PinTree.ItemLine(live.Items![0], live.Id, 1),
            new PinTree.PinLine(top.Id, 0), new PinTree.PinLine(lost.Id, 0), new PinTree.PinLine(shared.Id, 0),
        }, lines);
        var folded = PinTree.Lines([(inWork.Id, work.Id)], [work with { Collapsed = true }]);
        Assert.Equal(new PinTree.Line[] { new PinTree.FolderLine(work with { Collapsed = true }, 0) }, folded);
    }

    [Fact]
    public void OrderedOpensFoldedFolders()
    {
        var work = F("Work", collapsed: true);
        var (a, b, c) = (new PinDef(Guid.NewGuid(), "A", "a", "A"), new PinDef(Guid.NewGuid(), "B", "b", "B") { Folder = work.Id }, new PinDef(Guid.NewGuid(), "C", "c", "C"));
        Assert.Equal(new[] { b, a, c }, PinTree.Ordered([a, b, c], [work]));
    }

    [Fact]
    public void RemovingAFolderLiftsWhatItHeld()
    {
        var outer = F("Outer");
        var gone = F("Gone", outer.Id);
        var child = F("Child", gone.Id);
        var pin = new PinDef(Guid.NewGuid(), "P", "p", "P") { Folder = gone.Id };
        var (folders, pins) = PinTree.Removing(gone.Id, [outer, gone, child], [pin]);
        Assert.Equal(new[] { outer, child with { Parent = outer.Id } }, folders);
        Assert.Equal(outer.Id, pins[0].Folder);
        var (left, up) = PinFolders.Deleted([outer, gone, child], gone.Id)!.Value;
        Assert.Equal(outer.Id, up);
        Assert.Equal(2, left.Count);
        Assert.Null(PinFolders.Deleted([outer], Guid.NewGuid()));
    }

    [Fact]
    public void NewFolderGoesWhereItMay()
    {
        var one = F("1");
        var two = F("2", one.Id);
        var three = F("3", two.Id);
        IReadOnlyList<PinFolder> all = [one, two, three];
        var (made, id) = PinFolders.Made(all, "  Reading  ", one.Id)!.Value;
        Assert.Equal(("Reading", one.Id), (made[^1].Name, made[^1].Parent));
        Assert.Equal(id, made[^1].Id);
        Assert.Null(PinFolders.Made(all, parent: three.Id));
        var beside = PinFolders.Made(all, "", holding: Row(three.Id))!.Value;
        Assert.Equal((PinFolders.DefaultName, two.Id), (beside.Folders[^1].Name, beside.Folders[^1].Parent));
        Assert.Null(PinFolders.Made(all, holding: new PinnedTab(Guid.NewGuid()) { Pin = "S" }));
        Assert.Null(PinFolders.Made(all, holding: Row(everywhere: true)));
    }

    [Fact]
    public void FoldersRenamedFoldedMoved()
    {
        var (a, b) = (F("A"), F("B"));
        IReadOnlyList<PinFolder> all = [a, b];
        Assert.Equal("Z", PinFolders.Renamed(all, a.Id, "  Z ")![0].Name);
        Assert.Equal(200, PinFolders.Renamed(all, a.Id, new string('x', 300))![0].Name.Length);
        Assert.Null(PinFolders.Renamed(all, a.Id, "   "));
        Assert.True(PinFolders.Folded(all, a.Id, true)![0].Collapsed);
        Assert.Null(PinFolders.Folded(all, a.Id, false));
        Assert.True(PinFolders.Toggled(all, b.Id)![1].Collapsed);
        Assert.Equal(a.Id, PinFolders.MovedInto(all, b.Id, a.Id)![1].Parent);
        Assert.Null(PinFolders.MovedInto(all, a.Id, a.Id));
        Assert.Null(PinFolders.MovedInto(all, a.Id, null));
        Assert.Equal(new[] { b.Id, a.Id }, PinFolders.MovedTo(all, b.Id, new PinTree.FolderPlace(null, a.Id))!.Select(f => f.Id));
        Assert.Equal(new[] { b.Id, a.Id }, PinTree.Moving(a.Id, new PinTree.FolderPlace(null), all).Select(f => f.Id));
        Assert.Null(PinFolders.MovedTo(all, b.Id, new PinTree.FolderPlace(null)));
    }

    [Fact]
    public void APinMovesIntoAFolderAndTheRowFollows()
    {
        var work = F("Work");
        var live = new PinFolder(Guid.NewGuid(), "Feed") { Feed = "https://f/" };
        var square = new PinnedTab(Guid.NewGuid()) { Pin = "S" };
        var (a, b, loose) = (Row(), Row(), new PinnedTab(Guid.NewGuid()));
        var row = PinFolders.MovePin([square, a, b, loose], b.Id, work.Id, [work, live])!;
        Assert.Equal(new[] { square.Id, b.Id, a.Id, loose.Id }, row.Select(t => t.Id));
        Assert.Equal(work.Id, row[1].Folder);
        var intoLive = PinFolders.MovePin([square, a, b, loose], b.Id, live.Id, [work, live])!;
        Assert.Null(intoLive.First(t => t.Id == b.Id).Folder);
        var before = PinFolders.MovePin([square, a, b, loose], b.Id, null, [work], anchor: a.Id)!;
        Assert.Equal(new[] { square.Id, b.Id, a.Id, loose.Id }, before.Select(t => t.Id));
        Assert.Null(PinFolders.MovePin([square, a], square.Id, work.Id, [work]));
        var shared = Row(everywhere: true);
        Assert.Equal(new[] { square.Id, shared.Id, a.Id, loose.Id }, PinFolders.Arranged([a, square, loose, shared], [work]).Select(t => t.Id));
    }

    [Fact]
    public void WithoutEmptyKeepsFoldersThatHoldPins()
    {
        var outer = F("Outer");
        var inner = F("Inner", outer.Id);
        var empty = F("Empty");
        var kept = F("Kept");
        var pin = new PinDef(Guid.NewGuid(), "P", "p", "P") { Folder = inner.Id };
        Assert.Equal(new[] { outer, inner, kept }, PinTree.WithoutEmpty([outer, inner, empty, kept], new HashSet<Guid> { kept.Id }, [pin]));
    }
}
