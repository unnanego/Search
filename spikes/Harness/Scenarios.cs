using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace Harness;

public static class Scenarios
{
    private const string Paint = "<script>requestAnimationFrame(()=>requestAnimationFrame(()=>chrome.webview.postMessage('painted')))</script>";

    private static string Html(string body, string script = Paint) => $"<!doctype html><meta charset=utf-8><title>spike</title><body>{body}{script}</body>";

    private static async Task<CoreWebView2Environment> Env(Context c, bool extensions = false)
    {
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--host-resolver-rules=\"{c.Server.HostRules}\"", AreBrowserExtensionsEnabled = extensions };
        return await CoreWebView2Environment.CreateWithOptionsAsync("", c.Opts.Data, options);
    }

    private static Task Activate(Window window)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Activated += (_, _) => shown.TrySetResult();
        window.Activate();
        return shown.Task;
    }

    private static Task<string> NextMessage(CoreWebView2 core)
    {
        var next = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        handler = (_, e) => { core.WebMessageReceived -= handler; next.TrySetResult(e.TryGetWebMessageAsString()); };
        core.WebMessageReceived += handler;
        return next.Task;
    }

    private static void Quiet(CoreWebView2 core)
    {
        core.Settings.IsReputationCheckingRequired = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
    }

    /// Polls a script until it returns something other than null: what an isolated-world content script or an
    /// extension page left in the DOM.
    private static async Task<JsonElement?> PollJson(CoreWebView2 core, string expression, int seconds = 20)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until)
        {
            var raw = await core.ExecuteScriptAsync(expression);
            if (raw != "null" && JsonSerializer.Deserialize<string>(raw) is { } text) return JsonSerializer.Deserialize<JsonElement>(text);
            await Task.Delay(200);
        }
        return null;
    }

    // Spike 3a: cold and warm start, from process start to a painted page.
    public static async Task Startup(Context c)
    {
        var web = new WebView2();
        c.Window.Content = new Grid { Children = { web } };
        await Activate(c.Window); Clock.Mark("activated");
        var env = await Env(c); Clock.Mark("environment");
        await web.EnsureCoreWebView2Async(env); Clock.Mark("core");
        Quiet(web.CoreWebView2);
        c.Server.Pages["/index.html"] = Html("<h1>Hello</h1><p>A first page.</p>");
        var painted = NextMessage(web.CoreWebView2);
        web.CoreWebView2.Navigate(c.Server.Url("app.test", "index.html"));
        await painted; Clock.Mark("painted");
        await Task.Delay(2000);
        c.Result["memory"] = Memory.Measure(env);
    }

    // Spike 3b: what each live tab costs, and what hiding and suspending gives back.
    public static async Task Tabs(Context c)
    {
        var canvas = new Canvas();
        c.Window.Content = canvas;
        await Activate(c.Window);
        var env = await Env(c);
        c.Server.Pages["/tab.html"] = Html(string.Concat(Enumerable.Range(0, 200).Select(i => $"<p>Paragraph {i} of an ordinary page, with <a href=#{i}>a link</a>.</p>")));
        var tabs = new List<WebView2>();
        var memory = new Dictionary<string, object>();
        var created = Stopwatch.StartNew();
        for (var i = 0; i < c.Opts.Count; i++)
        {
            var web = new WebView2 { Width = 100, Height = 100 };
            Canvas.SetLeft(web, i % 10 * 110); Canvas.SetTop(web, i / 10 * 110);
            canvas.Children.Add(web);
            await web.EnsureCoreWebView2Async(env);
            Quiet(web.CoreWebView2);
            var painted = NextMessage(web.CoreWebView2);
            web.CoreWebView2.Navigate(c.Server.Url($"site{i}.test", "tab.html"));
            await painted;
            tabs.Add(web);
            if (i == 0) { await Task.Delay(1500); memory["one"] = Memory.Measure(env); }
        }
        c.Result["msToCreateAndPaintAll"] = created.ElapsedMilliseconds;
        c.Result["msPerTab"] = Math.Round(created.ElapsedMilliseconds / (double)c.Opts.Count, 1);
        await Task.Delay(3000);
        memory["allVisible"] = Memory.Measure(env);
        foreach (var web in tabs.Skip(1)) web.Visibility = Visibility.Collapsed;
        await Task.Delay(4000);
        memory["oneVisibleRestHidden"] = Memory.Measure(env);
        var suspended = 0;
        foreach (var web in tabs.Skip(1)) if (await web.CoreWebView2.TrySuspendAsync()) suspended++;
        await Task.Delay(4000);
        memory["restSuspended"] = Memory.Measure(env);
        foreach (var web in tabs.Skip(1)) web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        await Task.Delay(4000);
        memory["restSuspendedLowTarget"] = Memory.Measure(env);
        var closing = Stopwatch.StartNew();
        foreach (var web in tabs.Skip(1)) { web.Close(); canvas.Children.Remove(web); }
        c.Result["msToCloseRest"] = closing.ElapsedMilliseconds;
        await Task.Delay(4000);
        memory["restClosed"] = Memory.Measure(env);
        // Waking one: a slept tab built again from its address, as Sleep.swift's tabs come back.
        var wake = Stopwatch.StartNew();
        var woken = new WebView2 { Width = 100, Height = 100 };
        canvas.Children.Add(woken);
        await woken.EnsureCoreWebView2Async(env);
        var back = NextMessage(woken.CoreWebView2);
        woken.CoreWebView2.Navigate(c.Server.Url("site1.test", "tab.html"));
        await back;
        c.Result["msToWakeOne"] = wake.ElapsedMilliseconds;
        c.Result["suspended"] = suspended;
        c.Result["memory"] = memory;
    }

    // Spike 2: ad blocking as a bundled declarativeNetRequest extension vs WebResourceRequested in the app.
    public static async Task Shield(Context c)
    {
        var mode = c.Opts.Mode;
        var web = new WebView2();
        c.Window.Content = new Grid { Children = { web } };
        await Activate(c.Window);
        var env = await Env(c, extensions: mode == "dnr");
        await web.EnsureCoreWebView2Async(env);
        Quiet(web.CoreWebView2);
        var blocklist = Enumerable.Range(0, 50_000).Select(i => $"tracker{i}.example").Append("ads.test").ToHashSet();
        if (mode == "dnr")
        {
            var folder = Path.Combine(Path.GetTempPath(), "search-spike-shield-ext");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), """
                {"manifest_version":3,"name":"Spike Shield","version":"1","permissions":["declarativeNetRequest"],"host_permissions":["<all_urls>"],
                 "declarative_net_request":{"rule_resources":[{"id":"block","enabled":true,"path":"rules.json"}]}}
                """);
            var rules = new[] { new { id = 1, priority = 1, action = new { type = "block" }, condition = new { requestDomains = blocklist.ToArray() } } };
            File.WriteAllText(Path.Combine(folder, "rules.json"), JsonSerializer.Serialize(rules));
            var installed = Stopwatch.StartNew();
            var ext = await web.CoreWebView2.Profile.AddBrowserExtensionAsync(folder);
            c.Result["extension"] = new { ext.Id, ext.Name, msToInstall = installed.ElapsedMilliseconds };
        }
        var seen = 0;
        if (mode == "filter")
        {
            web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                seen++;
                var host = new Uri(e.Request.Uri).Host;
                for (var h = host; h.Length > 0; h = h.Contains('.') ? h[(h.IndexOf('.') + 1)..] : "")
                    if (blocklist.Contains(h)) { e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", ""); return; }
            };
        }
        string Imgs(string host, int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"<img src='{c.Server.Url(host, $"img/{i}.gif")}?r=RUN' width=1 height=1>"));
        var script = "<script>addEventListener('load',()=>{const imgs=[...document.images];chrome.webview.postMessage(JSON.stringify({ms:performance.now(),loaded:imgs.filter(i=>i.naturalWidth>0).length,failed:imgs.filter(i=>i.naturalWidth===0).length}))})</script>";
        var runs = new List<JsonElement>();
        for (var r = 0; r < c.Opts.Count; r++)
        {
            c.Server.Pages["/shield.html"] = Html(Imgs("app.test", 300).Replace("RUN", r.ToString()) + Imgs("ads.test", 300).Replace("RUN", r.ToString()), script);
            var done = NextMessage(web.CoreWebView2);
            web.CoreWebView2.Navigate(c.Server.Url("app.test", $"shield.html?r={r}"));
            runs.Add(JsonSerializer.Deserialize<JsonElement>(await done));
        }
        var ms = runs.Skip(1).Select(r => r.GetProperty("ms").GetDouble()).Order().ToArray();
        c.Result["runs"] = runs;
        c.Result["medianLoadMsExcludingFirst"] = ms.Length > 0 ? ms[ms.Length / 2] : null;
        c.Result["serverHits"] = c.Server.Hosts;
        c.Result["webResourceRequestedSeen"] = seen;
    }

    // Spike 1: which Chrome extension APIs answer in WebView2, what the extension sees of our "tabs", and whether
    // the action popup can be shown in a WebView2 of our own.
    public static async Task Extensions(Context c)
    {
        var web = new WebView2(); var popup = new WebView2();
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(400) } } };
        Grid.SetColumn(popup, 1);
        grid.Children.Add(web); grid.Children.Add(popup);
        c.Window.Content = grid;
        await Activate(c.Window);
        var env = await Env(c, extensions: true);
        await web.EnsureCoreWebView2Async(env);
        Quiet(web.CoreWebView2);
        var newWindows = new ConcurrentBag<object>();
        var hosted = new List<WebView2>();
        web.CoreWebView2.NewWindowRequested += async (_, e) =>
        {
            newWindows.Add(new { e.Uri, e.IsUserInitiated, e.Name });
            if (c.Opts.Mode != "handled") return;
            // As a browser would: the new "window" becomes a tab of ours, a WebView2 in our own tree.
            var deferral = e.GetDeferral();
            var tab = new WebView2 { Width = 10, Height = 10 };
            grid.Children.Add(tab); hosted.Add(tab);
            await tab.EnsureCoreWebView2Async(env);
            e.NewWindow = tab.CoreWebView2; e.Handled = true;
            deferral.Complete();
        };
        var folder = Path.Combine(AppContext.BaseDirectory, "extensions", "probe");
        CoreWebView2BrowserExtension ext;
        try { ext = await web.CoreWebView2.Profile.AddBrowserExtensionAsync(folder); }
        catch (Exception e) { c.Result["installError"] = e.Message; return; }
        c.Result["extension"] = new { ext.Id, ext.Name, ext.IsEnabled };
        c.Server.Pages["/probe.html"] = Html("<h1>Probe</h1>", "");
        c.Server.Pages["/created.html"] = Html("<h1>Created by the extension</h1>", "");
        web.CoreWebView2.Navigate(c.Server.Url("probe.test", "probe.html"));
        c.Result["fromWorker"] = await PollJson(web.CoreWebView2, "document.documentElement.dataset.probe ?? null", 40);
        await popup.EnsureCoreWebView2Async(env);
        var navigated = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.CoreWebView2.NavigationCompleted += (_, e) => navigated.TrySetResult(new { e.IsSuccess, status = e.WebErrorStatus.ToString(), e.HttpStatusCode });
        popup.CoreWebView2.Navigate($"chrome-extension://{ext.Id}/popup.html");
        c.Result["popupNavigation"] = await navigated.Task;
        c.Result["fromPopup"] = await PollJson(popup.CoreWebView2, "document.body?.dataset.probe ?? null", 10);
        c.Result["newWindowRequested"] = newWindows.ToArray();
        c.Result["installedExtensions"] = (await web.CoreWebView2.Profile.GetBrowserExtensionsAsync()).Select(e => new { e.Id, e.Name, e.IsEnabled }).ToArray();
        c.Result["serverHits"] = c.Server.Hosts;
    }

    // Spike 4: Chromium's own picture in picture, video and document, inside WebView2.
    public static async Task Pip(Context c)
    {
        var web = new WebView2();
        c.Window.Content = new Grid { Children = { web } };
        await Activate(c.Window);
        var env = await Env(c);
        await web.EnsureCoreWebView2Async(env);
        Quiet(web.CoreWebView2);
        c.Server.Pages["/pip.html"] = Html("<canvas width=320 height=180></canvas><video muted playsinline></video>", """
            <script>
            const cv=document.querySelector('canvas'),g=cv.getContext('2d');let f=0;
            (function draw(){g.fillStyle=`hsl(${f++%360} 80% 50%)`;g.fillRect(0,0,320,180);requestAnimationFrame(draw)})();
            const v=document.querySelector('video');v.srcObject=cv.captureStream(30);
            v.play().then(()=>chrome.webview.postMessage('painted'));
            </script>
            """);
        var ready = NextMessage(web.CoreWebView2);
        web.CoreWebView2.Navigate(c.Server.Url("app.test", "pip.html"));
        await ready;
        c.Result["features"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({pictureInPictureEnabled: document.pictureInPictureEnabled, documentPictureInPicture: 'documentPictureInPicture' in window})"))!);
        HashSet<uint> Pids() => env.GetProcessInfos().Select(p => (uint)p.ProcessId).Append((uint)Environment.ProcessId).ToHashSet();
        async Task<string> Gesture(string expression) => await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression, userGesture = true, awaitPromise = true, returnByValue = true }));
        c.Result["windowsBefore"] = Native.TopWindows(Pids());
        c.Result["videoPip"] = await Gesture("(async()=>{try{await document.querySelector('video').requestPictureInPicture();return 'ok, element set: '+!!document.pictureInPictureElement}catch(e){return 'error: '+e}})()");
        await Task.Delay(1500);
        c.Result["windowsDuringVideoPip"] = Native.TopWindows(Pids());
        await Gesture("document.exitPictureInPicture?.().catch(()=>{})");
        await Task.Delay(800);
        c.Result["documentPip"] = await Gesture("(async()=>{try{const w=await documentPictureInPicture.requestWindow({width:320,height:180});w.document.body.textContent='document pip';return 'ok'}catch(e){return 'error: '+e}})()");
        await Task.Delay(1500);
        c.Result["windowsDuringDocumentPip"] = Native.TopWindows(Pids());
    }

    // Spike 5: WinUI content drawn over a WebView2 (the omnibox, a peek), and a draggable strip with buttons in it.
    public static async Task Overlay(Context c)
    {
        var web = new WebView2();
        var overlay = new Border { Width = 200, Height = 100, Background = new SolidColorBrush(Colors.Red), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(50) };
        c.Window.Content = new Grid { Children = { web, overlay } };
        c.Window.ExtendsContentIntoTitleBar = true;
        await Activate(c.Window);
        var env = await Env(c);
        await web.EnsureCoreWebView2Async(env);
        Quiet(web.CoreWebView2);
        c.Server.Pages["/blue.html"] = Html("", "<style>html,body{margin:0;height:100%;background:#0000ff}</style>" + Paint);
        var painted = NextMessage(web.CoreWebView2);
        web.CoreWebView2.Navigate(c.Server.Url("app.test", "blue.html"));
        await painted;
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(c.Window.AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32(300, 0, 600, 40)]);
            source.SetRegionRects(NonClientRegionKind.Passthrough, [new RectInt32(0, 0, 300, 40)]);
            c.Result["captionRegions"] = source.GetRegionRects(NonClientRegionKind.Caption).Select(r => $"{r.X},{r.Y},{r.Width}x{r.Height}").ToArray();
        }
        catch (Exception e) { c.Result["captionRegionsError"] = e.Message; }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(c.Window);
        Native.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0003);
        Native.SetForegroundWindow(hwnd);
        await Task.Delay(1200);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        string Pixel(double x, double y)
        {
            var p = new Native.Point { X = (int)(x * scale), Y = (int)(y * scale) };
            Native.ClientToScreen(hwnd, ref p);
            var dc = Native.GetDC(0);
            try { return Native.Colour(Native.GetPixel(dc, p.X, p.Y)); } finally { Native.ReleaseDC(0, dc); }
        }
        c.Result["scale"] = scale;
        c.Result["overlayPixel (expect #FF0000)"] = Pixel(150, 100);
        c.Result["pagePixel (expect #0000FF)"] = Pixel(700, 500);
        Native.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0003);
    }
}
