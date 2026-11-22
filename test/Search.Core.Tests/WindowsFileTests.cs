using System.Text;
using Search.Core;

namespace Search.Core.Tests;

/// windows.json (WindowsFile.cs, Windows.swift's WindowRecord): the Mac's lines read and written back to the byte,
/// merge and tucked kept though nothing here uses them, read as leniently as the Mac reads them, and which window
/// holds each profile's session files.
public class WindowsFileTests
{
    private const string Work = "6F9619FF-8B86-D011-B42D-00C04FC964FF";

    public WindowsFileTests() => TempStore.Ready();

    /// As a Mac's JSONEncoder writes it: the oldest window's line with no rows, merged and tucked behind another;
    /// a second window with its row in the Work space, its column folded.
    private const string MacFile =
        """[{"id":"A1B2C3D4-0000-4000-8000-000000000001","frame":"{{120, 80}, {1280, 800}}","space":"00000000-0000-0000-0000-000000000001","rows":{},"merge":"BBBBBBBB-0000-4000-8000-000000000009","tucked":true},"""
        + """{"id":"A1B2C3D4-0000-4000-8000-000000000002","frame":"{{200.5, 100}, {1000, 700}}","space":"6F9619FF-8B86-D011-B42D-00C04FC964FF","rows":{"6F9619FF-8B86-D011-B42D-00C04FC964FF":{"tabs":[{"url":"https:\/\/example.com\/a","title":"Example","muted":false}],"active":0,"splits":[]}},"folded":true}]""";

    [Fact]
    public void AMacsLinesReadAndWriteBackToTheByte()
    {
        var records = WindowsFile.Decode(Encoding.UTF8.GetBytes(MacFile));
        Assert.NotNull(records);
        Assert.Equal(2, records.Count);
        var (first, second) = (records[0], records[1]);
        Assert.Equal((Session.FirstSpace, new Guid("BBBBBBBB-0000-4000-8000-000000000009"), (bool?)true, (bool?)null), (first.Space, first.Merge, first.Tucked, first.Folded));
        Assert.Empty(first.Rows);
        Assert.Equal(new WindowFrame(120, 80, 1280, 800), first.Rect);
        Assert.Equal((new Guid(Work), (bool?)true, (Guid?)null), (second.Space, second.Folded, second.Merge));
        Assert.Equal("https://example.com/a", second.Row(new Guid(Work)).Tabs[0].Url);
        Assert.Empty(second.Row(Session.FirstSpace).Tabs);
        Assert.Equal(MacFile, Encoding.UTF8.GetString(WindowsFile.Encode(records)));
    }

    [Fact]
    public void EachFieldReadsOnItsOwn()
    {
        var records = WindowsFile.Decode("""[{"id":7,"frame":["x"],"space":"nope","rows":{"A":{"tabs":[],"active":0},"B":{"tabs":"broken"}},"merge":"x","tucked":"yes","folded":1},{}]"""u8.ToArray());
        Assert.NotNull(records);
        var line = records[0];
        Assert.NotEqual(Guid.Empty, line.Id);
        Assert.Null(line.Frame);
        Assert.Equal(Session.FirstSpace, line.Space);
        // The rows go whole or not at all, as the Mac's try? has them.
        Assert.Empty(line.Rows);
        Assert.Equal(((Guid?)null, (bool?)null, (bool?)null), (line.Merge, line.Tucked, line.Folded));
        Assert.Equal(Session.FirstSpace, records[1].Space);
        // Only a line that isn't an object spoils the list.
        Assert.Null(WindowsFile.Decode("""[{}, 3]"""u8.ToArray()));
        Assert.Null(WindowsFile.Decode("{}"u8.ToArray()));
    }

    [Fact]
    public void AFrameIsAppKitsString()
    {
        Assert.Equal("{{120, 80}, {1280, 800}}", new WindowFrame(120, 80, 1280, 800).ToString());
        Assert.Equal("{{-1440, 12.5}, {900, 600}}", new WindowFrame(-1440, 12.5, 900, 600).ToString());
        Assert.Equal(new WindowFrame(-1440, 12.5, 900, 600), WindowFrame.Parse("{{-1440, 12.5}, {900, 600}}"));
        Assert.Equal(new WindowFrame(0, 0, 0, 0), WindowFrame.Parse("rubbish"));
        Assert.Null(new WindowRecord { Frame = "{{0, 0}, {90, 600}}" }.Rect);
        Assert.Null(new WindowRecord().Rect);
    }

    [Fact]
    public void TheOldestWindowsLineLeavesItsRowsToTheSessionFiles()
    {
        var row = new Dictionary<string, Session.Shape> { [Work] = new([new Session.Entry("https://a.example/", "A")], 0) };
        var oldest = new WindowRecord { Frame = "{{0, 0}, {800, 600}}", Rows = row };
        var other = new WindowRecord { Space = new Guid(Work), Rows = row };
        var waiting = new List<WindowRecord> { new(), new() { Frame = "{{5, 5}, {500, 500}}" } };
        var lines = WindowsFile.Lines(oldest, [other], waiting);
        Assert.Equal(3, lines.Count);
        Assert.Empty(lines[0].Rows);
        Assert.Equal(oldest.Frame, lines[0].Frame);
        Assert.Single(lines[1].Rows);
        Assert.Equal("{{5, 5}, {500, 500}}", lines[2].Frame);
        Assert.Single(WindowsFile.Fresh(lines));
    }

    [Fact]
    public void EachProfilesOldestWindowHoldsItsSessionFiles()
    {
        var (personal, club) = (Profile.PersonalId, Guid.NewGuid());
        var (w1, w2, w3, w4, w5) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var holders = WindowsFile.FileHolders([(w1, personal, false), (w2, club, true), (w3, club, false), (w4, personal, false), (w5, club, false)]);
        Assert.Equal(new HashSet<Guid> { w1, w3 }, holders);
    }

    [Fact]
    public void AWindowIsInItsSpacesProfile()
    {
        var club = Guid.NewGuid();
        var clubHome = Guid.NewGuid();
        var spaces = new Spaces(TempStore.Unique("spaces"), new Dictionary<string, string>(), id => id == club ? clubHome : null);
        spaces.Write([Space.First(), new Space(clubHome, "Club") { Profile = club }]);
        Assert.Equal(club, WindowsFile.ProfileOf(new WindowRecord { Space = clubHome }, spaces));
        Assert.Equal(Profile.PersonalId, WindowsFile.ProfileOf(new WindowRecord(), spaces));
        Assert.Equal(Profile.PersonalId, WindowsFile.ProfileOf(new WindowRecord { Space = Guid.NewGuid() }, spaces));
    }

    [Fact]
    public async Task WrittenAndReadBack()
    {
        var file = TempStore.Unique("windows");
        var records = WindowsFile.Decode(Encoding.UTF8.GetBytes(MacFile))!;
        await WindowsFile.Write(records, now: true, file: file);
        Assert.Equal(MacFile, await File.ReadAllTextAsync(Store.File(file)));
        Assert.Equal(records.Select(r => r.Id), WindowsFile.Read(file).Select(r => r.Id));
        Assert.Empty(WindowsFile.Read(TempStore.Unique("none")));
    }
}
