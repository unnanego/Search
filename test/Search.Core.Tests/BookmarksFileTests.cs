using System.Text;
using Search.Core;

namespace Search.Core.Tests;

public class BookmarksFileTests
{
    public BookmarksFileTests() => TempStore.Ready();

    private const string Chrome = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <!-- This is an automatically generated file.
             It will be read and overwritten.
             DO NOT EDIT! -->
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="1700000100" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
            <DL><p>
                <DT><A HREF="https://github.com/" ADD_DATE="1700000000" ICON="data:image/png;base64,iVBORw0KGgo=">GitHub</A>
                <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="0">Work</H3>
                <DL><p>
                    <DT><A HREF="https://example.com/a?b=1&amp;c=2" ADD_DATE="1700000000">Tom &amp; Jerry&#39;s &lt;page&gt; &#x263A; &#9731; &amp;lt;</A>
                    <DT><H3 ADD_DATE="1700000000">Deep</H3>
                    <DL><p>
                        <DT><A HREF="https://deep.example/">Deep page</A>
                    </DL><p>
                </DL><p>
                <DT><A HREF="javascript:alert(1)">Bookmarklet</A>
                <DT><A HREF="https://untitled.example/path"></A>
            </DL><p>
            <DT><H3 ADD_DATE="1700000000">Other bookmarks</H3>
            <DL><p>
                <DT><A HREF="http://old.example/">Old</A>
            </DL><p>
        </DL><p>
        """;

    private const string Firefox = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'none'; img-src data: *; object-src 'none'"></meta>
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks Menu</H1>

        <DL><p>
            <DT><A HREF="place:parent=menu________&amp;queryType=1" ADD_DATE="1700000000">Recent Tags</A>
            <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="1700000000">Mozilla Firefox</H3>
            <DL><p>
                <DT><A HREF="https://support.mozilla.org/products/firefox" ADD_DATE="1700000000" ICON_URI="https://support.mozilla.org/favicon.ico">Get Help</A>
            </DL><p>
            <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="1700000000" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks Toolbar</H3>
            <DL><p>
                <DT><A HREF="https://www.mozilla.org/" ADD_DATE="1700000000" SHORTCUTURL="moz" TAGS="a,b">Mozilla</A>
            </DL><p>
        </DL>
        """;

    /// Safari leaves out the list round the whole file.
    private const string Safari = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        	<HTML>
        	<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        	<Title>Bookmarks</Title>
        	<H1>Bookmarks</H1>
        	<DT><H3 FOLDED>Favorites</H3>
        	<DL><p>
        		<DT><A HREF="https://www.apple.com/">Apple</A>
        	</DL><p>
        	<DT><H3 FOLDED>Reading List</H3>
        	<DL><p>
        		<DT><A HREF="https://read.example/">To read</A>
        	</DL><p>
        	<DT><A HREF="https://top.example/">Top</A>
        </HTML>
        """;

    private static string Shape(IEnumerable<Bookmark> nodes) =>
        string.Join(", ", nodes.Select(n => n.IsFolder ? $"{n.Title}[{Shape(n.Children ?? [])}]" : $"{n.Title}={n.Url}"));

    [Fact]
    public void ChromesFile()
    {
        var nodes = BookmarksFile.Parse(Chrome);
        // The bar's pages at the top, as bringing Chrome's in directly puts them; no bookmarklet; a page with no
        // title called after its host.
        Assert.Equal(
            "GitHub=https://github.com/, Work[Tom & Jerry's <page> ☺ ☃ &lt;=https://example.com/a?b=1&c=2, Deep[Deep page=https://deep.example/]], "
            + "untitled.example=https://untitled.example/path, Other bookmarks[Old=http://old.example/]",
            Shape(nodes));
        Assert.All(nodes, n => Assert.NotEqual(Guid.Empty, n.Id));
    }

    [Fact]
    public void FirefoxsFile() =>
        Assert.Equal("Mozilla Firefox[Get Help=https://support.mozilla.org/products/firefox], Mozilla=https://www.mozilla.org/", Shape(BookmarksFile.Parse(Firefox)));

    [Fact]
    public void SafarisFile() =>
        Assert.Equal("Favorites[Apple=https://www.apple.com/], Reading List[To read=https://read.example/], Top=https://top.example/", Shape(BookmarksFile.Parse(Safari)));

    [Fact]
    public void WrittenLoosely()
    {
        // Lower case, single quotes and none, markup in a title, a list with no heading, and a file cut short.
        const string loose = """
            <dl><p>
              <dt><a add_date=1 href='https://single.example/'>Single</a>
              <dt><a href=https://bare.example/x>  <b>Bold</b> title  </a >
              <dl>
                <dt><a HREF="https://inside.example/">Inside</a>
              <dt><H3>Open</H3>
              <DL><p>
                <DT><A HREF="https://last.example/">Last</A>
            """;
        Assert.Equal("Single=https://single.example/, Bold title=https://bare.example/x, Folder[Inside=https://inside.example/, Open[Last=https://last.example/]]", Shape(BookmarksFile.Parse(loose)));
    }

    [Fact]
    public void NestedWithoutEndIsKept()
    {
        var html = new StringBuilder("<DL><p>\n");
        for (var i = 0; i < 100; i++) html.Append($"<DT><H3>F{i}</H3>\n<DL><p>\n");
        html.Append("<DT><A HREF=\"https://bottom.example/\">Bottom</A>\n");
        for (var i = 0; i < 100; i++) html.Append("</DL><p>\n");
        html.Append("</DL><p>\n");
        var nodes = BookmarksFile.Parse(html.ToString());
        var depth = 0;
        var level = nodes;
        while (level is [{ IsFolder: true } folder]) { depth++; level = folder.Children!.ToList(); }
        Assert.Equal("Bottom", Assert.Single(level).Title);
        Assert.Equal(63, depth);
        // And the file of it is not too deep for the bookmarks file either.
        Assert.Equal(Shape(nodes), Shape(Bookmarks.Decode(Bookmarks.Encode(nodes))!));
    }

    [Fact]
    public void ComesBackAsItWent()
    {
        List<Bookmark> roots =
        [
            Bookmark.Site("GitHub", new Uri("https://github.com/")),
            Bookmark.Folder("Work & \"Play\" <3", [Bookmark.Site("Ünïcode ☃", new Uri("https://example.com/a?b=1&c=\"2\"")), Bookmark.Folder("Empty", [])]),
            Bookmark.Folder("Bookmarks bar", [Bookmark.Site("Nested", new Uri("http://nested.example/"))]),
        ];
        var html = BookmarksFile.Export(roots);
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n", html.ReplaceLineEndings("\n"));
        Assert.Contains("<DT><H3 PERSONAL_TOOLBAR_FOLDER=\"true\">Bookmarks bar</H3>", html);
        Assert.Contains("<DT><A HREF=\"https://github.com/\">GitHub</A>", html);
        Assert.Equal(Shape(roots), Shape(BookmarksFile.Parse(html)));
    }

    [Fact]
    public void ReadingAFile()
    {
        Directory.CreateDirectory(Store.Folder);
        var file = Store.File(TempStore.Unique("bookmarks-export").Replace(".json", ".html"));
        File.WriteAllBytes(file, [.. Encoding.ASCII.GetBytes("<DL><DT><A HREF=\"https://cafe.example/\">Caf"), 0xE9, .. Encoding.ASCII.GetBytes("</A></DL>")]);
        Assert.Equal("Café=https://cafe.example/", Shape(BookmarksFile.Read(file)!));
        File.WriteAllText(file, Chrome, new UTF8Encoding(true));
        Assert.Equal(5, Bookmarks.CountOf(BookmarksFile.Read(file)!));
        File.WriteAllText(file, "<html><body>Not bookmarks</body></html>");
        Assert.Null(BookmarksFile.Read(file));
        Assert.Null(BookmarksFile.Read(file + ".missing"));
    }

    [Fact]
    public void Cancelling() => Assert.Throws<OperationCanceledException>(() => BookmarksFile.Parse(Chrome, new CancellationToken(true)));
}
