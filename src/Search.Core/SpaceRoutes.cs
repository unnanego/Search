using System.Text.Json;

namespace Search.Core;

// Send links to a space (SpaceRoutes.swift): links to chosen sites open in their space, with its sign-ins, as Arc's Air
// Traffic Control does. Off unless turned on, and only with spaces on. A rule is a site, its subdomains with it, and a
// space; the most specific site wins. It sends a link from another app, and a link a page opens in a new tab from a
// page in another space; the window decides which links those are. A link whose rule names a space that has gone stays
// where it is: the rule is passed over, and the link opens as it always did.
//
// Kept where the Mac keeps them, in its settings: "spaces.links" for on or off, "spaces.links.rules" for the rules, a
// list of {"site": "notion.so", "space": "<UUID>"}. Here that list is the setting's text, as JSON.

/// A site (a host, lowercased, without www.: "notion.so") and the space its links open in.
public sealed record SpaceRoute(string Site, Guid Space)
{
    public string Id => Site;
}

public sealed class SpaceRoutes : Observable
{
    public const string Key = "spaces.links", RulesKey = "spaces.links.rules";

    private readonly Spaces _spaces;
    private readonly IDictionary<string, string> _settings;
    private IReadOnlyList<SpaceRoute> _rules;

    /// `settings`: the spaces' own unless told.
    public SpaceRoutes(Spaces spaces, IDictionary<string, string>? settings = null)
    {
        _spaces = spaces;
        _settings = settings ?? spaces.Settings;
        _rules = Decode(_settings.TryGetValue(RulesKey, out var text) ? text : null);
    }

    /// Settings › Spaces & Pins › Send links to a space.
    public bool On { get => Spaces.Flag(_settings, Key); set { if (value == On) return; Spaces.Flag(_settings, Key, value); Changed(); } }

    /// Which site goes to which space, in the order they were added.
    public IReadOnlyList<SpaceRoute> Rules
    {
        get => _rules;
        private set { _rules = value; _settings[RulesKey] = Encode(value); Changed(); }
    }

    // Sites.

    /// What was typed, as the site a rule keeps: "https://www.Notion.so/x", "notion.so" and "*.notion.so" are all
    /// "notion.so". Null for anything that isn't a host.
    public static string? Site(string typed)
    {
        var text = typed.Trim().ToLowerInvariant();
        if (text.StartsWith("*.", StringComparison.Ordinal)) text = text[2..];
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Host.Length > 0 ? Clean(url.Host) : null;
    }

    /// The site of a web address; null for anything but http and https.
    public static string? SiteOf(Uri url) => url.IsAbsoluteUri && url.Scheme is "http" or "https" && url.Host.Length > 0 ? Clean(url.Host.ToLowerInvariant()) : null;

    /// An IPv6 address as it is; a name has no empty label, and one with no dot is only ever localhost.
    private static string? Clean(string host)
    {
        host = host.Trim('[', ']');
        if (host.EndsWith('.')) host = host[..^1];
        if (host.StartsWith("www.", StringComparison.Ordinal) && host[4..].Contains('.')) host = host[4..];
        if (host.Length is 0 or > 253 || !host.All(c => char.IsLetter(c) || char.IsNumber(c) || c is '-' or '.' or ':')) return null;
        if (host.Contains(':')) return host;
        return host.Split('.').All(label => label.Length > 0) && (host.Contains('.') || host == "localhost") ? host : null;
    }

    // Matching.

    /// The rule for an address: the one whose site is its host or a domain of it, the longest such site when more
    /// than one is.
    public static SpaceRoute? Match(Uri url, IEnumerable<SpaceRoute> routes) =>
        SiteOf(url) is { } host ? routes.Where(r => host == r.Site || (!host.Contains(':') && host.EndsWith("." + r.Site, StringComparison.Ordinal))).MaxBy(r => r.Site.Length) : null;

    /// The space a rule sends an address to: only while the setting and spaces are both on, and only a space that is
    /// still there.
    public static Space? SpaceFor(Uri url, IEnumerable<SpaceRoute> routes, bool on, IReadOnlyDictionary<Guid, Space> spaces) =>
        on && Match(url, routes) is { } route ? spaces.GetValueOrDefault(route.Space) : null;

    public Space? SpaceFor(Uri url) => SpaceFor(url, _rules, _spaces.UsesSpaces && On, _spaces.Known);

    /// A rule whose space has gone: passed over, and shown as such in Settings.
    public bool Gone(SpaceRoute route) => !_spaces.Known.ContainsKey(route.Space);

    // Changing.

    /// One site to one space, in place of any rule the site had.
    public void Set(string site, Guid space)
    {
        var routes = _rules.ToList();
        var at = routes.FindIndex(r => r.Site == site);
        if (at >= 0) { if (routes[at].Space == space) return; routes[at] = routes[at] with { Space = space }; } else routes.Add(new SpaceRoute(site, space));
        Rules = routes;
    }

    public void Remove(string site) { if (_rules.Any(r => r.Site == site)) Rules = _rules.Where(r => r.Site != site).ToList(); }

    /// The spaces a rule can name, every profile's; with `profileName` (once there is more than one profile), the
    /// profile's name before each: "Work › Mail".
    public IReadOnlyList<(Guid Id, string Name)> Choices(Func<Guid, string>? profileName = null) =>
        _spaces.All.Select(s => (s.Id, profileName is null ? s.Name : $"{profileName(s.ProfileId)} › {s.Name}")).ToList();

    // The setting.

    public static string Encode(IEnumerable<SpaceRoute> routes) => System.Text.Encoding.UTF8.GetString(SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var route in routes) { w.WriteStartObject(); w.WriteString("site", route.Site); SwiftJson.WriteUuid(w, "space", route.Space); w.WriteEndObject(); }
        w.WriteEndArray();
    }));

    /// Each rule that reads, as the Mac reads them: an entry of words alone, whose site is a site and whose space is a
    /// UUID. Any other is passed over; a setting that isn't a list is none.
    public static List<SpaceRoute> Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var routes = new List<SpaceRoute>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || e.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String)) continue;
                if (e.TryGetProperty("site", out var s) && Site(s.GetString()!) is { } site && e.TryGetProperty("space", out var i) && SwiftJson.Uuid(i) is { } space) routes.Add(new(site, space));
            }
            return routes;
        }
        catch (JsonException) { return []; }
    }
}
