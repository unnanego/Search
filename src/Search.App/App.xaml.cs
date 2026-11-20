using Microsoft.UI.Xaml;
using Search.Core;

namespace Search.App;

public partial class App : Application
{
    private MainWindow? _window;

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
        }
        catch (Exception e) { Log(e); throw; }
    }

    public static void Log(Exception? e)
    {
        if (e is null) return;
        try { Directory.CreateDirectory(Store.Folder); File.AppendAllText(Store.File("crash.log"), $"{DateTimeOffset.Now:O}\n{e}\n\n"); } catch (IOException) { }
    }
}
