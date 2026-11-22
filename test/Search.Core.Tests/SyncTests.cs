using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// Sync's pass (Sync.cs) against a folder a Mac writes in: the layout, a Mac's spaces, pins, bookmarks and settings
/// coming in, this PC's going out, what never changes being written once, and a Mac setting with no Windows home
/// riding along. The Mac is SyncMergeTests.Machine: the merge and file the Mac's Sync.swift runs.
public class SyncTests : IDisposable
{
    private const string MacDevice = "5D1E2B9A-7C1F-4E5B-9E33-2B8C6A1F0D47";
    private static readonly Guid Work = new("EEEEEEEE-0000-4000-8000-000000000004");
    private static readonly Guid MailPin = new("DDDDDDDD-0000-4000-8000-000000000006");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"search-sync-{Guid.NewGuid():N}");

    public SyncTests() { TempStore.Ready(); Directory.CreateDirectory(_folder); }
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    /// One PC: its models in files of their own, sync on with the test's folder.
    private sealed class World
    {
        public readonly Dictionary<string, string> Notes = [];
        public readonly Prefs Prefs = new();
        public readonly Profiles Profiles = new(TempStore.Unique("profiles"));
        public readonly Spaces Spaces;
        public readonly Pins Pins = new(TempStore.Unique("pins"), []);
        public readonly Dictionary<Guid, Bookmarks> Marks = [];
        public readonly Sync Sync;

        public World(string folder)
        {
            Spaces = new Spaces(TempStore.Unique("spaces"), Notes, Profiles.HomeOf);
            Sync = new Sync(Notes, Prefs, Spaces, Pins, Profiles, Bookmarks, name: "Test PC");
            Sync.TurnOn(folder);
        }

        public Bookmarks Bookmarks(Guid profile) => Marks.TryGetValue(profile, out var made) ? made : Marks[profile] = new Bookmarks(TempStore.Unique("bookmarks"), Notes);
    }

    private string Root => Path.Combine(_folder, Sync.Inside);
    private SyncMergeTests.Machine Mac(string scope, Dictionary<string, string>? items = null) => new(MacDevice, Root, items, scope);
    private static string Personal => Sync.Scope(Profile.PersonalId);

    /// The Mac's own profile file (SyncMergeTests.MacFile) and an app file with its settings, put in the folder.
    private void MacWrote()
    {
        Directory.CreateDirectory(Path.Combine(Root, Personal));
        File.WriteAllText(Path.Combine(Root, Personal, MacDevice + ".json"), SyncMergeTests.MacFile);
        var settings = new Dictionary<string, SyncEntry>
        {
            ["set:look"] = new("\"dark\"", 1763800000, MacDevice), ["set:glyph"] = new("\"icons\"", 1763800000, MacDevice),
            ["set:spaces"] = new("true", 1763800000, MacDevice), ["set:search.custom"] = new("\"https://kagi.com/search?q=%s\"", 1763800000, MacDevice),
            ["set:tabs.close.after"] = new("24", 1763800000, MacDevice),
        };
        Directory.CreateDirectory(Path.Combine(Root, Sync.AppScope));
        File.WriteAllBytes(Path.Combine(Root, Sync.AppScope, MacDevice + ".json"), SyncMerge.Encode(new SyncFile(1, MacDevice, "Ana’s MacBook Air", Sync.AppScope, 1763800200, settings)));
    }

    private static SyncFile Own(World pc, string scope) =>
        SyncMerge.Decode(File.ReadAllBytes(Path.Combine(pc.Sync.Root!, scope, pc.Sync.Device + ".json")), scope) ?? throw new Xunit.Sdk.XunitException("no file");

    [Fact]
    public async Task TheFirstPassLaysTheFolderOutAsTheMacDoes()
    {
        var pc = new World(_folder);
        var result = pc.Sync.Run();
        await Disk.Flush();
        Assert.True(result.Ran);
        Assert.Null(result.Problem);
        Assert.Equal(_folder, pc.Notes[Sync.FolderKey]);
        Assert.Equal(pc.Sync.Device, pc.Notes[Sync.DeviceKey]);
        Assert.NotNull(SwiftJson.Uuid(pc.Sync.Device));
        Assert.Equal(["app", "profile-00000000-0000-0000-0000-0000000000A1"], Directory.GetDirectories(Root).Select(Path.GetFileName).Order());
        var app = Own(pc, Sync.AppScope);
        Assert.Equal(("Test PC", SyncMerge.Format), (app.Name, app.Format));
        Assert.Equal(SyncSettings.Mapped.Select(k => "set:" + k).Order(), app.Items.Keys.Order());
        Assert.Equal("\"system\"", app.Items["set:look"].V);
        var personal = Own(pc, Personal);
        Assert.Equal("""{"colour":0,"name":"Personal"}""", personal.Items[SyncItems.SpaceKey(Space.FirstId)].V);
        Assert.True(File.Exists(Sync.StateFile(Personal)));
        Assert.NotNull(pc.Sync.LastSynced);
    }

    [Fact]
    public async Task AMacsThingsComeInAndItsOtherSettingsRideAlong()
    {
        MacWrote();
        var pc = new World(_folder);
        var result = pc.Sync.Run();
        await Disk.Flush();
        Assert.True(result.Settings);
        Assert.Contains(Profile.PersonalId, result.Profiles);
        Assert.Equal(["Ana’s MacBook Air"], result.Others);
        Assert.Equal(MacDevice, Assert.Single(pc.Sync.Peers).Device);
        // Settings: the ones Windows keeps, where it keeps them.
        Assert.Equal(Theme.Dark, pc.Prefs.Theme);
        Assert.True(pc.Spaces.UsesSpaces);
        Assert.Equal("https://kagi.com/search?q=%s", pc.Prefs.CustomEngine);
        Assert.Equal("24", pc.Notes[Closing.Key]);
        // Spaces, pins, bookmarks.
        Assert.Equal(["Personal", "Work"], pc.Spaces.Of(Profile.PersonalId).Select(s => s.Name));
        Assert.Equal(SpaceStrength.Rich, pc.Spaces.Find(Work)!.Strength);
        Assert.Equal("M", Assert.Single(pc.Pins.Defs(Space.FirstId)).Letter);
        Assert.Equal("Inbox – Mail", pc.Pins.Defs(Space.FirstId)[0].Title);
        Assert.Equal("Reading", Assert.Single(pc.Bookmarks(Profile.PersonalId).Roots).Title);
        // The glyph setting has no home here: carried as the Mac stamped it, never deleted.
        var app = Own(pc, Sync.AppScope);
        Assert.Equal(new SyncEntry("\"icons\"", 1763800000, MacDevice), app.Items["set:glyph"]);
        // The item from a newer Search, too.
        Assert.Equal("{\"later\":[1,2]}", Own(pc, Personal).Items["note:CCCCCCCC-0000-4000-8000-000000000005"].V);
    }

    [Fact]
    public async Task WhatCameInIsNotStampedAgain()
    {
        MacWrote();
        var pc = new World(_folder);
        // While the Mac's deletion is younger than a year, so it is kept.
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1763900000));
        await Disk.Flush();
        var mac = SyncMerge.Decode(File.ReadAllBytes(Path.Combine(Root, Personal, MacDevice + ".json")), Personal)!.Items;
        var mine = Own(pc, Personal).Items;
        // Every item the Mac had is as the Mac stamped it: the PC made the same texts, so nothing looked edited.
        foreach (var (key, entry) in mac) Assert.Equal(entry, mine[key]);
        var written = File.GetLastWriteTimeUtc(Path.Combine(Root, Personal, pc.Sync.Device + ".json"));
        var again = pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1763900100));
        await Disk.Flush();
        Assert.False(again.Changed);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(Root, Personal, pc.Sync.Device + ".json")));
    }

    [Fact]
    public async Task TheSameValueWrittenAnotherWayIsNotAnEdit()
    {
        var scope = Personal;
        var mac = Mac(scope, new()
        {
            [SyncItems.SpaceKey(Work)] = """{"colour":2,"name":"Work","sharesSignIns":true}""",
            // Not how a Mac writes it, but how some machine might: the PC must not argue.
            [SyncItems.GradientKey(Work)] = """{"grain":false,"angle":135.0,"colours":["#4FB3A9","#5E8FE0"]}""",
        });
        mac.Pass(1000);
        var pc = new World(_folder);
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(2000));
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(3000));
        await Disk.Flush();
        Assert.Equal(["#4FB3A9", "#5E8FE0"], pc.Spaces.Find(Work)!.Gradient!.Colours);
        Assert.Equal(new SyncEntry(mac.Items[SyncItems.GradientKey(Work)], 1000, MacDevice), Own(pc, scope).Items[SyncItems.GradientKey(Work)]);
    }

    [Fact]
    public async Task AnEditHereReachesTheMacAndADeletionThereComesHere()
    {
        MacWrote();
        var pc = new World(_folder);
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1763900000));
        await Disk.Flush();
        var mac = Mac(Personal, SyncMerge.Live(SyncMerge.Decode(Encoding.UTF8.GetBytes(SyncMergeTests.MacFile), Personal)!.Items));
        mac.Base = SyncMerge.Decode(Encoding.UTF8.GetBytes(SyncMergeTests.MacFile), Personal)!.Items.ToDictionary();
        pc.Spaces.Rename(Work, "Studio");
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1763900100));
        await Disk.Flush();
        mac.Pass(1763900200);
        Assert.Equal("""{"colour":2,"icon":"briefcase","name":"Studio","sharesSignIns":false}""", mac.Items[SyncItems.SpaceKey(Work)]);
        // The Mac deletes the space: here it goes, its pins and session with it, and the window is told.
        pc.Pins.Set(Work, [new PinDef(Guid.NewGuid(), "W", "https://work.example/", "Work")]);
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1763900300));
        await Disk.Flush();
        mac.Pass(1763900400);
        foreach (var key in mac.Items.Keys.Where(k => k.Contains(SwiftJson.Uuid(Work), StringComparison.Ordinal)).ToList()) mac.Items.Remove(key);
        mac.Items[SyncItems.SpacesOrder] = SortedJson.Strings([SwiftJson.Uuid(Space.FirstId)]);
        mac.Pass(1763900500);
        var result = pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1763900600));
        Assert.Equal([Work], result.RemovedSpaces);
        Assert.Null(pc.Spaces.Find(Work));
        Assert.Empty(pc.Pins.Defs(Work));
        Assert.Contains(Work, pc.Spaces.Erasing);
    }

    [Fact]
    public async Task APinMadeHereArrivesWithItsTitleOnce()
    {
        var pc = new World(_folder);
        var pin = new PinDef(MailPin, "M", "https://mail.example/", "Inbox (3)");
        pc.Pins.Set(Space.FirstId, [pin]);
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(1000));
        pc.Pins.Set(Space.FirstId, [pin with { Title = "Inbox (4)" }]);
        pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(2000));
        await Disk.Flush();
        var items = Own(pc, Personal).Items;
        Assert.Equal(new SyncEntry("\"Inbox (3)\"", 1000, pc.Sync.Device), items[SyncItems.PinTitle(MailPin)]);
        Assert.Equal("""{"home":"https://mail.example/","letter":"M"}""", items[SyncItems.PinKey(SwiftJson.Uuid(Space.FirstId), MailPin)].V);
    }

    [Fact]
    public async Task AnotherProfileSyncsInItsOwnFolderAndAStrangersIsLeftAlone()
    {
        var pc = new World(_folder);
        var club = pc.Profiles.Add("Club", pc.Spaces)!;
        var stranger = Mac(Sync.Scope(Guid.NewGuid()), new() { [SyncItems.SpaceKey(Guid.NewGuid())] = """{"colour":0,"name":"Theirs"}""" });
        stranger.Pass(500);
        var macClub = Mac(Sync.Scope(club.Id), new() { [SyncItems.BookmarkKey(Guid.NewGuid())] = """{"title":"Loose","url":"https://loose.example/"}""" });
        macClub.Pass(1000);
        var result = pc.Sync.Run(DateTimeOffset.FromUnixTimeSeconds(2000));
        await Disk.Flush();
        Assert.Contains(club.Id, result.Profiles);
        Assert.Equal("Loose", Assert.Single(pc.Bookmarks(club.Id).Roots).Title);
        Assert.Empty(pc.Bookmarks(Profile.PersonalId).Roots);
        Assert.DoesNotContain(pc.Spaces.All, s => s.Name == "Theirs");
        Assert.False(File.Exists(Path.Combine(stranger.Folder, pc.Sync.Device + ".json")));
        Assert.Equal("""{"colour":0,"icon":"house","name":"Club","sharesSignIns":true}""", Own(pc, Sync.Scope(club.Id)).Items[SyncItems.SpaceKey(club.Home)].V);
    }

    [Fact]
    public async Task RunAsyncReadsOffTheThreadAndAppliesTheSame()
    {
        MacWrote();
        var pc = new World(_folder);
        var result = await pc.Sync.RunAsync();
        Assert.True(result.Ran);
        Assert.Equal(Theme.Dark, pc.Prefs.Theme);
        Assert.False(pc.Sync.Running);
    }

    [Fact]
    public void AFolderThatIsntThereChangesNothing()
    {
        var pc = new World(Path.Combine(_folder, "unplugged"));
        pc.Spaces.Add("Work");
        var result = pc.Sync.Run();
        Assert.False(result.Ran);
        Assert.Equal("The folder isn’t there", result.Problem);
        Assert.Equal(2, pc.Spaces.All.Count);
        Assert.Null(pc.Sync.LastSynced);
        Assert.Contains("the folder isn’t there", pc.Sync.Summary(_ => "now"));
    }

    [Fact]
    public void OffRunsNothing()
    {
        var pc = new World(_folder);
        pc.Sync.TurnOff();
        Assert.False(pc.Sync.On);
        Assert.False(pc.Sync.Run().Ran);
        Assert.Equal("", pc.Sync.Summary(_ => "now"));
    }

    [Fact]
    public async Task TurningOffTakesThisMachinesFilesOutAndForgetsWhatWasAgreed()
    {
        var pc = new World(_folder);
        pc.Sync.Run();
        await Disk.Flush();
        var own = Path.Combine(Root, Personal, pc.Sync.Device + ".json");
        Assert.True(File.Exists(own));
        pc.Sync.TurnOff();
        await Disk.Flush();
        Assert.False(File.Exists(own));
        Assert.False(File.Exists(Sync.StateFile(Personal)));
        Assert.False(pc.Notes.ContainsKey(Sync.FolderKey));
        Assert.True(pc.Notes.ContainsKey(Sync.DeviceKey));
    }

    [Fact]
    public async Task TheSummarySaysWhereWhoAndWhen()
    {
        MacWrote();
        var pc = new World(_folder);
        Assert.Equal(_folder, pc.Sync.Summary(_ => "now"));
        pc.Sync.Run();
        await Disk.Flush();
        Assert.Equal($"{_folder} · Ana’s MacBook Air · just now", pc.Sync.Summary(_ => "just now"));
    }

    [Fact]
    public void ThisMachineKeepsItsIdAndHasAName()
    {
        var notes = new Dictionary<string, string> { [Sync.DeviceKey] = "KEPT" };
        var spaces = new Spaces(TempStore.Unique("spaces"), notes);
        var sync = new Sync(notes, new Prefs(), spaces, new Pins(TempStore.Unique("pins"), []), new Profiles(TempStore.Unique("profiles")));
        Assert.Equal("KEPT", sync.Device);
        Assert.False(string.IsNullOrWhiteSpace(sync.Name));
        Assert.False(sync.On);
    }

    [Fact]
    public void AnEngineTheMacDoesntKnowIsNeitherSentNorLost()
    {
        var notes = new Dictionary<string, string>();
        var prefs = new Prefs { Engine = "someday" };
        var settings = SyncSettings.For(prefs, new Spaces(TempStore.Unique("spaces"), notes), notes);
        Assert.Equal("\"bing\"", SyncSettings.Items(settings, new Dictionary<string, string> { ["set:search.engine"] = "\"bing\"" })["set:search.engine"]);
        Assert.False(SyncSettings.Items(settings).ContainsKey("set:search.engine"));
        Assert.True(SyncSettings.Apply(new Dictionary<string, string> { ["set:search.engine"] = "\"duckduckgo\"" }, settings));
        Assert.Equal("duckduckgo", prefs.Engine);
        Assert.False(SyncSettings.Apply(new Dictionary<string, string> { ["set:search.engine"] = "\"altavista\"", ["set:look"] = "\"sepia\"", ["set:tabs.close.after"] = "5" }, settings));
        Assert.Equal("duckduckgo", prefs.Engine);
    }
}
