using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class SessionTests
{
    private const string Work = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string Pin = "0E984725-C51C-4BF4-9960-E1C80E27ABA0";

    public SessionTests() => TempStore.Ready();

    private static DateTimeOffset Mac(double seconds) => SwiftJson.ReferenceDate.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));

    /// As a Mac's JSONEncoder writes it: slashes escaped, dates as seconds since 2001, UUIDs uppercase, the first
    /// split in the shape main once wrote (left, right, fraction), and keys this version doesn't know.
    private const string MacSession = """
        {"tabs":[{"url":"https:\/\/github.com\/","title":"GitHub","pin":"g","pinID":"0E984725-C51C-4BF4-9960-E1C80E27ABA0","home":"https:\/\/github.com\/","touched":785000000.5,"listed":true,"muted":false},
        {"url":"https:\/\/docs.example\/a","title":"Docs","groupID":"6F9619FF-8B86-D011-B42D-00C04FC964FF","letGo":785000100,"muted":true,"keepsake":{"x":1}},
        {"url":"https:\/\/example.com\/","title":"Ex","name":"Mine","groupID":"not-a-uuid","twinID":"6f9619ff-8b86-d011-b42d-00c04fc964ff","touched":"yesterday"}],
        "active":1,
        "groups":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"Work","colour":3}],
        "splits":[{"left":1,"right":2,"fraction":0.3}],
        "future":"ignored"}
        """;

    private static Session.Shape Decoded(string json) => Session.Decode(Encoding.UTF8.GetBytes(json)) ?? throw new Xunit.Sdk.XunitException("didn't decode");

    [Fact]
    public void AMacSessionReads()
    {
        var shape = Decoded(MacSession);
        Assert.Equal(3, shape.Tabs.Count);
        Assert.Equal(1, shape.Active);

        var pin = shape.Tabs[0];
        Assert.Equal(("https://github.com/", "GitHub", "g", "https://github.com/"), (pin.Url, pin.Title, pin.Pin, pin.Home));
        Assert.Equal(Guid.Parse(Pin), pin.PinId);
        Assert.Equal(Mac(785000000.5), pin.Touched);
        Assert.True(pin.Listed);

        var grouped = shape.Tabs[1];
        Assert.Equal(Guid.Parse(Work), grouped.GroupId);
        Assert.Equal(Mac(785000100), grouped.LetGo);
        Assert.True(grouped.Muted);

        // What doesn't make sense is taken as not there; the tab still comes back.
        var named = shape.Tabs[2];
        Assert.Equal("Mine", named.Name);
        Assert.Null(named.GroupId);
        Assert.Null(named.Touched);
        Assert.Equal(Guid.Parse(Work), named.TwinId);

        var group = Assert.Single(shape.Groups!);
        Assert.Equal((Guid.Parse(Work), "Work", false, (int?)3), (group.Id, group.Name, group.Collapsed, group.Colour));

        var split = Assert.Single(shape.Splits);
        Assert.Equal([1, 2], split.Tabs);
        Assert.Equal(0.3, split.Sizes[0], 10);
        Assert.Equal(0.7, split.Sizes[1], 10);
        Assert.Equal(SplitAxis.Horizontal, split.Axis);
        Assert.Null(split.Focused);
    }

    [Fact]
    public void BadGroupsMeanNoGroupsNotNoSession()
    {
        var shape = Decoded("""{"tabs":[{"url":"a","title":"b"}],"active":0,"groups":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"ok"},{"name":"no id"}]}""");
        Assert.Single(shape.Tabs);
        Assert.Null(shape.Groups);
        Assert.Empty(shape.Splits);
        Assert.Null(Decoded("""{"tabs":[],"active":0,"groups":"nonsense"}""").Groups);
    }

    [Fact]
    public void SplitsReadLeniently()
    {
        var shape = Decoded("""{"tabs":[],"active":0,"splits":[{"tabs":[0,1,2],"axis":"diagonal","focused":2}]}""");
        var split = Assert.Single(shape.Splits);
        Assert.Equal(SplitAxis.Horizontal, split.Axis);
        Assert.Equal(3, split.Sizes.Count);
        Assert.All(split.Sizes, s => Assert.Equal(1 / 3.0, s, 10));
        Assert.Equal(2, split.Focused);
        Assert.Equal(SplitAxis.Vertical, Decoded("""{"tabs":[],"active":0,"splits":[{"tabs":[0,1],"axis":"vertical","sizes":[0.4,0.6]}]}""").Splits[0].Axis);
        Assert.Empty(Decoded("""{"tabs":[],"active":0,"splits":[{"left":0}]}""").Splits);
    }

    [Theory]
    [InlineData("""{"tabs":[{"title":"no address"}],"active":0}""")]
    [InlineData("""{"tabs":[{"url":"a","title":"b","pin":5}],"active":0}""")]
    [InlineData("""{"tabs":[],"active":null}""")]
    [InlineData("""{"active":0}""")]
    [InlineData("""not json""")]
    public void WhatHasToBeThereHasToBeThere(string json) => Assert.Null(Session.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void WritesTheMacsShape()
    {
        var shape = new Session.Shape([
            new Session.Entry("https://github.com/", "GitHub") { Pin = "g", PinId = Guid.Parse(Pin), Touched = Mac(785000000.5), Listed = true },
            new Session.Entry("https://docs.example/a", "Docs") { GroupId = Guid.Parse(Work), Muted = true },
        ], 1)
        {
            Groups = [new TabGroup(Guid.Parse(Work), "Wörk") { Colour = 3 }],
            Splits = [new Session.Split([0, 1], [0.25, 0.75]) { Focused = 1 }],
        };
        var written = Encoding.UTF8.GetString(Session.Encode(shape));
        Assert.Equal(
            """{"tabs":[{"url":"https:\/\/github.com\/","title":"GitHub","pin":"g","pinID":"0E984725-C51C-4BF4-9960-E1C80E27ABA0","touched":785000000.5,"listed":true,"muted":false},"""
            + """{"url":"https:\/\/docs.example\/a","title":"Docs","groupID":"6F9619FF-8B86-D011-B42D-00C04FC964FF","muted":true}],"active":1"""
            + ""","groups":[{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"Wörk","collapsed":false,"colour":3}]"""
            + ""","splits":[{"tabs":[0,1],"axis":"horizontal","sizes":[0.25,0.75],"focused":1}]}""",
            written);
        Assert.Equal("""{"tabs":[],"active":0,"splits":[]}""", Encoding.UTF8.GetString(Session.Encode(Session.Shape.Empty)));
    }

    [Fact]
    public void AMacSessionComesBackTheSame()
    {
        var once = Session.Encode(Decoded(MacSession));
        Assert.Equal(once, Session.Encode(Session.Decode(once)!));
    }

    [Fact]
    public async Task EachSpaceKeepsItsOwnFile()
    {
        var space = Guid.NewGuid();
        var shape = new Session.Shape([new Session.Entry("https://a.example/", "A")], 0);
        await Session.Write(shape, now: false, space);
        await Disk.Flush();
        Assert.True(File.Exists(Store.File($"session-{space.ToString().ToUpperInvariant()}.json")));
        Assert.Equal("https://a.example/", Assert.Single(Session.Read(space).Tabs).Url);
        Session.Erase(space);
        Assert.Empty(Session.Read(space).Tabs);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), Session.FirstSpace);
    }

    [Fact]
    public void AnUnreadableSessionIsSetAside()
    {
        var space = Guid.NewGuid();
        var file = Store.File($"session-{SwiftJson.Uuid(space)}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ broken");
        Assert.Empty(Session.Read(space).Tabs);
        Assert.False(File.Exists(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, $"session-{SwiftJson.Uuid(space)}.unreadable-*.json"));
    }

    [Fact]
    public void AFreshStartKeepsPinsNamesAndGroups()
    {
        var work = Guid.Parse(Work);
        var gone = Guid.NewGuid();
        var shape = new Session.Shape([
            new Session.Entry("https://pin.example/", "Pin") { Pin = "p" },
            new Session.Entry("https://left.example/", "Left"),
            new Session.Entry("https://named.example/", "Named") { Name = "Mine" },
            new Session.Entry("https://grouped.example/", "Grouped") { GroupId = work },
            new Session.Entry("https://orphan.example/", "Orphan") { GroupId = gone },
        ], 1)
        {
            Groups = [new TabGroup(work, "Work"), new TabGroup(Guid.NewGuid(), "Empty")],
            Splits = [new Session.Split([2, 3], [0.5, 0.5]) { Focused = 3 }, new Session.Split([0, 1], [0.5, 0.5])],
        };
        var fresh = Session.Fresh(shape);
        Assert.Equal(["Pin", "Named", "Grouped"], fresh.Tabs.Select(t => t.Title));
        Assert.Equal(-1, fresh.Active);
        Assert.Equal("Work", Assert.Single(fresh.Groups!).Name);
        var split = Assert.Single(fresh.Splits);
        Assert.Equal([1, 2], split.Tabs);
        Assert.Equal(2, split.Focused);
        Assert.Null(Session.Fresh(new Session.Shape([new Session.Entry("a", "b")], 0)).Groups);
    }
}

public class SwiftJsonTests
{
    [Fact]
    public void DatesCountSecondsFrom2001()
    {
        Assert.Equal(0, SwiftJson.Seconds(SwiftJson.ReferenceDate));
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 1, 0, TimeSpan.Zero), SwiftJson.Date(60));
        // 2023-11-14T22:13:20Z, Unix 1_700_000_000.
        Assert.Equal(1_700_000_000 - 978_307_200, SwiftJson.Seconds(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
        Assert.Equal(785000000.123456, SwiftJson.Seconds(SwiftJson.Date(785000000.123456)!.Value), 6);
        Assert.Null(SwiftJson.Date(double.NaN));
        Assert.Null(SwiftJson.Date(1e300));
    }

    [Fact]
    public void UuidsAreUppercase()
    {
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        Assert.Equal("6F9619FF-8B86-D011-B42D-00C04FC964FF", SwiftJson.Uuid(id));
        Assert.Equal(id, SwiftJson.Uuid("6F9619FF-8B86-D011-B42D-00C04FC964FF"));
        Assert.Null(SwiftJson.Uuid("{6F9619FF-8B86-D011-B42D-00C04FC964FF}"));
    }

    [Fact]
    public void SlashesAreEscapedAndNothingElse()
    {
        var json = SwiftJson.Encode(w => { w.WriteStartObject(); w.WriteString("a", @"https://x/ü\/"); w.WriteEndObject(); });
        Assert.Equal("""{"a":"https:\/\/x\/ü\\\/"}""", Encoding.UTF8.GetString(json));
    }
}
