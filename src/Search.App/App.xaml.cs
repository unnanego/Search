using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Search.Bench;
using Search.Core;
using Search.Engine;

namespace Search.App;

public partial class App : Application
{
    /// The small notes the Mac keeps in its settings (spaces on, the space in front, the last bookmarks folder).
    public static Notes Notes { get; } = new();
    /// What every window shares: made once, before the first window.
    public static Shared Shared { get; private set; } = null!;
    /// Every window, oldest first (Windows.swift's Browsers.all). The oldest holds the session files; the others are
    /// lines in windows.json.
    public static List<MainWindow> Windows { get; } = new();
    /// The window last in front: where a link from another app, the bench and Ctrl+N act.
    public static MainWindow? Acting { get; private set; }
    private static BenchServer? _bench;
    /// Coding agents, while Settings lets them in (AgentServer): one for the app, working in the window in front.
    public static AgentServer? Agents { get; private set; }
    private static readonly AgentTrust Trust = new(SealedVault.Agents);
    /// Sync's passes, for the app (SyncDriver).
    public static SyncDriver Syncing { get; private set; } = null!;
    /// The windows closed while others stayed open, newest last, for Ctrl+Shift+T.
    private static readonly List<(WindowRecord Record, DateTimeOffset At)> _closed = new();
    private static bool _quitting;

    public App()
    {
        InitializeComponent();
        // Kept on this machine, in the store's folder, and nowhere else: no crash report is ever sent.
        UnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var prefs = Prefs.Load();
            // A switch some other program turned on, without the mark Settings leaves, is put back to off.
            AgentSwitch.Check(prefs);
            // A test run that asks for the bench gets it without a trip to Settings, as ./bench --test expects.
            if (Store.Testing && Environment.GetEnvironmentVariable("SEARCH_BENCH") == "1") prefs.Bench = true;
            Shared = new Shared(prefs);
            // What an import left behind when Search stopped in the middle of it: copies of another browser's files.
            _ = Task.Run(() => ImportRead.Sweep());
            var records = WindowsFile.Read();
            var first = Open(records.Count > 0 ? records[0] : null, primary: true);
            // Last time's other windows, each as it was left, once the first is up.
            foreach (var record in records.Skip(1)) Open(record, primary: false);
            first.Bring();
            foreach (var link in Program.Links(Environment.GetCommandLineArgs().Skip(1))) first.FromElsewhere(link);
            prefs.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Prefs.Bench)) Bench();
                if (e.PropertyName == nameof(Prefs.Agents)) { AgentSwitch.Turned(prefs.Agents, Trust); AgentsOn(); }
            };
            AgentsOn();
            if (prefs.AgentsRefused) first.DispatcherQueue.TryEnqueue(() => first.Notify("Letting coding agents use Search was turned on by something other than Settings, so it was turned off."));
            Syncing = new SyncDriver(Shared);
            Syncing.Start(first.DispatcherQueue);
            Bench();
            Launcher.Start(Shared.Prefs, first.DispatcherQueue);
            StartLive(first.DispatcherQueue);
            _ = Updater.CheckSoon();
        }
        catch (Exception e) { Log(e); throw; }
    }

    /// A window: last time's (its record), or a new one beside the one in front (Ctrl+N), with one empty tab in that
    /// window's space and folded as it is.
    public static MainWindow Open(WindowRecord? record = null, bool primary = false, MainWindow? from = null, Guid? profile = null)
    {
        var window = new MainWindow(Shared, record, primary, from, profile);
        Windows.Add(window);
        Acting ??= window;
        window.Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) return; Acting = window; Syncing?.Soon(0.5); };
        window.Closed += (_, _) => Closing(window);
        window.Activate();
        return window;
    }

    /// Ctrl+N: a new window, from the one in front, of its profile; or a window of another profile (Settings),
    /// in the space it was last in.
    public static MainWindow NewWindow(MainWindow from, Guid? profile = null) => profile is { } other && other != from.Profile ? Open(profile: other) : Open(from: from);

    /// A window of `profile` on screen: the one in front if it is one, the newest of the others, or a new one.
    public static MainWindow WindowOf(Guid profile) => Acting is { } front && front.Profile == profile ? front : Windows.LastOrDefault(w => w.Profile == profile) ?? Open(profile: profile);

    /// A window is closing. The last one is the app closing: everything written down first, tabs and all. One
    /// closed while others stay open goes, tabs and all, and Ctrl+Shift+T can bring it back; the session files it
    /// held go to the oldest window left. (Windows closed one by one are gone, as in Chrome; Exit keeps them all.)
    private static void Closing(MainWindow window)
    {
        if (_quitting || !Windows.Contains(window)) return;
        var owned = window.Owner && !window.Discarded;
        if (Windows.Count == 1) { Quit(); return; }
        // A window gone with its profile isn't one Ctrl+Shift+T brings back.
        if (!window.Discarded) _closed.Add((window.Record(rows: true), DateTimeOffset.UtcNow));
        if (_closed.Count > 10) _closed.RemoveAt(0);
        Windows.Remove(window);
        var heir = owned ? Windows.FirstOrDefault(w => w.Profile == window.Profile) : null;
        // The last window of its profile, others open: its rows stay in the profile's files, written now.
        if (owned && heir is null) window.WriteNow();
        window.Retire();
        if (Acting == window) Acting = Windows[^1];
        heir?.TakeFiles();
        SaveWindows(now: true);
    }

    /// Live folders' feeds (LiveFolders.cs): fetched a little after launch and every half hour while folders in
    /// pinned rows are on; nothing is fetched with them off.
    public static LiveFolders? Live { get; private set; }
    private static LiveSchedule? _liveSchedule;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _liveTimer;

    private static void StartLive(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        Live = new LiveFolders(Shared.Folders, new LiveFetch());
        _liveSchedule = new LiveSchedule();
        _liveTimer = queue.CreateTimer();
        _liveTimer.IsRepeating = false;
        _liveTimer.Tick += (_, _) =>
        {
            if (_liveSchedule.Due(Shared.Prefs.ShowsPinFolders)) _ = Live.RefreshAll(true);
            _liveTimer.Interval = _liveSchedule.Wait > TimeSpan.FromSeconds(1) ? _liveSchedule.Wait : TimeSpan.FromSeconds(1);
            _liveTimer.Start();
        };
        _liveTimer.Interval = _liveSchedule.Wait > TimeSpan.Zero ? _liveSchedule.Wait : TimeSpan.FromSeconds(1);
        _liveTimer.Start();
    }

    /// Exit (Ctrl+Shift+Q): every window written down as it is, so the next launch brings them all back.
    public static void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        foreach (var window in Windows) window.Shut();
        SaveWindows(now: true);
        Shared.Profiles.FlushHistories();
        _bench?.Dispose();
        Agents?.Dispose();
        Launcher.Stop();
        Disk.Flush().Wait(TimeSpan.FromSeconds(3));
        foreach (var window in Windows.ToList()) window.Close();
        Current.Exit();
    }

    /// When the newest closed window went, to weigh against the newest closed tab.
    public static DateTimeOffset? LastClosedAt => _closed.Count > 0 ? _closed[^1].At : null;

    /// Ctrl+Shift+T, when the last thing closed was a window: it comes back where it was, with its tabs.
    public static bool ReopenWindow()
    {
        if (_closed.Count == 0) return false;
        var last = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        if (!Shared.Spaces.All.Any(s => s.Id == last.Record.Space)) return ReopenWindow();
        Open(last.Record);
        SaveWindows();
        return true;
    }

    /// windows.json: the oldest window's frame and space first, then every other window whole.
    public static void SaveWindows(bool now = false)
    {
        if (Windows.Count == 0) return;
        _ = WindowsFile.Write(Windows.Select(w => w.Record(rows: !w.Owner)).ToList(), now);
    }

    /// The bench, on while Settings says so: one for the app, acting in the window it names or the one in front.
    private static void Bench()
    {
        if (Shared.Prefs.Bench && _bench is null)
        {
            _bench = new BenchServer(BenchServer.PipeFor(Store.Folder), request => Pick(request).Bench(request));
            try { _bench.Start(); } catch (IOException e) { Log(e); _bench.Dispose(); _bench = null; } catch (UnauthorizedAccessException e) { Log(e); _bench.Dispose(); _bench = null; }
        }
        else if (!Shared.Prefs.Bench && _bench is not null)
        {
            _bench.Dispose();
            _bench = null;
            foreach (var window in Windows) window.CloseBenchTabs();
        }
    }

    /// The agents' pipe, open while the setting is on.
    private static void AgentsOn()
    {
        if (Shared.Prefs.Agents && Agents is null)
        {
            var server = new AgentServer(AgentServer.PipeFor(Store.Folder), () => Acting, Trust, SynchronizationContext.Current)
            {
                Log = line => { if (Store.Testing) try { File.AppendAllText(Store.File("agents.log"), $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}\n"); } catch (IOException) { } }
            };
            if (Store.Testing && double.TryParse(Environment.GetEnvironmentVariable("SEARCH_AGENT_IDLE"), out var idle)) server.LetGoAfter = TimeSpan.FromSeconds(idle);
            try { server.Start(); Agents = server; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); server.Dispose(); }
        }
        else if (!Shared.Prefs.Agents && Agents is not null) { Agents.Dispose(); Agents = null; }
    }

    /// The window a bench request names ("window": its place, oldest first), or the one in front.
    private static MainWindow Pick(System.Text.Json.Nodes.JsonObject request) =>
        request["window"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var n) && n >= 0 && n < Windows.Count ? Windows[n] : Acting ?? Windows[0];

    /// Another launch of Search handed its activation here: a link from another app, or Search opened again.
    public static void Redirected(AppActivationArguments activation)
    {
        var arguments = activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? Program.Split(launch.Arguments) : [];
        var window = Acting;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            var target = Acting ?? window;
            if (target is null) return;
            var links = Program.Links(arguments.Skip(arguments.Length > 0 && arguments[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0)).ToList();
            foreach (var link in links) target.FromElsewhere(link);
            target.Bring();
        });
    }

    public static void Log(Exception? e)
    {
        if (e is null) return;
        try { Directory.CreateDirectory(Store.Folder); File.AppendAllText(Store.File("crash.log"), $"{DateTimeOffset.Now:O}\n{e}\n\n"); } catch (IOException) { }
    }
}
