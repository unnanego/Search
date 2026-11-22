using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class PinsTests
{
    private const string Space = "5C2C3B2E-4D5E-4F60-8A1B-2C3D4E5F6A7B";
    private const string Mail = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";
    private const string Hub = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string Folder = "1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB";
    private const string Work = "C9A646D3-9C61-4CB7-BFCD-EE2522C8F633";

    public PinsTests() => TempStore.Ready();

    /// As a Mac writes it: a space's pins, Personal's shared ones, another profile's, and a key this version doesn't know.
    private const string MacFile = $$"""
        {"{{Space}}":[{"id":"{{Mail}}","letter":"M","home":"https:\/\/mail.example.com\/inbox","title":"Inbox","name":"Mail"},
                      {"id":"{{Hub}}","letter":"G","home":"https:\/\/github.com","title":"GitHub","listed":true,"folder":"{{Folder}}","icon":"x"}],
         "shared":[{"id":"{{Folder}}","letter":"N","home":"https:\/\/news.example.com","title":"News"}],
         "shared:{{Work}}":[{"id":"{{Work}}","letter":"W","home":"https:\/\/work.example.com","title":"Work"}]}
        """;

    private static (Pins Pins, string File) Fresh(string? contents = null, IEnumerable<Guid>? spaces = null)
    {
        var file = TempStore.Unique("pins");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new Pins(file, spaces ?? []), file);
    }

    private static PinDef Def(string letter, string home, Guid? id = null) => new(id ?? Guid.NewGuid(), letter, home, letter + " page");

    [Fact]
    public void AMacFileReads()
    {
        var (pins, _) = Fresh(MacFile);
        var defs = pins.Defs(Guid.Parse(Space));
        Assert.Equal(2, defs.Count);
        Assert.Equal(new PinDef(Guid.Parse(Mail), "M", "https://mail.example.com/inbox", "Inbox") { Name = "Mail" }, defs[0]);
        Assert.Equal(new PinDef(Guid.Parse(Hub), "G", "https://github.com", "GitHub") { Listed = true, Folder = Guid.Parse(Folder) }, defs[1]);
        Assert.Equal("N", Assert.Single(pins.Shared(Pins.PersonalProfile)).Letter);
        Assert.Equal("W", Assert.Single(pins.Shared(Guid.Parse(Work))).Letter);
        Assert.Empty(pins.Defs(Guid.NewGuid()));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var space = Guid.Parse(Space);
        var json = Encoding.UTF8.GetString(Pins.Encode(new Dictionary<Guid, IReadOnlyList<PinDef>> { [space] = [new PinDef(Guid.Parse(Mail), "M", "https://mail.example.com/", "Inbox")] }, []));
        Assert.Equal($$"""{"{{Space}}":[{"id":"{{Mail}}","letter":"M","home":"https:\/\/mail.example.com\/","title":"Inbox"}]}""", json);
        var both = Encoding.UTF8.GetString(Pins.Encode(new Dictionary<Guid, IReadOnlyList<PinDef>>(), [new PinDef(Guid.Parse(Hub), "G", "g", "G") { Listed = true, Folder = Guid.Parse(Folder) }],
            new Dictionary<Guid, IReadOnlyList<PinDef>> { [Guid.Parse(Work)] = [new PinDef(Guid.Parse(Work), "W", "w", "W") { Name = "Job" }], [Guid.NewGuid()] = [] }));
        Assert.Equal($$"""{"shared":[{"id":"{{Hub}}","letter":"G","home":"g","title":"G","listed":true,"folder":"{{Folder}}"}],"shared:{{Work}}":[{"id":"{{Work}}","letter":"W","home":"w","title":"W","name":"Job"}]}""", both);
    }

    [Fact]
    public void KeepsTheSplitAPinWasPinnedIn()
    {
        var pair = Guid.NewGuid();
        var def = new PinDef(Guid.Parse(Hub), "G", "g", "G") { Listed = true, Pair = pair };
        var json = Pins.Encode(new Dictionary<Guid, IReadOnlyList<PinDef>> { [Guid.Parse(Space)] = [def] }, []);
        Assert.Contains($"\"pair\":\"{SwiftJson.Uuid(pair)}\"", Encoding.UTF8.GetString(json));
        Assert.Equal(def, Pins.Decode(json)!.Value.BySpace[Guid.Parse(Space)][0]);
        // Through a window's row and back: the pair is carried, not dropped.
        var (row, _) = Pins.Reconcile([], [def], new HashSet<Guid>());
        Assert.Equal(pair, Pins.Defs(row).Defs[0].Pair);
    }

    [Fact]
    public void WhatIsWrittenReadsBack()
    {
        var bytes = Encoding.UTF8.GetBytes(MacFile);
        var (bySpace, shared) = Pins.Decode(bytes)!.Value;
        var again = Pins.Decode(Pins.Encode(bySpace, shared, Pins.SharedLists(bytes).Where(p => p.Key != Pins.PersonalProfile).ToDictionary()))!.Value;
        Assert.Equal(bySpace[Guid.Parse(Space)], again.BySpace[Guid.Parse(Space)]);
        Assert.Equal(shared, again.Shared);
        Assert.Equal(2, Pins.SharedLists(bytes).Count);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData($$"""{"{{Space}}":[{"id":"{{Mail}}","home":"h","title":"t"}]}""")]
    [InlineData($$"""{"{{Space}}":[{"id":"{{Mail}}","letter":"M","home":"h","title":"t","listed":"yes"}]}""")]
    [InlineData($$$"""{"shared":{"id":"{{{Mail}}}"}}""")]
    public void AFileThatWontDecodeIsSetAside(string contents)
    {
        var (pins, file) = Fresh(contents);
        Assert.Empty(pins.BySpace);
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, Path.GetFileNameWithoutExtension(file) + ".unreadable-*.json"));
    }

    [Fact]
    public async Task WithNoFileThePinsComeFromTheSessions()
    {
        var space = Guid.NewGuid();
        var id = Guid.NewGuid();
        await Session.Write(new Session.Shape([new("https://a.example/now", "A") { Pin = "A", PinId = id, Home = "https://a.example/", Name = "Ay" }, new("https://b.example/", "B") { Pin = "B" }, new("https://c.example/", "C")], 0), now: true, space: space);
        var (pins, file) = Fresh(spaces: [space]);
        var defs = pins.Defs(space);
        Assert.Equal(2, defs.Count);
        Assert.Equal(new PinDef(id, "A", "https://a.example/", "A") { Name = "Ay" }, defs[0]);
        Assert.Equal(("B", "https://b.example/"), (defs[1].Letter, defs[1].Home));
        await Disk.Flush();
        Assert.Equal(defs, Pins.Decode(await File.ReadAllBytesAsync(Store.File(file)))!.Value.BySpace[space]);
    }

    [Fact]
    public async Task SetKeepsWritesAndTells()
    {
        var (pins, file) = Fresh();
        var space = Guid.NewGuid();
        var heard = new List<PinsChange>();
        pins.Changed += heard.Add;
        var window = new object();
        IReadOnlyList<PinDef> defs = [Def("A", "https://a.example/")];
        pins.Set(space, defs, window);
        pins.Set(space, defs.ToList(), window);
        Assert.Equal(new PinsChange(window, space, null), Assert.Single(heard));
        await Disk.Flush();
        Assert.Equal(defs, Pins.Decode(await File.ReadAllBytesAsync(Store.File(file)))!.Value.BySpace[space]);
        pins.Set(space, []);
        Assert.False(pins.BySpace.ContainsKey(space));
        Assert.Equal(2, heard.Count);
    }

    [Fact]
    public async Task SharedPinsAreEachProfilesOwn()
    {
        var (pins, file) = Fresh();
        var other = Guid.NewGuid();
        var a = Def("A", "a");
        Assert.True(pins.SetShared(Pins.PersonalProfile, [a, Def("B", "b")]));
        Assert.False(pins.SetShared(Pins.PersonalProfile, pins.Shared(Pins.PersonalProfile).ToList()));
        Assert.True(pins.SetShared(other, [Def("C", "c")]));
        Assert.True(pins.Unshare(Pins.PersonalProfile, a.Id));
        Assert.Equal("B", Assert.Single(pins.Shared(Pins.PersonalProfile)).Letter);
        await Disk.Flush();
        var lists = Pins.SharedLists(await File.ReadAllBytesAsync(Store.File(file)));
        Assert.Equal("B", Assert.Single(lists[Pins.PersonalProfile]).Letter);
        Assert.Equal("C", Assert.Single(lists[other]).Letter);
        pins.ForgetShared(other);
        Assert.Empty(pins.Shared(other));
    }

    [Fact]
    public void AdoptTellsOfEachChange()
    {
        var (pins, _) = Fresh();
        var (one, two) = (Guid.NewGuid(), Guid.NewGuid());
        pins.Set(one, [Def("A", "a")]);
        var heard = new List<PinsChange>();
        pins.Changed += heard.Add;
        var same = pins.Defs(one);
        pins.Adopt(new Dictionary<Guid, IReadOnlyList<PinDef>> { [one] = same, [two] = [Def("B", "b")] }, [one, two], [Def("S", "s")], Pins.PersonalProfile);
        Assert.Equal(new[] { new PinsChange(null, two, null), new PinsChange(null, null, Pins.PersonalProfile) }, heard);
        pins.Adopt(new Dictionary<Guid, IReadOnlyList<PinDef>>(), [one], pins.Shared(Pins.PersonalProfile), Pins.PersonalProfile);
        Assert.Empty(pins.Defs(one));
    }

    [Fact]
    public void ASpaceForgottenTakesItsFolders()
    {
        var (pins, _) = Fresh();
        var folders = new PinFolders(TempStore.Unique("pin-folders"));
        var space = Guid.NewGuid();
        pins.Set(space, [Def("A", "a")]);
        folders.Set(space, [new PinFolder(Guid.NewGuid(), "Work")]);
        pins.Forget(space, folders);
        Assert.Empty(pins.Defs(space));
        Assert.Empty(folders.Folders(space));
    }

    [Fact]
    public void TheTwoListsWorkedOut()
    {
        var (a, b, c) = (Def("A", "a") with { Folder = Guid.NewGuid() }, Def("B", "b"), Def("C", "c"));
        var shared = Pins.Share(a.Id, [a, b], [c]);
        Assert.Equal(new[] { b }, shared.Own);
        Assert.Equal(new[] { c, a with { Folder = null } }, shared.Shared);
        Assert.Equal((IReadOnlyList<PinDef>)[b], Pins.Share(Guid.NewGuid(), [b], []).Own);
        var back = Pins.Unshare(c.Id, [b], [c, a]);
        Assert.Equal(new[] { c, b }, back.Own);
        Assert.Equal(new[] { a }, back.Shared);
        var split = Pins.Split([a, b, c], new HashSet<Guid> { c.Id });
        Assert.Equal(new[] { a, b }, split.Own);
        Assert.Equal(new[] { c }, split.Shared);
        Assert.Equal(new[] { c, a, b }, Pins.Row([a, c, b], [c], showing: true));
        Assert.Equal(new[] { a, c, b }, Pins.Row([a, c, b], [c], showing: false));
        Assert.Null(Pins.SharedToWrite([], showing: true));
        Assert.Null(Pins.SharedToWrite([a], showing: false));
        Assert.Equal(new[] { a }, Pins.SharedToWrite([a], showing: true)!);
    }

    private static PinnedTab P(string? pin = null, Guid? pinId = null, bool listed = false, string? home = null, string? url = null, bool everywhere = false, Guid? folder = null, bool @private = false) =>
        new(Guid.NewGuid()) { Pin = pin, PinId = pinId, Listed = listed, Home = home, Url = url ?? home, Everywhere = everywhere, Folder = folder, Private = @private, Title = pin ?? "loose" };

    [Fact]
    public void ReconcileMatchesByIdThenLetterAndPageThenPlace()
    {
        var (a, b, c, d) = (Def("A", "https://a/"), Def("B", "https://b/") with { Listed = true, Name = "Bee" }, Def("C", "https://c/"), Def("D", "https://d/"));
        var byId = P("A", a.Id, home: "https://a/");
        var byPage = P("B", home: "https://b/");
        var byPlace = P("Z", home: "https://z/");
        var gone = P("Q", Guid.NewGuid(), home: "https://q/");
        var loose = P(url: "https://loose/");
        var (row, left) = Pins.Reconcile([loose, byPlace, gone, byPage, byId], [b, a, c, d], new HashSet<Guid> { a.Id });
        Assert.Equal(new[] { gone.Id }, left);
        Assert.Equal(byId.Id, row[0].Id);
        Assert.True(row[0].Everywhere);
        Assert.Equal(byPlace.Id, row[1].Id);
        Assert.Equal(("C", c.Id, "https://c/"), (row[1].Pin, row[1].PinId, row[1].Home));
        Assert.True(row[2].Fresh);
        Assert.Equal(("D", "https://d/", "D page"), (row[2].Pin, row[2].Url, row[2].Title));
        Assert.Equal(byPage.Id, row[3].Id);
        Assert.Equal((true, "Bee", b.Id), (row[3].Listed, row[3].Name, row[3].PinId));
        Assert.Equal(loose, row[4]);
    }

    [Fact]
    public void ReconcileWithFoldersPutsRowsInFolderOrder()
    {
        var folder = new PinFolder(Guid.NewGuid(), "Work");
        var (top, inside) = (Def("T", "t") with { Listed = true }, Def("I", "i") with { Listed = true, Folder = folder.Id });
        var (row, _) = Pins.Reconcile([], [top, inside], new HashSet<Guid>(), [folder]);
        Assert.Equal(new[] { "I", "T" }, row.Select(t => t.Pin));
    }

    [Fact]
    public void DefsAreTheRowsPinsWithIds()
    {
        var folder = Guid.NewGuid();
        var (defs, row) = Pins.Defs([P(url: "x"), P("A", home: "https://a/"), P("R", Guid.NewGuid(), listed: true, url: "https://r/", folder: folder), P("E", Guid.NewGuid(), listed: true, home: "e", everywhere: true, folder: folder), P("P", @private: true)]);
        Assert.Equal(new[] { "A", "R", "E" }, defs.Select(d => d.Letter));
        Assert.Equal(row[1].PinId, defs[0].Id);
        Assert.Null(defs[0].Listed);
        Assert.Equal((true, folder, "https://r/"), (defs[1].Listed, defs[1].Folder, defs[1].Home));
        Assert.Null(defs[2].Folder);
        Assert.Null(row[0].PinId);
    }

    [Fact]
    public void TheOldestWindowsSessionKeepsItsPins()
    {
        var (a, b, fresh) = (Def("A", "https://a/"), Def("B", "https://b/") with { Listed = true }, Def("N", "https://n/") with { Name = "New" });
        var shape = new Session.Shape([
            new("https://loose1/", "L1"), new("https://b/x", "B") { Pin = "B", Home = "https://b/" }, new("https://gone/", "G") { Pin = "G", PinId = Guid.NewGuid() },
            new("https://a/", "A") { Pin = "X", PinId = a.Id }, new("https://loose2/", "L2") { GroupId = Guid.NewGuid() }, new("https://loose3/", "L3")], 4)
        { Splits = [new Session.Split([4, 5], [0.5, 0.5]) { Focused = 5 }] };
        var kept = Pins.InSession(shape, [a, b, fresh]);
        Assert.Equal(new[] { "https://a/", "https://n/", "https://b/x", "https://loose1/", "https://loose2/", "https://loose3/" }, kept.Tabs.Select(e => e.Url));
        Assert.Equal(("A", a.Id), (kept.Tabs[0].Pin, kept.Tabs[0].PinId));
        Assert.Equal(new Session.Entry("https://n/", "N page") { Pin = "N", Name = "New", Home = "https://n/", PinId = fresh.Id }, kept.Tabs[1]);
        Assert.Equal((true, b.Id), (kept.Tabs[2].Listed, kept.Tabs[2].PinId));
        Assert.Equal(4, kept.Active);
        Assert.Equal(new[] { 4, 5 }, Assert.Single(kept.Splits).Tabs);
        Assert.Equal(5, kept.Splits[0].Focused);
        Assert.Equal(1, Pins.InSession(shape with { Active = 2 }, [a]).Active);
        Assert.Equal(-1, Pins.InSession(shape with { Active = -1 }, [a]).Active);
        Assert.Equal(Pins.FromSession(kept).Select(d => d.Id), new[] { a.Id, fresh.Id, b.Id });
    }

    [Fact]
    public void LettersAndHomes()
    {
        Assert.Equal("G", Pins.Monogram(new Uri("https://www.github.com/x")));
        Assert.Equal("•", Pins.Monogram(null));
        Assert.Equal("R", Pins.Monogram(new Uri("file:///C:/Users/me/report.pdf")));
        Assert.Equal("H", Pins.Monogram(null, "History"));
        Assert.Equal("Q", Pins.Letter("  quick "));
        Assert.Null(Pins.Letter("   "));
        Assert.True(Pins.SamePage("https://a.example/", "https://a.example"));
        Assert.False(Pins.SamePage("https://a.example/", null));
        Assert.Equal("https://a/", Pins.GoHome(P("A", home: "https://a/", url: "https://a/deep")));
        Assert.Null(Pins.GoHome(P("A", home: "https://a/", url: "https://a")));
        Assert.Null(Pins.GoHome(P(url: "https://a/")));
        Assert.Equal("https://b.example/", Pins.HomeOf(new Session.Entry("https://b.example/", "") { Pin = "B" }));
        Assert.Equal("h", Pins.HomeOf(new Session.Entry("u", "") { Pin = "B", Home = "h" }));
        Assert.Null(Pins.HomeOf(new Session.Entry("u", "")));
    }

    [Fact]
    public void PinningAndUnpinning()
    {
        var loose = P(url: "https://www.mail.example/inbox");
        var pinned = Pins.Pinned(loose, listed: true)!;
        Assert.Equal(("M", "https://www.mail.example/inbox", true), (pinned.Pin, pinned.Home, pinned.Listed));
        Assert.Null(Pins.Pinned(P(url: "https://a/", @private: true)));
        var un = Pins.Unpinned(pinned with { PinId = Guid.NewGuid(), Everywhere = true, Folder = Guid.NewGuid() });
        Assert.True(un.Pin is null && un.Home is null && un.PinId is null && un.Folder is null && !un.Listed && !un.Everywhere);
        Assert.Null(Pins.Listing(pinned with { Folder = Guid.NewGuid() }, false).Folder);
    }

    [Fact]
    public void APinTakesItsPlace()
    {
        var (s1, s2, r1, l1, l2) = (P("A"), P("B"), P("R", listed: true), P(), P());
        var square = P("N");
        Assert.Equal(new[] { s1, s2, square, r1, l1 }, Pins.Settled([s1, s2, r1, l1, square], square.Id).ToList());
        var row = P("M", listed: true);
        Assert.Equal(new[] { s1, r1, row, l1 }, Pins.Settled([row, s1, r1, l1], row.Id).ToList());
        Assert.Equal(new[] { s1, row, r1, l1 }, Pins.Settled([row, s1, r1, l1], row.Id, first: true).ToList());
        var gone = P();
        Assert.Equal(new[] { s1, r1, gone, l1, l2 }, Pins.Unpinning([gone, s1, r1, l1, l2], gone.Id).ToList());
    }

    [Fact]
    public void DroppedPinsKeepTheSharedOnesAhead()
    {
        var (e1, e2, own, row1, loose) = (P("E", everywhere: true), P("F", everywhere: true), P("O"), P("R", listed: true), P());
        IReadOnlyList<PinnedTab> line = [e1, e2, own, row1, loose];
        var carried = P("N");
        Assert.Equal(2, Pins.Slot(line, carried, false, 0, showsShared: true));
        Assert.Equal(0, Pins.Slot(line, carried, false, 0, showsShared: false));
        Assert.Equal(3, Pins.Slot(line, carried, false, 99, showsShared: true));
        Assert.Equal(1, Pins.Slot(line, e1, false, 5, showsShared: true));
        var dropped = Pins.Dropped(line, carried, false, 0, showsShared: true);
        Assert.Equal(new[] { e1.Id, e2.Id, carried.Id, own.Id, row1.Id, loose.Id }, dropped.Select(t => t.Id));
        var asRow = Pins.Dropped(line, own, true, 0, showsShared: true);
        Assert.Equal(new[] { e1.Id, e2.Id, own.Id, row1.Id, loose.Id }, asRow.Select(t => t.Id));
        Assert.True(asRow[2].Listed);
        var mixed = Pins.KeepBlocks([own, row1, e1, loose, P("G", listed: true, everywhere: true)]);
        Assert.Equal(new[] { "E", "O", "G", "R", null }, mixed.Select(t => t.Pin));
    }
}
