using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.Engine;

/// What a page's picker said (see curtain.js): an element chosen, Ctrl+Z, pointing over, or a selector it couldn't make.
public enum CurtainSaid { Chosen, Undo, Ended, Trouble }

/// One thing the picker said. Selector, label and note for Chosen (the note is its size and corner, measured as it went,
/// since a hidden thing has no size to measure later); Trouble's reason in Trouble.
public readonly record struct CurtainHeard(CurtainSaid Said, string Selector = "", string Label = "", string Note = "", string Trouble = "");

/// A site's two sheets for a page: what's hidden there, and your own style for it (null while that setting is off).
public readonly record struct CurtainDress(string Hiding, string? Style);

// Taking things off a page and keeping them off (Curtain.swift, the page's side; Hidden.swift's way back; and
// SiteStyles.swift's sheet, which goes the same way). Point at a cookie bar or a newsletter overlay and it goes, and
// is still gone next time: the selector the picker makes is kept by site (Hidden.cs) and put back by a stylesheet in
// place before the page has drawn a single frame, so nothing is ever seen appearing and vanishing.
//
// That stylesheet is armed for the next document, as Tab.arm(hiding:to:) does: a script for the tab's next documents,
// in Search's world, holding the CSS for the one site it's going to (and checking it got there). It's put in when a
// navigation starts, before the request has even gone, and replaced on the next. The page up now is dressed directly.
public static class Curtain
{
    /// Once, before any tab is shown: Pages.AddScript(Curtain.DocumentStartScript). The picker, asleep until asked.
    public static string DocumentStartScript => Scripts.Read("sheet.js") + "\n" + Scripts.Read("curtain.js");

    /// What's armed in each web view, so the same sheets aren't sent again for every link followed on a site. Kept by
    /// the XAML control, whose identity is the view's own; a tab woken from sleep has a new one, with nothing armed.
    private sealed class Armed { public string? Id, Source; public bool Followed; }
    private static readonly ConditionalWeakTable<WebView2, Armed> _armed = new();

    /// The stylesheet for a list of selectors. Each stands alone in its own rule: one the browser can't parse would
    /// otherwise take the whole list down with it.
    public static string Sheet(IEnumerable<string> selectors) => string.Join("\n", selectors.Select(s => s + " { display: none !important; }"));

    /// Keeps every tab of a window armed, once per window: for each web view as it's built (before its first page is
    /// asked for) and on every navigation it starts, redirects included. `dress` gives the sheets for a tab going to an
    /// address. Call Arm yourself after a change (a hide, a restore, a style typed) so the next page has it too.
    public static void Follow(Pages pages, Browser browser, Func<Tab, Uri?, CurtainDress> dress)
    {
        void Watch(Tab tab) => tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Tab.Live) && tab.Live) Built(tab); };
        void Built(Tab tab)
        {
            if (pages.ViewOf(tab) is not { CoreWebView2: { } core } view || _armed.GetOrCreateValue(view) is not { Followed: false } armed) return;
            armed.Followed = true;
            core.NavigationStarting += (sender, e) => { var next = Uri.TryCreate(e.Uri, UriKind.Absolute, out var url) ? url : null; _ = Arm(pages, tab, next, dress(tab, next)); };
            _ = Arm(pages, tab, tab.Url, dress(tab, tab.Url));
        }
        foreach (var tab in browser.Tabs) { Watch(tab); if (tab.Live) Built(tab); }
        browser.Tabs.CollectionChanged += (_, e) => { foreach (var tab in e.NewItems?.OfType<Tab>() ?? []) Watch(tab); };
    }

    /// The sheets for the tab's next documents, as long as they're of `next`'s site; nothing for an address that
    /// isn't a site's. Replaces what was armed before, unless it's the same.
    public static Task Arm(Pages pages, Tab tab, Uri? next, CurtainDress dress) => Arm(pages, tab, next, dress.Hiding, dress.Style);

    public static async Task Arm(Pages pages, Tab tab, Uri? next, string hiding, string? style)
    {
        if (pages.ViewOf(tab) is not { CoreWebView2: { } core } view) return;
        var armed = _armed.GetOrCreateValue(view);
        var host = Guard(next);
        var source = host is null || (hiding.Length == 0 && string.IsNullOrEmpty(style)) ? null : Dressing(host, hiding, style);
        if (source == armed.Source) return;
        var old = armed.Id;
        armed.Source = source;
        armed.Id = null;
        // The new one in first: the old one checks its host, and for the same host the new one runs after it anyway.
        if (source is not null)
        {
            var id = (await Call(core, "Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = source, ["worldName"] = World.Name }))?["identifier"]?.GetValue<string>();
            if (armed.Source == source) armed.Id = id;
            else if (id is not null) await Remove(core, id);
        }
        if (old is not null) await Remove(core, old);
    }

    /// The page up now dressed in what's hidden on its site (Tab.applyVeils): after a hide, a restore, an undo.
    public static Task Apply(Pages pages, Tab tab, string hiding) => pages.Evaluate(tab, Dressing(null, hiding, null));

    /// The page up now in your own style for its site; null or empty takes it off (SiteStyles.live).
    public static Task Style(Pages pages, Tab tab, string? css) => pages.Evaluate(tab, Dressing(null, null, css ?? ""));

    /// Ctrl+Shift+H: hovering lights an element up, a click takes it off and says so (curtain.chosen). Picking goes on
    /// after a pick, until Esc in the page, StopPicking, or another page; each ends with curtain.off.
    public static Task StartPicking(Pages pages, Tab tab) => pages.Evaluate(tab, "globalThis.__searchCurtain && __searchCurtain.on()");

    public static Task StopPicking(Pages pages, Tab tab) => pages.Evaluate(tab, "globalThis.__searchCurtain && __searchCurtain.off()");

    /// One hidden thing back on the page, outlined and scrolled to, while the pointer rests on its row in the list of
    /// what's hidden here: `hidingWithout` is the site's sheet without that one selector.
    public static Task Peek(Pages pages, Tab tab, string selector, string hidingWithout) =>
        pages.Evaluate(tab, $"globalThis.__searchCurtain && __searchCurtain.peek({Scripts.Literal(hidingWithout)}, {Scripts.Literal(selector)})");

    /// The page back as it was, leaving the list: `hiding` is the site's whole sheet.
    public static Task Unpeek(Pages pages, Tab tab, string hiding) => pages.Evaluate(tab, $"globalThis.__searchCurtain && __searchCurtain.unpeek({Scripts.Literal(hiding)})");

    /// A message from Pages.Message, if it's the picker's (kinds curtain.chosen, curtain.undo, curtain.off, curtain.trouble).
    public static bool TryHear(string kind, JsonElement body, out CurtainHeard heard)
    {
        CurtainHeard? said = kind switch
        {
            "curtain.chosen" when Text(body, "selector") is { Length: > 0 } selector => new CurtainHeard(CurtainSaid.Chosen, selector, Text(body, "label") ?? selector, Text(body, "note") ?? ""),
            "curtain.undo" => new CurtainHeard(CurtainSaid.Undo),
            "curtain.off" => new CurtainHeard(CurtainSaid.Ended),
            "curtain.trouble" => new CurtainHeard(CurtainSaid.Trouble, Trouble: Text(body, "trouble") ?? ""),
            _ => null
        };
        heard = said ?? default;
        return said is not null;
    }

    /// A site as the page spells it (location.hostname: punycode, no port), without www.; null for an address that has
    /// no site.
    private static string? Guard(Uri? url)
    {
        if (url is not { IsAbsoluteUri: true } || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.IdnHost)) return null;
        var host = url.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string Dressing(string? host, string? hiding, string? style) => $"{Scripts.Read("sheet.js")}\n({Scripts.Read("dress.js")})({Scripts.Literal(host)}, {Scripts.Literal(hiding)}, {Scripts.Literal(style)});";

    private static string? Text(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Task Remove(CoreWebView2 core, string id) => Call(core, "Page.removeScriptToEvaluateOnNewDocument", new JsonObject { ["identifier"] = id });

    private static async Task<JsonNode?> Call(CoreWebView2 core, string method, JsonObject args)
    {
        try { return JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync(method, args.ToJsonString())); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
