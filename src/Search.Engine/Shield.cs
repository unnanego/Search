using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.Engine;

// The ad blocker (Shield.swift, YouTubeAds.swift). No settings beyond on and off, no counter, no shield going green.
// On the Mac it is a WebKit content rule list; here it is the nearest thing Chromium has, a built-in extension made of
// declarativeNetRequest rules and a stylesheet, enforced in the browser's own networking before a request is made.
// The extension is written to the store's folder at launch from the lists below and installed in each profile once;
// it is rewritten and installed again only when the lists or the sites it's paused on change.
//
// YouTube's ads aren't someone else's requests: two scripts take them out of the player's own data in the page, and
// let one that plays anyway go by muted and fast (see YouTubeAds.swift for what they may and may not touch).
public static class Shield
{
    private const string ExtensionName = "Search Shield";

    /// Third parties whose only job is to watch or to sell. First-party requests are untouched.
    private static readonly string[] _unwanted =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "googletagservices.com", "google-analytics.com", "googletagmanager.com",
        "adservice.google.com", "amazon-adsystem.com", "adnxs.com", "adsrvr.org", "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com", "smartadserver.com", "sharethrough.com", "indexww.com", "bidswitch.net",
        "33across.com", "teads.tv", "moatads.com", "adroll.com", "scorecardresearch.com", "quantserve.com", "chartbeat.com", "hotjar.com",
        "mouseflow.com", "fullstory.com", "clarity.ms", "mixpanel.com", "amplitude.com", "segment.com", "segment.io", "branch.io", "appsflyer.com",
        "adjust.com", "analytics.tiktok.com", "connect.facebook.net", "ads-twitter.com", "analytics.twitter.com"
    ];

    /// The few slots that are reliably an advertisement and nothing else, kept short on purpose.
    private static readonly string[] _slots =
    [
        ".adsbygoogle", "ins.adsbygoogle", "[id^=\"google_ads_\"]", "[id^=\"div-gpt-ad\"]", "[id^=\"taboola-\"]", "#taboola-below-article",
        "iframe[src*=\"doubleclick.net\"]", "iframe[src*=\"googlesyndication\"]", "iframe[src*=\"amazon-adsystem\"]",
        ".ad-slot", ".ad-slot-container", ".top-banner-ad-container", ".ad-leaderboard", ".ad-billboard", ".ad-giga", ".ad-mpu", ".ad-mrec",
        ".ad-unit", ".adunit", ".adslot", ".dfp-ad", ".gpt-ad", ".w_ad"
    ];

    /// Slots too plain, or too much a site's own, to hide everywhere: only on the sites EasyList's own rules name.
    private static readonly (string[] Sites, string Selector)[] _slotsBySite =
    [
        (["as.com", "elpais.com"], ".ad"),
        (["theguardian.com"], ".top-fronts-banner-ad-container"),
        (["independent.co.uk", "the-independent.com"], "#billboard-wrapper"),
        (["cnn.com"], ".ad-slot-header__wrapper")
    ];

    /// Where YouTube's player reports and fetches ads, never the video.
    private static readonly string[] _youtubePaths = ["/api/stats/ads", "/pagead/", "/ptracking", "/get_midroll_info", "/youtubei/v1/player/ad_break", "/pcs/activeview"];
    public static readonly string[] YouTubeHosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com"];

    private static readonly Dictionary<CoreWebView2Profile, Task> _protected = new();
    private static string? _written;

    public static string Folder => Store.File("Shield");

    /// The extension in this profile, once per launch: a copy from an earlier launch may have been written from other
    /// lists, and WebView2 can't say which, so it's put in fresh (a few tens of milliseconds, the spike measured).
    public static Task Protect(CoreWebView2Profile profile, bool on, IReadOnlyCollection<string> paused)
    {
        Write(paused);
        lock (_protected) return _protected.TryGetValue(profile, out var done) ? done : _protected[profile] = Install(profile, on);
    }

    /// Shield switched on or off, or a site paused or not: every profile given the new copy.
    public static async Task Update(IEnumerable<CoreWebView2Profile> profiles, bool on, IReadOnlyCollection<string> paused)
    {
        Write(paused);
        foreach (var profile in profiles.Distinct().ToList())
        {
            Task install;
            lock (_protected) install = _protected[profile] = Install(profile, on);
            await install;
        }
    }

    private static async Task Install(CoreWebView2Profile profile, bool on)
    {
        foreach (var installed in await profile.GetBrowserExtensionsAsync()) if (installed.Name == ExtensionName) await installed.RemoveAsync();
        var added = await profile.AddBrowserExtensionAsync(Folder);
        if (!on) await added.EnableAsync(false);
    }
    /// The extension's files for these pauses, written only if they changed. Returns their version.
    private static string Write(IReadOnlyCollection<string> paused)
    {
        var rules = Rules(paused).ToJsonString();
        var css = string.Join(", ", _slots) + " { display: none !important; }";
        var sites = _slotsBySite.Select(s => s.Selector + " { display: none !important; }").ToArray();
        var excluded = paused.SelectMany(Patterns).ToArray();
        var scripts = new JsonArray(new JsonObject { ["matches"] = new JsonArray("<all_urls>"), ["exclude_matches"] = Array(excluded), ["css"] = new JsonArray("slots.css"), ["all_frames"] = true, ["run_at"] = "document_start" });
        for (var i = 0; i < _slotsBySite.Length; i++)
            scripts.Add(new JsonObject { ["matches"] = Array(_slotsBySite[i].Sites.SelectMany(Patterns)), ["exclude_matches"] = Array(excluded), ["css"] = new JsonArray($"site{i}.css"), ["all_frames"] = true, ["run_at"] = "document_start" });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rules + css + string.Join("", sites) + scripts.ToJsonString())))[..12].ToLowerInvariant();
        if (_written == hash) return hash;
        var manifest = new JsonObject
        {
            ["manifest_version"] = 3, ["name"] = ExtensionName, ["version"] = "1." + (Convert.ToUInt32(hash[..6], 16) % 60000), ["description"] = "Search's ad blocker.",
            ["permissions"] = new JsonArray("declarativeNetRequest"), ["host_permissions"] = new JsonArray("<all_urls>"),
            ["declarative_net_request"] = new JsonObject { ["rule_resources"] = new JsonArray(new JsonObject { ["id"] = "shield", ["enabled"] = true, ["path"] = "rules.json" }) },
            ["content_scripts"] = scripts
        };
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "manifest.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "rules.json"), rules);
        File.WriteAllText(Path.Combine(Folder, "slots.css"), css);
        for (var i = 0; i < sites.Length; i++) File.WriteAllText(Path.Combine(Folder, $"site{i}.css"), sites[i]);
        _written = hash;
        return hash;
    }

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());

    /// A site and every subdomain of it, as match patterns.
    private static string[] Patterns(string site) => [$"*://{site}/*", $"*://*.{site}/*"];

    internal static JsonArray Rules(IReadOnlyCollection<string> paused)
    {
        var rules = new JsonArray();
        var id = 1;
        JsonObject Rule(JsonObject action, JsonObject condition, int priority = 1) => new() { ["id"] = id++, ["priority"] = priority, ["action"] = action, ["condition"] = condition };
        var block = () => new JsonObject { ["type"] = "block" };
        rules.Add(Rule(block(), new JsonObject { ["requestDomains"] = Array(_unwanted), ["domainType"] = "thirdParty" }));
        foreach (var path in _youtubePaths) rules.Add(Rule(block(), new JsonObject { ["urlFilter"] = "||youtube.com" + path }));
        // The player's quality reports for an ad, never the video's own.
        rules.Add(Rule(block(), new JsonObject { ["regexFilter"] = @"^https?://([^/]+\.)?youtube\.com/api/stats/qoe\?.*adformat=" }));
        // Google's own ad paths and the video ads SDK, only from YouTube's pages: other players may need the SDK.
        foreach (var filter in new[] { "||google.com/pagead/", "||imasdk.googleapis.com/" })
            rules.Add(Rule(block(), new JsonObject { ["urlFilter"] = filter, ["initiatorDomains"] = new JsonArray("youtube.com", "youtube-nocookie.com") }));
        // Paused on a site: everything in its pages is let through, above every rule here.
        foreach (var site in paused)
            rules.Add(Rule(new JsonObject { ["type"] = "allowAllRequests" }, new JsonObject { ["requestDomains"] = new JsonArray(site), ["resourceTypes"] = new JsonArray("main_frame", "sub_frame") }, priority: 100));
        return rules;
    }

    /// The scripts for YouTube: the first in the page's own world, before its scripts; the second in Search's world.
    public static string YouTubePage(IReadOnlyCollection<string> paused) => YouTube(paused, Script("youtube-page.js"));
    public static string YouTubeSkipper(IReadOnlyCollection<string> paused) => YouTube(paused, Script("youtube-skipper.js"));

    private static string YouTube(IReadOnlyCollection<string> paused, string body) =>
        $"(function () {{ var hosts = {JsonSerializer.Serialize(YouTubeHosts)}, paused = {JsonSerializer.Serialize(paused)}; var site = location.hostname.replace(/^www\\./, ''); if (hosts.indexOf(location.hostname) < 0 || paused.indexOf(site) >= 0) return;\n{body}\n}})();";

    private static string Script(string name)
    {
        using var stream = typeof(Shield).Assembly.GetManifestResourceStream("Search.Engine.Scripts." + name) ?? throw new InvalidOperationException(name + " isn't embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
