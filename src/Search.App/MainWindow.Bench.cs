using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Search.Bench;
using Search.Core;
using Search.Engine;

namespace Search.App;

// The browser's side of the bench (Bench.swift): what each verb does here. Requests arrive off the UI thread and
// are answered on it. A bench tab is never selected, never in the session or the history, and can only be closed
// from here; outside a test run a script reaches no other tab.
public sealed partial class MainWindow
{
    private const int TextLimit = 120_000;
    private BenchServer? _bench;

    private void Bench()
    {
        if (_prefs.Bench && _bench is null)
        {
            _bench = new BenchServer(BenchServer.PipeFor(Store.Folder), request => OnUi(() => Answer(request)));
            try { _bench.Start(); } catch (IOException e) { App.Log(e); _bench.Dispose(); _bench = null; } catch (UnauthorizedAccessException e) { App.Log(e); _bench.Dispose(); _bench = null; }
        }
        else if (!_prefs.Bench && _bench is not null)
        {
            _bench.Dispose();
            _bench = null;
            foreach (var tab in _browser.Tabs.Where(t => t.Bench).ToList()) _browser.Close(tab);
        }
    }

    private Task<JsonObject> OnUi(Func<Task<JsonObject>> work)
    {
        var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () => { try { done.SetResult(await work()); } catch (Exception e) { done.SetResult(BenchServer.Error(e.Message)); } });
        return done.Task;
    }

    private async Task<JsonObject> Answer(JsonObject request)
    {
        string? Text(string key) => request[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        double? Number(string key) => request[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        bool Flag(string key) => request[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        switch (Text("do"))
        {
            case "open":
                if (Text("url") is not { } typed || Search.Core.Address.UrlFrom(typed) is not { } url) return BenchServer.Error("open needs a url");
                if (Flag("private") && !Store.Testing) return BenchServer.Error("open private only works on a --test run");
                var opened = _browser.Open(url, select: false, bench: true, @private: Flag("private"));
                await _pages.Ensure(opened);
                return Describe(opened);
            case "tabs":
                return new JsonObject { ["tabs"] = new JsonArray(_browser.Tabs.Where(t => t.Bench || Store.Testing).Select(t => (JsonNode)Describe(t)).ToArray()) };
            case "probe":
                return Probe();
            // Test runs only, as on the Mac: a key as the window takes it, and typing into the field or find.
            case "press" or "type" or "select" when !Store.Testing:
                return BenchServer.Error($"{Text("do")} only works on a --test run");
            case "press":
                if (Text("keys") is not { } combo) return BenchServer.Error("press needs keys, e.g. ctrl+t");
                var before = Describe(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot));
                return Press(combo) ? new JsonObject { ["ok"] = true, ["focusedBefore"] = before } : BenchServer.Error($"nothing answers {combo} (focus: {before})");
            case "type":
                if (Text("text") is not { } words) return BenchServer.Error("type needs text");
                TypeInto(Text("into") == "find" ? FindText : FieldText, words);
                await Task.Delay(50);
                return new JsonObject { ["ok"] = true };
            case "select":
                if (Text("id") is not { } which || _browser.Ordinary.FirstOrDefault(t => t.ShortId.StartsWith(which, StringComparison.OrdinalIgnoreCase)) is not { } chosen) return BenchServer.Error("select needs the id of an ordinary tab");
                _browser.Selected = chosen;
                return Describe(chosen);
            case "close" when Text("id") == "all":
                var all = _browser.Tabs.Where(t => t.Bench).ToList();
                foreach (var tab in all) _browser.Close(tab);
                return new JsonObject { ["closed"] = all.Count };
        }
        if (Text("id") is not { } id) return BenchServer.Error("unknown");
        if (Find(id) is not { } found) return BenchServer.Error($"no tab “{id}” — see tabs");
        switch (Text("do"))
        {
            case "wait":
                var until = DateTime.Now.AddSeconds(Number("seconds") ?? 20);
                while (DateTime.Now < until && (found.Loading || found.Url is null)) await Task.Delay(100);
                var settled = !found.Loading && found.Url is not null;
                if (settled) await Task.Delay(250);
                var waited = Describe(found);
                if (!settled) waited["timeout"] = true;
                return waited;
            case "text":
                var core = (await _pages.Ensure(found)).CoreWebView2;
                var text = Pages.Unquote(await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''")) ?? "";
                return new JsonObject { ["text"] = text.Length > TextLimit ? text[..TextLimit] : text, ["truncated"] = text.Length > TextLimit, ["url"] = found.Url?.AbsoluteUri, ["title"] = found.Title };
            case "shot":
                return await Shot(found, Text("path") ?? Path.Combine(Path.GetTempPath(), $"search-bench-{Guid.NewGuid():N}"[..21] + ".png"), Number("width"));
            case "click":
                if (Text("selector") is not { } selector) return BenchServer.Error("click needs a selector");
                var page = (await _pages.Ensure(found)).CoreWebView2;
                var script = $"(()=>{{const el=document.querySelector({JsonSerializer.Serialize(selector)});if(!el)return false;el.scrollIntoView({{block:'center'}});el.focus();el.click();return true}})()";
                return await page.ExecuteScriptAsync(script) == "true" ? new JsonObject { ["ok"] = true } : BenchServer.Error($"nothing matches {selector}");
            case "close":
                if (!found.Bench) return BenchServer.Error("not a bench tab — only tabs the bench opened can be closed from here");
                _browser.Close(found);
                return new JsonObject { ["closed"] = 1 };
            default:
                return BenchServer.Error("unknown");
        }
    }

    /// By any prefix of its id, as the bench names tabs. Outside a test run only the bench's own.
    private Tab? Find(string id) => _browser.Tabs.FirstOrDefault(t => (t.Bench || Store.Testing) && t.Id.ToString("N").StartsWith(id.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

    private JsonObject Describe(Tab tab) => new()
    {
        ["id"] = tab.ShortId, ["url"] = tab.Url?.AbsoluteUri, ["title"] = tab.Title, ["name"] = tab.Name, ["loading"] = tab.Loading,
        ["bench"] = tab.Bench, ["private"] = tab.Private, ["active"] = tab == _browser.Selected, ["asleep"] = !tab.Live, ["icon"] = tab.Icon ?? ""
    };

    /// A picture of the page, through Chromium's own capture, which works whether or not the tab is in front.
    private async Task<JsonObject> Shot(Tab tab, string path, double? width)
    {
        var core = (await _pages.Ensure(tab)).CoreWebView2;
        var args = new JsonObject { ["format"] = "png" };
        if (width is > 0)
        {
            var size = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """{"expression":"[innerWidth,innerHeight]","returnByValue":true}"""))!["result"]!["value"]!.AsArray();
            double w = size[0]!.GetValue<double>(), h = size[1]!.GetValue<double>();
            args["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = w, ["height"] = h, ["scale"] = width.Value / w };
        }
        var shot = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args.ToJsonString()))!["data"]!.GetValue<string>();
        var png = Convert.FromBase64String(shot);
        if (png.Length < 24) return BenchServer.Error("no picture");
        await File.WriteAllBytesAsync(path, png);
        int Big(int at) => png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3];
        return new JsonObject { ["path"] = path, ["width"] = Big(16), ["height"] = Big(20) };
    }

    private JsonObject Probe() => new()
    {
        ["settings"] = _settings is not null,
        ["field"] = FieldText.FocusState != FocusState.Unfocused,
        ["fieldText"] = FieldText.Text,
        ["placeholder"] = FieldText.PlaceholderText,
        ["suggesting"] = Offers.Visibility == Visibility.Visible,
        ["offering"] = new JsonArray((Offers.ItemsSource as IEnumerable<Offer> ?? []).Select(o => (JsonNode)JsonValue.Create(o.Suggestion.Kind + " " + o.Suggestion.Key)!).ToArray()),
        ["sidebar"] = _prefs.TabsOnSide,
        ["folded"] = _prefs.Folded,
        ["columnWidth"] = _prefs.ColumnWidth,
        ["appearance"] = Root.ActualTheme.ToString().ToLowerInvariant(),
        ["find"] = FindBar.Visibility == Visibility.Visible,
        ["findText"] = FindText.Text,
        ["findCount"] = FindCount.Text,
        ["focused"] = Describe(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot)),
        ["findRaw"] = _finding is { } f ? new JsonArray(f.Find.MatchCount, f.Find.ActiveMatchIndex) : null,
        ["fullScreen"] = _fullScreen,
        ["selected"] = _browser.Selected?.ShortId,
        ["tabs"] = _browser.Ordinary.Count(),
        ["windows"] = new JsonArray(new JsonObject { ["kind"] = "browser", ["title"] = Title, ["visible"] = AppWindow.IsVisible, ["frame"] = new JsonArray(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height) })
    };
}
