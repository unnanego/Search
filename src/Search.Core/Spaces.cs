using System.Text.Json;

namespace Search.Core;

// Spaces (Spaces.swift, the model and its file; Profiles.swift for which store a space's sites use): separate sets of
// tabs in the one window, each with its own cookies and sign-ins if you like, and a downloads folder of its own.
//
// Off unless turned on. Until then there is one space, the first, and nothing about it shows: its tabs are the
// session there has always been (session.json) and its sites use the store there has always been (WebView2's default
// profile), so turning spaces on signs nobody out. With spaces off a window shows its profile's home, whatever else
// the file holds; the others are kept in case they are turned on again.
//
// spaces.json is the Mac's, key for key (see SwiftJson): every space of every profile, in the order the user arranged
// them, each {id, name, colour, icon?, sharesSignIns?, downloads?, profile?, gradient?, strength?}. A space that
// doesn't read spoils the list, as Swift's decoder has it; such a file is set aside (Store.Quarantine), not
// overwritten. The few notes the Mac keeps in its settings (spaces on, the order kept, the space last in front, the
// stores still to erase) live in the settings handed over, as Bookmarks' do. Called from the UI thread.

public sealed record Space(Guid Id, string Name, int Colour = 0)
{
    /// The first space: the session and the store there were before spaces. The same id as Session.FirstSpace.
    public static readonly Guid FirstId = new("00000000-0000-0000-0000-000000000001");

    /// Its icon, one of Spaces.Icons (the Mac's SF Symbol names, so the file reads the same on both).
    public string? Icon { get; init; }
    /// Signed in wherever its profile's home is (the same cookies, only the tabs its own) rather than a store of its
    /// own. Chosen when it is made; null, for a space from before the choice, is a store of its own.
    public bool? SharesSignIns { get; init; }
    /// Where its downloads go; null for the folder in Settings.
    public string? Downloads { get; init; }
    /// The profile it belongs to; null for Personal, which every space from before profiles is in.
    public Guid? Profile { get; init; }
    /// Two or three colours in place of the one; null for the colour alone. Its colour is kept beside it.
    public SpaceGradient? Gradient { get; init; }
    /// How strongly its colour or gradient washes the chrome; null for Subtle.
    public SpaceStrength? Strength { get; init; }

    public Guid ProfileId => Profile ?? Spaces.PersonalProfile;
    public bool IsFirst => Id == FirstId;
    public static Space First() => new(FirstId, "Personal");

    /// The icon it shows: its own, or a house for the first and a briefcase for any other that has none yet.
    public string Symbol => Icon is { } icon && Spaces.Icons.Contains(icon) ? icon : IsFirst ? "house" : "briefcase";
    /// Its colour, or null for none (an index out of range from a newer Search included). With a gradient, its first.
    public Rgb? Tint => Gradient is { Usable: true } g ? g.Lead : Spaces.ColourAt(Colour);
    public SpaceLook? Look => Gradient is { Usable: true } g ? new SpaceLook.Gradient(g) : Spaces.ColourAt(Colour) is not null ? new SpaceLook.Colour(Colour) : null;
    public SpaceStrength WashStrength => Strength ?? SpaceStrength.Subtle;

    public SpaceLookChoice LookChoice
    {
        get
        {
            if (Gradient is { Usable: true } g) { var at = SpaceGradient.Presets.ToList().FindIndex(p => p.Gradient.Colours.SequenceEqual(g.Colours)); return at >= 0 ? new SpaceLookChoice.Preset(at) : new SpaceLookChoice.Custom(); }
            return Spaces.ColourAt(Colour) is not null ? new SpaceLookChoice.Colour(Colour) : new SpaceLookChoice.None();
        }
    }

    /// What the picker's preview says it is.
    public string LookName => LookChoice switch
    {
        SpaceLookChoice.Colour c => Spaces.ColourNames[c.Index],
        SpaceLookChoice.Preset p => SpaceGradient.Presets[p.Index].Name,
        SpaceLookChoice.Custom => "Custom gradient",
        _ => "No colour",
    };
}

public sealed class Spaces : Observable
{
    /// Profile.personalID: everything there was before profiles.
    public static readonly Guid PersonalProfile = new("00000000-0000-0000-0000-0000000000A1");

    /// How much of a space's colour the chrome wears at Subtle: enough to tell at the edge of the eye which space this
    /// is. The dark ground swallows more of a colour, so it takes a little more.
    public const double WashLight = 0.07, WashDark = 0.11;

    /// A handful, picked to read on the light ground and the dark one alike. The first stands for none, never drawn.
    public static readonly IReadOnlyList<Rgb> Colours = [new(0.45, 0.47, 0.52), new(0.26, 0.52, 0.96), new(0.20, 0.66, 0.45), new(0.96, 0.62, 0.20), new(0.90, 0.33, 0.40), new(0.62, 0.40, 0.90)];
    public static readonly IReadOnlyList<string> ColourNames = ["None", "Blue", "Green", "Orange", "Red", "Violet"];

    /// The icons a space can wear, as the Mac names them (SF Symbols), grouped as work, thinking, leisure and life.
    public static readonly IReadOnlyList<string> Icons =
    [
        "briefcase", "building.2", "desktopcomputer", "laptopcomputer", "chevron.left.forwardslash.chevron.right", "terminal",
        "sparkles", "brain.head.profile", "lightbulb", "gamecontroller", "beach.umbrella", "cup.and.saucer",
        "music.note", "film", "paintpalette", "camera", "house", "book",
        "graduationcap", "cart", "airplane", "dumbbell", "leaf", "heart",
    ];
    public static readonly IReadOnlyList<string> IconNames =
    [
        "Work", "Office", "Desktop", "Laptop", "Code", "Terminal", "AI", "Thinking", "Ideas", "Games", "Leisure", "Café",
        "Music", "Film", "Art", "Photos", "Home", "Reading", "Studies", "Shopping", "Travel", "Sport", "Nature", "Personal",
    ];

    private const string UsesKey = "spaces", OrderKey = "spaces.orderKept", CurrentKey = "space.current", ErasingKey = "spaces.erasing";

    private readonly string _fileName;
    private readonly Func<Guid, Guid?>? _homes;
    /// The list as the file has it, for telling whether it carries the user's order.
    private List<Space> _saved = [];
    private IReadOnlyList<Space> _all = [Space.First()];
    private Dictionary<Guid, Space> _known = new() { [Space.FirstId] = Space.First() };
    private Guid _current = Space.FirstId;

    /// `settings`: where the Mac's settings notes live (Bookmarks.SharedSettings, memory only, unless the app hands
    /// over its own). `homes`: each profile's home space (Profile.home), for a profile other than Personal; without
    /// it, or for a profile it doesn't know, the profile's first space in the file is its home.
    public Spaces(string file = "spaces.json", IDictionary<string, string>? settings = null, Func<Guid, Guid?>? homes = null)
    {
        _fileName = file;
        Settings = settings ?? Bookmarks.SharedSettings;
        _homes = homes;
        Load();
    }

    internal IDictionary<string, string> Settings { get; }

    /// Every space of every profile, in the order the user arranged them; the first space always among them.
    public IReadOnlyList<Space> All => _all;
    /// Every space by its id, as the file last had them.
    public IReadOnlyDictionary<Guid, Space> Known => _known;
    public Space? Find(Guid id) => _known.GetValueOrDefault(id);

    /// One profile's spaces in their order: what a window of that profile shows (with spaces on), and all it shows.
    public IReadOnlyList<Space> Of(Guid profile)
    {
        var mine = _all.Where(s => s.ProfileId == profile).ToList();
        if (KeepsOrder(_saved)) return mine;
        var home = HomeIn(profile, mine);
        return [.. mine.Where(s => s.Id == home), .. mine.Where(s => s.Id != home)];
    }

    /// The profile a space is in: Personal for one that isn't known.
    public Guid ProfileOf(Guid id) => Find(id)?.ProfileId ?? PersonalProfile;

    /// The profile's first space: the one a window shows with spaces off, and the one that can't be deleted. Known
    /// by its id, not its place, because the spaces can be reordered.
    public Guid Home(Guid profile) => HomeIn(profile, _all.Where(s => s.ProfileId == profile));

    private Guid HomeIn(Guid profile, IEnumerable<Space> block) => profile == PersonalProfile ? Space.FirstId : _homes?.Invoke(profile) ?? block.FirstOrDefault()?.Id ?? Space.FirstId;

    // On or off, and which is in front.

    /// Settings › Tabs › Spaces. Turned off, the window goes back to its profile's home.
    public bool UsesSpaces
    {
        get => Flag(Settings, UsesKey);
        set
        {
            if (value == UsesSpaces) return;
            Flag(Settings, UsesKey, value);
            if (!value) Remember(Home(ProfileOf(_current)), primary: true);
            Changed();
        }
    }

    /// The space new tabs are made in: the one on screen in the window in front.
    public Guid Current { get => _current; set => Set(ref _current, value); }

    /// A switch to space `id` (Ctrl+1–9, the menu): only with spaces on, and only to a space that is there. `primary`:
    /// this is the oldest window, whose space the app comes back to; every window notes its profile's last space.
    public bool Enter(Guid id, bool primary = true)
    {
        if (!UsesSpaces || Find(id) is null) return false;
        Remember(id, primary);
        return true;
    }

    private void Remember(Guid id, bool primary)
    {
        Current = id;
        if (primary) Settings[CurrentKey] = SwiftJson.Uuid(id);
        Settings[CurrentKey + "." + SwiftJson.Uuid(ProfileOf(id))] = SwiftJson.Uuid(id);
    }

    /// The space a profile's window was last in, or its home.
    public Guid LastSpace(Guid profile) =>
        Settings.TryGetValue(CurrentKey + "." + SwiftJson.Uuid(profile), out var text) && SwiftJson.Uuid(text) is { } last && Of(profile).Any(s => s.Id == last) ? last : Home(profile);

    /// Where a window opens: the space it showed (`record`), or for the first window the space the app was last in;
    /// with spaces off, that profile's home whatever it was.
    public (Guid Profile, Guid Space) Start(Guid? record = null)
    {
        var shown = record ?? (Settings.TryGetValue(CurrentKey, out var text) ? SwiftJson.Uuid(text) : null) ?? Space.FirstId;
        var profile = ProfileOf(shown);
        var loaded = Of(profile);
        var space = !UsesSpaces ? Home(profile) : loaded.Any(s => s.Id == shown) ? shown : loaded.FirstOrDefault()?.Id ?? Space.FirstId;
        return (profile, space);
    }

    /// What the chrome is washed with for `space`: nothing with spaces off, so nothing about spaces shows.
    public SpaceWash? WashOf(Space space, bool dark) => UsesSpaces ? SpaceWash.Of(space.Look, space.WashStrength, dark) : null;

    /// Where a download lands: the space's folder, or the one in Settings (always, with spaces off).
    public string DownloadsFolder(Space space, string settingsFolder) => UsesSpaces && space.Downloads is { } path ? path : settingsFolder;

    // Stores.

    /// Whether a space is signed out of the others, with a store of its own: any but the first space of a profile and
    /// the ones made signed in with it.
    public bool OwnsStore(Space space) => !(space.IsFirst || space.SharesSignIns == true || (space.ProfileId != PersonalProfile && Home(space.ProfileId) == space.Id));
    public bool OwnsStore(Guid id) => Find(id) is { } space ? OwnsStore(space) : id != Space.FirstId;

    /// The WebView2 profile (ProfileName) a space's tabs use. Null for the default one: Personal's first space and
    /// every space signed in with it, the store there always was. Another profile's spaces that share its sign-ins use
    /// the profile's id; a space with sign-ins of its own uses its own id. Uppercase UUIDs, as the Mac's WebKit store
    /// identifiers are the same ids.
    public string? StoreName(Space space) => OwnsStore(space) ? SwiftJson.Uuid(space.Id) : ProfileStoreName(space.ProfileId);
    public string? StoreName(Guid id) => Find(id) is { } space ? StoreName(space) : id == Space.FirstId ? null : SwiftJson.Uuid(id);
    /// A profile's own store: the default for Personal, its id for any other.
    public static string? ProfileStoreName(Guid profile) => profile == PersonalProfile ? null : SwiftJson.Uuid(profile);

    /// Where a space's pages are remembered: its profile's history, or for a space signed out of the others, a history
    /// of its own, so what is visited there never comes up in another space's suggestions.
    public string HistoryFile(Guid id) => OwnsStore(id) ? SpaceHistoryFile(id) : ProfileHistoryFile(ProfileOf(id));
    /// A profile's own history: history.json for Personal, as always; a file of its own for any other.
    public static string ProfileHistoryFile(Guid profile) => profile == PersonalProfile ? "history.json" : $"history-{SwiftJson.Uuid(profile)}.json";
    private static string SpaceHistoryFile(Guid id) => $"history-space-{SwiftJson.Uuid(id)}.json";

    /// The stores of deleted spaces still to be removed (WebView2's CoreWebView2Profile.Delete), kept across launches
    /// until each is gone, as the Mac keeps them until WebKit lets go.
    public IReadOnlyList<Guid> Erasing => Strings(Settings.TryGetValue(ErasingKey, out var text) ? text : null).Select(SwiftJson.Uuid).OfType<Guid>().ToList();
    /// That store is gone now.
    public void Erased(Guid id) => SetStrings(Settings, ErasingKey, Strings(Settings.TryGetValue(ErasingKey, out var text) ? text : null).Where(s => SwiftJson.Uuid(s) != id));

    // Changing. Each writes the space's profile's list, as a window has it.

    /// The icon a new space gets unless told: the first no space of the profile wears yet.
    public string FreeIcon(Guid profile) { var used = Of(profile).Select(s => s.Symbol).ToHashSet(); return Icons.FirstOrDefault(i => !used.Contains(i)) ?? "briefcase"; }

    /// A new space, at the end of its profile's: signed in where the others are, or starting afresh with its own
    /// cookies and sign-ins. Null for an empty name.
    public Space? Add(string name, Guid? profile = null, string? icon = null, bool sharesSignIns = true)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        var owner = profile ?? PersonalProfile;
        var made = new Space(Guid.NewGuid(), trimmed) { Icon = icon ?? FreeIcon(owner), SharesSignIns = sharesSignIns, Profile = owner == PersonalProfile ? null : owner };
        Write([.. Of(owner), made], owner);
        return made;
    }

    /// Dragged to another place among its profile's. The order is kept from then on, home away from the front included.
    public bool Move(Guid id, int index)
    {
        if (Find(id) is not { } space) return false;
        var mine = Of(space.ProfileId).ToList();
        var from = mine.FindIndex(s => s.Id == id);
        if (from < 0 || index < 0 || index >= mine.Count || from == index) return false;
        mine.RemoveAt(from);
        mine.Insert(index, space);
        Flag(Settings, OrderKey, true);
        Write(mine, space.ProfileId);
        return true;
    }

    public bool Rename(Guid id, string name) => name.Trim() is { Length: > 0 } trimmed && Change(id, s => s with { Name = trimmed });
    public bool SetIcon(Guid id, string icon) => Change(id, s => s with { Icon = icon });
    /// A colour picked is the colour wanted: the gradient goes.
    public bool SetColour(Guid id, int colour) => colour >= 0 && colour < Colours.Count && Change(id, s => s with { Colour = colour, Gradient = null });
    /// The space's gradient, or none to go back to its colour. One that can't be drawn (fewer than two colours) is none.
    public bool SetGradient(Guid id, SpaceGradient? gradient) => Change(id, s => s with { Gradient = gradient is { Usable: true } ? gradient : null });
    /// Subtle is kept as none, so the space reads and writes as before.
    public bool SetStrength(Guid id, SpaceStrength strength) => Change(id, s => s with { Strength = strength == SpaceStrength.Subtle ? null : strength });
    public bool SetDownloads(Guid id, string? folder) => Change(id, s => s with { Downloads = folder });

    /// A choice in the picker, made to the space at once. A colour (or none) takes the gradient away; a ready-made
    /// gradient keeps the direction and grain the space's gradient had.
    public bool ChooseLook(Guid id, SpaceLookChoice choice)
    {
        if (Find(id) is not { } space) return false;
        var current = space.Gradient is { Usable: true } g ? g : null;
        return choice switch
        {
            SpaceLookChoice.None => SetColour(id, 0),
            SpaceLookChoice.Colour c => SetColour(id, c.Index),
            SpaceLookChoice.Preset p when p.Index >= 0 && p.Index < SpaceGradient.Presets.Count =>
                SetGradient(id, new SpaceGradient(SpaceGradient.Presets[p.Index].Gradient.Colours, current?.Angle ?? SpaceGradient.DefaultAngle, current?.Grain ?? false)),
            SpaceLookChoice.Custom => SetGradient(id, current ?? SpaceGradient.Starting(space.Colour)),
            _ => false,
        };
    }

    /// The space's own gradient changed by `change`: its current one, or a first one from its colour.
    public bool EditGradient(Guid id, Func<SpaceGradient, SpaceGradient> change) =>
        Find(id) is { } space && SetGradient(id, change(space.Gradient is { Usable: true } g ? g : SpaceGradient.Starting(space.Colour)));

    /// Back to a look as it was: Escape in the picker.
    public void RestoreLook(Guid id, int colour, SpaceGradient? gradient, SpaceStrength? strength = null)
    {
        SetColour(id, colour);
        if (gradient is not null) SetGradient(id, gradient);
        SetStrength(id, strength ?? SpaceStrength.Subtle);
    }

    /// A space and its tabs gone: its session file erased, and, unless it was signed in with the others, its store
    /// noted for erasing (see Erasing) and its own history removed. The first space, and each profile's home, stay:
    /// they are where everything was before there were spaces. The window closes its tabs, and Pins forgets its pins.
    public bool Remove(Guid id)
    {
        if (id == Space.FirstId || Find(id) is not { } space || id == Home(space.ProfileId)) return false;
        var owned = space.SharesSignIns != true;
        if (_current == id) Remember(Home(space.ProfileId), primary: true);
        Write(Of(space.ProfileId).Where(s => s.Id != id).ToList(), space.ProfileId);
        Session.Erase(id);
        if (owned) EraseStore(id);
        return true;
    }

    /// A profile deleted (Profiles.Delete): every space of it gone as Remove takes one, its home too, and the
    /// profile's own store noted for erasing. Personal's never go. The spaces that went, for Pins to forget.
    public IReadOnlyList<Space> RemoveProfile(Guid profile)
    {
        if (profile == PersonalProfile) return [];
        var gone = _all.Where(s => s.ProfileId == profile).ToList();
        if (gone.Any(s => s.Id == _current)) Current = Space.FirstId;
        Write(_all.Where(s => s.ProfileId != profile).ToList());
        foreach (var space in gone)
        {
            Session.Erase(space.Id);
            if (space.SharesSignIns != true) EraseStore(space.Id);
        }
        NoteErasing(profile);
        Settings.Remove(CurrentKey + "." + SwiftJson.Uuid(profile));
        return gone;
    }

    /// A space's own store noted for erasing, and its own history removed.
    private void EraseStore(Guid id)
    {
        NoteErasing(id);
        // Through the queue, so a write of its history still on its way can't bring the file back after it.
        var history = Store.File(SpaceHistoryFile(id));
        Disk.Write(history, () => { try { System.IO.File.Delete(history); } catch (IOException) { } catch (UnauthorizedAccessException) { } return null; });
    }

    private void NoteErasing(Guid id) =>
        SetStrings(Settings, ErasingKey, Strings(Settings.TryGetValue(ErasingKey, out var text) ? text : null).Append(SwiftJson.Uuid(id)).Distinct().Order(StringComparer.Ordinal));

    /// The order is the one written from now on, wherever each profile's home is in it (Sync applies another
    /// machine's order so).
    internal void KeepOrder() => Flag(Settings, OrderKey, true);

    private bool Change(Guid id, Func<Space, Space> change)
    {
        if (Find(id) is not { } space) return false;
        var mine = Of(space.ProfileId).ToList();
        var at = mine.FindIndex(s => s.Id == id);
        var changed = change(mine[at]);
        if (changed == mine[at]) return false;
        mine[at] = changed;
        Write(mine, space.ProfileId);
        return true;
    }

    // The file.

    private string FilePath => Store.File(_fileName);

    /// One profile's spaces as a window now has them, in their order: the other profiles' are kept as the file has them.
    public void Write(IReadOnlyList<Space> spaces, Guid profile) =>
        Write([.. _all.Where(s => s.ProfileId != profile), .. spaces.Select(s => s with { Profile = profile == PersonalProfile ? null : profile })]);

    /// The whole list, every profile's spaces.
    public void Write(IReadOnlyList<Space> spaces)
    {
        var snapshot = spaces.ToList();
        _saved = snapshot;
        Arrange();
        Disk.Write(FilePath, () => Encode(snapshot));
        Changed(nameof(All));
    }

    private void Load()
    {
        byte[]? data = null;
        try { data = System.IO.File.ReadAllBytes(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (data is not null) { if (Decode(data) is { } list) _saved = list; else Store.Quarantine(FilePath); }
        Arrange();
    }

    /// The list as read: the first space once, in Personal, wherever it was put; added at the front if missing, so
    /// that home stays reachable. Home first, unless the file carries the user's order.
    private void Arrange()
    {
        List<Space> spaces;
        if (_saved.Count == 0) spaces = [Space.First()];
        else
        {
            spaces = [];
            var foundHome = false;
            foreach (var space in _saved)
            {
                if (!space.IsFirst) { spaces.Add(space); continue; }
                if (foundHome) continue;
                foundHome = true;
                spaces.Add(space with { Profile = null });
            }
            if (!foundHome) spaces.Insert(0, Space.First());
            if (!KeepsOrder(_saved)) spaces = [.. spaces.Where(s => s.IsFirst), .. spaces.Where(s => !s.IsFirst)];
        }
        _all = spaces;
        _known = new();
        foreach (var space in spaces) _known.TryAdd(space.Id, space);
    }

    /// Whether a list carries the user's order: a move made here marks it, and a move made by an older Search shows
    /// as a profile's home away from the front of its block, which only that move could have written.
    private bool KeepsOrder(List<Space> saved) =>
        Flag(Settings, OrderKey) || saved.GroupBy(s => s.ProfileId).Any(block => block.First().Id != HomeIn(block.Key, block));

    public static byte[] Encode(IEnumerable<Space> spaces) => SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var s in spaces)
        {
            w.WriteStartObject();
            SwiftJson.WriteUuid(w, "id", s.Id);
            w.WriteString("name", s.Name);
            w.WriteNumber("colour", s.Colour);
            if (s.Icon is { } icon) w.WriteString("icon", icon);
            if (s.SharesSignIns is { } shares) w.WriteBoolean("sharesSignIns", shares);
            if (s.Downloads is { } downloads) w.WriteString("downloads", downloads);
            if (s.Profile is { } profile) SwiftJson.WriteUuid(w, "profile", profile);
            if (s.Gradient is { } gradient) { w.WritePropertyName("gradient"); gradient.Write(w); }
            if (s.Strength is { } strength) w.WriteString("strength", strength.Raw());
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// Null when the file won't decode, as Swift's decoder sees it: every space needs its id, name and colour, and an
    /// icon, sharesSignIns, downloads or profile that is there has to be of its kind. A gradient or strength never
    /// fails: one that doesn't read is none.
    public static List<Space>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var spaces = new List<Space>();
            foreach (var item in document.RootElement.EnumerateArray())
                if (ReadSpace(item) is { } space) spaces.Add(space); else return null;
            return spaces;
        }
        catch (JsonException) { return null; }
    }

    private static Space? ReadSpace(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id
            || !e.TryGetProperty("name", out var n) || SwiftJson.Text(n) is not { } name || !e.TryGetProperty("colour", out var c) || SwiftJson.Int(c) is not { } colour) return null;
        if (!Optional(e, "icon", SwiftJson.Text, out var icon) || !Optional(e, "downloads", SwiftJson.Text, out var downloads)) return null;
        if (!Optional(e, "sharesSignIns", SwiftJson.Bool, out var shares) || !Optional<Guid>(e, "profile", SwiftJson.Uuid, out var profile)) return null;
        return new Space(id, name, colour)
        {
            Icon = icon, Downloads = downloads, SharesSignIns = shares, Profile = profile,
            Gradient = SwiftJson.Has(e, "gradient", out var g) ? SpaceGradient.Read(g) : null,
            Strength = SwiftJson.Has(e, "strength", out var s) ? SpaceStrengths.Parse(SwiftJson.Text(s)) : null,
        };
    }

    /// decodeIfPresent: missing or null is fine, there but not of its kind is not.
    private static bool Optional(JsonElement e, string key, Func<JsonElement, string?> read, out string? value) { value = SwiftJson.Has(e, key, out var v) ? read(v) : null; return value is not null || !SwiftJson.Has(e, key, out _); }
    private static bool Optional<T>(JsonElement e, string key, Func<JsonElement, T?> read, out T? value) where T : struct { value = SwiftJson.Has(e, key, out var v) ? read(v) : null; return value is not null || !SwiftJson.Has(e, key, out _); }

    // Colours.

    /// Colour `index`, or null for none: 0, or an index out of range from a newer Search.
    public static Rgb? ColourAt(int index) => index > 0 && index < Colours.Count ? Colours[index] : null;

    /// The wash's opacity for colour `colour` at Subtle, or null for none. Null draws nothing at all.
    public static double? Wash(int? colour, bool dark) => colour is { } c && ColourAt(c) is not null ? dark ? WashDark : WashLight : null;

    /// Colour `colour` as `strength` washes with it (SpaceStrength.Tone): the colour itself at Subtle, so that wash is
    /// exactly as it always was. Null (clear) for an index out of range.
    public static Rgb? Toned(int colour, SpaceStrength strength, bool dark)
    {
        if (colour < 0 || colour >= Colours.Count) return null;
        return strength == SpaceStrength.Subtle ? Colours[colour] : strength.Tone(OkLab.FromRgb(Colours[colour]), dark).Srgb;
    }

    // The settings notes: a flag is "true" or "false", a list is a JSON array of strings.

    internal static bool Flag(IDictionary<string, string> settings, string key) => settings.TryGetValue(key, out var text) && text is "true" or "1";
    internal static void Flag(IDictionary<string, string> settings, string key, bool on) => settings[key] = on ? "true" : "false";

    internal static List<string> Strings(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().Select(SwiftJson.Text).OfType<string>().ToList() : [];
        }
        catch (JsonException) { return []; }
    }

    internal static void SetStrings(IDictionary<string, string> settings, string key, IEnumerable<string> values) =>
        settings[key] = System.Text.Encoding.UTF8.GetString(SwiftJson.Encode(w => { w.WriteStartArray(); foreach (var v in values) w.WriteStringValue(v); w.WriteEndArray(); }));
}
