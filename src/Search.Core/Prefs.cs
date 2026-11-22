using System.Text.Json;
using System.Text.Json.Serialization;

namespace Search.Core;

public enum Theme { System, Light, Dark }

// Everything there is to set, in one place (Prefs.swift, the part Phase 1 needs). Kept as settings.json in the
// store's folder, so a test world's settings are its own like everything else in it.
public sealed class Prefs : Observable
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private bool _tabsOnSide = true, _folded, _bench, _agents;
    private Theme _theme;
    private string _engine = "google", _customEngine = "";
    private double _columnWidth = 240;
    private bool _shield = true, _savePasswords = true, _siteStyles, _littleLinks = true;
    private List<string> _shieldPaused = [];

    /// Tabs down the left (a new install's choice, as on the Mac) or across the top.
    public bool TabsOnSide { get => _tabsOnSide; set { if (Set(ref _tabsOnSide, value)) Save(); } }
    /// The column folded away with Ctrl+S.
    public bool Folded { get => _folded; set { if (Set(ref _folded, value)) Save(); } }
    public Theme Theme { get => _theme; set { if (Set(ref _theme, value)) Save(); } }
    public string Engine { get => _engine; set { if (Set(ref _engine, value)) Save(); } }
    public string CustomEngine { get => _customEngine; set { if (Set(ref _customEngine, value)) Save(); } }
    public double ColumnWidth { get => _columnWidth; set { if (Set(ref _columnWidth, Math.Clamp(value, 160, 480))) Save(); } }
    /// Settings › Passwords: offer to keep a sign-in once it has worked.
    public bool SavePasswords { get => _savePasswords; set { if (Set(ref _savePasswords, value)) Save(); } }
    /// Your own CSS for a site, as Arc's Boosts: off until turned on, as on the Mac.
    public bool SiteStyles { get => _siteStyles; set { if (Set(ref _siteStyles, value)) Save(); } }
    /// Settings › General: a link from another app opens in a small window (Little.swift), on for a new install.
    public bool LittleLinks { get => _littleLinks; set { if (Set(ref _littleLinks, value)) Save(); } }
    /// The ad blocker, on unless turned off.
    public bool Shield { get => _shield; set { if (Set(ref _shield, value)) Save(); } }
    /// Sites the ad blocker is off for: the ones it broke.
    public List<string> ShieldPaused { get => _shieldPaused; set { _shieldPaused = value ?? []; Changed(); } }

    public bool ShieldPausedOn(string? host) => host is not null && _shieldPaused.Contains(Site(host));
    public void PauseShield(string host, bool off)
    {
        var site = Site(host);
        if (off == _shieldPaused.Contains(site)) return;
        if (off) _shieldPaused.Add(site); else _shieldPaused.Remove(site);
        _shieldPaused.Sort(StringComparer.Ordinal);
        Changed(nameof(ShieldPaused));
        Save();
    }
    private static string Site(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();

    /// Settings › General › Let a script drive Search: the bench socket.
    public bool Bench { get => _bench; set { if (Set(ref _bench, value)) Save(); } }

    /// Settings › About › Developer › Let coding agents use Search (AgentServer). Off unless turned on there, which
    /// leaves the mark it needs at launch (SwitchMark).
    public bool Agents { get => _agents; set { if (Set(ref _agents, value)) Save(); } }
    /// Found on at launch without the switch's mark, and put back to off: the window says so once.
    [JsonIgnore] public bool AgentsRefused { get; private set; }

    /// At launch, before anything listens: a setting some other program wrote, with no mark from the switch behind
    /// it, is put back to off. A test run keeps what it was given.
    public void CheckAgents(bool marked) => CheckAgents(marked, Store.Testing);

    internal void CheckAgents(bool marked, bool testing)
    {
        if (!Agents || marked || testing) return;
        AgentsRefused = true;
        Agents = false;
    }

    /// Reading the file sets every property; none of that is a change worth writing back.
    private static bool _reading;
    private static string File => Store.File("settings.json");

    public static Prefs Load()
    {
        Prefs? read = null;
        _reading = true;
        try { if (System.IO.File.Exists(File)) read = JsonSerializer.Deserialize<Prefs>(System.IO.File.ReadAllBytes(File), _json); }
        catch (JsonException) { Store.Quarantine(File); }
        finally { _reading = false; }
        return read ?? new Prefs();
    }

    private void Save() { if (!_reading) Disk.Write(File, () => JsonSerializer.SerializeToUtf8Bytes(this, _json)); }
}
