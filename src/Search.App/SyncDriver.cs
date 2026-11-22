using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Search.Core;

namespace Search.App;

// Sync with your other computers, through a folder you choose (Sync.swift), run for the app: Search writes small
// files into the folder (OneDrive, Dropbox, a shared drive) and whatever moves it between computers does the rest.
// Off unless turned on. A pass runs a second after launch, every 30 seconds, two seconds after a change here, when
// another computer's file changes in the folder, and when a window comes to the front. What a pass applies raises
// the models' own events, which are not changes to send back (Sync.Running).
public sealed class SyncDriver(Shared shared)
{
    private Sync Sync => shared.Sync;
    private DispatcherQueue? _queue;
    private DispatcherQueueTimer? _every, _soon;
    private FileSystemWatcher? _watcher;
    private bool _again;

    /// A pass finished: what it said, for Settings and the windows.
    public event Action<SyncResult>? Ran;

    public void Start(DispatcherQueue queue)
    {
        _queue = queue;
        shared.Spaces.PropertyChanged += Changed;
        shared.Pins.Changed += _ => Soon();
        shared.Prefs.PropertyChanged += Changed;
        foreach (var profile in shared.Profiles.List) shared.BookmarksOf(profile.Id).PropertyChanged += Changed;
        shared.Profiles.PropertyChanged += (_, _) => { foreach (var profile in shared.Profiles.List) { var b = shared.BookmarksOf(profile.Id); b.PropertyChanged -= Changed; b.PropertyChanged += Changed; } Soon(); };
        _every = queue.CreateTimer();
        _every.Interval = TimeSpan.FromSeconds(30);
        _every.Tick += (_, _) => _ = Now();
        _every.Start();
        Watch();
        if (Sync.On) Soon(1);
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => Soon();

    /// A pass in a moment; changes close together make one.
    public void Soon(double seconds = 2)
    {
        if (!Sync.On || Sync.Running || _queue is null) return;
        if (_soon is null) { _soon = _queue.CreateTimer(); _soon.IsRepeating = false; _soon.Tick += (_, _) => _ = Now(); }
        _soon.Interval = TimeSpan.FromSeconds(seconds);
        _soon.Start();
    }

    /// A pass now, unless one is running (then one more once it ends).
    public async Task<SyncResult?> Now()
    {
        if (!Sync.On) return null;
        if (Sync.Running) { _again = true; return null; }
        var result = await Sync.RunAsync();
        foreach (var space in result.RemovedSpaces) foreach (var window in App.Windows.ToList()) window.SpaceGone(space);
        Ran?.Invoke(result);
        if (_again) { _again = false; Soon(); }
        return result;
    }

    public void TurnOn(string folder) { Sync.TurnOn(folder); Watch(); Soon(0.2); }

    public void TurnOff() { Sync.TurnOff(); Watch(); }

    /// Another computer's file changed in the folder: a pass soon. Only while Sync is on, on its own folder.
    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (Sync.Root is not { } root) return;
        try
        {
            Directory.CreateDirectory(root);
            _watcher = new FileSystemWatcher(root, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler heard = (_, e) => { if (!e.Name?.EndsWith(Sync.Device + ".json", StringComparison.OrdinalIgnoreCase) ?? true) _queue?.TryEnqueue(() => Soon()); };
            _watcher.Changed += heard;
            _watcher.Created += heard;
            _watcher.Renamed += (_, e) => heard(null!, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { App.Log(e); }
    }
}
