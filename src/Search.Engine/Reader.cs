using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Search.Core;
using Windows.Foundation;

namespace Search.Engine;

/// How the article looks: light, dark, or as the window is. Light whenever reading mode doesn't follow the window.
public enum ReaderLook { Light, Dark, System }

/// What asking for reading mode came to. Left: the article was up, and the page is being loaded again. Busy: still
/// asking the site for its markdown from the last press. Moved: the page went somewhere else meanwhile, and the answer
/// belonged to a page that isn't there any more. Only Nothing has anything to say ("Nothing to read on this page").
public enum ReaderOutcome { Read, Left, Nothing, Busy, Moved }

// Reading mode (Reader.swift): the article, and nothing that was arranged around it, in place of the page, in the same
// tab and the same document, as on the Mac: the address, the history and Back stay the page's own, its pictures load
// with its cookies, and every relative address in the article still means what it meant. Leaving it loads the page
// again rather than putting the old markup back: restored HTML looks right and does nothing, every listener the page
// had having been thrown away with it.
//
// The article is the site's own markdown copy of the page when there is one and you've asked for it (Settings'
// "reads twins"), drawn by Markdown.cs; otherwise reader.js works it out from the page.
public static class Reader
{
    private static readonly HashSet<Tab> _seeking = new();

    private static string Call(string call) => Scripts.Read("reader.js") + "\n;" + call;

    private static string Name(ReaderLook look) => look switch { ReaderLook.Dark => "dark", ReaderLook.System => "system", _ => "light" };

    /// F9: the article if the page isn't one, the page again if it is.
    public static async Task<ReaderOutcome> Toggle(Pages pages, Tab tab, ReaderLook look, bool twins)
    {
        if (await Showing(pages, tab)) { tab.Do(TabCommand.Reload); return ReaderOutcome.Left; }
        return await Open(pages, tab, look, twins);
    }

    /// Whether the page up is an article now: after Toggle, after Back to one (the document as it was left), and so
    /// whether the look's switch belongs in its corner.
    public static async Task<bool> Showing(Pages pages, Tab tab) => await pages.Evaluate(tab, "!!document.getElementById('office-reader')") is JsonValue v && v.TryGetValue<bool>(out var up) && up;

    /// The article, in place of the page: the site's twin first when `twins`, the guess otherwise or failing that.
    public static async Task<ReaderOutcome> Open(Pages pages, Tab tab, ReaderLook look, bool twins)
    {
        if (pages.CoreOf(tab) is not { } core) return ReaderOutcome.Nothing;
        if (!_seeking.Add(tab)) return ReaderOutcome.Busy;
        var moved = false;
        void Started(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e) => moved = true;
        var handler = new TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>(Started);
        core.NavigationStarting += handler;
        try
        {
            var page = core.Source;
            bool Gone() => moved || !ReferenceEquals(pages.CoreOf(tab), core) || core.Source != page;
            if (twins)
            {
                var found = await pages.Evaluate(tab, Call("__searchReader.twin()"));
                // Gone somewhere else while the twin was on its way: it belongs to a page that isn't there any more, and
                // so does the guess.
                if (Gone()) return ReaderOutcome.Moved;
                if (Text(found?["text"]) is { } text && Uri.TryCreate(Text(found?["url"]), UriKind.Absolute, out var address) && Markdown.Page(text, address) is { Html.Length: > 0 } drawn)
                {
                    var from = Text(found?["from"]) is { } site ? site.Length == 0 ? "markdown" : site + " · markdown" : "markdown";
                    var title = drawn.Title ?? Text(found?["title"]) ?? "";
                    var shown = await pages.Evaluate(tab, Call($"__searchReader.show({Scripts.Literal(drawn.Html)}, {Scripts.Literal(title)}, {Scripts.Literal(from)}, {Scripts.Literal(Name(look))})"));
                    return Text(shown) == "read" ? ReaderOutcome.Read : ReaderOutcome.Nothing;
                }
            }
            var guessed = await pages.Evaluate(tab, Call($"__searchReader.guess({Scripts.Literal(Name(look))})"));
            return Text(guessed) == "read" ? ReaderOutcome.Read : ReaderOutcome.Nothing;
        }
        finally
        {
            core.NavigationStarting -= handler;
            _seeking.Remove(tab);
        }
    }

    /// A look put on the article up now (the switch in its corner); it holds for the article and goes with the page.
    public static Task Look(Pages pages, Tab tab, ReaderLook look) => pages.Evaluate(tab, look == ReaderLook.System
        ? "document.documentElement.removeAttribute('data-office-look')"
        : $"document.documentElement.setAttribute('data-office-look', {Scripts.Literal(Name(look))})");

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
