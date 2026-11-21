using Microsoft.Web.WebView2.Core;
using Search.Core;

namespace Search.Engine;

// The one WebView2 environment a window's pages share: one browser process, one data folder, the store's.
public static class Web
{
    private static Task<CoreWebView2Environment>? _environment;

    private static Task<CoreWebView2Environment>? _private;

    public static Task<CoreWebView2Environment> Environment => _environment ??= Create(Store.WebData);

    /// Private tabs' own browser: a process and a data folder of their own, emptied at each launch, so nothing a private
    /// tab did shares a process, a cache or a disk folder with the browser you use.
    public static Task<CoreWebView2Environment> Private => _private ??= Create(PrivateData(fresh: true));

    public static string PrivateData(bool fresh = false)
    {
        var folder = Store.File("WebView2 private");
        if (fresh) try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return folder;
    }

    private static async Task<CoreWebView2Environment> Create(string folder)
    {
        // Extensions are on for Shield, a built-in extension (and Phase 4's). A test run may add browser arguments, e.g. --host-resolver-rules for a local test server. The browser
        // somebody uses never reads this.
        var extra = Store.Testing ? System.Environment.GetEnvironmentVariable("SEARCH_WEBVIEW_ARGS") ?? "" : "";
        return await CoreWebView2Environment.CreateWithOptionsAsync("", folder, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = extra, AreBrowserExtensionsEnabled = true });
    }
}
