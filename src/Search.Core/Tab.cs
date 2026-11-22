namespace Search.Core;

public enum TabCommand { Back, Forward, Reload, ReloadFromOrigin, Stop }

// One page in the row (Tab.swift). The model only: where it is, what it's called, whether it's loading. The web view
// behind it is the engine's, built the first time the tab is shown, so a tab restored from last time costs nothing
// until it's clicked, and let go of again when the tab sleeps.
public sealed class Tab : Observable
{
    private Uri? _url;
    private string _title = "";
    private string? _name;
    private string? _icon;
    private bool _loading, _canGoBack, _canGoForward, _live, _muted;

    public Guid Id { get; } = Guid.NewGuid();
    /// Opened by a script through the bench: never selected for you, never in the session or the history.
    public bool Bench { get; init; }
    /// A bench tab a coding agent opened, or one its pages opened (AgentServer): it stays on the web, downloads nothing,
    /// is asked for nothing, and none of its keys reach the window.
    public bool Agent { get; init; }
    /// Its own cookie jar, and nothing left behind when it closes.
    public bool Private { get; init; }
    /// A peek at a link (Peek.swift): a page over the page, not in the row until it's kept.
    public bool Peek { get; set; }
    private Guid? _pinId, _groupId, _folder;
    private bool _listed;
    private string? _letter;

    /// A pin (Pins.swift): its identity, the letter or icon it wears, the page it was pinned at. Null for a tab.
    public Guid? PinId { get => _pinId; set { if (Set(ref _pinId, value)) Changed(nameof(Pinned)); } }
    public string? Letter { get => _letter; set => Set(ref _letter, value); }
    public Uri? Home { get; set; }
    public bool Pinned => _pinId is not null;
    /// A pin kept as a row under the squares (Prefs.PinRows), not a square.
    public bool Listed { get => _listed; set => Set(ref _listed, value); }
    /// For a pinned row, the folder among the pinned rows that holds it (PinFolders.cs).
    public Guid? Folder { get => _folder; set => Set(ref _folder, value); }
    /// The split a pin was pinned in on the Mac (PinDef.Pair), kept so it isn't lost when this window writes pins.json.
    public Guid? PinPair { get; set; }
    /// The tab group it's in (TabGroup.swift), if any.
    public Guid? GroupId { get => _groupId; set => Set(ref _groupId, value); }
    /// Playing sound right now: a tab that is isn't put to sleep or closed.
    public bool Audible { get; set; }
    /// Where the page was scrolled to when it was put to sleep, to go back to when it wakes.
    public (double X, double Y)? Scroll { get; set; }
    /// Its page's sessionStorage when it was put to sleep, as Sleep.KeepSession has it, until the woken page has it back
    /// (KeptSession.swift). Held in memory only, never written to the session file.
    public string? KeptSession { get; set; }
    /// The WebView2 profile its pages use: its space's own sign-ins (Spaces.StoreName), or null for the default.
    public string? Store { get; init; }
    public DateTimeOffset? Touched { get; set; }
    /// The first 8 hex digits of its id, as the bench names tabs.
    public string ShortId => Id.ToString("N")[..8];

    public Uri? Url { get => _url; set { if (Set(ref _url, value)) Changed(nameof(Shown)); } }
    public string Title { get => _title; set { if (Set(ref _title, value)) Changed(nameof(Shown)); } }
    /// The name you gave the tab, when you gave it one.
    public string? Name { get => _name; set { if (Set(ref _name, value)) Changed(nameof(Shown)); } }
    public string? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool Loading { get => _loading; set => Set(ref _loading, value); }
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; set => Set(ref _canGoForward, value); }
    /// Whether a web view is behind it right now.
    public bool Live { get => _live; set => Set(ref _live, value); }
    public bool Muted { get => _muted; set => Set(ref _muted, value); }

    /// What the row says: your name for it, the page's title, or failing both its address without the parts nobody reads.
    public string Shown => Name is { Length: > 0 } ? Name : Title is { Length: > 0 } ? Title : Url is { } u ? Pretty(u) : "New Tab";

    /// Asked of the engine; a tab without a web view yet keeps the address and is taken there when it's built.
    public event Action<Tab, Uri>? NavigateRequested;
    public event Action<Tab, TabCommand>? CommandRequested;

    public void Go(Uri url) { Url = url; NavigateRequested?.Invoke(this, url); }
    public void Do(TabCommand command) => CommandRequested?.Invoke(this, command);

    private static string Pretty(Uri url)
    {
        if (string.IsNullOrEmpty(url.Host)) return url.OriginalString;
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        return url.AbsolutePath is "" or "/" ? host : host + url.AbsolutePath;
    }
}
