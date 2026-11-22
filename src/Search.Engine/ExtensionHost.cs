using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.Engine;

// Chrome extensions, on WebView2 (Extensions.swift's browser half). Chromium runs them natively; what is here is the
// install (from the Chrome Web Store through Crx, or from a folder), the copy Search loads (the manifest as you chose
// it, with the shim in front of its worker), and keeping every profile Search uses in step with the list: a space
// with sign-ins of its own is a profile of its own, and each profile has its own installed copy and its own saved
// data, as Chrome profiles do. Private tabs have none.
//
// What the spikes found that shapes this (spikes/VERDICTS.md, spike 7): adding an extension again from the same folder
// loads its new files and keeps what it saved; removing it throws that away. So an update or a change of sites is an
// add again, never a remove and an add; and a profile is given each extension again once per launch, so whatever
// changed while that profile wasn't open reaches it.
public sealed class ExtensionHost
{
    private readonly IDictionary<string, string> _settings;
    private readonly Dictionary<string, (CoreWebView2Profile Profile, Task Ready)> _profiles = new();
    private readonly Dictionary<string, Manifest> _manifests = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string ForgettingKey = "extensions.forgetting";
    private const string CheckedKey = "extensions.checked";

    public ExtensionList List { get; }
    /// Store ids being fetched and installed right now.
    public HashSet<string> Busy { get; } = new();
    /// Why each extension that should be running isn't, in Chromium's words, until it next loads.
    public Dictionary<string, string> Errors { get; } = new();
    /// The list, what's busy, or an extension's files changed.
    public event Action? Changed;

    public ExtensionHost(IDictionary<string, string> settings)
    {
        _settings = settings;
        List = new ExtensionList(Folder);
    }

    public static string Folder => Store.File("Extensions");
    public static string FolderOf(string id) => Path.Combine(Folder, id);
    /// Where the store is asked, which a test run may point at a server of its own.
    private static string? Endpoint => Store.Testing ? System.Environment.GetEnvironmentVariable("SEARCH_CRX_ENDPOINT") : null;

    public Manifest? ManifestOf(string id)
    {
        if (_manifests.TryGetValue(id, out var known)) return known;
        try { return Manifest.Read(FolderOf(id)) is { } read ? _manifests[id] = read : null; } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// An extension's page by its path in the package: "popup.html" → chrome-extension://id/popup.html.
    public static string PageUrl(string id, string path) => path.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ? path : $"chrome-extension://{id}/{path.TrimStart('/')}";
    public string? IconFile(string id, int size) => ManifestOf(id)?.IconPath(size) is { } icon && File.Exists(Path.Combine(FolderOf(id), icon)) ? Path.Combine(FolderOf(id), icon) : null;

    // MARK: profiles

    /// A profile's extensions as the list says, once per launch; the first page in it waits for this, so content
    /// scripts that run at document start are there for it.
    public Task Ready(CoreWebView2Profile profile)
    {
        lock (_profiles)
        {
            if (_profiles.TryGetValue(profile.ProfilePath, out var known)) return known.Ready;
            var ready = Sync(profile);
            _profiles[profile.ProfilePath] = (profile, ready);
            return ready;
        }
    }

    private IEnumerable<CoreWebView2Profile> Profiles { get { lock (_profiles) return _profiles.Values.Select(p => p.Profile).ToList(); } }

    private async Task Sync(CoreWebView2Profile profile)
    {
        IReadOnlyList<CoreWebView2BrowserExtension> present;
        try { present = await profile.GetBrowserExtensionsAsync(); } catch (Exception e) { Log(e); return; }
        // Removed while this profile wasn't open: gone from it now. Only what Search itself put in is ever taken out.
        var forgetting = Forgetting();
        foreach (var gone in present.Where(p => forgetting.Contains(p.Id))) try { await gone.RemoveAsync(); } catch (Exception e) { Log(e); }
        foreach (var item in List.Items) await Load(profile, item, present);
    }

    private async Task Load(CoreWebView2Profile profile, Installed item, IReadOnlyList<CoreWebView2BrowserExtension>? present = null)
    {
        present ??= await profile.GetBrowserExtensionsAsync();
        var there = present.FirstOrDefault(p => p.Id == item.Id);
        try
        {
            if (!item.Enabled) { if (there is not null && there.IsEnabled) await there.EnableAsync(false); return; }
            if (!Directory.Exists(FolderOf(item.Id))) { Errors[item.Id] = "Its files are missing; remove it and add it again."; return; }
            // One turned off is turned on as the profile lists it: added again, it stays off and says it's on.
            if (there is { IsEnabled: false }) await there.EnableAsync(true);
            await profile.AddBrowserExtensionAsync(FolderOf(item.Id));
            Errors.Remove(item.Id);
        }
        catch (Exception e) { Errors[item.Id] = e.Message; Log(e); }
    }

    /// Given again to every profile open now: its new files loaded, what it saved kept.
    private async Task Everywhere(string id)
    {
        _manifests.Remove(id);
        if (List.Find(id) is not { } item) return;
        foreach (var profile in Profiles) await Load(profile, item);
        Changed?.Invoke();
    }

    // MARK: installing

    /// From the Chrome Web Store, by a link or an id: fetched, its signature checked against the id, unpacked, asked
    /// about, and loaded. Null if you said no.
    public async Task<Installed?> InstallFromStore(string id, Func<Manifest, Task<bool>> confirm)
    {
        if (!Busy.Add(id)) return null;
        Changed?.Invoke();
        var staged = Staging(id);
        try
        {
            var crx = await Fetch(Crx.DownloadUrl(id, Endpoint));
            var verified = Crx.Verify(crx, id);
            Crx.Unpack(verified.Zip, staged);
            var manifest = Manifest.Read(staged) ?? throw new CrxRefused(CrxRefusal.Unpack);
            if (!await confirm(manifest)) return null;
            Prepare(staged, id, Convert.ToBase64String(verified.PublicKey), fresh: true);
            Replace(staged, FolderOf(id));
            var item = new Installed(id, manifest.Name ?? id, manifest.Version ?? "0", Enabled: true, FromStore: true, manifest.Grants().Order().ToList(), List.Find(id)?.Pinned);
            List.Put(item);
            Unforget(id);
            await Everywhere(id);
            return item;
        }
        finally { Busy.Remove(id); TryDelete(staged); Changed?.Invoke(); }
    }

    /// From a folder, as Chrome's "Load unpacked": copied in, so Reload brings in its later edits. A folder added again
    /// is the same extension, reloaded. Its copy has a key of Search's own, so its id is known before it loads and stays
    /// the same; the folder's own "key" is ignored, as on the Mac, since store keys are public.
    public async Task<Installed?> InstallFolder(string source, Func<Manifest, Task<bool>> confirm)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(Path.Combine(source, "manifest.json"))) throw new InvalidDataException("That folder has no manifest.json.");
        if (List.Items.FirstOrDefault(i => !i.FromStore && string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase)) is { } known) { await Reload(known.Id); return List.Find(known.Id); }
        var key = ExtensionPrep.NewKey();
        var id = ExtensionPrep.IdOfKeyBase64(key);
        var staged = Staging(id);
        try
        {
            CopyFolder(source, staged);
            var manifest = Manifest.Read(staged) ?? throw new CrxRefused(CrxRefusal.Unpack);
            if (!await confirm(manifest)) return null;
            Prepare(staged, id, key, fresh: true);
            Replace(staged, FolderOf(id));
            var item = new Installed(id, manifest.Name ?? id, manifest.Version ?? "0", Enabled: true, FromStore: false, manifest.Grants().Order().ToList(), Source: source);
            List.Put(item);
            Unforget(id);
            await Everywhere(id);
            return item;
        }
        finally { TryDelete(staged); }
    }

    /// A folder's latest edits brought in under the same id and key; one from the store, simply loaded again.
    public async Task Reload(string id)
    {
        if (List.Find(id) is not { } item) return;
        if (!item.FromStore && item.Source is { } source && File.Exists(Path.Combine(source, "manifest.json")))
        {
            var key = ExtensionPrep.ReadOriginal(FolderOf(id)) is not null && ReadKey(FolderOf(id)) is { } kept ? kept : ExtensionPrep.NewKey();
            var staged = Staging(id);
            try
            {
                CopyFolder(source, staged);
                var manifest = Manifest.Read(staged) ?? throw new CrxRefused(CrxRefusal.Unpack);
                Prepare(staged, id, key, fresh: true);
                Replace(staged, FolderOf(id));
                List.Put(item with { Name = manifest.Name ?? item.Name, Version = manifest.Version ?? item.Version, Permissions = manifest.Grants().Order().ToList() });
            }
            finally { TryDelete(staged); }
        }
        await Everywhere(id);
    }

    public async Task Remove(string id)
    {
        foreach (var profile in Profiles)
            try { foreach (var ext in (await profile.GetBrowserExtensionsAsync()).Where(e => e.Id == id)) await ext.RemoveAsync(); } catch (Exception e) { Log(e); }
        List.Remove(id);
        SiteAccess.Forget(id, _settings);
        _manifests.Remove(id);
        Errors.Remove(id);
        // Profiles not open this launch let go of it when they next open.
        Forget(id);
        TryDelete(FolderOf(id));
        Changed?.Invoke();
    }

    public async Task SetEnabled(string id, bool on)
    {
        List.SetEnabled(id, on);
        if (List.Find(id) is { } item) foreach (var profile in Profiles) await Load(profile, item);
        Changed?.Invoke();
    }

    public void SetPinned(string id, bool on) { List.SetPinned(id, on); Changed?.Invoke(); }

    /// Where it runs: its copy's manifest rewritten from the original, and loaded again.
    public async Task SetSites(string id, SiteAccess access)
    {
        access.Save(id, _settings);
        if (!Directory.Exists(FolderOf(id))) return;
        Prepare(FolderOf(id), id, ReadKey(FolderOf(id)), fresh: false);
        await Everywhere(id);
    }

    public SiteAccess SitesOf(string id) => SiteAccess.Of(id, _settings);

    // MARK: updates

    /// Once a day at most: each store extension asked whether there's a newer version, and one that wants more than
    /// it was allowed asked about first.
    public async Task CheckForUpdates(Func<Manifest, Task<bool>> confirm, bool force = false)
    {
        if (!force && _settings.TryGetValue(CheckedKey, out var last) && DateTimeOffset.TryParse(last, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(20)) return;
        _settings[CheckedKey] = DateTimeOffset.UtcNow.ToString("O");
        foreach (var item in List.Items.Where(i => i.FromStore).ToList()) try { await Update(item, confirm); } catch (Exception e) { Log(e); }
    }

    private async Task Update(Installed item, Func<Manifest, Task<bool>> confirm)
    {
        var xml = await _http.GetStringAsync(Crx.UpdateCheckUrl(item.Id, item.Version, Endpoint));
        if (Crx.NewerVersion(xml, item.Version) is null) return;
        var verified = Crx.Verify(await Fetch(Crx.DownloadUrl(item.Id, Endpoint)), item.Id);
        var staged = Staging(item.Id);
        try
        {
            Crx.Unpack(verified.Zip, staged);
            var manifest = Manifest.Read(staged) ?? throw new CrxRefused(CrxRefusal.Unpack);
            if (manifest.More(item.Permissions).Count > 0 && !await confirm(manifest)) return;
            if (List.Find(item.Id) is not { FromStore: true } current || current.Version != item.Version) return;
            Prepare(staged, item.Id, Convert.ToBase64String(verified.PublicKey), fresh: true);
            Replace(staged, FolderOf(item.Id));
            List.Put(current with { Name = manifest.Name ?? current.Name, Version = manifest.Version ?? current.Version, Permissions = manifest.Grants().Order().ToList() });
            await Everywhere(item.Id);
        }
        finally { TryDelete(staged); }
    }

    // MARK: pieces

    /// The copy as Search loads it: the shim and the bridge beside the extension's files, its manifest as you chose.
    private void Prepare(string folder, string id, string? key, bool fresh)
    {
        var own = Manifest.ReadObject(Path.Combine(folder, "manifest.json")) ?? throw new CrxRefused(CrxRefusal.Unpack);
        var original = fresh ? own : ExtensionPrep.ReadOriginal(folder) ?? own;
        ExtensionPrep.Write(folder, original, key, SiteAccess.Of(id, _settings), Scripts.Read("extension-worker.js"), new Dictionary<string, string> { ["search-browser/bridge.html"] = "<!doctype html><meta charset=utf-8><title>Search</title>" });
        _manifests.Remove(id);
    }

    private static string? ReadKey(string folder)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))?["key"]?.GetValue<string>(); } catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }

    private static async Task<byte[]> Fetch(Uri url)
    {
        using var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new CrxRefused(CrxRefusal.Download, (int)response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return bytes.Length > 0 ? bytes : throw new CrxRefused(CrxRefusal.Empty);
    }

    /// The staged copy put where the old one was, the old one only taken away once the new one is in its place.
    private static void Replace(string staged, string target)
    {
        var old = target + ".old-" + Guid.NewGuid().ToString("N")[..6];
        if (Directory.Exists(target)) Directory.Move(target, old);
        Directory.Move(staged, target);
        TryDelete(old);
    }

    private static string Staging(string id) => Path.Combine(Folder, ".staging-" + id + "-" + Guid.NewGuid().ToString("N")[..6]);

    /// A folder copied file by file; a link anywhere in it is refused, since the copy is rewritten in place and a link
    /// would have that rewrite land wherever it points. Version control's folder stays behind.
    private static void CopyFolder(string source, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"“{entry.Name}” is a link; Search copies folders without links.");
            var to = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo dir) { if (dir.Name != ".git") CopyFolder(dir.FullName, to); }
            else File.Copy(entry.FullName, to);
        }
    }

    private HashSet<string> Forgetting() => _settings.TryGetValue(ForgettingKey, out var raw) && JsonSerializer.Deserialize<List<string>>(raw) is { } ids ? ids.ToHashSet() : [];
    private void Forget(string id) { var ids = Forgetting(); ids.Add(id); _settings[ForgettingKey] = JsonSerializer.Serialize(ids.Order().ToList()); }
    private void Unforget(string id) { var ids = Forgetting(); if (ids.Remove(id)) _settings[ForgettingKey] = JsonSerializer.Serialize(ids.Order().ToList()); }

    private static void TryDelete(string folder) { try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static void Log(Exception e) { try { File.AppendAllText(Store.File("extensions.log"), $"{DateTime.Now:O} {e}\n"); } catch (IOException) { } }

    // MARK: Search's own pages of an extension

    /// The page shim for one of Search's surfaces (a popup, a side panel, the bridge) or for a tab, with the window of
    /// the tab in front and the windows that are Search's own.
    public static string PageShim(string mode, long? front, IEnumerable<long> surfaces) => Scripts.Read("extension-page.js")
        .Replace("__SEARCH_MODE__", JsonSerializer.Serialize(mode)).Replace("__SEARCH_FRONT__", front?.ToString() ?? "null").Replace("__SEARCH_SURFACES__", JsonSerializer.Serialize(surfaces));

    public static string BridgeUrl(string id) => PageUrl(id, "search-browser/bridge.html");

    /// What a press of the button means, asked of the extension itself in its bridge page: its popup for this tab, its
    /// side panel if it opens on a press, or else its onClicked, told through the shim in its worker.
    public const string Press = """
        (async () => {
          const front = await window.__searchReady();
          const c = chrome, action = c.action || c.browserAction, tabId = front && front.tabId != null ? front.tabId : undefined, of = tabId != null ? { tabId } : {};
          let popup = '';
          try { popup = action && action.getPopup ? await action.getPopup(of) : ''; } catch (e) { }
          if (popup) return { popup: new URL(popup, location.origin).href };
          try {
            const behavior = c.sidePanel && c.sidePanel.getPanelBehavior ? await c.sidePanel.getPanelBehavior() : null;
            if (behavior && behavior.openPanelOnActionClick) {
              const options = await c.sidePanel.getOptions(of);
              if (options && options.enabled !== false && options.path) return { panel: new URL(options.path, location.origin).href };
            }
          } catch (e) { }
          try { await c.runtime.sendMessage({ __search: 'click', tabId }); } catch (e) { }
          return { clicked: true };
        })()
        """;

    /// The side panel's page for the tab in front, if the extension set one; else the manifest's.
    public const string PanelPath = """
        (async () => {
          const front = await window.__searchReady();
          try { const o = await chrome.sidePanel.getOptions(front && front.tabId != null ? { tabId: front.tabId } : {}); if (o && o.enabled !== false && o.path) return new URL(o.path, location.origin).href; } catch (e) { }
          return null;
        })()
        """;
}
