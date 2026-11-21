using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Search.Core;

namespace Search.App;

public partial class App : Application
{
    private static MainWindow? _window;

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
            // A test run that asks for the bench gets it without a trip to Settings, as ./bench --test expects.
            if (Store.Testing && Environment.GetEnvironmentVariable("SEARCH_BENCH") == "1") prefs.Bench = true;
            _window = new MainWindow(prefs, new History());
            _window.Activate();
            foreach (var link in Program.Links(Environment.GetCommandLineArgs().Skip(1))) _window.FromElsewhere(link);
            _ = Updater.CheckSoon();
        }
        catch (Exception e) { Log(e); throw; }
    }

    /// Another launch of Search handed its activation here: a link from another app, or Search opened again.
    public static void Redirected(AppActivationArguments activation)
    {
        var arguments = activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? Program.Split(launch.Arguments) : [];
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null) return;
            var links = Program.Links(arguments.Skip(arguments.Length > 0 && arguments[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0)).ToList();
            foreach (var link in links) _window.FromElsewhere(link);
            _window.Bring();
        });
    }

    public static void Log(Exception? e)
    {
        if (e is null) return;
        try { Directory.CreateDirectory(Store.Folder); File.AppendAllText(Store.File("crash.log"), $"{DateTimeOffset.Now:O}\n{e}\n\n"); } catch (IOException) { }
    }
}