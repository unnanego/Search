using System.ComponentModel;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class SpacesTests
{
    private const string First = "00000000-0000-0000-0000-000000000001";
    private const string Work = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";
    private const string Play = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string Club = "1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB";
    private const string ClubHome = "C9A646D3-9C61-4CB7-BFCD-EE2522C8F633";
    private const string ClubOwn = "7D444840-9DC0-11D1-B245-5FFDCE74FAD2";

    public SpacesTests() => TempStore.Ready();

    /// As a Mac's JSONEncoder writes it: the first space as it was made, a space with everything (slashes escaped, a
    /// key this version doesn't know), a space from before icons, and another profile's home and a space of its own.
    private const string MacFile = $$"""
        [{"id":"{{First}}","name":"Personal","colour":0},
         {"id":"{{Work}}","name":"Work","colour":1,"icon":"terminal","sharesSignIns":false,"downloads":"\/Users\/ana\/Downloads\/Work","gradient":{"colours":["#4FB3A9","#5E8FE0"],"angle":90,"grain":true},"strength":"rich","later":[1]},
         {"id":"{{Play}}","name":"Play","colour":0},
         {"id":"{{ClubHome}}","name":"Club","colour":4,"icon":"house","sharesSignIns":true,"profile":"{{Club}}"},
         {"id":"{{ClubOwn}}","name":"Secret","colour":0,"icon":"heart","sharesSignIns":false,"profile":"{{Club}}"}]
        """;

    private static (Spaces Spaces, string File, Dictionary<string, string> Settings) Fresh(string? contents = null, Dictionary<string, string>? settings = null, Func<Guid, Guid?>? homes = null)
    {
        var file = TempStore.Unique("spaces");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        settings ??= [];
        return (new Spaces(file, settings, homes), file, settings);
    }

    private static Guid G(string text) => Guid.Parse(text);
    private static Guid? ClubHomes(Guid profile) => profile == G(Club) ? G(ClubHome) : null;

    private static async Task<List<Space>> Written(string file)
    {
        await Disk.Flush();
        return Spaces.Decode(await File.ReadAllBytesAsync(Store.File(file))) ?? throw new Xunit.Sdk.XunitException("didn't decode");
    }

    [Fact]
    public void TheFirstSpaceIsTheSessionsFirstSpace()
    {
        Assert.Equal(G(First), Space.FirstId);
        Assert.Equal(Session.FirstSpace, Space.FirstId);
        Assert.True(Space.First().IsFirst);
        Assert.Equal(("Personal", 0, "house"), (Space.First().Name, Space.First().Colour, Space.First().Symbol));
    }

    [Fact]
    public void NoFileIsOneSpaceAndNothingShows()
    {
        var (spaces, _, _) = Fresh();
        Assert.Equal([Space.First()], spaces.All);
        Assert.False(spaces.UsesSpaces);
        Assert.Equal((Spaces.PersonalProfile, Space.FirstId), spaces.Start());
        Assert.Null(spaces.StoreName(Space.FirstId));
        Assert.Equal("session.json", Path.GetFileName(SessionFile(Space.FirstId)));
    }

    [Fact]
    public void AMacFileReads()
    {
        var (spaces, _, _) = Fresh(MacFile, homes: ClubHomes);
        Assert.Equal([G(First), G(Work), G(Play), G(ClubHome), G(ClubOwn)], spaces.All.Select(s => s.Id));
        var work = spaces.Find(G(Work))!;
        Assert.Equal(("Work", 1, "terminal", false, "/Users/ana/Downloads/Work"), (work.Name, work.Colour, work.Icon, work.SharesSignIns, work.Downloads));
        Assert.Null(work.Profile);
        Assert.Equal(new SpaceGradient(["#4FB3A9", "#5E8FE0"], 90, true), work.Gradient);
        Assert.Equal(SpaceStrength.Rich, work.Strength);
        Assert.IsType<SpaceLook.Gradient>(work.Look);
        var play = spaces.Find(G(Play))!;
        Assert.Equal(("briefcase", null, null, SpaceStrength.Subtle), (play.Symbol, play.Look, play.Tint, play.WashStrength));
        Assert.Equal(G(Club), spaces.Find(G(ClubOwn))!.ProfileId);
        Assert.Equal([G(First), G(Work), G(Play)], spaces.Of(Spaces.PersonalProfile).Select(s => s.Id));
        Assert.Equal([G(ClubHome), G(ClubOwn)], spaces.Of(G(Club)).Select(s => s.Id));
        Assert.Equal(G(ClubHome), spaces.Home(G(Club)));
        Assert.Equal(G(Club), spaces.ProfileOf(G(ClubOwn)));
        Assert.Equal(Spaces.PersonalProfile, spaces.ProfileOf(Guid.NewGuid()));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var list = new List<Space>
        {
            Space.First(),
            new(G(Work), "Wörk", 1) { Icon = "terminal", SharesSignIns = false, Downloads = "/Users/ana/Downloads", Gradient = new(["#4FB3A9", "#5E8FE0"]), Strength = SpaceStrength.Vivid },
            new(G(ClubHome), "Club") { SharesSignIns = true, Profile = G(Club) },
        };
        Assert.Equal(
            $$"""[{"id":"{{First}}","name":"Personal","colour":0},{"id":"{{Work}}","name":"Wörk","colour":1,"icon":"terminal","sharesSignIns":false,"downloads":"\/Users\/ana\/Downloads","gradient":{"colours":["#4FB3A9","#5E8FE0"],"angle":135,"grain":false},"strength":"vivid"},{"id":"{{ClubHome}}","name":"Club","colour":0,"sharesSignIns":true,"profile":"{{Club}}"}]""",
            Encoding.UTF8.GetString(Spaces.Encode(list)));
        Assert.Equal(list, Spaces.Decode(Spaces.Encode(list)));
    }

    [Fact]
    public void AGradientOrStrengthThatDoesntReadIsNone()
    {
        var (spaces, _, _) = Fresh($$"""[{"id":"{{First}}","name":"Personal","colour":0,"gradient":5,"strength":"ultra"},{"id":"{{Work}}","name":"Work","colour":2,"gradient":{"colours":["#4FB3A9",3],"angle":"x"},"strength":null,"icon":null}]""");
        var first = spaces.Find(Space.FirstId)!;
        Assert.False(first.Gradient!.Usable);
        Assert.Equal(SpaceStrength.Subtle, first.Strength);
        var work = spaces.Find(G(Work))!;
        Assert.Equal((0, SpaceGradient.DefaultAngle), (work.Gradient!.Colours.Count, work.Gradient.Angle));
        Assert.Equal(new SpaceLook.Colour(2), work.Look);
        Assert.Null(work.Strength);
        Assert.Null(work.Icon);
    }

    [Theory]
    [InlineData("""[{"id":"{{First}}","name":"Personal","colour":0,"icon":5}]""")]
    [InlineData("""[{"id":"{{First}}","name":"Personal"}]""")]
    [InlineData("""[{"id":"not a uuid","name":"Personal","colour":0}]""")]
    [InlineData("""[{"id":"{{First}}","name":"Personal","colour":0,"profile":"nope"}]""")]
    [InlineData("""[{"id":"{{First}}","name":"Personal","colour":0,"sharesSignIns":"yes"}]""")]
    [InlineData("""{"spaces":[]}""")]
    [InlineData("""[{"id":""")]
    public void AFileThatDoesntReadIsSetAside(string contents)
    {
        var (spaces, file, _) = Fresh(contents.Replace("{{First}}", First));
        Assert.Equal([Space.First()], spaces.All);
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, Path.GetFileNameWithoutExtension(file) + ".unreadable-*.json"));
    }

    [Fact]
    public void TheFirstSpaceIsRepaired()
    {
        // Missing: back at the front. Twice: the first one kept. In another profile: back in Personal.
        var (missing, _, _) = Fresh($$"""[{"id":"{{Work}}","name":"Work","colour":0}]""");
        Assert.Equal([Space.FirstId, G(Work)], missing.All.Select(s => s.Id));
        var (twice, _, _) = Fresh($$"""[{"id":"{{First}}","name":"Home","colour":1,"profile":"{{Club}}"},{"id":"{{Work}}","name":"Work","colour":0},{"id":"{{First}}","name":"Again","colour":0}]""");
        Assert.Equal([Space.FirstId, G(Work)], twice.All.Select(s => s.Id));
        Assert.Equal(("Home", null), (twice.Find(Space.FirstId)!.Name, twice.Find(Space.FirstId)!.Profile));
    }

    [Fact]
    public void HomeComesFirstUnlessTheOrderIsKept()
    {
        // A file whose blocks each start at home, the first space at the end of the list: home first again.
        var file = $$"""[{"id":"{{ClubHome}}","name":"Club","colour":0,"profile":"{{Club}}"},{"id":"{{First}}","name":"Personal","colour":0},{"id":"{{Work}}","name":"Work","colour":0}]""";
        var (plain, _, _) = Fresh(file, homes: ClubHomes);
        Assert.Equal([Space.FirstId, G(ClubHome), G(Work)], plain.All.Select(s => s.Id));
        // Moved here: the order is the user's.
        var (kept, _, _) = Fresh(file, new() { ["spaces.orderKept"] = "true" }, ClubHomes);
        Assert.Equal([G(ClubHome), G(First), G(Work)], kept.All.Select(s => s.Id));
        // Moved by an older Search: Personal's home away from the front of its block says so.
        var (older, _, _) = Fresh($$"""[{"id":"{{Work}}","name":"Work","colour":0},{"id":"{{First}}","name":"Personal","colour":0}]""");
        Assert.Equal([G(Work), G(First)], older.All.Select(s => s.Id));
        Assert.Equal([G(Work), G(First)], older.Of(Spaces.PersonalProfile).Select(s => s.Id));
    }

    [Fact]
    public async Task AddingPutsANewSpaceAtTheEnd()
    {
        var (spaces, file, _) = Fresh(MacFile, homes: ClubHomes);
        var made = spaces.Add("  Reading  ")!;
        Assert.Equal(("Reading", 0, true, null), (made.Name, made.Colour, made.SharesSignIns, made.Profile));
        // The house is the first's, the briefcase Play's (it has no icon of its own).
        Assert.Equal("building.2", made.Icon);
        Assert.Equal(made, spaces.Of(Spaces.PersonalProfile)[^1]);
        Assert.Null(spaces.Add("   "));
        var own = spaces.Add("Alone", G(Club), "book", sharesSignIns: false)!;
        Assert.Equal((G(Club), "book", false), (own.ProfileId, own.Icon, own.SharesSignIns));
        // The profile written last goes last in the file; each block keeps its order.
        Assert.Equal([G(First), G(Work), G(Play), made.Id, G(ClubHome), G(ClubOwn), own.Id], (await Written(file)).Select(s => s.Id));
    }

    [Fact]
    public void FreeIconIsTheFirstNotWorn()
    {
        var (spaces, _, _) = Fresh();
        Assert.Equal("briefcase", spaces.Add("A")!.Icon);
        Assert.Equal("building.2", spaces.Add("B")!.Icon);
        Assert.Equal("briefcase", spaces.FreeIcon(G(Club)));
    }

    [Fact]
    public async Task MovingKeepsTheOrder()
    {
        var (spaces, file, settings) = Fresh(MacFile, homes: ClubHomes);
        Assert.False(spaces.Move(G(Work), 7));
        Assert.True(spaces.Move(Space.FirstId, 2));
        Assert.Equal([G(Work), G(Play), G(First)], spaces.Of(Spaces.PersonalProfile).Select(s => s.Id));
        Assert.Equal("true", settings["spaces.orderKept"]);
        await Disk.Flush();
        var again = new Spaces(file, settings, ClubHomes);
        Assert.Equal([G(Work), G(Play), G(First)], again.Of(Spaces.PersonalProfile).Select(s => s.Id));
    }

    [Fact]
    public async Task ChangesAreWrittenAndAnnounced()
    {
        var (spaces, file, _) = Fresh(MacFile, homes: ClubHomes);
        var said = new List<string?>();
        spaces.PropertyChanged += (_, e) => said.Add(e.PropertyName);
        Assert.True(spaces.Rename(G(Play), "  Games "));
        Assert.False(spaces.Rename(G(Play), " "));
        Assert.False(spaces.Rename(Guid.NewGuid(), "Nobody"));
        Assert.True(spaces.SetIcon(G(Play), "gamecontroller"));
        Assert.True(spaces.SetColour(G(Work), 3));
        Assert.False(spaces.SetColour(G(Work), 6));
        Assert.True(spaces.SetStrength(G(Work), SpaceStrength.Subtle));
        Assert.True(spaces.SetDownloads(G(Play), @"D:\Games"));
        var written = await Written(file);
        var play = written.Single(s => s.Id == G(Play));
        Assert.Equal(("Games", "gamecontroller", @"D:\Games"), (play.Name, play.Icon, play.Downloads));
        var work = written.Single(s => s.Id == G(Work));
        Assert.Equal((3, null, null), (work.Colour, work.Gradient, work.Strength));
        Assert.Equal(5, said.Count(n => n == nameof(Spaces.All)));
    }

    [Fact]
    public void GradientsComeAndGo()
    {
        var (spaces, _, _) = Fresh(MacFile);
        // One colour can't be drawn: none, which it already had.
        Assert.False(spaces.SetGradient(G(Play), new(["#FF0000"])));
        Assert.Null(spaces.Find(G(Play))!.Gradient);
        Assert.True(spaces.ChooseLook(G(Play), new SpaceLookChoice.Custom()));
        Assert.Equal(new SpaceLookChoice.Preset(1), spaces.Find(G(Play))!.LookChoice);
        Assert.True(spaces.EditGradient(G(Play), g => g with { Angle = 45, Grain = true }));
        Assert.True(spaces.ChooseLook(G(Play), new SpaceLookChoice.Preset(0)));
        var play = spaces.Find(G(Play))!;
        Assert.Equal(("Sunrise", 45d, true), (play.LookName, play.Gradient!.Angle, play.Gradient.Grain));
        Assert.True(spaces.EditGradient(G(Play), g => g with { Colours = [.. g.Colours, "#123456"] }));
        Assert.IsType<SpaceLookChoice.Custom>(spaces.Find(G(Play))!.LookChoice);
        Assert.True(spaces.ChooseLook(G(Play), new SpaceLookChoice.Colour(5)));
        Assert.Equal(("Violet", null), (spaces.Find(G(Play))!.LookName, spaces.Find(G(Play))!.Gradient));
        spaces.RestoreLook(G(Work), 1, new(["#4FB3A9", "#5E8FE0"], 90, true), SpaceStrength.Rich);
        Assert.Equal(spaces.Find(G(Work))!, Spaces.Decode(Encoding.UTF8.GetBytes(MacFile.Replace(",\"later\":[1]", "")))!.Single(s => s.Id == G(Work)));
        Assert.True(spaces.ChooseLook(G(Work), new SpaceLookChoice.None()));
        Assert.Equal("No colour", spaces.Find(G(Work))!.LookName);
    }

    [Fact]
    public async Task RemovingASpaceErasesItsSessionAndStore()
    {
        var (spaces, file, settings) = Fresh(MacFile, homes: ClubHomes);
        spaces.UsesSpaces = true;
        foreach (var id in new[] { G(Work), G(Play), G(ClubOwn) }) await Session.Write(new Session.Shape([new("https://example.com/", "Example")], 0), now: true, id);
        var history = Store.File($"history-space-{Work}.json");
        File.WriteAllText(history, "{}");
        Assert.True(spaces.Enter(G(Work)));

        Assert.False(spaces.Remove(Space.FirstId));
        Assert.False(spaces.Remove(G(ClubHome)));
        Assert.True(spaces.Remove(G(Work)));
        Assert.False(spaces.Remove(G(Work)));
        await Disk.Flush();
        Assert.Null(spaces.Find(G(Work)));
        Assert.DoesNotContain(G(Work), (await Written(file)).Select(s => s.Id));
        Assert.False(File.Exists(SessionFile(G(Work))));
        Assert.True(File.Exists(SessionFile(G(Play))));
        Assert.False(File.Exists(history));
        // Its own store, to erase; and the window that showed it is at home.
        Assert.Equal([G(Work)], spaces.Erasing);
        Assert.Equal(Space.FirstId, spaces.Current);
        Assert.Equal(First, settings["space.current"]);

        // A space signed in with the others has nothing of its own to erase. One from before the choice, and another
        // profile's own, do.
        var shared = spaces.Add("Shared")!;
        Assert.True(spaces.Remove(shared.Id));
        Assert.Equal([G(Work)], spaces.Erasing);
        Assert.True(spaces.Remove(G(Play)));
        Assert.True(spaces.Remove(G(ClubOwn)));
        Assert.Equal([G(Work), G(Play), G(ClubOwn)], spaces.Erasing);
        spaces.Erased(G(Work));
        Assert.Equal([G(Play), G(ClubOwn)], spaces.Erasing);
        Assert.False(File.Exists(SessionFile(G(ClubOwn))));
    }

    [Fact]
    public void EachSpaceNamesItsStore()
    {
        var (spaces, _, _) = Fresh(MacFile, homes: ClubHomes);
        var played = spaces.Add("Shared")!;
        // The first space, and one made signed in with it: the default profile, the store there always was.
        Assert.Null(spaces.StoreName(Space.FirstId));
        Assert.Null(spaces.StoreName(played));
        Assert.False(spaces.OwnsStore(played));
        // Signed out of the others, or from before the choice: a store of its own, by its id.
        Assert.Equal(Work, spaces.StoreName(G(Work)));
        Assert.Equal(Play, spaces.StoreName(G(Play)));
        // Another profile's home shares the profile's store; its own space has its own.
        Assert.Equal(Club, spaces.StoreName(G(ClubHome)));
        Assert.Equal(ClubOwn, spaces.StoreName(G(ClubOwn)));
        Assert.Equal(Club, Spaces.ProfileStoreName(G(Club)));
        Assert.Null(Spaces.ProfileStoreName(Spaces.PersonalProfile));
        // History goes with the store.
        Assert.Equal("history.json", spaces.HistoryFile(played.Id));
        Assert.Equal($"history-space-{Work}.json", spaces.HistoryFile(G(Work)));
        Assert.Equal($"history-{Club}.json", spaces.HistoryFile(G(ClubHome)));
    }

    [Fact]
    public void WithSpacesOffNothingShows()
    {
        var (spaces, _, settings) = Fresh(MacFile, new() { ["space.current"] = Work }, ClubHomes);
        var work = spaces.Find(G(Work))!;
        Assert.False(spaces.Enter(G(Work)));
        Assert.Null(spaces.WashOf(work, dark: false));
        Assert.Equal(@"C:\Downloads", spaces.DownloadsFolder(work, @"C:\Downloads"));
        Assert.Equal((Spaces.PersonalProfile, Space.FirstId), spaces.Start());
        Assert.Equal((G(Club), G(ClubHome)), spaces.Start(G(ClubOwn)));

        spaces.UsesSpaces = true;
        Assert.Equal("true", settings["spaces"]);
        Assert.Equal((Spaces.PersonalProfile, G(Work)), spaces.Start());
        Assert.Equal((G(Club), G(ClubOwn)), spaces.Start(G(ClubOwn)));
        Assert.Equal((Spaces.PersonalProfile, Space.FirstId), spaces.Start(Guid.NewGuid()));
        Assert.NotNull(spaces.WashOf(work, dark: true));
        Assert.Equal("/Users/ana/Downloads/Work", spaces.DownloadsFolder(work, @"C:\Downloads"));
        Assert.True(spaces.Enter(G(ClubOwn), primary: false));
        Assert.Equal(G(ClubOwn), spaces.Current);
        Assert.Equal(Work, settings["space.current"]);
        Assert.Equal(ClubOwn, settings[$"space.current.{Club}"]);
        Assert.Equal(G(ClubOwn), spaces.LastSpace(G(Club)));
        Assert.Equal(Space.FirstId, spaces.LastSpace(Spaces.PersonalProfile));
        Assert.False(spaces.Enter(Guid.NewGuid()));

        // Turned off: back to the profile's home.
        var said = new List<string?>();
        spaces.PropertyChanged += (_, e) => said.Add(e.PropertyName);
        spaces.UsesSpaces = false;
        Assert.Equal(G(ClubHome), spaces.Current);
        Assert.Contains(nameof(Spaces.UsesSpaces), said);
        Assert.Contains(nameof(Spaces.Current), said);
    }

    [Fact]
    public void TheWashFollowsTheLook()
    {
        var (spaces, _, _) = Fresh(MacFile);
        spaces.UsesSpaces = true;
        var colour = spaces.WashOf(spaces.Find(G(Work))! with { Gradient = null, Strength = null }, dark: false)!;
        Assert.Equal((Spaces.Colours[1], Spaces.WashLight), (colour.Colour!.Value, colour.Opacity));
        Assert.Null(spaces.WashOf(spaces.Find(G(Play))!, dark: false));
        var gradient = spaces.WashOf(spaces.Find(G(Work))!, dark: true)!;
        Assert.Null(gradient.Colour);
        Assert.Equal((24, 90d, 0.22, SpaceStrengths.GrainDark * 1.25), (gradient.Stops.Count, gradient.Angle, gradient.Opacity, gradient.Grain));
    }

    private static string SessionFile(Guid space) => Store.File(space == Space.FirstId ? "session.json" : $"session-{SwiftJson.Uuid(space)}.json");
}
