using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Harness;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Clock.Mark("launched");
        var opts = Options.Parse(Environment.GetCommandLineArgs());
        var window = new Window { Title = $"Search spike: {opts.Scenario}" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        var result = new Dictionary<string, object?> { ["scenario"] = opts.Scenario, ["args"] = opts.Raw };
        try
        {
            using var server = new LocalServer();
            var ctx = new Context(opts, window, server, result);
            var run = opts.Scenario switch
            {
                "startup" => Scenarios.Startup(ctx),
                "tabs" => Scenarios.Tabs(ctx),
                "shield" => Scenarios.Shield(ctx),
                "extensions" => Scenarios.Extensions(ctx),
                "pip" => Scenarios.Pip(ctx),
                "overlay" => Scenarios.Overlay(ctx),
                "windows" => Scenarios.Windows(ctx),
                "keeper" => Scenarios.Keeper(ctx),
                "addfolder" => Scenarios.AddFolder(ctx),
                _ => throw new ArgumentException($"unknown scenario {opts.Scenario}")
            };
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(opts.Timeout)));
            if (finished != run) result["error"] = "timeout";
            else await run;
        }
        catch (Exception e) { result["error"] = e.ToString(); }
        result["marks"] = Clock.Marks;
        File.WriteAllText(opts.Out, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Exit();
    }
}
