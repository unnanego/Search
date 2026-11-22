using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// Sync's merge (SyncMerge.cs, the Mac's SyncTests.swift case for case): machines that each write only their own file
/// agree, item by item, whatever order they run in; and the file is the one a Mac writes, to the byte.
public class SyncMergeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"search-sync-merge-{Guid.NewGuid():N}");

    public SyncMergeTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    /// One machine: what it has, what it last agreed, its own clock. A pass reads the others' files from the folder
    /// and writes its own there, through the same encode and decode the app uses.
    internal sealed class Machine
    {
        public readonly string Device, Scope, Folder;
        public Dictionary<string, string> Items;
        public Dictionary<string, SyncEntry>? Base;
        /// Seconds this machine's clock is off by.
        public double Skew;

        public Machine(string device, string root, Dictionary<string, string>? items = null, string scope = "app")
        {
            Device = device;
            Scope = scope;
            Folder = Path.Combine(root, scope);
            Items = items ?? [];
            Directory.CreateDirectory(Folder);
        }

        public string File => Path.Combine(Folder, Device + ".json");

        public Dictionary<string, SyncEntry> Pass(double now, Func<string, string, bool>? understood = null)
        {
            var theirs = new List<IReadOnlyDictionary<string, SyncEntry>>();
            foreach (var path in Directory.GetFiles(Folder))
                if (path != File && SyncMerge.Decode(System.IO.File.ReadAllBytes(path), Scope) is { } read && read.Device != Device) theirs.Add(read.Items);
            var merged = SyncMerge.Pass(Items, Base, theirs, Device, now + Skew, understood);
            Items = SyncMerge.Live(merged);
            Base = merged;
            System.IO.File.WriteAllBytes(File, SyncMerge.Encode(new SyncFile(SyncMerge.Format, Device, Device, Scope, now, merged)));
            return merged;
        }
    }

    private Machine Mac(string device, Dictionary<string, string>? items = null, string scope = "app") => new(device, _folder, items, scope);

    /// Every machine passes until nothing changes: what they all end with.
    private static void Settle(Machine[] machines, double start)
    {
        var now = start;
        for (var round = 0; round < 4; round++)
            foreach (var machine in machines) machine.Pass(now += 1);
    }

    // The merge.

    [Fact]
    public void EditsOnTwoMachinesBothArrive()
    {
        var (a, b) = (Mac("A", new() { ["x"] = "1" }), Mac("B"));
        Settle([a, b], 1000);
        a.Items["a-only"] = "a";
        b.Items["b-only"] = "b";
        Settle([a, b], 2000);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "1", ["a-only"] = "a", ["b-only"] = "b" }, a.Items);
        Assert.Equal(a.Items, b.Items);
    }

    [Fact]
    public void TheLaterEditOfOneItemWins()
    {
        var (a, b) = (Mac("A", new() { ["name"] = "\"Work\"" }), Mac("B"));
        Settle([a, b], 1000);
        a.Items["name"] = "\"Office\"";
        b.Items["name"] = "\"Studio\"";
        a.Pass(2000);
        b.Pass(2001);
        Settle([a, b], 2002);
        Assert.Equal("\"Studio\"", a.Items["name"]);
        Assert.Equal("\"Studio\"", b.Items["name"]);
    }

    [Fact]
    public void ADeletionTravelsAndStaysDeleted()
    {
        var (a, b) = (Mac("A", new() { ["keep"] = "1", ["drop"] = "2" }), Mac("B"));
        Settle([a, b], 1000);
        Assert.Equal("2", b.Items["drop"]);
        a.Items.Remove("drop");
        Settle([a, b], 2000);
        Assert.False(b.Items.ContainsKey("drop"));
        Assert.False(a.Items.ContainsKey("drop"));
        Assert.Equal("1", b.Items["keep"]);
        // Still a tombstone in every file, so a third machine doesn't bring it back.
        Assert.Null(a.Base!["drop"].V);
        Assert.Contains("drop", SyncMerge.Tombstoned(a.Base));
    }

    [Fact]
    public void AnEditAfterADeletionBringsItBackAndADeletionAfterAnEditWins()
    {
        var (a, b) = (Mac("A", new() { ["x"] = "1" }), Mac("B"));
        Settle([a, b], 1000);
        a.Items.Remove("x");
        a.Pass(2000);
        b.Items["x"] = "2";
        b.Pass(2005);
        Settle([a, b], 2010);
        Assert.Equal("2", a.Items["x"]);
        Assert.Equal("2", b.Items["x"]);
        b.Items["x"] = "3";
        b.Pass(3000);
        a.Items.Remove("x");
        a.Pass(3005);
        Settle([a, b], 3010);
        Assert.False(a.Items.ContainsKey("x"));
        Assert.False(b.Items.ContainsKey("x"));
    }

    [Fact]
    public void AMachineWhoseClockIsBehindStillWinsWithAnEditMadeAfterItSawTheValue()
    {
        var (a, b) = (Mac("A", new() { ["x"] = "1" }), Mac("B"));
        b.Skew = 3600;
        Settle([a, b], 1000);
        b.Items["x"] = "from B";
        b.Pass(2000);
        a.Pass(2001);
        Assert.Equal("from B", a.Items["x"]);
        a.Items["x"] = "from A";
        Settle([a, b], 2002);
        Assert.Equal("from A", a.Items["x"]);
        Assert.Equal("from A", b.Items["x"]);
    }

    [Fact]
    public void AMachineJoiningTakesWhatIsAgreedAndAddsWhatOnlyItHas()
    {
        var a = Mac("A", new() { ["set:look"] = "\"dark\"", ["space:shared"] = "\"A's name\"" });
        a.Pass(1000);
        var b = Mac("B", new() { ["set:look"] = "\"light\"", ["space:shared"] = "\"B's name\"", ["space:b"] = "\"mine\"" });
        Settle([b, a], 5000);
        Assert.Equal("\"dark\"", b.Items["set:look"]);
        Assert.Equal("\"A's name\"", b.Items["space:shared"]);
        Assert.Equal("\"mine\"", a.Items["space:b"]);
        Assert.Equal(a.Items, b.Items);
    }

    [Fact]
    public void TheFirstMachineInAnEmptyFolderStampsItsValues() => Assert.Equal(1000, Mac("A", new() { ["x"] = "1" }).Pass(1000)["x"].T);

    [Fact]
    public void TheMergeIsTheSameInAnyOrder()
    {
        IReadOnlyDictionary<string, SyncEntry>[] maps =
        [
            new Dictionary<string, SyncEntry> { ["k"] = new("1", 5, "A"), ["j"] = new(null, 3, "A") },
            new Dictionary<string, SyncEntry> { ["k"] = new("2", 5, "B"), ["j"] = new("x", 3, "A") },
            new Dictionary<string, SyncEntry> { ["k"] = new("0", 4, "C"), ["i"] = new("y", 1, "C") },
        ];
        var one = SyncMerge.Merge(maps);
        Assert.True(SyncMerge.Same(one, SyncMerge.Merge(Enumerable.Reverse(maps))));
        Assert.True(SyncMerge.Same(one, SyncMerge.Merge([maps[1], maps[2], maps[0]])));
        Assert.Equal("2", one["k"].V); // same time: the device id decides
        Assert.Null(one["j"].V); // same time and device: the deletion
    }

    [Fact]
    public void TheLastTieIsTheLargerText()
    {
        Assert.True(SyncMerge.Wins(new("b", 1, "A"), new("a", 1, "A")));
        Assert.False(SyncMerge.Wins(new("a", 1, "A"), new("a", 1, "A")));
        // Swift orders by Unicode scalar: an emoji (beyond the BMP) is after U+FF5E, whatever UTF-16 says.
        Assert.True(SyncMerge.Wins(new("\U0001F600", 1, "A"), new("\uFF5E", 1, "A")));
    }

    [Fact]
    public void ANewPassWithNothingChangedChangesNothing()
    {
        var (a, b) = (Mac("A", new() { ["x"] = "1" }), Mac("B", new() { ["y"] = "2" }));
        Settle([a, b], 1000);
        var before = a.Base;
        var written = SyncMerge.Decode(File.ReadAllBytes(a.File), "app");
        var after = a.Pass(9000);
        Assert.True(SyncMerge.Same(before, after));
        Assert.True(SyncMerge.Same(written!.Items, SyncMerge.Decode(File.ReadAllBytes(a.File), "app")!.Items));
    }

    [Fact]
    public void OldDeletionsAreForgotten()
    {
        var year = SyncMerge.TombstoneLife;
        var kept = SyncMerge.Prune(new Dictionary<string, SyncEntry> { ["gone"] = new(null, 10, "A"), ["recent"] = new(null, year, "A"), ["here"] = new("1", 10, "A") }, year + 100);
        Assert.False(kept.ContainsKey("gone"));
        Assert.True(kept.ContainsKey("recent"));
        Assert.True(kept.ContainsKey("here"));
    }

    [Fact]
    public void TheStampIsLaterThanAnythingSeen()
    {
        var stamped = SyncMerge.Stamp(new Dictionary<string, string> { ["x"] = "2" }, new Dictionary<string, SyncEntry> { ["x"] = new("1", 5000, "B") }, "A", 1000);
        Assert.Equal(5000.001, stamped["x"].T, 6);
        Assert.Equal("A", stamped["x"].D);
    }

    // Files that can't be trusted.

    [Fact]
    public void DamagedAndForeignFilesAreIgnoredAndDeleteNothing()
    {
        var a = Mac("A", new() { ["x"] = "1", ["y"] = "2" });
        a.Pass(1000);
        File.WriteAllText(Path.Combine(a.Folder, "broken.json"), "{not json");
        File.WriteAllBytes(Path.Combine(a.Folder, "empty.json"), []);
        File.WriteAllBytes(Path.Combine(a.Folder, "N.json"), SyncMerge.Encode(new SyncFile(99, "N", null, "app", 1, new Dictionary<string, SyncEntry> { ["x"] = new(null, 1e12, "N") })));
        File.WriteAllBytes(Path.Combine(a.Folder, "P.json"), SyncMerge.Encode(new SyncFile(1, "P", null, "profile-other", 1, new Dictionary<string, SyncEntry> { ["y"] = new(null, 1e12, "P") })));
        // A readable file with one bad stamp: that entry alone is left out.
        File.WriteAllBytes(Path.Combine(a.Folder, "O.json"), SyncMerge.Encode(new SyncFile(1, "O", null, "app", 1, new Dictionary<string, SyncEntry> { ["x"] = new(null, -5, "O"), ["z"] = new("3", 2000, "O") })));
        a.Pass(3000);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2", ["z"] = "3" }, a.Items);
        Assert.Null(SyncMerge.Decode("[]"u8.ToArray(), "app"));
    }

    [Fact]
    public void AFileSwiftWouldNotDecodeIsNotReadAtAll()
    {
        // A stamp that isn't a number fails the whole file in Swift's decoder, and so here.
        Assert.Null(SyncMerge.Decode("""{"device":"A","format":1,"items":{"x":{"d":"A","t":"soon","v":"1"}},"scope":"app","written":1}"""u8.ToArray(), "app"));
        Assert.Null(SyncMerge.Decode("""{"device":"A","format":1,"items":{"x":{"d":"A","t":1,"v":2}},"scope":"app","written":1}"""u8.ToArray(), "app"));
        Assert.Null(SyncMerge.Decode("""{"device":"A","format":1,"items":{},"name":3,"scope":"app","written":1}"""u8.ToArray(), "app"));
        Assert.Null(SyncMerge.Decode("""{"device":"","format":1,"items":{},"scope":"app","written":1}"""u8.ToArray(), "app"));
        // A null name or value is no name or value; a key it doesn't know is passed over.
        var read = SyncMerge.Decode("""{"device":"A","format":1,"items":{"x":{"d":"A","t":1,"v":null}},"name":null,"scope":"app","written":1,"later":true}"""u8.ToArray(), "app");
        Assert.NotNull(read);
        Assert.Null(read.Name);
        Assert.Null(read.Items["x"].V);
    }

    [Fact]
    public void AnItemThisBuildCantReadIsCarriedAndNeverDeleted()
    {
        bool Reads(string key, string _) => !key.StartsWith("future:", StringComparison.Ordinal);
        var (a, b) = (Mac("A", new() { ["x"] = "1" }), Mac("B"));
        a.Items["future:thing"] = "{}";
        a.Pass(1000, Reads);
        b.Pass(1001, Reads);
        b.Items.Remove("future:thing");
        b.Pass(1002, Reads);
        Assert.Equal("{}", b.Base!["future:thing"].V);
        a.Pass(1003, Reads);
        Assert.Equal("{}", a.Items["future:thing"]);
        Assert.False(SyncItems.Understood("future:thing", "{}"));
        Assert.False(SyncItems.Understood($"space:{SwiftJson.Uuid(Guid.NewGuid())}", "{broken"));
        Assert.True(SyncItems.Understood("set:look", "\"dark\""));
    }

    [Fact]
    public void AMacSettingWindowsHasNoPlaceForIsCarriedAlong()
    {
        // The Mac's glyph setting: a PC never has it, never deletes it, and hands it back as it was.
        var mac = Mac("MAC", new() { ["set:glyph"] = "\"icons\"", ["set:look"] = "\"dark\"" });
        mac.Pass(1000);
        var pc = Mac("PC", new() { ["set:look"] = "\"light\"" });
        pc.Pass(1001, SyncItems.Understood);
        pc.Items.Remove("set:glyph");
        pc.Pass(1002, SyncItems.Understood);
        mac.Pass(1003);
        Assert.Equal("\"icons\"", mac.Items["set:glyph"]);
        Assert.Equal("\"dark\"", pc.Items["set:look"]);
        Assert.False(SyncItems.Understood("set:glyph", "\"icons\""));
        Assert.True(SyncItems.Understood("set:shield", "true"));
    }

    [Fact]
    public void EveryBookmarkVanishingAtOnceIsNotDeletedEverywhere()
    {
        var tree = Enumerable.Range(0, 6).Select(i => Bookmark.Site($"Site {i}", new Uri($"https://s{i}.example/"))).ToList();
        var scope = Sync.Scope(Profile.PersonalId);
        var (a, b) = (Mac("A", SyncItems.Bookmarks(tree), scope), Mac("B", null, scope));
        Settle([a, b], 1000);
        a.Items = [];
        Settle([a, b], 2000);
        Assert.Equal(6, SyncItems.ApplyBookmarks(b.Items).Count);
        Assert.Equal(6, SyncItems.ApplyBookmarks(a.Items).Count);
        // One removed on purpose still goes.
        a.Items = SyncItems.Bookmarks(tree.Skip(1).ToList());
        Settle([a, b], 3000);
        Assert.Equal(tree.Skip(1).Select(t => t.Id), SyncItems.ApplyBookmarks(b.Items).Select(t => t.Id));
    }

    [Fact]
    public void EachProfileIsItsOwnFolder()
    {
        var (work, home) = (Guid.NewGuid(), Guid.NewGuid());
        var aWork = Mac("A", new() { ["x"] = "work" }, Sync.Scope(work));
        var aHome = Mac("A", new() { ["x"] = "home" }, Sync.Scope(home));
        var bWork = Mac("B", null, Sync.Scope(work));
        var bHome = Mac("B", null, Sync.Scope(home));
        Settle([aWork, aHome, bWork, bHome], 1000);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "work" }, bWork.Items);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "home" }, bHome.Items);
        Assert.Equal("profile-" + SwiftJson.Uuid(work), Sync.Scope(work));
    }

    // Spaces.

    private static Space S(string name, Guid? id = null, int colour = 0, string? icon = null, bool? shares = true) => new(id ?? Guid.NewGuid(), name, colour) { Icon = icon, SharesSignIns = shares };

    [Fact]
    public void SpacesGoAndComeBackTheSame()
    {
        List<Space> list = [Space.First(), S("Work", colour: 2, icon: "briefcase"), S("Play", shares: false)];
        var items = SyncItems.Spaces(list);
        var back = SyncItems.ApplySpaces(items, [], Profile.PersonalId, Space.FirstId);
        Assert.Equal(list.Select(s => s.Id), back.Select(s => s.Id));
        Assert.Equal(items, SyncItems.Spaces(back));
        Assert.False(back[2].SharesSignIns);
    }

    [Fact]
    public void AReorderOnOneMachineAndANewSpaceOnTheOtherBothStay()
    {
        var (work, play) = (S("Work"), S("Play"));
        var scope = Sync.Scope(Profile.PersonalId);
        var (a, b) = (Mac("A", SyncItems.Spaces([Space.First(), work, play]), scope), Mac("B", null, scope));
        Settle([a, b], 1000);
        var bList = SyncItems.ApplySpaces(b.Items, [], Profile.PersonalId, Space.FirstId);
        bList.Add(S("New"));
        b.Items = SyncItems.Spaces(bList);
        b.Pass(2000);
        a.Items = SyncItems.Spaces([play, Space.First(), work]);
        a.Pass(2001);
        Settle([a, b], 2002);
        var final = SyncItems.ApplySpaces(a.Items, [], Profile.PersonalId, Space.FirstId);
        Assert.Equal(["Play", "Personal", "Work", "New"], final.Select(s => s.Name));
        Assert.Equal(final.Select(s => s.Id), SyncItems.ApplySpaces(b.Items, bList, Profile.PersonalId, Space.FirstId).Select(s => s.Id));
    }

    [Fact]
    public void ASpaceDeletedElsewhereGoesButTheHomeNeverDoes()
    {
        var work = S("Work");
        List<Space> local = [Space.First(), work];
        var items = SyncItems.Spaces(local);
        items.Remove(SyncItems.SpaceKey(work.Id));
        items.Remove(SyncItems.SpaceKey(Space.FirstId));
        Assert.Equal([Space.FirstId], SyncItems.ApplySpaces(items, local, Profile.PersonalId, Space.FirstId).Select(s => s.Id));
    }

    [Fact]
    public void WhatIsOnlyThisMachinesStaysAndSignInsNeverChangeOnASpaceThatExists()
    {
        var mine = S("Work", shares: false) with { Downloads = @"C:\Users\me\Work" };
        var theirs = mine with { Name = "Studio", SharesSignIns = true, Downloads = "/Users/them/Elsewhere" };
        var after = SyncItems.ApplySpaces(SyncItems.Spaces([Space.First(), theirs]), [Space.First(), mine], Profile.PersonalId, Space.FirstId);
        Assert.Equal("Studio", after[1].Name);
        Assert.Equal(@"C:\Users\me\Work", after[1].Downloads);
        Assert.False(after[1].SharesSignIns);
        // And this machine doesn't then argue: its items say what was agreed.
        var agreed = SyncItems.Spaces([Space.First(), theirs]);
        Assert.Equal(agreed, SyncItems.Spaces(after, agreed));
    }

    [Fact]
    public void ASpaceOfAnotherProfileIsMadeInThatProfile()
    {
        var profile = Guid.NewGuid();
        var home = S("Clients");
        Assert.Equal(profile, SyncItems.ApplySpaces(SyncItems.Spaces([home]), [], profile, home.Id)[0].Profile);
        Assert.Null(SyncItems.ApplySpaces(SyncItems.Spaces([home]), [], Profile.PersonalId, Space.FirstId).First(s => s.Id == home.Id).Profile);
    }

    [Fact]
    public void ASpaceWithNoNameIsCalledSpace() =>
        Assert.Equal("Space", SyncItems.ApplySpaces(new Dictionary<string, string> { [SyncItems.SpaceKey(Guid.NewGuid())] = """{"colour":0,"name":""}""" }, [], Profile.PersonalId, Space.FirstId).Last().Name);

    [Fact]
    public void AGradientSyncsAsAnItemOfItsOwn()
    {
        var studio = new Space(Guid.NewGuid(), "Studio", 2) { SharesSignIns = true };
        var plain = SyncItems.Spaces([Space.First(), studio]);
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("gradient:", StringComparison.Ordinal));
        studio = studio with { Gradient = new SpaceGradient(["#4FB3A9", "#E8A13A"], 45, true) };
        var items = SyncItems.Spaces([Space.First(), studio]);
        Assert.Equal(plain[SyncItems.SpaceKey(studio.Id)], items[SyncItems.SpaceKey(studio.Id)]);
        Assert.Equal("""{"angle":45,"colours":["#4FB3A9","#E8A13A"],"grain":true}""", items[SyncItems.GradientKey(studio.Id)]);
        var back = SyncItems.ApplySpaces(items, [], Profile.PersonalId, Space.FirstId);
        Assert.Equal(studio.Gradient, back.First(s => s.Id == studio.Id).Gradient);
        Assert.Equal(items, SyncItems.Spaces(back));
        var gone = SyncItems.ApplySpaces(plain, back, Profile.PersonalId, Space.FirstId);
        Assert.Null(gone.First(s => s.Id == studio.Id).Gradient);
        Assert.Equal(2, gone.First(s => s.Id == studio.Id).Colour);
    }

    [Fact]
    public void AStrengthSyncsAsAnItemOfItsOwn()
    {
        var studio = new Space(Guid.NewGuid(), "Studio", 2) { SharesSignIns = true };
        var plain = SyncItems.Spaces([Space.First(), studio]);
        Assert.Equal(plain, SyncItems.Spaces([Space.First(), studio with { Strength = SpaceStrength.Subtle }]));
        studio = studio with { Strength = SpaceStrength.Vivid };
        var items = SyncItems.Spaces([Space.First(), studio]);
        Assert.Equal("\"vivid\"", items[SyncItems.StrengthKey(studio.Id)]);
        var back = SyncItems.ApplySpaces(items, [], Profile.PersonalId, Space.FirstId);
        Assert.Equal(SpaceStrength.Vivid, back.First(s => s.Id == studio.Id).Strength);
        Assert.Null(SyncItems.ApplySpaces(plain, back, Profile.PersonalId, Space.FirstId).First(s => s.Id == studio.Id).Strength);
        Assert.True(SyncItems.Understood(SyncItems.StrengthKey(studio.Id), "\"rich\""));
        Assert.False(SyncItems.Understood(SyncItems.StrengthKey(studio.Id), "\"ultra\""));
    }

    [Fact]
    public void AnOlderSearchCarriesTheGradientAlong()
    {
        var studio = new Space(Guid.NewGuid(), "Studio", 2) { SharesSignIns = true, Gradient = new SpaceGradient(["#4FB3A9", "#E8A13A"]) };
        var newer = SyncMerge.Pass(SyncItems.Spaces([Space.First(), studio]), null, [], "new", 1000);
        bool Older(string key, string text) => !key.StartsWith("gradient:", StringComparison.Ordinal) && SyncItems.Understood(key, text);
        var after = SyncMerge.Pass(SyncItems.Spaces([Space.First(), studio with { Gradient = null }]), newer, [newer], "old", 2000, Older);
        Assert.Equal(newer[SyncItems.GradientKey(studio.Id)], after[SyncItems.GradientKey(studio.Id)]);
        Assert.Equal(newer[SyncItems.SpaceKey(studio.Id)], after[SyncItems.SpaceKey(studio.Id)]);
    }

    // Pins.

    private static PinDef P(string letter, string home, string title = "Title", bool? listed = null) => new(Guid.NewGuid(), letter, home, title) { Listed = listed };

    [Fact]
    public void PinsGoAndComeBackTheSame()
    {
        var s = Guid.NewGuid();
        List<PinDef> own = [P("M", "https://mail.example/"), P("C", "https://cal.example/", listed: true)];
        List<PinDef> shared = [P("N", "https://notes.example/")];
        var items = SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = own }, shared);
        var (bySpace, back) = SyncItems.ApplyPins(items, [s], new Dictionary<Guid, IReadOnlyList<PinDef>>(), []);
        Assert.Equal<PinDef>(own, bySpace[s]);
        Assert.Equal<PinDef>(shared, back);
    }

    [Fact]
    public void APinsPageTitleNeverCountsAsAnEdit()
    {
        var s = Guid.NewGuid();
        var mail = P("M", "https://mail.example/", "Inbox (3)");
        var agreed = SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = [mail] }, []);
        Assert.Equal(agreed, SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = [mail with { Title = "Inbox (4)" }] }, [], agreed));
    }

    [Fact]
    public void APinSharedOnOneMachineAndEditedOnAnotherIsShownOnce()
    {
        var s = Guid.NewGuid();
        var mail = P("M", "https://mail.example/");
        var items = SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = [mail] }, [mail]);
        foreach (var (k, v) in SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = [mail with { Letter = "E" }] }, [])) items[k] = v;
        var (bySpace, shared) = SyncItems.ApplyPins(items, [s], new Dictionary<Guid, IReadOnlyList<PinDef>>(), []);
        Assert.Equal([mail.Id], shared.Select(d => d.Id));
        Assert.False(bySpace.ContainsKey(s));
    }

    [Fact]
    public void PinsOfSpacesNotAskedForAreLeftAlone()
    {
        var (mine, another) = (Guid.NewGuid(), Guid.NewGuid());
        var items = SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [mine] = [P("A", "https://a.example/")], [another] = [P("B", "https://b.example/")] }, []);
        Assert.Equal([mine], SyncItems.ApplyPins(items, [mine], new Dictionary<Guid, IReadOnlyList<PinDef>>(), []).BySpace.Keys);
    }

    [Fact]
    public void APinKeepsItsFolderHere()
    {
        var s = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var row = P("D", "https://docs.example/", listed: true) with { Folder = folder };
        var items = SyncItems.Pins(new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = [row with { Folder = null }] }, []);
        Assert.Equal(folder, SyncItems.ApplyPins(items, [s], new Dictionary<Guid, IReadOnlyList<PinDef>> { [s] = [row] }, []).BySpace[s][0].Folder);
        Assert.DoesNotContain("folder", items[SyncItems.PinKey(SwiftJson.Uuid(s), row.Id)]);
    }

    // Bookmarks.

    [Fact]
    public void BookmarksGoAndComeBackTheSame()
    {
        List<Bookmark> tree =
        [
            Bookmark.Site("Example", new Uri("https://example.com/")),
            Bookmark.Folder("Work", [Bookmark.Site("Docs", new Uri("https://docs.example/")), Bookmark.Folder("Deep", [Bookmark.Site("Deeper", new Uri("https://deep.example/"))])]),
            Bookmark.Folder("Empty", []),
        ];
        var back = SyncItems.ApplyBookmarks(SyncItems.Bookmarks(tree));
        Assert.True(Bookmarks.Same(tree, back));
        Assert.Equal(SyncItems.Bookmarks(tree), SyncItems.Bookmarks(back));
    }

    [Fact]
    public void ABookmarkAddedToAFolderDeletedElsewhereIsKept()
    {
        var docs = Bookmark.Site("Docs", new Uri("https://docs.example/"));
        var scope = Sync.Scope(Profile.PersonalId);
        var (a, b) = (Mac("A", SyncItems.Bookmarks([Bookmark.Folder("Work", [docs])]), scope), Mac("B", null, scope));
        Settle([a, b], 1000);
        a.Items = SyncItems.Bookmarks([]);
        a.Pass(2000);
        var added = Bookmark.Site("New", new Uri("https://new.example/"));
        var bTree = SyncItems.ApplyBookmarks(b.Items);
        bTree[0] = bTree[0] with { Children = [.. bTree[0].Children!, added] };
        b.Items = SyncItems.Bookmarks(bTree);
        b.Pass(2001);
        Settle([a, b], 2002);
        Assert.Equal([added.Id], SyncItems.ApplyBookmarks(a.Items).Select(n => n.Id));
        Assert.Equal([added.Id], SyncItems.ApplyBookmarks(b.Items).Select(n => n.Id));
    }

    [Fact]
    public void TwoFoldersMovedIntoEachOtherLoseNothing()
    {
        var (one, two) = (Bookmark.Folder("One", []), Bookmark.Folder("Two", []));
        var items = SyncItems.Bookmarks([one, two]);
        items.Remove(SyncItems.ChildrenKey(SyncItems.RootFolder));
        items[SyncItems.ChildrenKey(SwiftJson.Uuid(one.Id))] = SortedJson.Strings([SwiftJson.Uuid(two.Id)]);
        items[SyncItems.ChildrenKey(SwiftJson.Uuid(two.Id))] = SortedJson.Strings([SwiftJson.Uuid(one.Id)]);
        Assert.Equal(2, Bookmarks.Folders(SyncItems.ApplyBookmarks(items)).Count);
    }

    // The texts and the file, as a Mac writes them.

    [Fact]
    public void ItemTextsAreTheMacs()
    {
        var id = new Guid("0E984725-C51C-4BF4-9960-E1C80E27ABA0");
        Assert.Equal("""{"colour":1,"icon":"terminal","name":"Work","sharesSignIns":false}""", new SyncedSpace("Work", 1, "terminal", false).Text());
        Assert.Equal("""{"colour":0,"name":"Café \"Ō\" 😀"}""", new SyncedSpace("Café \"Ō\" 😀", 0).Text());
        Assert.Equal("""{"home":"https://mail.example/inbox","letter":"M","listed":true,"name":"Mail"}""", new SyncedPin("M", "https://mail.example/inbox", "Mail", true).Text());
        Assert.Equal("""{"title":"A\nB\u0001","url":"https://x.example/a/b"}""", new SyncedBookmark("A\nB\u0001", "https://x.example/a/b").Text());
        Assert.Equal($"""["{SwiftJson.Uuid(id)}"]""", SyncItems.Spaces([new Space(id, "W")])[SyncItems.SpacesOrder]);
    }

    /// A profile's file as a Mac writes it: keys sorted Foundation's way, the item texts with their quotes and slashes
    /// escaped inside, a tombstone with no "v", an item from a newer Search, a stamp of 0 from a Mac that joined.
    internal const string MacScope = "profile-00000000-0000-0000-0000-0000000000A1";
    internal static readonly string MacFile = string.Concat("""
        {"device":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","format":1,"items":{
        "bm:AAAAAAAA-0000-4000-8000-000000000001":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000.25,"v":"{\"title\":\"Docs\",\"url\":\"https:\/\/example.com\/docs\"}"},
        "bm:BBBBBBBB-0000-4000-8000-000000000002":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"{\"title\":\"Reading\"}"},
        "bm:FFFFFFFF-0000-4000-8000-000000000003":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800100.5},
        "gradient:EEEEEEEE-0000-4000-8000-000000000004":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":0,"v":"{\"angle\":135,\"colours\":[\"#4FB3A9\",\"#5E8FE0\"],\"grain\":false}"},
        "kids:BBBBBBBB-0000-4000-8000-000000000002":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"[\"AAAAAAAA-0000-4000-8000-000000000001\"]"},
        "kids:root":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"[\"BBBBBBBB-0000-4000-8000-000000000002\"]"},
        "note:CCCCCCCC-0000-4000-8000-000000000005":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"{\"later\":[1,2]}"},
        "order:pins:00000000-0000-0000-0000-000000000001":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"[\"DDDDDDDD-0000-4000-8000-000000000006\"]"},
        "order:spaces":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"[\"00000000-0000-0000-0000-000000000001\",\"EEEEEEEE-0000-4000-8000-000000000004\"]"},
        "pin:00000000-0000-0000-0000-000000000001:DDDDDDDD-0000-4000-8000-000000000006":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"{\"home\":\"https:\/\/mail.example.com\/\",\"letter\":\"M\",\"listed\":true}"},
        "space:00000000-0000-0000-0000-000000000001":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"{\"colour\":0,\"icon\":\"house\",\"name\":\"Personal\"}"},
        "space:EEEEEEEE-0000-4000-8000-000000000004":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":0,"v":"{\"colour\":2,\"icon\":\"briefcase\",\"name\":\"Work\",\"sharesSignIns\":false}"},
        "strength:EEEEEEEE-0000-4000-8000-000000000004":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":0,"v":"\"rich\""},
        "title:pin:DDDDDDDD-0000-4000-8000-000000000006":{"d":"5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47","t":1763800000,"v":"\"Inbox – Mail\""}
        },"name":"Ana’s MacBook Air","scope":"profile-00000000-0000-0000-0000-0000000000A1","written":1763800200.125}
        """.Split('\n').Select(line => line.Trim()));

    [Fact]
    public void AMacsFileReadsAndWritesBackToTheByte()
    {
        var bytes = Encoding.UTF8.GetBytes(MacFile);
        var file = SyncMerge.Decode(bytes, MacScope);
        Assert.NotNull(file);
        Assert.Equal(("5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47", "Ana’s MacBook Air", 1763800200.125), (file.Device, file.Name, file.Written));
        Assert.Equal(14, file.Items.Count);
        Assert.Equal("""{"title":"Docs","url":"https://example.com/docs"}""", file.Items["bm:AAAAAAAA-0000-4000-8000-000000000001"].V);
        Assert.Null(file.Items["bm:FFFFFFFF-0000-4000-8000-000000000003"].V);
        Assert.Equal(0, file.Items["space:EEEEEEEE-0000-4000-8000-000000000004"].T);
        Assert.Equal(MacFile, Encoding.UTF8.GetString(SyncMerge.Encode(file)));
        // In another profile's folder it is not read.
        Assert.Null(SyncMerge.Decode(bytes, "app"));
    }

    [Fact]
    public void AMacsFileComesInAsSpacesPinsAndBookmarks()
    {
        var live = SyncMerge.Live(SyncMerge.Decode(Encoding.UTF8.GetBytes(MacFile), MacScope)!.Items);
        var spaces = SyncItems.ApplySpaces(live, [Space.First()], Profile.PersonalId, Space.FirstId);
        Assert.Equal(["Personal", "Work"], spaces.Select(s => s.Name));
        var work = spaces[1];
        Assert.Equal((2, "briefcase", (bool?)false, SpaceStrength.Rich), (work.Colour, work.Icon, work.SharesSignIns, work.Strength));
        Assert.Equal(["#4FB3A9", "#5E8FE0"], work.Gradient!.Colours);
        var (bySpace, _) = SyncItems.ApplyPins(live, [Space.FirstId], new Dictionary<Guid, IReadOnlyList<PinDef>>(), []);
        var pin = Assert.Single(bySpace[Space.FirstId]);
        Assert.Equal(("M", "https://mail.example.com/", "Inbox – Mail", (bool?)true), (pin.Letter, pin.Home, pin.Title, pin.Listed));
        var tree = SyncItems.ApplyBookmarks(live);
        var folder = Assert.Single(tree);
        Assert.Equal("Reading", folder.Title);
        Assert.Equal("https://example.com/docs", Assert.Single(folder.Children!).Url);
        // Made again here, they are the very texts the Mac wrote: nothing to stamp.
        var again = SyncItems.Spaces(spaces, live);
        foreach (var (key, text) in SyncItems.Pins(bySpace, [], live)) again[key] = text;
        foreach (var (key, text) in SyncItems.Bookmarks(tree)) again[key] = text;
        foreach (var (key, text) in again) Assert.Equal(live[key], text);
        Assert.Equal(live.Keys.Where(k => !k.StartsWith("note:", StringComparison.Ordinal)).Order(), again.Keys.Order());
    }

    [Fact]
    public void TheAgreedStateReadsBack()
    {
        var map = new Dictionary<string, SyncEntry> { ["a/b"] = new("\"x/y\"", 12.5, "D"), ["gone"] = new(null, 3, "D") };
        Assert.True(SyncMerge.Same(map, SyncMerge.DecodeState(SyncMerge.EncodeState(map))));
        Assert.Null(SyncMerge.DecodeState("[1]"u8.ToArray()));
    }

    [Fact]
    public void KeysAreInFoundationsOrder()
    {
        string[] keys = ["b10", "B2", "a", "b9", "_x", "A"];
        Assert.Equal(["_x", "A", "a", "B2", "b9", "b10"], keys.Order(SortedJson.KeyOrder));
        Assert.True(SortedJson.CompareKeys("bm:9ABC", "bm:12AB") < 0);
        Assert.True(SortedJson.CompareKeys("kids:BBBB", "kids:root") < 0);
    }

    [Fact]
    public void NumbersAreSwifts()
    {
        Assert.Equal("135", SortedJson.Number(135.0));
        Assert.Equal("0.5", SortedJson.Number(0.5));
        Assert.Equal("1763800000.25", SortedJson.Number(1763800000.25));
        Assert.Equal("1e-05", SortedJson.Number(0.00001));
        Assert.Equal("0", SortedJson.Number(0.0));
    }

    [Fact]
    public void TheSameValueWrittenAnotherWayIsTheSame()
    {
        Assert.True(SortedJson.Same("""{"angle":135.0,"colours":["#1"],"grain":false}""", """{"grain":false,"colours":["#1"],"angle":135}"""));
        Assert.False(SortedJson.Same("""{"colour":0,"name":"A"}""", """{"colour":0,"name":"A","sharesSignIns":false}"""));
        Assert.False(SortedJson.Same("{broken", "{broken!"));
    }

    [Fact]
    public void OrderedKeepsTheOrderThenTheFallbackThenTheRest()
    {
        Assert.Equal(["c", "a", "b", "d", "e"], SyncMerge.Ordered(new HashSet<string> { "a", "b", "c", "d", "e" }, ["c", "x", "a"], ["b", "a", "d"]).Take(5));
        Assert.Equal(["b", "z"], SyncMerge.Ordered(new HashSet<string> { "z", "b" }, []));
    }
}
