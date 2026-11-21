using System.ComponentModel;
using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class SiteStylesTests
{
    public SiteStylesTests() => TempStore.Ready();

    /// As a Mac's JSONEncoder writes it: a site to its CSS, slashes escaped.
    private const string MacFile = """{"example.com":"body { font-family: serif; }\n\/* mine *\/ a { color: red }","news.example.com":"img { display: none }"}""";

    private static (SiteStyles Styles, string File) Fresh(string? contents = null)
    {
        var file = TempStore.Unique("styles");
        Directory.CreateDirectory(Store.Folder);
        if (contents is not null) File.WriteAllText(Store.File(file), contents);
        return (new SiteStyles(file), file);
    }

    private static async Task<string> Written(string file)
    {
        await Disk.Flush();
        return await File.ReadAllTextAsync(Store.File(file));
    }

    [Fact]
    public void AMacFileReads()
    {
        var (styles, _) = Fresh(MacFile);
        Assert.Equal("body { font-family: serif; }\n/* mine */ a { color: red }", styles.Written("example.com"));
        Assert.Equal(["example.com", "news.example.com"], styles.Hosts);
        Assert.Equal("", styles.Written("other.com"));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var byHost = new Dictionary<string, string> { ["example.com"] = "a { color: red } /* é */", ["b.com"] = "p{}" };
        Assert.Equal("""{"example.com":"a { color: red } \/* é *\/","b.com":"p{}"}""", Encoding.UTF8.GetString(SiteStyles.Encode(byHost)));
        Assert.Equal(byHost, SiteStyles.Decode(SiteStyles.Encode(byHost)));
    }

    [Theory]
    [InlineData("""{"a.com":5}""")]
    [InlineData("""["a.com"]""")]
    [InlineData("""{"a.com":""")]
    public void WhatSwiftWontDecodeDoesNot(string json) => Assert.Null(SiteStyles.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task ACorruptFileIsSetAside()
    {
        var (styles, file) = Fresh("""{"a.com":5}""");
        Assert.Empty(styles.ByHost);
        Assert.Single(Directory.GetFiles(Store.Folder, $"{Path.GetFileNameWithoutExtension(file)}.unreadable-*.json"));
        styles.Set("p { margin: 0 }", "a.com");
        Assert.Equal("""{"a.com":"p { margin: 0 }"}""", await Written(file));
    }

    [Fact]
    public void OnlyWhileTheSettingIsOn()
    {
        var (styles, _) = Fresh(MacFile);
        var told = new List<string?>();
        styles.StyleChanged += told.Add;
        Assert.Null(styles.Css("example.com"));
        styles.On = true;
        styles.On = true;
        Assert.Equal("img { display: none }", styles.Css("news.example.com"));
        Assert.Null(styles.Css("other.com"));
        Assert.Null(styles.Css(null));
        styles.On = false;
        // Off takes every style off the pages and keeps them all.
        Assert.Null(styles.Css("news.example.com"));
        Assert.Equal("img { display: none }", styles.Written("news.example.com"));
        Assert.Equal([null, null], told);
    }

    [Fact]
    public async Task SettingAndForgetting()
    {
        var (styles, file) = Fresh();
        var told = new List<string?>();
        var heard = new List<string?>();
        styles.StyleChanged += told.Add;
        ((INotifyPropertyChanged)styles).PropertyChanged += (_, e) => heard.Add(e.PropertyName);
        styles.Set("a { color: red }", "example.com");
        styles.Set("a { color: red }", "example.com");
        styles.Set("b { }", "z.com");
        Assert.Equal(["example.com", "z.com"], told);
        Assert.Equal(["example.com", "z.com"], styles.Hosts);
        Assert.Contains(nameof(SiteStyles.ByHost), heard);

        // Nothing but whitespace is no style, and the site is forgotten.
        styles.Set("  \n ", "example.com");
        styles.Remove("z.com");
        Assert.Empty(styles.Hosts);
        Assert.Equal(["example.com", "z.com", "example.com", "z.com"], told);
        Assert.Equal("{}", await Written(file));
    }

    [Fact]
    public void SitesAreWebsitesWithoutWww()
    {
        Assert.Equal("example.com", SiteStyles.Host(new Uri("https://www.example.com/x")));
        Assert.Equal("news.example.com", SiteStyles.Host(new Uri("http://NEWS.example.com")));
        Assert.Null(SiteStyles.Host(new Uri("file:///C:/page.html")));
        Assert.Null(SiteStyles.Host(new Uri("ftp://example.com/")));
        Assert.Null(SiteStyles.Host(null));
    }

    [Fact]
    public void TheScriptCantBeBrokenOutOf()
    {
        var css = "a::after { content: \"</style>`${x}\\\" }" + (char)0x2028 + "\n";
        var literal = SiteStyles.Literal(css);
        Assert.Equal("\"a::after { content: \\\"\\u003c\\/style>`${x}\\\\\\\" }\\u2028\\n\"", literal);
        Assert.Equal($"({SiteStyles.Live[1..^7]})({literal});", SiteStyles.Script(css));
        Assert.EndsWith(")(css);", SiteStyles.Live);
        Assert.Contains("document.getElementById('office-style')", SiteStyles.Live);
        Assert.Equal("\"\\u0001\"", SiteStyles.Literal(((char)1).ToString()));
    }
}
