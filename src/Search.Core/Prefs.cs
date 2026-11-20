using System.Text.Json;
using System.Text.Json.Serialization;

namespace Search.Core;

public enum Theme { System, Light, Dark }

// Everything there is to set, in one place (Prefs.swift, the part Phase 1 needs). Kept as settings.json in the
// store's folder, so a test world's settings are its own like everything else in it.
public sealed class Prefs : Observable
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private bool _tabsOnSide = true, _folded, _bench;
    private Theme _theme;
    private string _engine = "google", _customEngine = "";
    private double _columnWidth = 240;

    /// Tabs down the left (a new install's choice, as on the Mac) or across the top.
    public bool TabsOnSide { get => _tabsOnSide; set { if (Set(ref _tabsOnSide, value)) Save(); } }
    /// The column folded away with Ctrl+S.
    public bool Folded { get => _folded; set { if (Set(ref _folded, value)) Save(); } }
    public Theme Theme { get => _theme; set { if (Set(ref _theme, value)) Save(); } }
    public string Engine { get => _engine; set { if (Set(ref _engine, value)) Save(); } }
    public string CustomEngine { get => _customEngine; set { if (Set(ref _customEngine, value)) Save(); } }
    public double ColumnWidth { get => _columnWidth; set { if (Set(ref _columnWidth, Math.Clamp(value, 160, 480))) Save(); } }
    /// Settings › General › Let a script drive Search: the bench socket.
    public bool Bench { get => _bench; set { if (Set(ref _bench, value)) Save(); } }

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
