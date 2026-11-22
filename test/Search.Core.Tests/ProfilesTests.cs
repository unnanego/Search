using Search.Core;

namespace Search.Core.Tests;

/// Profiles (Profiles.cs, the Mac's Profiles.swift model): Personal for good where the file has it, profiles.json, a new
/// profile with its home space, and a deleted one taking every file of its own with it while Personal's stay put.
public class ProfilesTests
{
    public ProfilesTests() => TempStore.Ready();

    private static (Profiles Profiles, string File) Fresh(string? contents = null)
    {
        var file = TempStore.Unique("profiles");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new Profiles(file), file);
    }

    [Fact]
    public void NoFileIsPersonalAlone()
    {
        var (profiles, _) = Fresh();
        Assert.Equal([Profile.Personal], profiles.List);
        Assert.False(profiles.Several);
        Assert.Equal(Profile.Personal, profiles.Get(Guid.NewGuid()));
        Assert.Equal(new Guid("00000000-0000-0000-0000-0000000000A1"), Profile.PersonalId);
        Assert.Equal(Space.FirstId, Profile.Personal.Home);
    }

    [Fact]
    public void AMacsFileReadsInItsOwnOrderPersonalWithTheHomeItHas()
    {
        var club = Guid.NewGuid();
        var home = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var (profiles, _) = Fresh($$"""[{"id":"{{SwiftJson.Uuid(club)}}","name":"Club","home":"{{SwiftJson.Uuid(home)}}"},{"id":"00000000-0000-0000-0000-0000000000A1","name":"Me","home":"{{SwiftJson.Uuid(mine)}}"}]""");
        // Personal where it was moved to, with the home its first space's deletion left it.
        Assert.Equal(["Club", "Me"], profiles.List.Select(p => p.Name));
        Assert.Equal(mine, profiles.HomeOf(Profile.PersonalId));
        Assert.Equal(home, profiles.HomeOf(club));
        Assert.Null(profiles.HomeOf(Guid.NewGuid()));
        Assert.Equal("Me", profiles.Get(Guid.NewGuid()).Name);
        Assert.True(profiles.Several);
        // Twice: the first one counts, where it stands. A file without it (only ever written by hand): first, at home.
        var (twice, _) = Fresh($$"""[{"id":"{{SwiftJson.Uuid(club)}}","name":"Club","home":"{{SwiftJson.Uuid(home)}}"},{"id":"00000000-0000-0000-0000-0000000000A1","name":"Me","home":"{{SwiftJson.Uuid(mine)}}"},{"id":"00000000-0000-0000-0000-0000000000A1","name":"Again","home":"{{SwiftJson.Uuid(home)}}"}]""");
        Assert.Equal(["Club", "Me"], twice.List.Select(p => p.Name));
        var (without, _) = Fresh($$"""[{"id":"{{SwiftJson.Uuid(club)}}","name":"Club","home":"{{SwiftJson.Uuid(home)}}"}]""");
        Assert.Equal([Profile.Personal, new Profile(club, "Club", home)], without.List);
    }

    [Fact]
    public async Task PersonalRehomedIsAFileAndBackAtTheFirstSpaceIsNone()
    {
        var (profiles, file) = Fresh();
        var work = Guid.NewGuid();
        profiles.Rehome(Profile.PersonalId, work);
        await Disk.Flush();
        Assert.Equal($$"""[{"id":"00000000-0000-0000-0000-0000000000A1","name":"Personal","home":"{{SwiftJson.Uuid(work)}}"}]""", File.ReadAllText(Store.File(file)));
        Assert.Equal(work, new Profiles(file).HomeOf(Profile.PersonalId));
        profiles.Rehome(Profile.PersonalId, Space.FirstId);
        await Disk.Flush();
        Assert.False(File.Exists(Store.File(file)));
    }

    [Fact]
    public async Task AFileThatDoesntReadIsSetAside()
    {
        var (profiles, file) = Fresh("""[{"id":"nope","name":"Club"}]""");
        Assert.Equal([Profile.Personal], profiles.List);
        await Disk.Flush();
        Assert.False(File.Exists(Store.File(file)));
    }

    [Fact]
    public async Task ANewProfileComesWithItsHomeSpace()
    {
        var (profiles, file) = Fresh();
        var spaces = new Spaces(TempStore.Unique("spaces"), new Dictionary<string, string>(), profiles.HomeOf);
        Assert.Null(profiles.Add("  ", spaces));
        var club = profiles.Add(" Club ", spaces)!;
        Assert.Equal("Club", club.Name);
        var home = spaces.Find(club.Home)!;
        Assert.Equal((club.Id, (bool?)true, "house", "Club"), (home.Profile!.Value, home.SharesSignIns, home.Icon, home.Name));
        Assert.Equal(club.Home, spaces.Home(club.Id));
        Assert.Equal([club.Home], spaces.Of(club.Id).Select(s => s.Id));
        // The home shares the profile's store: WebView2's profile named by the profile's id, beside the spaces' own.
        Assert.Equal(SwiftJson.Uuid(club.Id), spaces.StoreName(home));
        Assert.Equal(club.StoreName, spaces.StoreName(home));
        await Disk.Flush();
        Assert.Equal($$"""[{"id":"00000000-0000-0000-0000-0000000000A1","name":"Personal","home":"00000000-0000-0000-0000-000000000001"},{"id":"{{SwiftJson.Uuid(club.Id)}}","name":"Club","home":"{{SwiftJson.Uuid(club.Home)}}"}]""", File.ReadAllText(Store.File(file)));
        Assert.Equal(profiles.List, new Profiles(file).List);
    }

    [Fact]
    public async Task PersonalCanBeRenamedAndNamedBackIsNoFile()
    {
        var (profiles, file) = Fresh();
        Assert.True(profiles.Rename(Profile.PersonalId, "Me"));
        await Disk.Flush();
        Assert.Equal("Me", new Profiles(file).List[0].Name);
        Assert.False(profiles.Rename(Profile.PersonalId, " "));
        Assert.True(profiles.Rename(Profile.PersonalId, "Personal"));
        await Disk.Flush();
        Assert.False(File.Exists(Store.File(file)));
    }

    [Fact]
    public async Task DeletingAProfileTakesEverythingOfItsOwn()
    {
        var (profiles, _) = Fresh();
        var notes = new Dictionary<string, string>();
        var spaces = new Spaces(TempStore.Unique("spaces"), notes, profiles.HomeOf);
        var pins = new Pins(TempStore.Unique("pins"), []);
        var club = profiles.Add("Club", spaces)!;
        var secret = spaces.Add("Secret", club.Id, sharesSignIns: false)!;
        var work = spaces.Add("Work")!;
        pins.Set(club.Home, [new PinDef(Guid.NewGuid(), "C", "https://club.example/", "Club")]);
        pins.Set(work.Id, [new PinDef(Guid.NewGuid(), "W", "https://work.example/", "Work")]);
        pins.SetShared(club.Id, [new PinDef(Guid.NewGuid(), "S", "https://shared.example/", "Shared")]);
        await Session.Write(new Session.Shape([new Session.Entry("https://club.example/", "Club")], 0), now: true, space: club.Home);
        notes["space.current." + SwiftJson.Uuid(club.Id)] = SwiftJson.Uuid(club.Home);
        foreach (var name in new[] { club.HistoryFile, club.ArchiveFile, $"history-space-{SwiftJson.Uuid(secret.Id)}.json" }) File.WriteAllText(Store.File(name), "[]");
        profiles.Bookmarks(club.Id).Add(new Uri("https://club.example/"), "Club");
        await Disk.Flush();
        Assert.True(File.Exists(Store.File(club.BookmarksFile)));

        Assert.False(profiles.Delete(Profile.PersonalId, spaces, pins));
        Assert.True(profiles.Delete(club.Id, spaces, pins));
        await Disk.Flush();
        Assert.Equal([Profile.Personal], profiles.List);
        Assert.Equal([Space.FirstId, work.Id], spaces.All.Select(s => s.Id));
        Assert.Empty(pins.Defs(club.Home));
        Assert.Empty(pins.Shared(club.Id));
        Assert.Single(pins.Defs(work.Id));
        Assert.Empty(Session.Read(club.Home).Tabs);
        foreach (var name in new[] { club.HistoryFile, club.ArchiveFile, club.BookmarksFile, $"history-space-{SwiftJson.Uuid(secret.Id)}.json" })
            Assert.False(File.Exists(Store.File(name)), name);
        // Its store and its signed-out space's are erased; its home shared the profile's, so isn't one of its own.
        Assert.Equal(new[] { club.Id, secret.Id }.Order(), spaces.Erasing.Order());
        Assert.False(notes.ContainsKey("space.current." + SwiftJson.Uuid(club.Id)));
    }

    [Fact]
    public void APersonalsFilesAreTheOnesThereAlwaysWere()
    {
        var p = Profile.Personal;
        Assert.Equal((null, "history.json", "bookmarks.json", "archive.json", "profile-00000000-0000-0000-0000-0000000000A1"), (p.StoreName, p.HistoryFile, p.BookmarksFile, p.ArchiveFile, p.SyncScope));
        var other = new Profile(new Guid("1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB"), "work", Guid.NewGuid());
        Assert.Equal(("1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB", "history-1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB.json", "bookmarks-1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB.json"),
            (other.StoreName, other.HistoryFile, other.BookmarksFile));
        Assert.Equal("archive-1B4E28BA-2FA1-11D2-883F-B9A761BDE3FB.json", other.ArchiveFile);
        Assert.Equal("W", other.Letter);
    }

    [Fact]
    public void EachProfilesHistoryAndBookmarksAreMadeOnce()
    {
        var (profiles, _) = Fresh();
        var id = Guid.NewGuid();
        Assert.Same(profiles.Bookmarks(id), profiles.Bookmarks(id));
        Assert.Same(profiles.History(id), profiles.History(id));
        Assert.NotSame(profiles.Bookmarks(id), profiles.Bookmarks(Profile.PersonalId));
    }
}
