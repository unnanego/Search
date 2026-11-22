using System.Text.Json;
using System.Text.Json.Nodes;

namespace Search.Core;

// Where an extension runs: Chrome's "Site access", per extension (ExtensionSiteAccess.swift).
//
// Settings › Extensions, an extension's "Runs on", and a right-click on its button. Every site it asked for (as ever,
// and the default), only the sites you name, or only where you click its button.
//
// On the Mac that is held in WebKit's own permissions. WebView2 has no API to withhold any of an extension's sites, so
// here the copy Search loads is restricted instead: its manifest is rewritten (Restrict) to ask only for what you let
// it have, and Chromium then never puts its content scripts in the pages it shouldn't be on. Changing "Runs on" means
// preparing the copy again and loading it again.

/// What an extension is let onto.
public enum SiteMode
{
    /// Every site it asked for: what it always was.
    All,
    /// The sites you name, each with its subdomains.
    Some,
    /// None until you click its button, then that tab's site.
    Click,
}

public sealed record SiteAccess
{
    public SiteMode Mode { get; init; }
    /// The sites named for Some, as hosts ("example.com"). Kept when another mode is picked, so going back to it brings
    /// them back.
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public SiteAccess(SiteMode mode = SiteMode.All, IEnumerable<string>? hosts = null) { Mode = mode; Hosts = hosts?.ToList() ?? []; }

    public bool Restricted => Mode != SiteMode.All;

    public static string Title(SiteMode mode) => mode switch { SiteMode.Some => "Some sites", SiteMode.Click => "On click", _ => "All sites" };

    public bool Equals(SiteAccess? other) => other is not null && Mode == other.Mode && Hosts.SequenceEqual(other.Hosts);
    public override int GetHashCode() => Hosts.Aggregate(Mode.GetHashCode(), (hash, h) => HashCode.Combine(hash, h));

    // Kept.

    /// One note per extension beside its others ("extensions.private.…"), written only once it isn't the default: an
    /// extension never touched has none, and reads as every site, as before there was a choice.
    public static string Key(string id) => $"extensions.sites.{id}";

    public static SiteAccess Of(string id, IDictionary<string, string> notes)
    {
        if (!notes.TryGetValue(Key(id), out var text)) return new SiteAccess();
        try
        {
            if (JsonNode.Parse(text) is not JsonObject kept) return new SiteAccess();
            var mode = (kept["mode"] as JsonValue)?.TryGetValue<string>(out var m) == true ? m switch { "some" => SiteMode.Some, "click" => SiteMode.Click, _ => SiteMode.All } : SiteMode.All;
            var hosts = (kept["hosts"] as JsonArray ?? []).Select(h => (h as JsonValue)?.TryGetValue<string>(out var s) == true ? Host(s) : null).OfType<string>();
            return new SiteAccess(mode, Unique(hosts));
        }
        catch (JsonException) { return new SiteAccess(); }
    }

    public void Save(string id, IDictionary<string, string> notes)
    {
        if (this == new SiteAccess()) { notes.Remove(Key(id)); return; }
        notes[Key(id)] = new JsonObject { ["mode"] = Mode.ToString().ToLowerInvariant(), ["hosts"] = new JsonArray([.. Hosts.Select(h => (JsonNode?)h)]) }.ToJsonString();
    }

    public static void Forget(string id, IDictionary<string, string> notes) => notes.Remove(Key(id));

    // Hosts.

    private const string HostLetters = "abcdefghijklmnopqrstuvwxyz0123456789.-_";

    /// The host in what was typed (a site, an address, "*.site"), lower case, without "www." or a port. Null for what
    /// names no site.
    public static string? Host(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "http://" + text;
        // "*." isn't a host Uri reads; it is the same site with its subdomains, which every named site has anyway.
        var split = text.IndexOf("://", StringComparison.Ordinal) + 3;
        if (string.CompareOrdinal(text, split, "*.", 0, 2) == 0) text = text[..split] + text[(split + 2)..];
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.HostNameType is UriHostNameType.Unknown) return null;
        var host = url.IdnHost.TrimEnd('.');
        if (host.StartsWith("www.", StringComparison.Ordinal) && host[4..].Contains('.')) host = host[4..];
        return host.Length > 0 && host.All(HostLetters.Contains) && !host.StartsWith('.') && !host.Contains("..") ? host : null;
    }

    /// Whether `host` is one of `hosts` or under one: example.com covers news.example.com, never notexample.com or
    /// example.com.evil.
    public static bool Covers(IEnumerable<string> hosts, string host)
    {
        host = host.ToLowerInvariant();
        return hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    public bool Covers(string host) => Mode switch { SiteMode.All => true, SiteMode.Some => Covers(Hosts, host), _ => false };

    /// The site a page's address names, for "this site": web pages only.
    public static string? Site(Uri? url) => url is { IsAbsoluteUri: true } && url.Scheme is "http" or "https" ? url.Host.ToLowerInvariant() : null;

    public SiteAccess Adding(string host) => this with { Mode = SiteMode.Some, Hosts = Unique([.. Hosts, host]) };

    /// The site and anything named under it: taking example.com away shouldn't leave news.example.com quietly allowed.
    public SiteAccess Removing(string host) => this with { Hosts = Hosts.Where(h => h != host && !h.EndsWith("." + host, StringComparison.Ordinal) && !host.EndsWith("." + h, StringComparison.Ordinal)).ToList() };

    /// In the order first named, each once.
    public static IReadOnlyList<string> Unique(IEnumerable<string> hosts) => hosts.Distinct(StringComparer.Ordinal).ToList();

    // Patterns.

    /// What is left of one pattern an extension asked for, on `host` (and under it when `subdomains`), as a match
    /// pattern's text. The asked-for scheme, port and path are kept; null when the two don't meet. A pattern narrower
    /// than the host is kept as it is. "*.host" is the host itself and its subdomains, as Chrome reads it.
    public static string? Narrow(MatchPattern pattern, string host, bool subdomains)
    {
        if (!pattern.Web) return null;
        var scheme = pattern.Scheme;
        var port = pattern.Port is { } p ? ":" + p : "";
        var path = pattern.AllUrls ? "/*" : pattern.Path;
        var asked = pattern.Host;
        var mine = subdomains && !IsAddress(host) ? "*." + host : host;
        if (asked == "*") return $"{scheme}://{mine}{port}{path}";
        if (asked.StartsWith("*.", StringComparison.Ordinal))
        {
            var bare = asked[2..];
            // Its wildcard covers the host: the host it is, then.
            if (host == bare || host.EndsWith("." + bare, StringComparison.Ordinal)) return $"{scheme}://{mine}{port}{path}";
            // Under the host: kept, when subdomains count.
            return subdomains && bare.EndsWith("." + host, StringComparison.Ordinal) ? $"{scheme}://{asked}{port}{path}" : null;
        }
        return asked == host || (subdomains && asked.EndsWith("." + host, StringComparison.Ordinal)) ? $"{scheme}://{asked}{port}{path}" : null;
    }

    /// An IP address or a one-word host (localhost), which has no subdomains.
    private static bool IsAddress(string host) => !host.Contains('.') || host.All(c => char.IsAsciiDigit(c) || c == '.');

    /// The patterns that let an extension onto `host` as far as it asked: never files or extension pages.
    public static IReadOnlyList<string> Patterns(IEnumerable<MatchPattern> asked, string host, bool subdomains) =>
        asked.Select(p => Narrow(p, host, subdomains)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// Extensions that only do their job everywhere: a password manager fills sign-ins on whatever site you're on.
    /// Told, never stopped. `said` is what it set through chrome.privacy.
    public static bool NeedsEverySite(string name, IReadOnlyDictionary<string, object?>? said = null)
    {
        if (said is not null && said.TryGetValue("privacy.services.passwordSavingEnabled", out var saving) && saving is false) return true;
        name = name.ToLowerInvariant();
        return _everywhere.Any(name.Contains);
    }

    private static readonly string[] _everywhere = ["password", "passkey", "1password", "bitwarden", "lastpass", "dashlane", "keeper", "proton pass", "nordpass", "enpass", "roboform"];

    // The copy WebView2 loads.

    /// The manifest the loaded copy gets, asking only for what it is let onto. For Some: every pattern among its host
    /// permissions (and, for Manifest V2, among its permissions) and every content script's matches becomes what is
    /// left of it on each named site, its exclusions kept; a content script left matching nothing is dropped. For
    /// Click: no sites and no content scripts at all, and activeTab, which is what Chromium gives a click on its button
    /// (and lets Search run its scripts there). Optional host permissions are left alone: asking for them still asks.
    /// A new object either way; `manifest` isn't touched.
    public static JsonObject Restrict(JsonObject manifest, SiteAccess access)
    {
        var copy = manifest.DeepClone().AsObject();
        switch (access.Mode)
        {
            case SiteMode.Some:
                if (copy["host_permissions"] is JsonArray hosts) copy["host_permissions"] = Narrowed(hosts, access.Hosts, keepOthers: true);
                if (copy["permissions"] is JsonArray permissions) copy["permissions"] = Narrowed(permissions, access.Hosts, keepOthers: true);
                if (copy["content_scripts"] is JsonArray scripts)
                {
                    var kept = new JsonArray();
                    foreach (var script in scripts.Select(s => s?.DeepClone()))
                    {
                        if (script is not JsonObject one) continue;
                        if (one["matches"] is JsonArray matches) one["matches"] = Narrowed(matches, access.Hosts, keepOthers: false);
                        if (one["matches"] is JsonArray { Count: > 0 }) kept.Add(one);
                    }
                    copy["content_scripts"] = kept;
                }
                break;
            case SiteMode.Click:
                copy.Remove("host_permissions");
                copy.Remove("content_scripts");
                var rest = (copy["permissions"] as JsonArray ?? []).Where(p => !IsPatternNode(p)).Select(p => p?.DeepClone()).ToList();
                if (!rest.Any(p => Text(p) == "activeTab")) rest.Add("activeTab");
                copy["permissions"] = new JsonArray([.. rest]);
                break;
        }
        return copy;
    }

    /// Each pattern in the list replaced by what is left of it on the named hosts; what isn't a pattern (a permission's
    /// name, among Manifest V2's permissions) kept as it is when `keepOthers`.
    private static JsonArray Narrowed(JsonArray list, IReadOnlyList<string> hosts, bool keepOthers)
    {
        var output = new List<JsonNode?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            if (MatchPattern.Parse(Text(node)) is not { } pattern) { if (keepOthers) output.Add(node?.DeepClone()); continue; }
            foreach (var host in hosts)
                if (Narrow(pattern, host, subdomains: true) is { } narrowed && seen.Add(narrowed)) output.Add(narrowed);
        }
        return new JsonArray([.. output]);
    }

    private static bool IsPatternNode(JsonNode? node) => MatchPattern.IsPattern(Text(node));
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
