using Search.Core;
using Search.Engine;

namespace Search.App;

// What every window shares (Windows.swift's Shared): the settings, the profiles and what each of them holds (its
// history, bookmarks and archive, made once and handed to each of its windows), downloads, the hidden elements and
// site styles, the vault, the spaces and their routes, the pins, the extensions and Sync. Each window has a Browser of
// its own: its tabs, the space it shows, its field and panels. Made once, before the first window.
public sealed class Shared(Prefs prefs)
{
    public Prefs Prefs { get; } = prefs;
    public Profiles Profiles { get; } = new();
    public Hidden Hidden { get; } = new();
    public SiteStyles Styles { get; } = new();
    public Downloads Downloads { get; } = new();
    public Vault Vault { get; } = new();
    public Spaces Spaces => _spaces ??= new Spaces(settings: App.Notes, homes: Profiles.HomeOf, rehome: Profiles.Rehome);
    public SpaceRoutes Routes => _routes ??= new SpaceRoutes(Spaces);
    public ExtensionHost Extensions { get; } = new(App.Notes);
    /// The pins, the same in every window; before there is a pins.json, the oldest window's session files hold them.
    public Pins Pins => _pins ??= new Pins(spaces: Spaces.All.Select(s => s.Id).ToArray());
    /// The folders among the pinned rows (PinFolders.cs).
    public PinFolders Folders => _folders ??= new PinFolders();
    public Sync Sync => _sync ??= new Sync(App.Notes, Prefs, Spaces, Pins, Profiles, bookmarks: BookmarksOf, folders: Folders);
    private PinFolders? _folders;
    private Spaces? _spaces;
    private SpaceRoutes? _routes;
    private Pins? _pins;
    private Sync? _sync;
    private readonly Dictionary<Guid, Archive> _archives = new();

    public History HistoryOf(Guid profile) => Profiles.History(profile);
    public Bookmarks BookmarksOf(Guid profile) => Profiles.Bookmarks(profile, App.Notes);
    public Archive ArchiveOf(Guid profile) => _archives.TryGetValue(profile, out var made) ? made : _archives[profile] = new Archive(Archive.FileName(profile));
}
