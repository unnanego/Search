using System.Globalization;
using System.Text.Json;

namespace Search.Core;

// Profiles (Profiles.swift, the model): whole separate browsers in one app, as in Chrome and Arc. A profile holds its
// own spaces (and so their tabs, pins, groups and session files), its own cookies and sign-ins, its own history,
// archive and bookmarks. A window belongs to one profile for its whole life: the profile of the space it shows.
//
// There is always one, Personal: everything there was before profiles, left where it was, so nobody's data moves. It
// can't be removed, only renamed; it comes first unless moved, the order being profiles.json's own. Shared by every
// profile, on purpose: the settings, the extensions, the passwords, downloads and the icons of sites.
//
// Nothing of a profile lives in a folder of its own; on the Mac each kind of file is suffixed with the profile's id
// beside Personal's, in the one store folder, and so it is here:
//   profiles.json                   [{"id","name","home"}], in their order; no file while Personal is the only
//                                   profile, is called Personal and is at home in the first space
//   spaces.json                     every profile's spaces, each other profile's marked "profile": <id>
//   session-<space>.json            a space's tabs, groups and splits (Personal's first space: session.json)
//   pins.json                       pins by space, and the ones every space shows under "shared:<id>"
//   history-<id>.json               its history (Personal: history.json)
//   bookmarks-<id>.json             its bookmarks (Personal: bookmarks.json)
//   archive-<id>.json               the tabs it closed by itself (Personal: archive.json)
//   sync-profile-<id>.json          what Sync last agreed for it (Search Sync/profile-<id>/ in the folder)
// Its cookies are WebView2's profile named by its id (the Mac's WKWebsiteDataStore(forIdentifier: id)): uppercase,
// as Spaces.StoreName has every store, beside the spaces with sign-ins of their own, which are named by theirs.
// Profiles have no colour or icon on the Mac: the badge is the first letter of the name.

/// One profile. `Home` is its first space: what a window of the profile shows with spaces off. Any space can be deleted
/// while the profile keeps another; the home's going makes the next one in the order the home (Personal's too, kept in
/// profiles.json, which an older Search reads back as the first space).
public sealed record Profile(Guid Id, string Name, Guid Home)
{
    public static readonly Guid PersonalId = Spaces.PersonalProfile;
    public static Profile Personal { get; } = new(PersonalId, "Personal", Space.FirstId);

    public bool IsPersonal => Id == PersonalId;
    /// The badge: the name's first letter, uppercased.
    public string Letter => Name.Length == 0 ? "" : StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    /// Its cookies and sign-ins: WebView2's default profile for Personal, the profile's id for any other.
    public string? StoreName => Spaces.ProfileStoreName(Id);
    public string HistoryFile => Spaces.ProfileHistoryFile(Id);
    public string BookmarksFile => Bookmarks.FileFor(Id);
    public string ArchiveFile => Archive.FileName(Id);
    public string SyncScope => Sync.Scope(Id);
}

public sealed class Profiles : Observable
{
    private readonly string _fileName;
    private readonly Dictionary<Guid, History> _histories = [];
    private readonly Dictionary<Guid, Bookmarks> _bookmarks = [];
    private IReadOnlyList<Profile> _list;

    public Profiles(string file = "profiles.json") { _fileName = file; _list = Read(); }

    /// Every profile, in the order the user put them: Personal first unless moved. The order is profiles.json's own.
    public IReadOnlyList<Profile> List => _list;
    /// More than one: only then does anything about profiles show.
    public bool Several => _list.Count > 1;
    public Profile Get(Guid id) => _list.FirstOrDefault(p => p.Id == id) ?? _list.First(p => p.IsPersonal);
    public bool Has(Guid id) => _list.Any(p => p.Id == id);
    /// A profile's home space, for Spaces (its `homes`): null for one this list doesn't have.
    public Guid? HomeOf(Guid id) => _list.FirstOrDefault(p => p.Id == id)?.Home;

    // What each holds, made once.

    /// Its history: Personal's in history.json, as always; another's in a file of its own.
    public History History(Guid id) => _histories.TryGetValue(id, out var made) ? made : _histories[id] = new History(Spaces.ProfileHistoryFile(id));

    /// Its bookmarks. `settings`: where the Mac's two small bookmark notes live (see Bookmarks), the same for all.
    public Bookmarks Bookmarks(Guid id, IDictionary<string, string>? settings = null) =>
        _bookmarks.TryGetValue(id, out var made) ? made : _bookmarks[id] = new Bookmarks(Search.Core.Bookmarks.FileFor(id), settings);

    /// Quitting: every history made so far, written now.
    public void FlushHistories() { foreach (var history in _histories.Values) history.Flush(); }

    // Making, renaming, deleting.

    /// A new profile and its first space, which shares the profile's own store: signed out of everything, with
    /// nothing in its history. `home`: the space it starts with, for one made by an import. Null for an empty name.
    public Profile? Add(string name, Spaces spaces, Space? home = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        var id = Guid.NewGuid();
        var space = (home ?? new Space(Guid.NewGuid(), trimmed) { Icon = "house" }) with { Profile = id, SharesSignIns = true };
        var made = new Profile(id, trimmed, space.Id);
        _list = [.. _list, made];
        Write();
        Changed(nameof(List));
        spaces.Write([.. spaces.All, space]);
        return made;
    }

    /// Its home deleted (Spaces.Remove): the space that takes its place.
    public void Rehome(Guid id, Guid space)
    {
        var at = _list.ToList().FindIndex(p => p.Id == id);
        if (at < 0 || _list[at].Home == space) return;
        _list = [.. _list.Select((p, i) => i == at ? p with { Home = space } : p)];
        Write();
        Changed(nameof(List));
    }

    public bool Rename(Guid id, string name)
    {
        var trimmed = name.Trim();
        var at = _list.ToList().FindIndex(p => p.Id == id);
        if (trimmed.Length == 0 || at < 0 || _list[at].Name == trimmed) return false;
        _list = [.. _list.Select((p, i) => i == at ? p with { Name = trimmed } : p)];
        Write();
        Changed(nameof(List));
        return true;
    }

    /// A profile and all it holds, gone from this machine: its spaces with their tabs and pins, its store and the
    /// stores of its spaces with sign-ins of their own (noted for erasing, Spaces.Erasing), its history, archive and
    /// bookmarks. Personal stays. The app closes the profile's windows first (opening one of Personal's if no other
    /// would be left), and forgets the downloads its stores asked for.
    public bool Delete(Guid id, Spaces spaces, Pins pins, PinFolders? folders = null)
    {
        if (id == Profile.PersonalId || !Has(id)) return false;
        foreach (var space in spaces.RemoveProfile(id)) pins.Forget(space.Id, folders);
        pins.ForgetShared(id);
        if (_histories.Remove(id, out var history)) history.Retire(); else Remove(Spaces.ProfileHistoryFile(id));
        if (_bookmarks.Remove(id, out var bookmarks)) bookmarks.Retire(); else Remove(Search.Core.Bookmarks.FileFor(id));
        Archive.Forget(id);
        _list = [.. _list.Where(p => p.Id != id)];
        Write();
        Changed(nameof(List));
        return true;
    }

    /// Through the queue, so a write still on its way can't bring the file back after it.
    private static void Remove(string name)
    {
        var file = Store.File(name);
        Disk.Write(file, () => { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } return null; });
    }

    // The file.

    private string FilePath => Store.File(_fileName);

    private List<Profile> Read()
    {
        byte[]? data = null;
        try { data = File.ReadAllBytes(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var saved = data is null ? [] : Decode(data) ?? Quarantined();
        // Where Personal stands in the file is where it was moved to, with the home it has now; a file without it (only
        // ever written by hand) has it first, at home in the first space.
        var at = saved.FindIndex(p => p.IsPersonal);
        var list = saved.Where(p => !p.IsPersonal).ToList();
        list.Insert(Math.Max(at, 0), at >= 0 ? Profile.Personal with { Name = saved[at].Name, Home = saved[at].Home } : Profile.Personal);
        return list;
    }

    private List<Profile> Quarantined() { Store.Quarantine(FilePath); return []; }

    /// A list with only Personal, named as it always was, is no file at all.
    private void Write()
    {
        var snapshot = _list.ToList();
        var file = FilePath;
        if (snapshot is [var only] && only == Profile.Personal) { Remove(_fileName); return; }
        Disk.Write(file, () => Encode(snapshot));
    }

    public static byte[] Encode(IEnumerable<Profile> profiles) => SwiftJson.Encode(w =>
    {
        w.WriteStartArray();
        foreach (var p in profiles)
        {
            w.WriteStartObject();
            SwiftJson.WriteUuid(w, "id", p.Id);
            w.WriteString("name", p.Name);
            SwiftJson.WriteUuid(w, "home", p.Home);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// Null when the file won't decode, as Swift's decoder sees it: every profile needs its id, name and home.
    public static List<Profile>? Decode(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var made = new List<Profile>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var i) || SwiftJson.Uuid(i) is not { } id || !e.TryGetProperty("name", out var n)
                    || SwiftJson.Text(n) is not { } name || !e.TryGetProperty("home", out var h) || SwiftJson.Uuid(h) is not { } home) return null;
                made.Add(new Profile(id, name, home));
            }
            return made;
        }
        catch (JsonException) { return null; }
    }
}
