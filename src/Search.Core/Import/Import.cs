using System.Text.Json;

namespace Search.Core;

// Reading what another browser on this PC already holds (Import.swift): which browsers there are, their profiles,
// their bookmarks, history, passwords, open tabs and Chrome Web Store extensions, and how much of each there is,
// counted from the files alone. Nothing in the other browser changes, and nothing is taken into Search here: the
// import panel shows the counts, and what it brings goes in through ImportTake.
//
// Two families. Every Chromium browser keeps a "User Data" folder (Opera keeps its one profile in its own folder),
// with "Local State" naming its profiles and one folder per profile: Bookmarks as JSON, History and Login Data as
// SQLite, Preferences saying where each extension came from, Sessions/ for what is open. Every Gecko browser keeps a
// profiles.ini and a folder per profile: places.sqlite for bookmarks and history together, logins.json and key4.db
// for passwords, the session store as .jsonlz4. The formats are the Mac's; only where they live is Windows'.
//
// Every read goes through ImportRead: regular files only, no link followed, inside the browser's folder, capped.
// Databases are read from a copy (Snapshot). Only http and https addresses come, with any password in them dropped.

/// One profile of a browser: its folder's name, which the readers go by, and what the browser calls it.
public sealed record ImportProfile(string Id, string Name);

/// Somewhere the other browser has been: as History.Take wants it.
public sealed record ImportedPlace(Uri Url, string Title, int Count, DateTimeOffset Last);

/// The bookmarks, and whether every profile asked for read cleanly: false when there was no profile, or a file there
/// didn't read. A profile with no bookmarks file has none and reads cleanly. Replace takes nothing out unless true.
public sealed record BookmarkRead(IReadOnlyList<Bookmark> Nodes, bool Complete);

/// What there is to bring, counted before anything is: no key asked for, nothing decrypted. `AppBound`: passwords a
/// Chromium browser locked to itself (Chrome 127 and later), which can't be read here and are said so before asking.
public sealed record ImportPreview(int Bookmarks, int Places, int Passwords, int AppBound, IReadOnlyList<string> Extensions, SessionSnapshot Session)
{
    public static ImportPreview None => new(0, 0, 0, 0, [], new SessionSnapshot());
}

/// A browser whose folder is on this PC but couldn't be read, said by name rather than left out.
public sealed record ImportUnreadable(ImportSource Source, string Message);

/// Where browsers keep their data: %LOCALAPPDATA% and %APPDATA%, and Arc's packaged folder (the one holding
/// StorableSidebar.json and User Data) when it is somewhere else. A test run reads made-up profiles from its own
/// folder instead, never a real browser's.
public sealed record ImportRoots(string Local, string Roaming, string? Arc = null)
{
    public static ImportRoots Default => Store.Testing
        ? new(Path.Combine(Store.File("Import"), "Local"), Path.Combine(Store.File("Import"), "Roaming"))
        : new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// Arc for Windows is an MSIX package: %LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_<publisher>\LocalCache\Local\Arc.
    public string ArcFolder
    {
        get
        {
            if (Arc is { } chosen) return chosen;
            var packages = Path.Combine(Local, "Packages");
            string[] found = [];
            try { if (Directory.Exists(packages)) found = Directory.GetDirectories(packages, "TheBrowserCompany.Arc*"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var package = found.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? Path.Combine(packages, "TheBrowserCompany.Arc_ttt1ap7aakyb4");
            return Path.Combine(package, "LocalCache", "Local", "Arc");
        }
    }
}

/// One browser to bring things over from, whichever family, so the panel works in one vocabulary. Everything that
/// reads takes a profile: its folder's name, or null for every profile at once ("All profiles").
public abstract class ImportSource
{
    /// Places brought at most: what it takes to finish an address on the first day.
    public const int PlaceLimit = 3000;

    protected ImportSource(string name) => Name = name;

    public string Name { get; }
    public abstract string Id { get; }
    /// The folder its profiles are in, which every read stays inside.
    public abstract string Root { get; }
    public abstract bool HasProfiles { get; }
    /// Every profile, by the name the browser gives it. Guest and System profiles aren't anybody's.
    public abstract IReadOnlyList<ImportProfile> Profiles { get; }
    /// The profile used most recently, brought in unless another is chosen.
    public abstract string? Usual { get; }
    public abstract bool ImportsPasswords { get; }

    public abstract BookmarkRead Bookmarks(string? profile = null);
    public abstract IReadOnlyList<ImportedPlace> Places(string? profile = null, int limit = PlaceLimit);
    /// The other browser's icons for these pages, by site (ImportUrl.Site): the largest it kept for the page, or
    /// failing that for the site's front door.
    public abstract IReadOnlyDictionary<string, byte[]> Icons(IEnumerable<Uri> urls, string? profile = null, int limit = 400);
    /// The passwords: the one moment a key is used. Read off the UI thread.
    public abstract FoundLogins Passwords(string? profile = null);
    /// The open tabs and tab groups, at most SessionImport.Limit of them.
    public abstract SessionSnapshot Sessions(string? profile = null);
    public abstract ImportPreview Preview(string? profile = null, int limit = PlaceLimit);

    /// Chrome Web Store ids, to install fresh from the store (ExtensionHost.InstallFromStore): a Chromium browser's.
    public virtual IReadOnlyList<string> Extensions(string? profile = null) => [];
    /// Its spaces and pins, Arc's sidebar or Zen's spaces read into its shape; null for any other browser.
    public virtual ArcSidebar? Sidebar(string? profile = null) => null;
    public (int Spaces, int Pinned)? SidebarCounts(string? profile = null) => Sidebar(profile) is { } s ? (s.Spaces.Count, s.PinnedCount + s.Favorites.Count) : null;
    /// Its open tabs are its pinned tabs again (Arc's session holds what its spaces do): then they don't come both ways.
    public virtual bool SessionRepeatsSpaces => false;
    /// Zen keeps spaces, pins and Essentials of its own; its open tabs then leave its pins behind.
    public virtual bool KeepsSpaces => false;

    public override string ToString() => Name;

    /// Every browser Search knows how to read, wherever `roots` says they keep their data.
    public static IReadOnlyList<ImportSource> Known(ImportRoots? roots = null)
    {
        var at = roots ?? ImportRoots.Default;
        string Local(params string[] parts) => Path.Combine([at.Local, .. parts]);
        string Roaming(params string[] parts) => Path.Combine([at.Roaming, .. parts]);
        const string ChromeSteps = "In Chrome, open chrome://password-manager/settings, choose Export passwords, and bring in the CSV file it saves.";
        return
        [
            new ChromiumSource("Chrome", Local("Google", "Chrome", "User Data"), exportSteps: ChromeSteps),
            new ChromiumSource("Edge", Local("Microsoft", "Edge", "User Data"), exportSteps: "In Edge, open Settings › Passwords, choose Export passwords, and bring in the CSV file it saves."),
            new ChromiumSource("Brave", Local("BraveSoftware", "Brave-Browser", "User Data"), exportSteps: "In Brave, open brave://password-manager/settings, choose Export passwords, and bring in the CSV file it saves."),
            new ChromiumSource("Arc", Path.Combine(at.ArcFolder, "User Data"), sidebar: Path.Combine(at.ArcFolder, "StorableSidebar.json")),
            new ChromiumSource("Vivaldi", Local("Vivaldi", "User Data")),
            new ChromiumSource("Opera", Roaming("Opera Software", "Opera Stable")),
            new ChromiumSource("Opera GX", Roaming("Opera Software", "Opera GX Stable")),
            new ChromiumSource("Chromium", Local("Chromium", "User Data"), exportSteps: ChromeSteps.Replace("Chrome,", "Chromium,", StringComparison.Ordinal)),
            // Chrome's and Edge's other channels keep their own data.
            new ChromiumSource("Chrome Beta", Local("Google", "Chrome Beta", "User Data"), exportSteps: ChromeSteps),
            new ChromiumSource("Chrome Dev", Local("Google", "Chrome Dev", "User Data"), exportSteps: ChromeSteps),
            new ChromiumSource("Chrome Canary", Local("Google", "Chrome SxS", "User Data"), exportSteps: ChromeSteps),
            new ChromiumSource("Edge Beta", Local("Microsoft", "Edge Beta", "User Data")),
            new ChromiumSource("Edge Dev", Local("Microsoft", "Edge Dev", "User Data")),
            new ChromiumSource("Edge Canary", Local("Microsoft", "Edge SxS", "User Data")),
            new ChromiumSource("Thorium", Local("Thorium", "User Data")),
            new ChromiumSource("Naver Whale", Local("Naver", "Naver Whale", "User Data")),
            new ChromiumSource("Comet", Local("Perplexity", "Comet", "User Data")),
            // Yandex keeps its passwords in a store of its own, behind a second key: its bookmarks, history and tabs still come.
            new ChromiumSource("Yandex", Local("Yandex", "YandexBrowser", "User Data"), importsPasswords: false),
            new GeckoSource("Firefox", Roaming("Mozilla", "Firefox")),
            new GeckoSource("Zen", Roaming("zen")),
            new GeckoSource("Floorp", Roaming("Floorp")),
            new GeckoSource("LibreWolf", Roaming("librewolf")),
            new GeckoSource("Waterfox", Roaming("Waterfox")),
        ];
    }

    /// Only the browsers on this PC with a profile to read.
    public static IReadOnlyList<ImportSource> Installed(ImportRoots? roots = null) => Known(roots).Where(s => s.HasProfiles).ToList();

    /// Browsers whose folder is here but gave nothing to read, or not all of it: said, not left out.
    public static IReadOnlyList<ImportUnreadable> Unreadable(ImportRoots? roots = null)
    {
        var at = roots ?? ImportRoots.Default;
        string Looked(string path) => path.Replace(at.Local, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase).Replace(at.Roaming, "%APPDATA%", StringComparison.OrdinalIgnoreCase);
        var said = new List<ImportUnreadable>();
        foreach (var source in Known(at).OfType<ChromiumSource>())
        {
            if (!Directory.Exists(source.Root)) continue;
            var (found, error) = source.Discovery();
            if (found.Count > 0 && error is null) continue;
            var where = Looked(source.Root);
            said.Add(new(source, error is null ? $"{source.Name}'s data folder is on this PC, but no browser profile was found in {where}."
                : found.Count > 0 ? $"Some of {source.Name}'s profiles in {where} couldn't be read: {error.Message}"
                : $"Search couldn't read {source.Name}'s data in {where}: {error.Message}"));
        }
        return said;
    }

    /// The order a person reads folder names in: "Profile 2" before "Profile 10".
    internal static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                var c = string.CompareOrdinal(x, y);
                if (c != 0) return c;
                continue;
            }
            var d = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (d != 0) return d;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    // What the readers share.

    internal static JsonDocument? Json(byte[]? data)
    {
        if (data is null) return null;
        try { return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 512, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
    }

    internal static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    internal static string? Text(JsonElement e, string key) => SwiftJson.Text(Get(e, key));
    internal static bool Flag(JsonElement e, string key) => Get(e, key).ValueKind == JsonValueKind.True;
    internal static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    internal static IEnumerable<JsonElement> Objects(JsonElement e) => Items(e).Where(x => x.ValueKind == JsonValueKind.Object);

    /// One number from a copy of a SQLite file: zero for a file that isn't there or won't answer.
    internal static long[] Counts(string file, string inside, params string[] sql)
    {
        var counts = new long[sql.Length];
        if (!File.Exists(file)) return counts;
        using var copy = Snapshot.Of(file, inside);
        using var db = copy?.Open();
        if (db is null) return counts;
        for (var i = 0; i < sql.Length; i++) counts[i] = db.Scalar(sql[i]) ?? 0;
        return counts;
    }

    /// The pages wanted for icons, one per site, at most `limit`.
    internal static List<(string Site, Uri Url)> Wanted(IEnumerable<Uri> urls, int limit)
    {
        var wanted = new List<(string, Uri)>();
        var seen = new HashSet<string>();
        foreach (var url in urls)
        {
            if (!url.IsAbsoluteUri || ImportUrl.Site(url) is not { } site || !seen.Add(site)) continue;
            wanted.Add((site, url));
            if (wanted.Count >= limit) break;
        }
        return wanted;
    }

    /// `sql` asked of each wanted page and then its site's front door; the first answer with something in it.
    internal static void Icons(Sqlite db, string sql, List<(string Site, Uri Url)> wanted, Dictionary<string, byte[]> found)
    {
        foreach (var (site, url) in wanted)
        {
            if (found.ContainsKey(site)) continue;
            foreach (var door in new[] { url.AbsoluteUri, $"{url.Scheme}://{url.Authority}/" })
            {
                var bytes = db.Rows(sql, door).Select(r => r.Blob(0)).FirstOrDefault();
                if (bytes is not { Length: > 60 }) continue;
                found[site] = bytes;
                break;
            }
        }
    }

    private static readonly DateTimeOffset Epoch1601 = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// Microseconds since 1601, Chromium's idea of a date; null for none or nonsense.
    internal static DateTimeOffset? FromChromium(long micros) => micros > 0 && micros < (DateTimeOffset.MaxValue - Epoch1601).Ticks / 10 ? Epoch1601.AddTicks(micros * 10) : null;
    /// Microseconds since 1970, Firefox's: no 1601 offset.
    internal static DateTimeOffset? FromGecko(long micros) => micros > 0 && micros < (DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).Ticks / 10 ? DateTimeOffset.UnixEpoch.AddTicks(micros * 10) : null;
}

/// A Chromium browser: Chrome, Edge, Brave, Arc, Vivaldi, Opera and the rest, all keeping the same files.
public sealed partial class ChromiumSource : ImportSource
{
    private static readonly string[] Markers = ["Login Data", "Bookmarks", "History", "Extensions"];

    /// `exportSteps`: how to export its passwords to a CSV, for when they are locked to it. `sidebar`: Arc's
    /// StorableSidebar.json, beside its User Data.
    public ChromiumSource(string name, string root, bool importsPasswords = true, string? exportSteps = null, string? sidebar = null) : base(name)
    {
        Root = root;
        ImportsPasswords = importsPasswords;
        ExportSteps = exportSteps ?? $"In {name}, open its password settings, choose Export passwords, and bring in the CSV file it saves.";
        SidebarFile = sidebar;
    }

    public override string Id => "chromium-" + Name;
    public override string Root { get; }
    public override bool ImportsPasswords { get; }
    public string ExportSteps { get; }
    public string? SidebarFile { get; }
    public bool IsArc => SidebarFile is not null;
    public override bool SessionRepeatsSpaces => IsArc;

    /// The same browser read from another folder: one a person chose because its data isn't where it should be.
    public ChromiumSource WithRoot(string root) => new(Name, root, ImportsPasswords, ExportSteps, SidebarFile);

    public override bool HasProfiles => Discovery().Folders.Count > 0;

    /// Every profile folder (one holding passwords, bookmarks, history or extensions), and the first error met on the
    /// way, kept apart from an empty, readable folder. Most browsers keep one folder per profile; Opera keeps its only
    /// profile in the browser's folder itself.
    public (IReadOnlyList<string> Folders, Exception? Error) Discovery()
    {
        if (!Directory.Exists(Root)) return ([], null);
        string[] inside = [];
        Exception? error = null;
        try { inside = Directory.GetDirectories(Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e; }
        var found = new List<string>();
        foreach (var folder in inside.Order(StringComparer.OrdinalIgnoreCase).Prepend(Root))
        {
            if (folder != Root)
            {
                if (Path.GetFileName(folder).StartsWith('.') || !ImportRead.Inside(folder, Root)) continue;
                try { if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.Hidden)) continue; _ = Directory.EnumerateFileSystemEntries(folder).Any(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error ??= e; continue; }
            }
            if (Markers.Any(m => Path.Exists(Path.Combine(folder, m)))) found.Add(folder);
        }
        return (found, error);
    }

    /// The profile folders to read: the one named, or every one for null.
    public IReadOnlyList<string> Folders(string? only) => Discovery().Folders.Where(f => only is null || Path.GetFileName(f) == only).ToList();

    private static readonly string[] NotAnybodys = ["Guest Profile", "System Profile"];

    public override IReadOnlyList<ImportProfile> Profiles
    {
        get
        {
            var names = LocalState().Names;
            var rootName = Path.GetFileName(Root.TrimEnd('\\'));
            return Discovery().Folders.Select(f => Path.GetFileName(f)).Where(n => !NotAnybodys.Contains(n)).Order(Comparer<string>.Create(Natural))
                .Select(id => new ImportProfile(id, names.GetValueOrDefault(id) ?? (id == rootName ? Name : id))).ToList();
        }
    }

    /// What the browser said it used last, or else the profile whose files changed last.
    public override string? Usual
    {
        get
        {
            var ids = Profiles.Select(p => p.Id).ToList();
            if (LocalState().Last.FirstOrDefault(ids.Contains) is { } last) return last;
            DateTime Touched(string folder) => new[] { "History", "Bookmarks", "Login Data" }.Select(f => Path.Combine(folder, f)).Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            return Discovery().Folders.Where(f => ids.Contains(Path.GetFileName(f))).OrderByDescending(Touched).Select(f => Path.GetFileName(f)).FirstOrDefault();
        }
    }

    /// What "Local State" says of the profiles: the name each folder goes by ("Work", "Person 1"), and the folder used
    /// last: "last_used", then "last_active_profiles".
    public (IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> Last) LocalState()
    {
        using var doc = Json(ImportRead.TryData(Path.Combine(Root, "Local State"), Root));
        var profile = doc is null ? default : Get(doc.RootElement, "profile");
        var names = new Dictionary<string, string>();
        if (Get(profile, "info_cache") is { ValueKind: JsonValueKind.Object } cache)
            foreach (var entry in cache.EnumerateObject())
                if (Text(entry.Value, "name")?.Trim() is { Length: > 0 } name) names[entry.Name] = name.Length > 60 ? name[..60] : name;
        var last = new List<string>();
        if (Text(profile, "last_used") is { } used) last.Add(used);
        last.AddRange(Items(Get(profile, "last_active_profiles")).Select(SwiftJson.Text).OfType<string>());
        return (names, last);
    }

    // Bookmarks: one JSON file, the bar first, then Other and Mobile as folders.

    public override BookmarkRead Bookmarks(string? profile = null)
    {
        var found = new List<Bookmark>();
        var folders = Folders(profile);
        var complete = folders.Count > 0;
        foreach (var folder in folders)
        {
            var file = Path.Combine(folder, "Bookmarks");
            if (!File.Exists(file)) continue;
            using var doc = Json(ImportRead.TryData(file, Root));
            if (doc is null || Get(doc.RootElement, "roots") is not { ValueKind: JsonValueKind.Object } roots) { complete = false; continue; }
            found.AddRange(Nodes(Get(Get(roots, "bookmark_bar"), "children"), 0));
            foreach (var (key, title) in new[] { ("other", "Other"), ("synced", "Mobile") })
                if (Nodes(Get(Get(roots, key), "children"), 0) is { Count: > 0 } kids) found.Add(Bookmark.Folder(title, kids));
        }
        return new(found, complete);
    }

    private static List<Bookmark> Nodes(JsonElement children, int depth)
    {
        var nodes = new List<Bookmark>();
        if (depth >= 64) return nodes;
        foreach (var entry in Objects(children))
        {
            var name = Text(entry, "name") ?? "";
            switch (Text(entry, "type"))
            {
                case "folder": nodes.Add(Bookmark.Folder(name, Nodes(Get(entry, "children"), depth + 1))); break;
                case "url" when ImportUrl.Page(Text(entry, "url")) is { } url: nodes.Add(Bookmark.Site(name, url)); break;
            }
        }
        return nodes;
    }

    // History: microseconds since 1601.

    public override IReadOnlyList<ImportedPlace> Places(string? profile = null, int limit = PlaceLimit)
    {
        var places = new List<ImportedPlace>();
        foreach (var folder in Folders(profile))
        {
            var file = Path.Combine(folder, "History");
            if (!File.Exists(file)) continue;
            using var copy = Snapshot.Of(file, Root);
            using var db = copy?.Open();
            if (db is null) continue;
            foreach (var row in db.Rows($"SELECT url, title, visit_count, last_visit_time FROM urls WHERE hidden = 0 AND visit_count > 0 ORDER BY last_visit_time DESC LIMIT {limit}"))
                if (ImportUrl.Page(row.Text(0)) is { } url) places.Add(new(url, row.Text(1) ?? "", (int)Math.Clamp(row.Long(2), 1, History.MostVisits), FromChromium(row.Long(3)) ?? DateTimeOffset.UtcNow));
        }
        return places.OrderByDescending(p => p.Last).Take(limit).ToList();
    }

    public override IReadOnlyDictionary<string, byte[]> Icons(IEnumerable<Uri> urls, string? profile = null, int limit = 400)
    {
        var found = new Dictionary<string, byte[]>();
        var wanted = Wanted(urls, limit);
        if (wanted.Count == 0) return found;
        foreach (var folder in Folders(profile))
        {
            var file = Path.Combine(folder, "Favicons");
            if (!File.Exists(file)) continue;
            using var copy = Snapshot.Of(file, Root);
            using var db = copy?.Open();
            if (db is null) continue;
            Icons(db, "SELECT b.image_data FROM icon_mapping m JOIN favicon_bitmaps b ON b.icon_id = m.icon_id WHERE m.page_url = ? AND b.width BETWEEN 16 AND 256 ORDER BY b.width DESC LIMIT 1", wanted, found);
        }
        return found;
    }

    // Counting.

    /// From the files alone: bookmarks from the file, places up to the limit, and passwords as rows with something in
    /// them that aren't a site marked never. No key is used here.
    public override ImportPreview Preview(string? profile = null, int limit = PlaceLimit)
    {
        long places = 0, passwords = 0, bound = 0;
        foreach (var folder in Folders(profile))
            places += Counts(Path.Combine(folder, "History"), Root, "SELECT COUNT(*) FROM urls WHERE hidden = 0 AND visit_count > 0 AND (url LIKE 'http:%' OR url LIKE 'https:%')")[0];
        foreach (var file in PasswordFiles(profile))
        {
            var counts = Counts(file, Root, "SELECT COUNT(*) FROM logins WHERE blacklisted_by_user = 0 AND length(password_value) > 0",
                "SELECT COUNT(*) FROM logins WHERE blacklisted_by_user = 0 AND hex(substr(password_value, 1, 3)) = '763230'");
            passwords += counts[0];
            bound += counts[1];
        }
        return new(Search.Core.Bookmarks.CountOf(Bookmarks(profile).Nodes), (int)Math.Min(limit, places), (int)passwords, (int)bound, Extensions(profile), Sessions(profile));
    }

    // Extensions.

    /// The extensions a profile has from the Chrome Web Store, by id, to be installed fresh from the store, never
    /// copied from here. Its Preferences and Secure Preferences say where each came from: only ones the person added
    /// from the store count, not ones built in, unpacked, put there by a policy or by the browser itself, nor themes,
    /// nor ones from another store (Edge's). A profile without either file has only its Extensions folder to go by.
    public override IReadOnlyList<string> Extensions(string? profile = null)
    {
        var ids = new List<string>();
        foreach (var folder in Folders(profile))
        {
            var docs = new List<JsonDocument>();
            try
            {
                var settings = new Dictionary<string, List<JsonElement>>();
                foreach (var name in new[] { "Preferences", "Secure Preferences" })
                {
                    if (Json(ImportRead.TryData(Path.Combine(folder, name), Root)) is not { } doc) continue;
                    docs.Add(doc);
                    if (Get(Get(doc.RootElement, "extensions"), "settings") is not { ValueKind: JsonValueKind.Object } all) continue;
                    foreach (var entry in all.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
                        (settings.TryGetValue(entry.Name, out var list) ? list : settings[entry.Name] = []).Add(entry.Value);
                }
                IEnumerable<string> found;
                if (settings.Count == 0)
                {
                    var dir = Path.Combine(folder, "Extensions");
                    if (!ImportRead.IsPlainFolder(dir) || !ImportRead.Inside(dir, Root)) continue;
                    try { found = Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)).ToList(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                }
                else found = settings.Where(e => FromStore(e.Value)).Select(e => e.Key);
                foreach (var id in found.Order(StringComparer.Ordinal)) if (IsStoreId(id) && !ids.Contains(id)) ids.Add(id);
            }
            finally { foreach (var doc in docs) doc.Dispose(); }
        }
        return ids;
    }

    /// The first file's word for a key wins, as the Swift merges them.
    private static bool FromStore(List<JsonElement> entries)
    {
        JsonElement Field(string key) => entries.Select(e => Get(e, key)).FirstOrDefault(v => v.ValueKind != JsonValueKind.Undefined);
        // Location 1 is Chromium's "internal": added by the person.
        if (!(SwiftJson.Int(Field("location")) == 1 || Field("from_webstore").ValueKind == JsonValueKind.True)) return false;
        if (Field("was_installed_by_default").ValueKind == JsonValueKind.True || Field("was_installed_by_oem").ValueKind == JsonValueKind.True) return false;
        var manifest = Field("manifest");
        if (Get(manifest, "theme").ValueKind != JsonValueKind.Undefined) return false;
        if (Text(manifest, "update_url") is { } update)
        {
            var host = Uri.TryCreate(update, UriKind.Absolute, out var url) ? url.Host.ToLowerInvariant() : "";
            if (host != "google.com" && !host.EndsWith(".google.com", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// Thirty-two letters from a to p: the shape of a store extension's id.
    internal static bool IsStoreId(string text) => text.Length == 32 && text.All(c => c is >= 'a' and <= 'p');

    /// Arc's sidebar, for Arc.
    public override ArcSidebar? Sidebar(string? profile = null) => IsArc ? Arc.Sidebar(this, profile) : null;
}

/// A Gecko browser: Firefox, Zen, Floorp, LibreWolf, Waterfox. History and bookmarks live together in places.sqlite.
public sealed partial class GeckoSource : ImportSource
{
    public GeckoSource(string name, string root) : base(name) => Root = root;

    public override string Id => "mozilla-" + Name;
    public override string Root { get; }
    public override bool ImportsPasswords => true;
    public override bool KeepsSpaces => Name == "Zen";
    public override bool HasProfiles => Files().Count > 0;

    /// Every profile's places.sqlite, newest first so the profile in use leads: in Profiles\ or straight in the
    /// browser's folder, one file once however it was reached.
    public IReadOnlyList<string> Files()
    {
        var found = new List<string>();
        foreach (var folder in new[] { Path.Combine(Root, "Profiles"), Root })
        {
            if (!Directory.Exists(folder)) continue;
            var direct = Path.Combine(folder, "places.sqlite");
            if (File.Exists(direct)) found.Add(direct);
            string[] inside = [];
            try { inside = Directory.GetDirectories(folder); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            found.AddRange(inside.Where(d => !Path.GetFileName(d).StartsWith('.')).Select(d => Path.Combine(d, "places.sqlite")).Where(File.Exists));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return found.Where(f => ImportRead.Inside(f, Root) && seen.Add(ImportRead.FinalPath(f) ?? f)).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
    }

    /// The one profile's places.sqlite, by its folder's name, or every one for null.
    public IReadOnlyList<string> Files(string? only) => Files().Where(f => only is null || Path.GetFileName(Path.GetDirectoryName(f)) == only).ToList();

    /// The profile folders: the one chosen, or every one, newest first.
    public IReadOnlyList<string> Folders(string? only) => Files(only).Select(f => Path.GetDirectoryName(f)!).ToList();

    public override IReadOnlyList<ImportProfile> Profiles
    {
        get
        {
            var names = Ini().Names;
            return Files().Select(f => Path.GetFileName(Path.GetDirectoryName(f))!).Distinct().Select(id => new ImportProfile(id, names.GetValueOrDefault(id) ?? id)).ToList();
        }
    }

    /// The Install section's Default (the profile the browser itself opens), else the one marked Default=1, else the
    /// newest.
    public override string? Usual
    {
        get
        {
            var ids = Profiles.Select(p => p.Id).ToList();
            return Ini().Usual is { } usual && ids.Contains(usual) ? usual : ids.FirstOrDefault();
        }
    }

    /// What profiles.ini says: the name each profile folder goes by, and the one in use.
    public (IReadOnlyDictionary<string, string> Names, string? Usual) Ini()
    {
        var names = new Dictionary<string, string>();
        string? install = null, marked = null;
        foreach (var file in new[] { Path.Combine(Root, "profiles.ini"), Path.Combine(Root, "Profiles", "profiles.ini") })
        {
            if (!File.Exists(file) || ImportRead.TryData(file, Root, 1 << 20) is not { } data) continue;
            var section = "";
            var fields = new Dictionary<string, string>();
            void Close()
            {
                var path = fields.GetValueOrDefault("Path") is { } p ? Path.GetFileName(p.Replace('/', '\\').TrimEnd('\\')) : null;
                if (section.StartsWith("Install", StringComparison.Ordinal) && fields.GetValueOrDefault("Default") is { } at) install ??= Path.GetFileName(at.Replace('/', '\\').TrimEnd('\\'));
                else if (section.StartsWith("Profile", StringComparison.Ordinal) && path is not null)
                {
                    if (fields.GetValueOrDefault("Name") is { Length: > 0 } name) names[path] = name;
                    if (fields.GetValueOrDefault("Default") == "1") marked ??= path;
                }
                fields.Clear();
            }
            foreach (var raw in System.Text.Encoding.UTF8.GetString(data).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { Close(); section = line[1..^1]; }
                else if (line.IndexOf('=') is var equals and > 0) fields[line[..equals]] = line[(equals + 1)..];
            }
            Close();
        }
        return (names, install ?? marked);
    }

    // Bookmarks: rows in moz_bookmarks pointing at moz_places, the tree rebuilt from the flat list.

    public override BookmarkRead Bookmarks(string? profile = null)
    {
        var found = new List<Bookmark>();
        var files = Files(profile);
        var complete = files.Count > 0;
        foreach (var file in files)
            if (BookmarkNodes(file) is { } nodes) found.AddRange(nodes); else complete = false;
        return new(found, complete);
    }

    private sealed record Raw(long Id, int Type, long Parent, string Title, string? Url, string Guid);

    /// The toolbar's pages first at the top, then the menu's, then Other and Mobile as folders: the order Chromium's
    /// come in. The tags folder is Firefox's own bookkeeping. A damaged file can make a folder its own ancestor: each
    /// folder is taken once, and no deeper than 64.
    private List<Bookmark>? BookmarkNodes(string file)
    {
        using var copy = Snapshot.Of(file, Root);
        using var db = copy?.Open();
        if (db is null) return null;
        const string Sql = "SELECT b.id, b.type, b.parent, b.title, p.url, b.guid FROM moz_bookmarks b LEFT JOIN moz_places p ON b.fk = p.id WHERE b.type IN (1, 2) ORDER BY b.position ASC";
        if (!db.Prepares(Sql)) return null;
        var items = db.Rows(Sql).Select(r => new Raw(r.Long(0), (int)r.Long(1), r.Long(2), r.Text(3) ?? "", r.Text(4), r.Text(5) ?? "")).ToList();
        var byParent = items.GroupBy(i => i.Parent).ToDictionary(g => g.Key, g => g.ToList());
        var byGuid = new Dictionary<string, Raw>();
        foreach (var item in items.Where(i => i.Guid.Length > 0)) byGuid[item.Guid] = item;
        var taken = new HashSet<long>();
        List<Bookmark> Children(long parent, int depth)
        {
            var nodes = new List<Bookmark>();
            if (depth >= 64) return nodes;
            foreach (var item in byParent.GetValueOrDefault(parent) ?? [])
            {
                if (item.Type == 1) { if (ImportUrl.Page(item.Url) is { } url) nodes.Add(Bookmark.Site(item.Title, url)); continue; }
                if (item.Type != 2 || item.Guid == "tags________" || !taken.Add(item.Id)) continue;
                nodes.Add(Bookmark.Folder(item.Title.Length == 0 ? "Folder" : item.Title, Children(item.Id, depth + 1)));
            }
            return nodes;
        }
        var top = new List<Bookmark>();
        if (byGuid.GetValueOrDefault("toolbar_____") is { } toolbar) top.AddRange(Children(toolbar.Id, 0));
        if (byGuid.GetValueOrDefault("menu________") is { } menu) top.AddRange(Children(menu.Id, 0));
        foreach (var (guid, title) in new[] { ("unfiled_____", "Other"), ("mobile______", "Mobile") })
            if (byGuid.GetValueOrDefault(guid) is { } root && Children(root.Id, 0) is { Count: > 0 } kids) top.Add(Bookmark.Folder(title, kids));
        return top;
    }

    // History: microseconds since 1970.

    public override IReadOnlyList<ImportedPlace> Places(string? profile = null, int limit = PlaceLimit)
    {
        var places = new List<ImportedPlace>();
        foreach (var file in Files(profile))
        {
            using var copy = Snapshot.Of(file, Root);
            using var db = copy?.Open();
            if (db is null) continue;
            foreach (var row in db.Rows($"SELECT url, title, visit_count, last_visit_date FROM moz_places WHERE hidden = 0 AND visit_count > 0 AND last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT {limit}"))
                if (ImportUrl.Page(row.Text(0)) is { } url) places.Add(new(url, row.Text(1) ?? "", (int)Math.Clamp(row.Long(2), 1, History.MostVisits), FromGecko(row.Long(3)) ?? DateTimeOffset.UtcNow));
        }
        return places.OrderByDescending(p => p.Last).Take(limit).ToList();
    }

    public override IReadOnlyDictionary<string, byte[]> Icons(IEnumerable<Uri> urls, string? profile = null, int limit = 400)
    {
        var found = new Dictionary<string, byte[]>();
        var wanted = Wanted(urls, limit);
        if (wanted.Count == 0) return found;
        foreach (var folder in Folders(profile))
        {
            var file = Path.Combine(folder, "favicons.sqlite");
            if (!File.Exists(file)) continue;
            using var copy = Snapshot.Of(file, Root);
            using var db = copy?.Open();
            if (db is null) continue;
            Icons(db, "SELECT i.data FROM moz_pages_w_icons p JOIN moz_icons_to_pages m ON m.page_id = p.id JOIN moz_icons i ON i.id = m.icon_id WHERE p.page_url = ? AND i.data IS NOT NULL ORDER BY i.width DESC LIMIT 1", wanted, found);
        }
        return found;
    }

    /// As Chromium's: bookmarks from the file, places up to the limit, and the logins logins.json lists. key4.db is
    /// not opened, so a primary password is only found out when they are brought.
    public override ImportPreview Preview(string? profile = null, int limit = PlaceLimit)
    {
        long places = 0;
        var passwords = 0;
        foreach (var file in Files(profile))
        {
            places += Counts(file, Root, "SELECT COUNT(*) FROM moz_places WHERE hidden = 0 AND visit_count > 0 AND last_visit_date IS NOT NULL AND (url LIKE 'http:%' OR url LIKE 'https:%')")[0];
            var folder = Path.GetDirectoryName(file)!;
            if (!File.Exists(Path.Combine(folder, "key4.db"))) continue;
            using var doc = Json(ImportRead.TryData(Path.Combine(folder, "logins.json"), Root));
            if (doc is null) continue;
            passwords += Objects(Get(doc.RootElement, "logins")).Count(l => Text(l, "hostname") is not null && Text(l, "encryptedPassword") is { Length: > 0 });
        }
        return new(Search.Core.Bookmarks.CountOf(Bookmarks(profile).Nodes), (int)Math.Min(limit, places), passwords, 0, [], Sessions(profile));
    }

    /// Zen's spaces and pins, for Zen.
    public override ArcSidebar? Sidebar(string? profile = null) => KeepsSpaces ? Zen.Sidebar(this, profile) : null;
}
