using System.ComponentModel;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class HiddenTests
{
    public HiddenTests() => TempStore.Ready();

    private static DateTimeOffset Mac(double seconds) => SwiftJson.Date(seconds)!.Value;

    /// As a Mac's JSONEncoder writes it: a site to its list, a slash escaped, a note left out, and a key this version
    /// doesn't know.
    private const string MacFile = """
        {"news.example.com":[{"selector":"#cookie-bar","label":"Cookie banner","note":"1200×80 · bottom centre","date":785000000.5},
        {"selector":"a[href^=\"\/promo\"]","label":"Promo","date":785000001,"colour":"red"}]}
        """;

    private static (Hidden Hidden, string File) Fresh(string? contents = null)
    {
        var file = TempStore.Unique("hidden");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new Hidden(file), file);
    }

    private static async Task<string> Written(string file)
    {
        await Disk.Flush();
        return await File.ReadAllTextAsync(Store.File(file));
    }

    [Fact]
    public void AMacFileReads()
    {
        var (hidden, _) = Fresh(MacFile);
        var veils = hidden.Veils("news.example.com");
        Assert.Equal(2, veils.Count);
        Assert.Equal(new Veil("#cookie-bar", "Cookie banner", "1200×80 · bottom centre", Mac(785000000.5)), veils[0]);
        Assert.Equal(new Veil("a[href^=\"/promo\"]", "Promo", null, Mac(785000001)), veils[1]);
        Assert.Empty(hidden.Veils("example.com"));
        Assert.Empty(hidden.Veils(null));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var byHost = new Dictionary<string, IReadOnlyList<Veil>> { ["news.example.com"] = [new Veil("a[href^=\"/promo\"]", "Promo", null, Mac(785000001)), new Veil("#x", "X", "10×10", Mac(785000000.5))] };
        Assert.Equal(
            """{"news.example.com":[{"selector":"a[href^=\"\/promo\"]","label":"Promo","date":785000001},{"selector":"#x","label":"X","note":"10×10","date":785000000.5}]}""",
            Encoding.UTF8.GetString(Hidden.Encode(byHost)));
        var back = Hidden.Decode(Encoding.UTF8.GetBytes(MacFile))!;
        Assert.Equal(back["news.example.com"], Hidden.Decode(Hidden.Encode(back))!["news.example.com"]);
    }

    [Theory]
    [InlineData("""{"a.com":[{"label":"no selector","date":1}]}""")]
    [InlineData("""{"a.com":[{"selector":"#x","label":"no date"}]}""")]
    [InlineData("""{"a.com":[{"selector":"#x","label":"x","date":"yesterday"}]}""")]
    [InlineData("""{"a.com":[{"selector":"#x","label":"x","note":5,"date":1}]}""")]
    [InlineData("""{"a.com":"not a list"}""")]
    [InlineData("""[]""")]
    public void WhatSwiftWontDecodeDoesNot(string json) => Assert.Null(Hidden.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task ACorruptFileIsSetAside()
    {
        var (hidden, file) = Fresh("""{"a.com":[{"label":"no selector","date":1}]}""");
        Assert.Empty(hidden.ByHost);
        Assert.False(File.Exists(Store.File(file)));
        Assert.Single(Directory.GetFiles(Store.Folder, $"{Path.GetFileNameWithoutExtension(file)}.unreadable-*.json"));
        hidden.Hide("#x", "X", "", "a.com");
        Assert.Contains("\"#x\"", await Written(file));
    }

    [Fact]
    public void SitesAreHostsWithoutWww()
    {
        Assert.Equal("example.com", Hidden.Host(new Uri("https://WWW.Example.com/page")));
        Assert.Equal("news.example.com", Hidden.Host(new Uri("http://news.example.com/")));
        Assert.Null(Hidden.Host(new Uri("about:blank")));
        Assert.Null(Hidden.Host(new Uri("mailto:someone@example.com")));
        Assert.Null(Hidden.Host(null));
    }

    [Fact]
    public async Task HidingAndPuttingBack()
    {
        var (hidden, file) = Fresh();
        var heard = 0;
        ((INotifyPropertyChanged)hidden).PropertyChanged += (_, _) => heard++;
        hidden.Hide("#a", "A", "1×1 · top left", "example.com");
        hidden.Hide("#b", "B", "", "example.com");
        hidden.Hide("#a", "Again", "", "example.com");
        Assert.Equal(["#a", "#b"], hidden.Veils("example.com").Select(v => v.Id));
        Assert.Equal(2, heard);

        Assert.Equal("#b", hidden.Undo("example.com")!.Selector);
        Assert.Equal("#a", Assert.Single(hidden.Veils("example.com")).Selector);
        hidden.Restore(hidden.Veils("example.com")[0], "example.com");
        // A site left with nothing hidden is forgotten, in the file too.
        Assert.False(hidden.ByHost.ContainsKey("example.com"));
        Assert.Null(hidden.Undo("example.com"));
        Assert.Equal("{}", await Written(file));

        hidden.Hide("#c", "C", "", "other.com");
        hidden.RestoreAll("other.com");
        Assert.Empty(hidden.ByHost);
    }

    [Fact]
    public async Task WhatIsWrittenReadsBack()
    {
        var (hidden, file) = Fresh();
        hidden.Hide(".promo", "Promo", "300×250 · middle right", "example.com");
        await Disk.Flush();
        var again = new Hidden(file);
        var veil = Assert.Single(again.Veils("example.com"));
        var made = hidden.Veils("example.com")[0];
        Assert.Equal((made.Selector, made.Label, made.Note), (veil.Selector, veil.Label, veil.Note));
        // A date is a double of seconds in the file: as close as that holds it.
        Assert.True(Math.Abs((made.Date - veil.Date).TotalMilliseconds) < 1);
    }

    [Fact]
    public void TheStylesheet()
    {
        var (hidden, _) = Fresh(MacFile);
        Assert.Equal("#cookie-bar { display: none !important; }\na[href^=\"/promo\"] { display: none !important; }", hidden.Css("news.example.com"));
        Assert.Equal("#cookie-bar { display: none !important; }", hidden.Css("news.example.com", without: "a[href^=\"/promo\"]"));
        Assert.Equal("", hidden.Css("example.com"));
        Assert.Equal("", hidden.Css(null));
    }

    [Fact]
    public void TheScriptCantBeBrokenOutOf()
    {
        var script = Hidden.Script("a::after { content: \"`${x}\\\" }");
        Assert.Contains("sheet.textContent = `a::after { content: \"\\`\\${x}\\\\\" }`;", script);
        Assert.StartsWith("(function () {\n  var sheet = document.getElementById('office-veil');", script.ReplaceLineEndings("\n"));
        Assert.EndsWith("})();", script);
    }
}
