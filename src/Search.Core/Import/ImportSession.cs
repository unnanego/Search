using System.Buffers.Binary;
using System.Text;

namespace Search.Core;

// The tabs a Chromium browser has open, and their tab groups (ImportSession.swift), read from the newest "Session_*"
// in a profile's Sessions folder, or the older "Current Session" beside it. The file is read whole into memory
// through the same checked, capped read every import uses, so a browser still writing it is never touched.
//
// A session file is SNSS: "SNSS", a version, then frames (a size, a command, the command's bytes) appended as the
// browser goes. Only the commands that say what is open are read; any other is passed over by its size. A torn frame
// at the end ends the read; a file that isn't SNSS, or is encrypted, has nothing to bring. Recently closed tabs and
// saved tab groups (kept in the sync store) stay behind. Gecko's sessions come into the same snapshot (see
// ImportGeckoTabs.cs) and go in the same way (ImportTake.Session).

/// A tab group's id in the file: Chromium's random 128-bit token, or one made from Gecko's group id.
public readonly record struct GroupToken(ulong High, ulong Low);

public sealed record ImportedTab(Uri Url, string Title, bool Pinned, GroupToken? Group = null);

public sealed record ImportedGroup(string Title, bool Collapsed);

/// Each open normal window's tabs in the order they stand, and the groups those tabs are in, by token.
public sealed class SessionSnapshot
{
    public List<List<ImportedTab>> Windows { get; init; } = [];
    public Dictionary<GroupToken, ImportedGroup> Groups { get; init; } = [];
    /// How many there were before the limit, when more than it.
    public int Found { get; set; }
    public int Tabs => Windows.Sum(w => w.Count);

    /// No more than `limit` tabs, window by window, and only the groups those still hold.
    public SessionSnapshot Capped(int limit = SessionImport.Limit)
    {
        var capped = new SessionSnapshot { Found = Math.Max(Found, Tabs) };
        var room = limit;
        foreach (var window in Windows)
        {
            if (room <= 0) break;
            var kept = window.Take(room).ToList();
            room -= kept.Count;
            capped.Windows.Add(kept);
        }
        foreach (var token in capped.Windows.SelectMany(w => w).Select(t => t.Group).OfType<GroupToken>().Distinct())
            if (Groups.TryGetValue(token, out var group)) capped.Groups[token] = group;
        return capped;
    }

    /// Another profile's added: windows after these, groups kept first come.
    public void Add(SessionSnapshot other)
    {
        Windows.AddRange(other.Windows);
        Found += other.Found;
        foreach (var (token, group) in other.Groups) Groups.TryAdd(token, group);
    }
}

public static class SessionImport
{
    /// At most this many tabs come over, so a session of thousands doesn't make thousands of tabs at once.
    public const int Limit = 300;

    // The commands that say what is open (components/sessions in Chromium).
    private const byte Window = 0, Index = 2, Navigation = 6, SelectedNavigation = 7, WindowType = 9, Pinned = 12, TabClosed = 16, WindowClosed = 17, Pruned = 24, Group = 25, GroupMetadata = 27;

    public static SessionSnapshot Read(byte[] data)
    {
        var file = new Cursor(data);
        // The magic, then a version this reads: 1, or 3. Anything else, an encrypted file among them, is none to bring.
        if (file.Bytes(4) is not { } magic || !magic.AsSpan().SequenceEqual("SNSS"u8) || file.I32() is not (1 or 3)) return new SessionSnapshot();

        var windowOf = new Dictionary<int, int>();
        var windowTabs = new Dictionary<int, HashSet<int>>();
        var windowOrder = new List<int>();
        var windowType = new Dictionary<int, int>();
        var tabIndex = new Dictionary<int, int>();
        var tabSeen = new Dictionary<int, int>();
        var entries = new Dictionary<int, Dictionary<int, (string Url, string Title)>>();
        var selected = new Dictionary<int, int>();
        var pinned = new HashSet<int>();
        var tabGroup = new Dictionary<int, GroupToken>();
        var meta = new Dictionary<GroupToken, ImportedGroup>();

        void Note(int tab) => tabSeen.TryAdd(tab, tabSeen.Count);
        void NoteWindow(int window) { if (!windowOrder.Contains(window)) windowOrder.Add(window); }
        void Close(int tab)
        {
            if (windowOf.Remove(tab, out var window) && windowTabs.TryGetValue(window, out var set)) set.Remove(tab);
            tabIndex.Remove(tab); entries.Remove(tab); selected.Remove(tab); pinned.Remove(tab); tabGroup.Remove(tab);
        }

        while (file.Left > 0)
        {
            if (file.U16() is not { } size || size < 1 || file.Left < size || file.U8() is not { } id || file.Bytes(size - 1) is not { } body) break;
            var frame = new Cursor(body);
            switch (id)
            {
                case Window when frame.I32() is { } window && frame.I32() is { } tab:
                    if (windowOf.TryGetValue(tab, out var old) && windowTabs.TryGetValue(old, out var was)) was.Remove(tab);
                    windowOf[tab] = window;
                    (windowTabs.TryGetValue(window, out var set) ? set : windowTabs[window] = []).Add(tab);
                    NoteWindow(window);
                    Note(tab);
                    break;
                case Index when frame.I32() is { } tab && frame.I32() is { } index:
                    tabIndex[tab] = index;
                    Note(tab);
                    break;
                case Navigation:
                    // A pickle: its size, the tab, the entry's index, its address, its title, then the page's own state.
                    frame.Skip(4);
                    if (frame.I32() is { } navTab && frame.I32() is { } at && frame.Utf8() is { } url && frame.Utf16() is { } title)
                    {
                        (entries.TryGetValue(navTab, out var navs) ? navs : entries[navTab] = [])[at] = (url, title);
                        Note(navTab);
                    }
                    break;
                case SelectedNavigation when frame.I32() is { } tab && frame.I32() is { } index:
                    selected[tab] = index;
                    Note(tab);
                    break;
                case WindowType when frame.I32() is { } window && frame.I32() is { } type:
                    windowType[window] = type;
                    NoteWindow(window);
                    break;
                case Pinned when frame.I32() is { } tab && frame.U8() is { } state:
                    if (state != 0) pinned.Add(tab); else pinned.Remove(tab);
                    Note(tab);
                    break;
                case TabClosed when frame.I32() is { } tab:
                    Close(tab);
                    break;
                case WindowClosed when frame.I32() is { } window:
                    windowType.Remove(window);
                    if (windowTabs.Remove(window, out var gone)) foreach (var tab in gone) Close(tab);
                    break;
                case Pruned when frame.I32() is { } tab && frame.I32() is { } index && frame.I32() is { } count && index >= 0 && count > 0 && entries.TryGetValue(tab, out var navs):
                    // Entries [index, index + count) are gone and the ones after move down by count, as Chromium does.
                    long from = index, end = (long)index + count;
                    int? Moved(long at) => at < from ? (int)at : at >= end ? (int)(at - count) : null;
                    var kept = new Dictionary<int, (string, string)>();
                    foreach (var (spot, entry) in navs) if (Moved(spot) is { } to) kept[to] = entry;
                    entries[tab] = kept;
                    if (selected.TryGetValue(tab, out var current))
                    {
                        if ((Moved(current) ?? (index > 0 ? index - 1 : null)) is { } now) selected[tab] = now; else selected.Remove(tab);
                    }
                    break;
                case Group when frame.I32() is { } tab:
                    // A raw struct: the tab, padding for the token's alignment, the token, whether it is in a group at all.
                    frame.Skip(4);
                    if (frame.U64() is { } high && frame.U64() is { } low)
                    {
                        if (frame.U8() is null or 0) tabGroup.Remove(tab); else tabGroup[tab] = new GroupToken(high, low);
                    }
                    Note(tab);
                    break;
                case GroupMetadata:
                    // A pickle: its size, the token, the name, the colour, then (in newer files) whether it is folded.
                    frame.Skip(4);
                    if (frame.U64() is { } h && frame.U64() is { } l && frame.Utf16() is { } name)
                    {
                        frame.Skip(4);
                        meta[new GroupToken(h, l)] = new ImportedGroup(name, frame.I32() is { } folded && folded != 0);
                    }
                    break;
            }
        }

        var snapshot = new SessionSnapshot();
        foreach (var window in windowOrder)
        {
            // Popups, apps and devtools aren't tabs to keep. A window never given a type is a normal one.
            if (windowType.TryGetValue(window, out var type) && type != 0) continue;
            var tabs = (windowTabs.GetValueOrDefault(window) ?? [])
                .OrderBy(t => tabIndex.TryGetValue(t, out var i) ? i : int.MaxValue).ThenBy(t => tabSeen.GetValueOrDefault(t))
                .Select(tab =>
                {
                    if (!entries.TryGetValue(tab, out var navs) || navs.Count == 0) return null;
                    var at = selected.TryGetValue(tab, out var s) && navs.ContainsKey(s) ? s : navs.Keys.Max();
                    if (ImportUrl.Page(navs[at].Url) is not { } url) return null;
                    // A pin is never in a group.
                    var pin = pinned.Contains(tab);
                    return new ImportedTab(url, Titled(navs[at].Title, url), pin, pin ? null : tabGroup.TryGetValue(tab, out var g) ? g : null);
                })
                .OfType<ImportedTab>().ToList();
            if (tabs.Count > 0) snapshot.Windows.Add(tabs);
        }
        // Only groups that still have tabs; one never named gets a plain name.
        var unnamed = 0;
        foreach (var token in snapshot.Windows.SelectMany(w => w).Select(t => t.Group).OfType<GroupToken>())
        {
            if (snapshot.Groups.ContainsKey(token)) continue;
            if (meta.TryGetValue(token, out var found) && found.Title.Trim().Length > 0) snapshot.Groups[token] = found with { Title = Titled(found.Title, null) };
            else snapshot.Groups[token] = new ImportedGroup($"Group {++unnamed}", meta.TryGetValue(token, out var m) && m.Collapsed);
        }
        snapshot.Found = snapshot.Tabs;
        return snapshot;
    }

    /// A title fit to keep: trimmed, capped, never empty.
    public static string Titled(string title, Uri? url) => ImportUrl.Named(title) ?? (url is null ? "Untitled" : url.Host.Length > 0 ? url.Host : url.AbsoluteUri);

    /// Bytes a frame at a time, little-endian; a read past the end is null, never a throw.
    private struct Cursor(byte[] data)
    {
        private int _at;

        public readonly int Left => data.Length - _at;

        public byte[]? Bytes(int count)
        {
            if (count < 0 || Left < count) return null;
            var bytes = data.AsSpan(_at, count).ToArray();
            _at += count;
            return bytes;
        }

        public void Skip(int count) => _at += Math.Min(Math.Max(count, 0), Left);
        public byte? U8() => Left >= 1 ? data[_at++] : null;
        public int? U16() { if (Left < 2) return null; var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_at)); _at += 2; return v; }
        public int? I32() { if (Left < 4) return null; var v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_at)); _at += 4; return v; }
        public ulong? U64() { if (Left < 8) return null; var v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_at)); _at += 8; return v; }

        /// A pickle string: a byte count, the bytes, padding to four.
        public string? Utf8()
        {
            if (I32() is not { } count || count < 0 || Bytes(count) is not { } bytes) return null;
            Skip((4 - count % 4) % 4);
            return Encoding.UTF8.GetString(bytes);
        }

        /// A pickle string16: a unit count, UTF-16LE, padding to four. A lone surrogate becomes the replacement character.
        public string? Utf16()
        {
            if (I32() is not { } count || count < 0 || (long)count * 2 > Left || Bytes(count * 2) is not { } bytes) return null;
            Skip((4 - count * 2 % 4) % 4);
            return Encoding.Unicode.GetString(bytes);
        }
    }
}

public sealed partial class ChromiumSource
{
    /// The open tabs and tab groups of one profile, or of every one, from the newest session file in each, capped.
    public override SessionSnapshot Sessions(string? profile = null)
    {
        var all = new SessionSnapshot();
        foreach (var folder in Folders(profile))
            if (SessionData(folder) is { } data) all.Add(SessionImport.Read(data));
        return all.Capped();
    }

    /// The newest "Session_*" in Sessions, or "Current Session" in an older profile.
    private byte[]? SessionData(string folder)
    {
        var sessions = Path.Combine(folder, "Sessions");
        if (ImportRead.IsPlainFolder(sessions))
        {
            string[] names = [];
            try { names = Directory.GetFiles(sessions, "Session_*"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var newest = names.Select(f => new FileInfo(f)).Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
            if (newest is not null && ImportRead.TryData(newest.FullName, Root) is { } data) return data;
        }
        return ImportRead.TryData(Path.Combine(folder, "Current Session"), Root);
    }
}
