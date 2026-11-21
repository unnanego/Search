using System.Diagnostics;
using Search.Core;

namespace Search.Core.Tests;

// Markdown, turned into reading mode's HTML (MarkdownTests.swift, case for case).
public sealed class MarkdownTests
{
    private static readonly Uri Base = new("https://docs.example.test/guide/start.md");

    private static string Html(string text) => Markdown.Page(text, Base).Html;

    // the title

    [Fact]
    public void FirstHeadingBecomesTheTitleAndLeavesTheBody()
    {
        var page = Markdown.Page("# Getting *started*\n\nHello.", Base);
        Assert.Equal("Getting started", page.Title);
        Assert.Equal("<p>Hello.</p>", page.Html);
    }

    [Fact]
    public void HeadingBelowASectionIsNotTheTitle()
    {
        var page = Markdown.Page("## Setup\n\n# Later\n", Base);
        Assert.Null(page.Title);
        Assert.Contains("<h1 id=\"later\">Later</h1>", page.Html);
    }

    [Fact]
    public void FrontMatterGivesTheTitleAndIsNotShown()
    {
        var page = Markdown.Page("---\ntitle: \"Intro to Things\"\nurl: https://x.test\n---\n\nBody text.", Base);
        Assert.Equal("Intro to Things", page.Title);
        Assert.Equal("<p>Body text.</p>", page.Html);
    }

    [Fact]
    public void NoteToAgentsAboutLlmsTxtIsDropped()
    {
        var page = Markdown.Page("> ## Documentation Index\n> Fetch the complete documentation index at: https://docs.example.test/llms.txt\n\n# Quickstart\n\n> A real quote.", Base);
        Assert.Equal("Quickstart", page.Title);
        Assert.DoesNotContain("llms.txt", page.Html);
        Assert.Equal("<blockquote><p>A real quote.</p></blockquote>", page.Html);
    }

    // nothing gets through as markup of its own

    [Fact]
    public void RawScriptIsDropped() => Assert.DoesNotContain("<script", Html("<script>alert(1)</script>\n\nText <script>x()</script> here").ToLowerInvariant());

    [Fact]
    public void DangerousAddressesAreNotLinked()
    {
        var html = Html("[click](javascript:alert(1)) ![x](data:text/html,hi) <a href=\"javascript:bad()\">a</a>");
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("data:", html);
        Assert.Contains("click", html);
    }

    [Fact]
    public void AttributesAreTrimmedToTheSafeOnes() =>
        Assert.Equal("<img alt=\"A\" src=\"https://docs.example.test/a.png\" loading=\"lazy\">", Html("<img src=\"/a.png\" onerror=\"steal()\" alt=\"A\">"));

    [Fact]
    public void TextIsEscaped() => Assert.Equal("<p>1 &lt; 2 &amp; &quot;so&quot; 3 &gt; 2</p>", Html("1 < 2 & \"so\" 3 > 2"));

    [Fact]
    public void EntitiesPassThrough() => Assert.Equal("<p>AT&amp;T &copy; &#8212;</p>", Html("AT&amp;T &copy; &#8212;"));

    // addresses

    [Fact]
    public void RelativeAddressesResolveAgainstTheTwin()
    {
        var html = Html("[next](next.md) [root](/pricing) [here](#setup) ![pic](img/a.png)");
        Assert.Contains("href=\"https://docs.example.test/guide/next.md\"", html);
        Assert.Contains("href=\"https://docs.example.test/pricing\"", html);
        Assert.Contains("href=\"#setup\"", html);
        Assert.Contains("src=\"https://docs.example.test/guide/img/a.png\"", html);
    }

    [Fact]
    public void EmptyPictureIsLeftOut() => Assert.Equal("<p>Before  after</p>", Html("Before ![]() after"));

    [Fact]
    public void ReferenceLinks()
    {
        var html = Html("See [the docs][d] and [Docs].\n\n[d]: https://x.test/a \"Title\"\n[docs]: https://x.test/b");
        Assert.Contains("<a href=\"https://x.test/a\" title=\"Title\">the docs</a>", html);
        Assert.Contains("<a href=\"https://x.test/b\">Docs</a>", html);
    }

    [Fact]
    public void BareAddressesAreLinkedWithoutTheirFullStop()
    {
        var html = Html("Go to https://example.test/a_b. Or www.example.test!");
        Assert.Contains("<a href=\"https://example.test/a_b\">https://example.test/a_b</a>.", html);
        Assert.Contains("<a href=\"https://www.example.test\">www.example.test</a>!", html);
    }

    [Fact]
    public void AddressesWithSpacesAreSpelledSafely() => Assert.Contains("href=\"https://docs.example.test/guide/a%20b.md\"", Html("[x](<a b.md>)"));

    // inline

    [Fact]
    public void Emphasis() => Assert.Equal("<p><em>a</em> <strong>b</strong> <em>c</em> <strong>d</strong> <del>e</del></p>", Html("*a* **b** _c_ __d__ ~~e~~"));

    [Fact]
    public void UnderscoresInsideWordsAndStarsInsideCodeStayPut() =>
        Assert.Equal("<p>snake_case_name and <code>a*b*c</code> and 2 * 3 * 4</p>", Html("snake_case_name and `a*b*c` and 2 * 3 * 4"));

    [Fact]
    public void EscapedPunctuation() => Assert.Equal("<p>*not em* and [not a link]</p>", Html(@"\*not em\* and \[not a link\]"));

    [Fact]
    public void HardBreaks() => Assert.Equal("<p>one<br>two<br>\nthree\nfour</p>", Html("one  \ntwo\\\nthree\nfour"));

    // blocks

    [Fact]
    public void FencedCodeKeepsItsTextAndLanguage() =>
        Assert.Equal("<pre><code class=\"language-bash\">echo &quot;&lt;hi&gt;&quot; *x*</code></pre>", Html("```bash theme={null}\necho \"<hi>\" *x*\n```"));

    [Fact]
    public void FourSpacesIsNotCode() => Assert.Equal("<p>Indented, as MDX does.</p>", Html("    Indented, as MDX does."));

    [Fact]
    public void TightAndLooseLists()
    {
        Assert.Equal("<ul><li>a</li><li>b</li></ul>", Html("- a\n- b"));
        Assert.Equal("<ul><li><p>a</p></li><li><p>b</p></li></ul>", Html("- a\n\n- b"));
    }

    [Fact]
    public void NestedAndOrderedLists() =>
        Assert.Equal("<ol start=\"3\"><li>three</li><li>four\n<ul><li>inner</li><li>more</li></ul></li><li>five</li></ol>", Html("3. three\n4. four\n   - inner\n   - more\n5. five"));

    [Fact]
    public void CodeInsideAListItem()
    {
        var html = Html("1. Run:\n\n   ```bash\n   mint dev \\\n     --port 3000\n   ```\n2. Done");
        Assert.Contains("<pre><code class=\"language-bash\">mint dev \\\n  --port 3000</code></pre>", html);
        Assert.StartsWith("<ol><li><p>Run:</p>", html);
    }

    [Fact]
    public void TaskList() =>
        Assert.Equal("<ul><li><input type=\"checkbox\" disabled> todo</li><li><input type=\"checkbox\" checked disabled> done</li></ul>", Html("- [ ] todo\n- [x] done"));

    [Fact]
    public void SentenceEndingInANumberIsNotAList() => Assert.Equal("<p>It began\n1984. Then it ended.</p>", Html("It began\n1984. Then it ended."));

    [Fact]
    public void Table() =>
        Assert.Equal("<div class=\"office-wide\"><table><thead><tr><th style=\"text-align:left\">Name</th><th style=\"text-align:right\">Cost</th></tr></thead><tbody><tr><td style=\"text-align:left\"><code>a|b</code></td><td style=\"text-align:right\"><strong>1</strong></td></tr></tbody></table></div>",
            Html("| Name | Cost |\n| :--- | ---: |\n| `a|b` | **1** |"));

    [Fact]
    public void SetextHeadingsAndRules()
    {
        var page = Markdown.Page("Title\n=====\n\nSub\n---\n\n***", Base);
        Assert.Equal("Title", page.Title);
        Assert.Equal("<h2 id=\"sub\">Sub</h2>\n<hr>", page.Html);
    }

    [Fact]
    public void RepeatedHeadingsGetTheirOwnIds() => Assert.Equal("<h2 id=\"setup\">Setup</h2>\n<h2 id=\"setup-1\">Setup</h2>", Html("## Setup\n## Setup"));

    [Fact]
    public void CommentsAreDropped() => Assert.Equal("<p>Text  end</p>", Html("<!-- a\nb -->\nText <!-- c --> end"));

    // what docs sites write

    [Fact]
    public void ComponentsGiveTheirTitlesAndStepAside()
    {
        var html = Html("<Steps>\n  <Step title=\"Make your first call\">\n    Install the SDK and send a message.\n\n    [Go to the quickstart](/get-started)\n  </Step>\n</Steps>");
        Assert.Equal("<p class=\"office-part\"><strong>Make your first call</strong></p>\n<p>Install the SDK and send a message.</p>\n<p><a href=\"https://docs.example.test/get-started\">Go to the quickstart</a></p>", html);
    }

    [Fact]
    public void CardTitleLinksWhereTheCardGoes()
    {
        var html = Html("<Card\n  title=\"Pricing\"\n  href=\"/pricing\"\n  icon=\"coins\"\n>\n  How it's billed.\n</Card>");
        Assert.StartsWith("<p class=\"office-part\"><a href=\"https://docs.example.test/pricing\"><strong>Pricing</strong></a></p>", html);
        Assert.Contains("<p>How it&#39;s billed.</p>", html);
    }

    [Fact]
    public void CalloutsBecomeQuotes() =>
        Assert.Equal("<blockquote>\n<p>Looking to chat? Visit <a href=\"https://app.test\">the app</a>.</p>\n</blockquote>", Html("<Note>\n  Looking to chat? Visit [the app](https://app.test).\n</Note>"));

    [Fact]
    public void InlineHtmlThatIsKnownIsKept() =>
        Assert.Equal("<p><a href=\"https://x.test\"><u>Markdown</u></a> <kbd>⌘R</kbd> plain</p>", Html("[<u>Markdown</u>](https://x.test) <kbd>⌘R</kbd> <span>plain</span>"));

    [Fact]
    public void DetailsSurvive() =>
        Assert.Equal("<details>\n<summary>More</summary>\n<p>Hidden text.</p>\n</details>", Html("<details>\n<summary>More</summary>\n\nHidden text.\n\n</details>"));

    // size

    [Fact]
    public void ALargeDocumentRendersQuickly()
    {
        const string section = "## Section\n\nSome **bold** text with a [link](https://example.test/page) and `code`, and more words to read.\n\n- one\n- two with *emphasis*\n\n```swift\nlet x = 1\n```\n\n";
        var text = string.Concat(Enumerable.Repeat(section, 2000));
        var clock = Stopwatch.StartNew();
        var page = Markdown.Page(text, Base);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.Contains("id=\"section-1999\"", page.Html);
    }
}
