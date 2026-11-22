namespace Search.Core;

// What was read from another browser, taken into Search's own model (Browser.takeBookmarks, takePlaces, takeArc,
// takeSession and takeAsleep in Browser.swift, PinTree.fromArc in PinFolders.swift). Nothing twice: a bookmark already
// at its level, a page already in a row, or a pin already there isn't added again, so bringing the same browser in a
// second time brings only what is new.
//
// Pages that come in as tabs come asleep, at the end of a space's row: wherever that row is kept is the app's to say
// (the window on screen, one set aside, or the space's session file), through ImportTarget.Asleep; IntoBrowser,
// IntoShape and IntoSession do each. Passwords aren't taken here: they are the vault's (FoundLogins, CsvLogins).

/// A page to put asleep at the end of a row, in the tab group of that name when it has one (made if missing).
public sealed record AsleepPage(Uri Url, string Title, string? Group = null, bool Collapsed = false);

public sealed record BookmarksTaken(int Added, int Already, bool Kept)
{
    /// What the panel says of it.
    public string Said(string name) => Kept ? $"Couldn't read all of {name}'s bookmarks: what came from it before was kept, {Added} new"
        : Added == 0 && Already == 0 ? $"No bookmarks in {name}" : Already == 0 ? $"{Added} bookmarks" : $"{Added} new bookmarks, {Already} already here";
}

/// `Brought`: every address made a pin or a tab, for their icons (ImportSource.Icons).
public sealed record SidebarTaken(int Spaces, int Pins, int Tabs, IReadOnlyList<Uri> Brought);

public sealed record TabsTaken(int Pins, int Tabs, int Groups, IReadOnlyList<Uri> Brought);

/// Where an import lands: the window's profile and the space on screen, the stores it changes, and the two pinned-row
/// settings that decide how a pinned list comes in. `ProfileFor`: the Search profile for one of Arc's profile folders
/// ("Profile 1"); every one is this window's profile unless told. `Asleep`: puts pages at the end of a space's row and
/// says how many tabs and new groups that made; the space's session file unless told (IntoSession).
public sealed record ImportTarget(Spaces Spaces, Pins Pins, PinFolders Folders, Guid Profile, Guid Space)
{
    public bool ListsPins { get; init; }
    public bool PinFolders { get; init; }
    public Func<string, Guid?>? ProfileFor { get; init; }
    public Func<Guid, IReadOnlyList<AsleepPage>, (int Tabs, int Groups)> Asleep { get; init; } = ImportTake.IntoSession;
    /// Passed on with Pins.Changed, so the window that made the change knows it was its own.
    public object? From { get; init; }
}

public static class ImportTake
{
    /// Another browser's bookmarks, merged in a folder of its name (or at the top for the first browser), as
    /// Bookmarks.TakeNoting does. `replacing`: what came from it before, as recorded, goes first; only when every
    /// profile read cleanly, since a file that didn't read is not an empty browser (`Kept` then says so).
    public static BookmarksTaken Bookmarks(Bookmarks marks, ImportRecords records, string name, BookmarkRead read, bool replacing = false)
    {
        var kept = false;
        if (replacing && records.Of(name) is { BookmarkIds.Count: > 0 } earlier)
        {
            if (read.Complete) { marks.Withdraw(earlier.BookmarkIds); records.ForgetBookmarks(name); }
            else kept = true;
        }
        var (added, already, ids) = marks.TakeNoting(read.Nodes, name);
        records.Note(name, ids, added);
        return new(added, already, kept);
    }

    /// The other browser's history, into this profile's: counted as it was counted there. Returns how many places.
    public static int Places(History history, IReadOnlyList<ImportedPlace> places, ImportRecords? records = null, string? name = null)
    {
        foreach (var place in places) history.Take(place.Url, place.Title, place.Count, place.Last);
        history.Settle();
        if (records is not null && name is not null) records.Note(name, places: places.Count);
        return places.Count;
    }

    /// Arc's sidebar, or Zen's spaces read into its shape. Each of its spaces becomes a space here (the one of the same
    /// name if there is one), its pinned list that space's, folders as they fit; the favourites (Arc) or Essentials
    /// (Zen) become the pins of every space of their profile. A lone space, with spaces off, comes into the space on
    /// screen; more than one turns spaces on.
    ///
    /// The pinned list is split the way Arc holds it. With pinned rows on, a page it keeps loose is a row, and one it
    /// filed in a folder stays a tab in a group of that folder's name, because a pin is never in a group and the
    /// folder is what would be lost. With folders in pinned rows on as well, its folders come as folders of rows and
    /// every page is a row. With rows off, the whole list comes in as tabs, asleep.
    public static SidebarTaken Sidebar(ArcSidebar sidebar, ImportTarget target)
    {
        int made = 0, pins = 0, added = 0;
        var brought = new List<Uri>();
        Guid ProfileOf(ArcSpace arc) => arc.Profile == "Default" ? target.Profile : target.ProfileFor?.Invoke(arc.Profile) ?? target.Profile;
        var targets = new List<(Guid Id, ArcSpace? Space)>();
        if (sidebar.Spaces.Count > 1 || (target.Spaces.UsesSpaces && sidebar.Spaces.Count > 0))
        {
            target.Spaces.UsesSpaces = true;
            foreach (var arc in sidebar.Spaces)
            {
                var profile = ProfileOf(arc);
                var mine = target.Spaces.Of(profile);
                if (mine.FirstOrDefault(s => string.Equals(s.Name, arc.Name, StringComparison.OrdinalIgnoreCase)) is { } same) { targets.Add((same.Id, arc)); continue; }
                var space = new Space(Guid.NewGuid(), arc.Name, arc.Colour) { Icon = arc.Symbol, SharesSignIns = true, Gradient = arc.Gradient is { Usable: true } g ? g : null };
                target.Spaces.Write([.. mine, space], profile);
                targets.Add((space.Id, arc));
                made++;
            }
        }
        // A lone space of another profile: that profile's first space.
        else if (sidebar.Spaces.FirstOrDefault() is { } lone && ProfileOf(lone) is var other && other != target.Profile) targets.Add((target.Spaces.Home(other), lone));
        else targets.Add((target.Space, sidebar.Spaces.FirstOrDefault()));

        var folded = target.ListsPins && target.PinFolders;
        foreach (var (id, arc) in targets)
        {
            var defs = target.Pins.Defs(id).ToList();
            // The pins it had before any of this, for telling one already here from one this import is adding: two
            // favourites of Arc's own on one site are two favourites, and stay two.
            var had = defs.ToList();
            bool Fresh(Uri url) => !had.Any(p => SamePin(p, url)) && !defs.Any(p => p.Home == url.AbsoluteUri);
            var favourites = arc is not null && sidebar.FavoritesByProfile.TryGetValue(arc.Profile, out var own) ? own : sidebar.Favorites;
            foreach (var favourite in favourites.Where(f => Fresh(f.Url)))
            {
                defs.Add(new PinDef(Guid.NewGuid(), Pins.Monogram(favourite.Url), favourite.Url.AbsoluteUri, favourite.Title));
                pins++;
                brought.Add(favourite.Url);
            }
            var nodes = arc?.Pinned ?? [];
            var pinned = Opened(nodes);
            brought.AddRange(pinned.Select(p => p.Item.Url));
            (IReadOnlyList<PinFolder> Folders, List<(ArcItem Item, Guid? Folder)> Pages)? filed = folded ? Filed(nodes, target.Folders.Folders(id)) : null;
            var rows = filed?.Pages ?? (target.ListsPins ? pinned.Where(p => p.Folder is null).Select(p => (p.Item, (Guid?)null)).ToList() : []);
            foreach (var (item, folder) in rows.Where(r => Fresh(r.Item.Url)))
            {
                defs.Add(new PinDef(Guid.NewGuid(), Pins.Monogram(item.Url), item.Url.AbsoluteUri, item.Title) { Listed = true, Folder = folder });
                pins++;
            }
            if (filed is not null)
            {
                // Before the pins, so the row rebuilt for them finds their folders.
                var before = target.Folders.Folders(id).Select(f => f.Id).ToHashSet();
                target.Folders.Set(id, PinTree.WithoutEmpty(filed.Value.Folders, before, defs), target.From);
            }
            target.Pins.Set(id, defs, target.From);
            var asleep = folded ? [] : rows.Count == 0 ? pinned : pinned.Where(p => p.Folder is not null).ToList();
            if (asleep.Count > 0) added += target.Asleep(id, asleep.Select(p => new AsleepPage(p.Item.Url, p.Item.Title, p.Folder)).ToList()).Tabs;
        }
        return new(made, pins, added, brought);
    }

    /// Another browser's open tabs, from the snapshot read when the panel counted them, into the space on screen: its
    /// pinned tabs as pins, the rest asleep at the end of the row, a grouped tab in the group of that name, made if
    /// missing and folded as it was.
    public static TabsTaken OpenTabs(SessionSnapshot snapshot, ImportTarget target)
    {
        var all = snapshot.Capped().Windows.SelectMany(w => w).ToList();
        var defs = target.Pins.Defs(target.Space).ToList();
        var had = defs.ToList();
        var brought = new List<Uri>();
        foreach (var page in all.Where(p => p.Pinned))
        {
            if (had.Any(p => SamePin(p, page.Url)) || defs.Any(p => p.Home == page.Url.AbsoluteUri)) continue;
            defs.Add(new PinDef(Guid.NewGuid(), Pins.Monogram(page.Url), page.Url.AbsoluteUri, page.Title));
            brought.Add(page.Url);
        }
        var pins = defs.Count - had.Count;
        if (pins > 0) target.Pins.Set(target.Space, defs, target.From);
        var pages = all.Where(p => !p.Pinned).Select(p => p.Group is { } token && snapshot.Groups.TryGetValue(token, out var group)
            ? new AsleepPage(p.Url, p.Title, group.Title, group.Collapsed) : new AsleepPage(p.Url, p.Title)).ToList();
        var (tabs, groups) = pages.Count > 0 ? target.Asleep(target.Space, pages) : (0, 0);
        brought.AddRange(pages.Select(p => p.Url));
        return new(pins, tabs, groups, brought);
    }

    // Where pages asleep go.

    /// Into a space's session file, for a space no window has up: read, added to, written at once.
    public static (int Tabs, int Groups) IntoSession(Guid space, IReadOnlyList<AsleepPage> pages)
    {
        var (shape, tabs, groups) = IntoShape(Session.Read(space), pages);
        if (tabs > 0) Session.Write(shape, now: true, space: space);
        return (tabs, groups);
    }

    /// Into a saved row: each page not already there after the last of its group's tabs, or at the end. The tab in
    /// front stays the one in front.
    public static (Session.Shape Shape, int Tabs, int Groups) IntoShape(Session.Shape shape, IReadOnlyList<AsleepPage> pages)
    {
        var entries = shape.Tabs.ToList();
        var groups = (shape.Groups ?? []).ToList();
        var active = shape.Active;
        var seen = entries.Select(e => Key(e.Url)).ToHashSet();
        int added = 0, made = 0;
        foreach (var page in pages)
        {
            if (!seen.Add(page.Url.AbsoluteUri)) continue;
            Guid? group = null;
            if (page.Group is { } name)
            {
                var found = groups.FirstOrDefault(g => g.Name == name);
                if (found is null) { found = new TabGroup(Guid.NewGuid(), name, page.Collapsed); groups.Add(found); made++; }
                group = found.Id;
            }
            var last = group is { } g ? entries.FindLastIndex(e => e.Pin is null && e.GroupId == g) : -1;
            var at = last >= 0 ? last + 1 : entries.Count;
            entries.Insert(at, new Session.Entry(page.Url.AbsoluteUri, page.Title) { GroupId = group });
            if (at <= active) active++;
            added++;
        }
        return (shape with { Tabs = entries, Active = active, Groups = groups.Count > 0 ? groups : shape.Groups }, added, made);
    }

    /// Into the row a window has on screen: tabs with no page loaded until they are gone to, grouped by name.
    public static (int Tabs, int Groups) IntoBrowser(Browser browser, IReadOnlyList<AsleepPage> pages)
    {
        var seen = browser.Ordinary.Where(t => t.Url is not null).Select(t => t.Url!.AbsoluteUri).ToHashSet();
        int added = 0, made = 0;
        foreach (var page in pages)
        {
            if (!seen.Add(page.Url.AbsoluteUri)) continue;
            var tab = new Tab { Title = page.Title, Store = browser.Store };
            tab.Url = page.Url;
            browser.Insert(tab, browser.Ordinary.Count());
            added++;
            if (page.Group is not { } name) continue;
            if (browser.Groups.FirstOrDefault(g => g.Name == name) is { } group) { browser.Group(tab, group); continue; }
            var fresh = browser.Group(tab, null, name);
            if (page.Collapsed) browser.SetGroup(fresh with { Collapsed = true });
            made++;
        }
        return (added, made);
    }

    // Pins.

    /// Whether a pin is the one a page would make: the same site, and one address inside the other (mail.google.com
    /// and mail.google.com/mail/u/0/), or failing a site to compare, the same address. github.com/me and
    /// github.com/work are two pins, and stay two. A site is its host without www., with its port: localhost:3000 and
    /// localhost:5173 are two sites.
    public static bool SamePin(PinDef pin, Uri url)
    {
        if (!Uri.TryCreate(pin.Home, UriKind.Absolute, out var home) || SiteOf(url) is not { } mine || SiteOf(home) is not { } theirs) return pin.Home == url.AbsoluteUri;
        if (mine != theirs) return false;
        var (a, b) = (FolderOf(url), FolderOf(home));
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    private static string? SiteOf(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Host.Length == 0) return null;
        var host = url.Host.ToLowerInvariant();
        var name = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        return url.IsDefaultPort ? name : $"{name}:{url.Port}";
    }

    /// The path, ending in a slash, so that /me is never taken for the start of /media.
    private static string FolderOf(Uri url) { var path = url.AbsolutePath.ToLowerInvariant(); return path.EndsWith('/') ? path : path + "/"; }

    /// A pinned list with its folders opened out in their order, each page with the top folder it came from.
    internal static List<(ArcItem Item, string? Folder)> Opened(IEnumerable<ArcNode> nodes, string? folder = null)
    {
        var pages = new List<(ArcItem, string?)>();
        foreach (var node in nodes)
            if (node is ArcNode.Page page) pages.Add((page.Item, folder));
            else if (node is ArcNode.Folder inner) pages.AddRange(Opened(inner.Items, folder ?? inner.Title));
        return pages;
    }

    /// A pinned list as pinned rows in folders: each of its folders a folder here, made inside `parent` unless one of
    /// the same name is already there, which is filled instead; deeper than PinTree.MaxDepth, a folder's pages go into
    /// the deepest one that may hold them. The folders as they now are, and each page with the folder it goes in.
    internal static (IReadOnlyList<PinFolder> Folders, List<(ArcItem Item, Guid? Folder)> Pages) Filed(IReadOnlyList<ArcNode> nodes, IReadOnlyList<PinFolder> existing, Guid? parent = null)
    {
        var folders = existing.ToList();
        var pages = new List<(ArcItem, Guid?)>();
        void Take(IEnumerable<ArcNode> list, Guid? within, int depth)
        {
            foreach (var node in list)
            {
                if (node is ArcNode.Page page) { pages.Add((page.Item, within)); continue; }
                if (node is not ArcNode.Folder inner) continue;
                if (depth >= PinTree.MaxDepth) { Take(inner.Items, within, depth); continue; }
                var same = folders.FirstOrDefault(f => f.Parent == within && !f.IsLive && string.Equals(f.Name, inner.Title, StringComparison.OrdinalIgnoreCase));
                var id = same?.Id ?? Guid.NewGuid();
                if (same is null) folders.Add(new PinFolder(id, inner.Title) { Parent = within });
                Take(inner.Items, id, depth + 1);
            }
        }
        Take(nodes, parent, parent is { } p ? PinTree.Depth(p, folders) : 0);
        return (folders, pages);
    }

    private static string Key(string url) => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsoluteUri : url;
}
