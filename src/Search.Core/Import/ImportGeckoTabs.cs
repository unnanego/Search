using System.Text;
using System.Text.Json;

namespace Search.Core;

// The tabs a Firefox, Zen or other Gecko browser has open, and its tab groups (ImportGeckoTabs.swift, and the session
// store reading in ImportZen.swift): sessionstore-backups\recovery.jsonlz4 while it runs, sessionstore.jsonlz4 once it
// has quit, whichever is newer. Each normal window's tabs in order, a tab's page the entry it was on, groups by name
// and whether they were folded, into the same snapshot Chromium's are read into (ImportSession.cs).
//
// A .jsonlz4 is Mozilla's own: "mozLz40\0", the size unpacked as four little-endian bytes, then one raw LZ4 block,
// unpacked here by hand (MozLz4.Unpack). Capped before unpacking (the checked read's 64 MB) and after (160 MB), and an
// unpacking that doesn't come to exactly the size the header says is a damaged file, not a session.

public static class MozLz4
{
    private static readonly byte[] Magic = "mozLz40\0"u8.ToArray();
    /// No session is anywhere near this unpacked; a header claiming more is a damaged one.
    public const int Limit = 160 << 20;

    /// What a .jsonlz4 holds, or null when it isn't one or doesn't unpack to the size it says.
    public static byte[]? Decode(ReadOnlySpan<byte> data, int limit = Limit)
    {
        if (data.Length <= 12 || !data[..8].SequenceEqual(Magic)) return null;
        var size = (long)data[8] | (long)data[9] << 8 | (long)data[10] << 16 | (long)data[11] << 24;
        return size > 0 && size <= limit ? Unpack(data[12..], (int)size) : null;
    }

    /// One raw LZ4 block, unpacked into exactly `size` bytes: sequences of a token (literals' length over the match's),
    /// the literals, then a two-byte offset back and the match, the last sequence literals only. Null for anything
    /// that would read or write past an end.
    public static byte[]? Unpack(ReadOnlySpan<byte> source, int size)
    {
        var target = new byte[size];
        int from = 0, to = 0;
        while (from < source.Length)
        {
            int token = source[from++];
            if (Length(source, ref from, token >> 4, size) is not { } literals || literals > source.Length - from || literals > size - to) return null;
            source.Slice(from, literals).CopyTo(target.AsSpan(to));
            from += literals;
            to += literals;
            if (from == source.Length) break;
            if (source.Length - from < 2) return null;
            var offset = source[from] | source[from + 1] << 8;
            from += 2;
            if (offset == 0 || offset > to || Length(source, ref from, token & 15, size) is not { } match || (match += 4) > size - to) return null;
            // Byte by byte: a match may overlap what it is copying, which is how LZ4 writes a run.
            for (var i = 0; i < match; i++) target[to + i] = target[to - offset + i];
            to += match;
        }
        return to == size ? target : null;
    }

    /// A length from its four bits, and the bytes of 255 that follow fifteen.
    private static int? Length(ReadOnlySpan<byte> source, ref int from, int start, int most)
    {
        var length = start;
        if (start < 15) return length;
        byte next;
        do
        {
            if (from >= source.Length) return null;
            next = source[from++];
            length += next;
            if (length > most) return null;
        } while (next == 255);
        return length;
    }

    /// A .jsonlz4 file inside `inside`, read and unpacked as JSON; null for one that isn't.
    public static JsonDocument? Json(string file, string inside) =>
        ImportRead.TryData(file, inside) is { } packed && Decode(packed) is { } data ? ImportSource.Json(data) : null;
}

/// A Gecko browser's session store: what it has open.
public static class MozillaSession
{
    /// The newest session store in a profile, read; null when there is none.
    public static JsonDocument? Store(string folder)
    {
        var candidates = new[] { Path.Combine(folder, "sessionstore-backups", "recovery.jsonlz4"), Path.Combine(folder, "sessionstore.jsonlz4"), Path.Combine(folder, "sessionstore-backups", "recovery.baklz4") }
            .Select(f => new FileInfo(f)).Where(f => f.Exists && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var file in candidates) if (MozLz4.Json(file.FullName, folder) is { } top) return top;
        return null;
    }

    public static bool Normal(JsonElement window) => !ImportSource.Flag(window, "isPopup") && !ImportSource.Flag(window, "isPrivate") && !ImportSource.Flag(window, "isTaskbarTab");

    /// Every normal window's tabs. `leavingPins` leaves pinned tabs and Essentials behind, for Zen, whose pins come
    /// with its spaces. `salt` keeps two profiles' groups apart.
    public static SessionSnapshot Read(JsonElement top, bool leavingPins, string salt = "")
    {
        var snapshot = new SessionSnapshot();
        var unnamed = 0;
        var number = -1;
        foreach (var window in ImportSource.Objects(ImportSource.Get(top, "windows")))
        {
            number++;
            if (!Normal(window)) continue;
            var groups = new Dictionary<string, ImportedGroup>();
            foreach (var raw in ImportSource.Objects(ImportSource.Get(window, "groups")))
            {
                // Zen's folders and split views are groups too: a folder's tabs are pinned, a split view's side by side.
                if (ImportSource.Text(raw, "id") is not { } id || ImportSource.Flag(raw, "splitView")) continue;
                groups[id] = new ImportedGroup(ImportSource.Text(raw, "name") ?? "", ImportSource.Flag(raw, "collapsed"));
            }
            var tabs = new List<ImportedTab>();
            foreach (var tab in ImportSource.Objects(ImportSource.Get(window, "tabs")))
            {
                var pinned = ImportSource.Flag(tab, "pinned") || ImportSource.Flag(tab, "zenEssential") || ImportSource.Text(tab, "zenEssential") == "true";
                if (leavingPins && pinned) continue;
                if (ImportSource.Flag(tab, "zenIsGlance") || ImportSource.Flag(tab, "zenIsEmpty")) continue;
                var entries = ImportSource.Objects(ImportSource.Get(tab, "entries")).ToList();
                if (entries.Count == 0) continue;
                var index = SwiftJson.Int(ImportSource.Get(tab, "index")) is { } i ? Math.Clamp(i, 1, entries.Count) - 1 : entries.Count - 1;
                if (ImportUrl.Page(ImportSource.Text(entries[index], "url")) is not { } url) continue;
                GroupToken? token = null;
                if (!pinned && ImportSource.Text(tab, "groupId") is { } groupId && groups.TryGetValue(groupId, out var group))
                {
                    var made = new GroupToken(Fnv($"{salt}/{number}"), Fnv(groupId));
                    if (!snapshot.Groups.ContainsKey(made))
                        snapshot.Groups[made] = group.Title.Trim().Length == 0 ? new ImportedGroup($"Group {++unnamed}", group.Collapsed) : group with { Title = SessionImport.Titled(group.Title, null) };
                    token = made;
                }
                tabs.Add(new ImportedTab(url, SessionImport.Titled(ImportSource.Text(entries[index], "title") ?? "", url), pinned, token));
            }
            if (tabs.Count > 0) snapshot.Windows.Add(tabs);
        }
        snapshot.Found = snapshot.Tabs;
        return snapshot;
    }

    /// FNV-1a, for a group's token from its id: the same id, the same group.
    internal static ulong Fnv(string text)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text)) hash = unchecked((hash ^ b) * 0x100000001b3UL);
        return hash;
    }
}

public sealed partial class GeckoSource
{
    /// The open tabs and tab groups of one profile, or every one, capped as Chromium's are. Zen's pins stay behind:
    /// they come with its spaces.
    public override SessionSnapshot Sessions(string? profile = null)
    {
        var all = new SessionSnapshot();
        foreach (var folder in Folders(profile))
        {
            using var top = MozillaSession.Store(folder);
            if (top is not null) all.Add(MozillaSession.Read(top.RootElement, KeepsSpaces, Path.GetFileName(folder)));
        }
        return all.Capped();
    }
}
