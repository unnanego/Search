using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        // The test world's WebView2 data never mixes with the real browser's (Store.cs).
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Store.WebData, new CoreWebView2EnvironmentOptions());
        await Page.EnsureCoreWebView2Async(environment);
        Search.Engine.Privacy.Apply(Page.CoreWebView2.Settings);
        Page.Source = new Uri("about:blank");
    }
}
