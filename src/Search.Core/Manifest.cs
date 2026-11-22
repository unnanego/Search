using System.Text.Json;
using System.Text.Json.Nodes;

namespace Search.Core;

// An extension's manifest.json, read for what Search shows and asks about: its name, its button, its pages, the sites
// and permissions it wants, and those in words for the question asked before it is added (describe in
// Extensions.swift). WebView2 reads the manifest for itself; this is only Search's side of it.
//
// Read from the extension's own manifest, not the one Search rewrote for the copy WebView2 loads (ExtensionPrep):
// that one has had its sites narrowed and Search's worker put in, and an update has to be compared against what the
// extension itself asks for.
public sealed class Manifest
{
    internal static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _locale;
    private readonly Dictionary<string, Dictionary<string, string>?> _messages = new(StringComparer.OrdinalIgnoreCase);

    public JsonObject Json { get; }
    /// The folder it came from: where its locales and Search's notes on it are. Null for one read from text alone.
    public string? Folder { get; }

    /// `locale` is the one to name it in first ("en", "pt_BR"), then the manifest's default_locale.
    public Manifest(JsonObject json, string? folder = null, string locale = "en") { Json = json; Folder = folder; _locale = locale.Replace('-', '_'); }

    /// The extension in `folder`, or null when it has no manifest that reads.
    public static Manifest? Read(string folder, string locale = "en")
    {
        var json = ExtensionPrep.ReadOriginal(folder) ?? ReadObject(Path.Combine(folder, "manifest.json"));
        return json is null ? null : new Manifest(json, folder, locale);
    }

    /// Chrome's manifests may carry comments and trailing commas; null for a file that is missing or no object.
    public static JsonObject? ReadObject(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) as JsonObject : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    // What it is.

    public int? ManifestVersion => Json["manifest_version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    public string? Name => Localized(Text(Json["name"]));
    public string? Version => Text(Json["version"]);
    public string? Description => Localized(Text(Json["description"]));

    /// Its button: "action" in Manifest V3, "browser_action" or "page_action" in V2.
    private JsonObject? Action => Json["action"] as JsonObject ?? Json["browser_action"] as JsonObject ?? Json["page_action"] as JsonObject;

    public string? ActionTitle => Localized(Text(Action?["default_title"]));
    public string? Popup => Filled(Text(Action?["default_popup"]));
    public string? SidePanel => Filled(Text((Json["side_panel"] as JsonObject)?["default_path"]));
    public string? Options => Filled(Text((Json["options_ui"] as JsonObject)?["page"])) ?? Filled(Text(Json["options_page"]));

    /// The best icon for `size` pixels, relative to the folder: its button's own if it has any, its icons otherwise.
    /// The smallest that is at least that big, else the biggest there is.
    public string? IconPath(int size)
    {
        var icons = Icons(Action?["default_icon"]);
        if (icons.Count == 0) icons = Icons(Json["icons"]);
        if (icons.Count == 0) return null;
        var best = icons.Where(i => i.Size >= size).OrderBy(i => i.Size).Select(i => i.Path).FirstOrDefault() ?? icons.MaxBy(i => i.Size).Path;
        return best.TrimStart('/');
    }

    /// One path for every size ("default_icon": "icon.png") or a map of sizes to paths.
    private static List<(int Size, string Path)> Icons(JsonNode? node)
    {
        if (Filled(Text(node)) is { } one) return [(int.MaxValue, one)];
        if (node is not JsonObject map) return [];
        return map.Select(p => (Ok: int.TryParse(p.Key, out var size), Size: size, Path: Filled(Text(p.Value)))).Where(p => p.Ok && p.Path is not null).Select(p => (p.Size, p.Path!)).ToList();
    }

    // What it asks for.

    /// Its permissions by name: Manifest V2 lists sites among them, which are in HostPatterns instead.
    public IReadOnlyList<string> Permissions => Strings(Json["permissions"]).Where(p => !MatchPattern.IsPattern(p)).ToList();
    public IReadOnlyList<string> OptionalPermissions => Strings(Json["optional_permissions"]).Where(p => !MatchPattern.IsPattern(p)).ToList();

    /// Every site it asks for: host_permissions, the sites among Manifest V2's permissions, and where its content
    /// scripts run. Each once, in the order it names them.
    public IReadOnlyList<string> HostPatterns =>
        Strings(Json["host_permissions"]).Concat(Strings(Json["permissions"]).Where(MatchPattern.IsPattern))
            .Concat((Json["content_scripts"] as JsonArray ?? []).SelectMany(s => Strings((s as JsonObject)?["matches"]))).Distinct(StringComparer.Ordinal).ToList();

    /// Its Manifest V3 service worker, and whether it is an ES module.
    public (string Path, bool Module)? Worker =>
        Json["background"] is JsonObject b && Filled(Text(b["service_worker"])) is { } path ? (path, Text(b["type"]) == "module") : null;

    /// Its Manifest V2 background scripts.
    public IReadOnlyList<string> BackgroundScripts => Strings((Json["background"] as JsonObject)?["scripts"]);

    /// The permissions Search itself wrote into the manifest, which it never asked for.
    public IReadOnlySet<string> Added => Folder is null ? new HashSet<string>() : Strings(ReadArray(Path.Combine(Folder, ExtensionPrep.AddedFile))).ToHashSet(StringComparer.Ordinal);

    private static JsonNode? ReadArray(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// What it was allowed, as it is written down (Installed.Permissions) and compared on every update: its
    /// permissions and the sites it reaches ("site:" and the pattern). An update that adds any is asked about again.
    /// The Mac also writes "search:" before the APIs Search answers for WebKit; WebView2 answers them all itself, so
    /// here they are plain permissions like the rest.
    public IReadOnlyList<string> Grants()
    {
        var added = Added;
        return Permissions.Where(p => !added.Contains(p)).Concat(HostPatterns.Select(p => "site:" + p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// What it asks for now that `allowed` (its grants when it was added or last asked about) doesn't have.
    public IReadOnlyList<string> More(IEnumerable<string> allowed) { var had = allowed.ToHashSet(StringComparer.Ordinal); return Grants().Where(g => !had.Contains(g)).ToList(); }

    /// Chrome's own APIs, which Search answers itself on the Mac, and what each lets an extension do.
    public static readonly IReadOnlyList<(string Name, string Words)> SearchAnswered =
    [
        ("userScripts", "Run scripts you add to it on websites"), ("history", "Read and change your history"),
        ("bookmarks", "Read and change your bookmarks"), ("downloads", "Manage your downloads"),
        ("privacy", "Change your privacy settings"), ("browsingData", "Clear your browsing data"),
        ("management", "See your other extensions"), ("notifications", "Show notifications"),
        ("sessions", "See your recently closed tabs"), ("topSites", "See your most visited sites"),
        ("readingList", "Read and change your reading list"), ("downloads.open", "Open files it downloads"),
        ("desktopCapture", "Record your screen or a window, when you choose what to share"),
        ("tabCapture", "Record a tab, when you ask it to"),
    ];

    /// The permissions worth a line of their own, in the order they are said.
    public static readonly IReadOnlyList<(string Name, string Words)> PermissionWords =
    [
        ("tabs", "See your open tabs and their addresses"), ("cookies", "Read and change cookies"),
        ("webNavigation", "See where you go"), ("webRequest", "See the requests pages make"),
        ("declarativeNetRequest", "Block or change requests pages make"), ("clipboardWrite", "Write to the clipboard"),
        ("nativeMessaging", "Talk to apps on this PC"), ("scripting", "Run scripts in pages"),
    ];

    /// What it wants, in words, for the question asked before it is added: its sites, then the permissions worth
    /// saying, then Chrome's own APIs. Leaving out what Search itself added to the manifest.
    public IReadOnlyList<string> Describe()
    {
        var output = new List<string>();
        var added = Added;
        var patterns = HostPatterns.Select(MatchPattern.Parse).OfType<MatchPattern>().ToList();
        if (patterns.Any(p => p.AllHosts)) output.Add("Read and change everything on every website");
        else if (patterns.Count > 0)
        {
            var hosts = patterns.Select(p => p.Host).Where(h => h.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (hosts.Count > 0) output.Add("Read and change what's on " + string.Join(", ", hosts.Take(4)) + (hosts.Count > 4 ? $" and {hosts.Count - 4} more" : ""));
        }
        var declared = Permissions.Where(p => !added.Contains(p)).ToHashSet(StringComparer.Ordinal);
        output.AddRange(PermissionWords.Concat(SearchAnswered).Where(w => declared.Contains(w.Name)).Select(w => w.Words));
        return output;
    }

    // Its words in its own languages.

    /// "__MSG_name__" put in from _locales: the locale asked for, its language alone, then the manifest's default. A
    /// message it doesn't have is nothing, as Chrome has it.
    public string? Localized(string? text)
    {
        if (text is null || !text.Contains("__MSG_", StringComparison.Ordinal)) return text;
        var output = new System.Text.StringBuilder();
        var at = 0;
        while (text.IndexOf("__MSG_", at, StringComparison.Ordinal) is var start and >= 0 && text.IndexOf("__", start + 6, StringComparison.Ordinal) is var end and >= 0)
        {
            output.Append(text, at, start - at).Append(Message(text[(start + 6)..end]) ?? "");
            at = end + 2;
        }
        return output.Append(text, at, text.Length - at).ToString();
    }

    private string? Message(string key)
    {
        var language = _locale.Split('_')[0];
        foreach (var locale in new[] { _locale, language, Text(Json["default_locale"]) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            if (Messages(locale) is { } messages && messages.TryGetValue(key, out var found)) return found;
        return null;
    }

    private Dictionary<string, string>? Messages(string locale)
    {
        if (Folder is null || locale.Contains('/') || locale.Contains('\\') || locale.Contains("..")) return null;
        if (_messages.TryGetValue(locale, out var known)) return known;
        Dictionary<string, string>? read = null;
        if (ReadObject(Path.Combine(Folder, "_locales", locale, "messages.json")) is { } json)
        {
            read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in json) if (Text((value as JsonObject)?["message"]) is { } message) read[key] = message;
        }
        return _messages[locale] = read;
    }

    // Pieces.

    internal static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string? Filled(string? text) => string.IsNullOrEmpty(text) ? null : text;
    internal static IReadOnlyList<string> Strings(JsonNode? node) => node is JsonArray list ? list.Select(Text).OfType<string>().ToList() : [];
}
