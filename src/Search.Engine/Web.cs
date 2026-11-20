using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.Engine;

// The one WebView2 environment a window's pages share: one browser process, one data folder, the store's.
public static class Web
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> Environment => _environment ??= Create();

    private static async Task<CoreWebView2Environment> Create()
    {
        // A test run may add browser arguments, e.g. --host-resolver-rules for a local test server. The browser
        // somebody uses never reads this.
        var extra = Store.Testing ? System.Environment.GetEnvironmentVariable("SEARCH_WEBVIEW_ARGS") ?? "" : "";
        return await CoreWebView2Environment.CreateWithOptionsAsync("", Store.WebData, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = extra });
    }
}
