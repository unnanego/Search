using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            case "press" or "type" or "select" or "answer" or "keep" or "pick" or "panelaction" or "edge" or "keeplittle" or "keeppeek" or "spaces" or "addspace" or "space" or "route" when !Store.Testing:
                return BenchServer.Error($"{Text("do")} only works on a --test run");
            case "press":
                if (Text("keys") is not { } combo) return BenchServer.Error("press needs keys, e.g. ctrl+t");
                var before = Describe(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot));
                return Press(combo) ? new JsonObject { ["ok"] = true, ["focusedBefore"] = before } : BenchServer.Error($"nothing answers {combo} (focus: {before})");
            case "type":
                if (Text("text") is not { } words) return BenchServer.Error("type needs text");
                TypeIntoNamed(Text("into") ?? "field", words);
                await Task.Delay(50);
                return new JsonObject { ["ok"] = true };
            case "answer":
                if (_asking is null) return BenchServer.Error("nothing is being asked");
                Answer(Flag("allow"));
                return new JsonObject { ["ok"] = true };
            case "keep":
                if (_keeping is null) return BenchServer.Error("nothing is offered for keeping");
                AnswerKeeping(Text("answer") ?? "later");
                return new JsonObject { ["ok"] = true };
            case "pick":
                if (LoginRows.ItemsSource is not IList<PanelRow> rows || Logins.Visibility != Visibility.Visible) return BenchServer.Error("no accounts are offered");
                var index = (int)(Number("index") ?? 0);
                if (index < 0 || index >= rows.Count || rows[index].Item is not Search.Core.Account account) return BenchServer.Error("no such account");
                _loginsShown = DateTime.UtcNow.AddSeconds(-1);
                await Pick(account);
                return new JsonObject { ["ok"] = true };
            case "panelaction":
                if (PanelRows.ItemsSource is not IList<PanelRow> panelRows || (int)(Number("index") ?? 0) is var at && (at < 0 || at >= panelRows.Count)) return BenchServer.Error("no such panel row");
                OnPanelRowAction(new Button { Tag = panelRows[at] }, new RoutedEventArgs());
                return new JsonObject { ["ok"] = true };
            case "keeppeek":
                if (_peek is null) return BenchServer.Error("no peek");
                KeepPeek();
                return new JsonObject { ["ok"] = true };
            case "keeplittle":
                if (_littles.FirstOrDefault() is not { } little) return BenchServer.Error("no small window");
                if (little.Tab.Url is { } kept) OpenHere(kept);
                little.Close();
                return new JsonObject { ["ok"] = true };
            case "spaces":
                _spaces.UsesSpaces = Flag("on");
                DrawSpaces();
                return new JsonObject { ["ok"] = true };
            case "addspace":
                if (_spaces.Add(Text("name") ?? "Space", sharesSignIns: !Flag("own")) is not { } made) return BenchServer.Error("couldn't make it");
                GoToSpace(made.Id);
                return new JsonObject { ["ok"] = true, ["store"] = _spaces.StoreName(made.Id) };
            case "space":
                GoToSpaceNumber((int)(Number("number") ?? 1));
                return new JsonObject { ["space"] = CurrentSpace.Name };
            case "route":
                if (Text("site") is not { } site || _spaces.All.FirstOrDefault(s => s.Name == Text("name")) is not { } to) return BenchServer.Error("route needs a site and a space's name");
                _routes.On = true;
                _routes.Set(site, to.Id);
                return new JsonObject { ["ok"] = true };
            case "edge":
                SlideOut();
                return new JsonObject { ["ok"] = _slidOut };
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
            case "cdp" when Store.Testing:
                if (Text("method") is not { } method) return BenchServer.Error("cdp needs a method");
                var cdpCore = (await _pages.Ensure(found)).CoreWebView2;
                return new JsonObject { ["value"] = JsonNode.Parse(await cdpCore.CallDevToolsProtocolMethodAsync(method, request["params"]?.ToJsonString() ?? "{}")) };
            case "world" when Store.Testing:
                if (Text("expression") is not { } expression) return BenchServer.Error("world needs an expression");
                await _pages.Ensure(found);
                return new JsonObject { ["value"] = (await _pages.Evaluate(found, expression))?.DeepClone() };
            case "pin" when Store.Testing:
                if (Flag("off")) _browser.Unpin(found); else _browser.Pin(found);
                return Describe(found);
            case "group" when Store.Testing:
                if (Flag("off")) _browser.Ungroup(found);
                else _browser.Group(found, _browser.Groups.FirstOrDefault(g => g.Name == Text("name")), Text("name") ?? "");
                return Describe(found);
            case "fold" when Store.Testing:
                if (_browser.Groups.FirstOrDefault(g => g.Id == found.GroupId) is { } folded) _browser.SetGroup(folded with { Collapsed = !folded.Collapsed });
                return Describe(found);
            case "close":
                if (!found.Bench) return BenchServer.Error("not a bench tab — only tabs the bench opened can be closed from here");
                _browser.Close(found);
                return new JsonObject { ["closed"] = 1 };
            default:
                return BenchServer.Error("unknown");
        }
    }

    /// By any prefix of its id, as the bench names tabs. Outside a test run only the bench's own.
    private Tab? Find(string id) => (_peek is { } peek && peek.ShortId.StartsWith(id, StringComparison.OrdinalIgnoreCase) && Store.Testing ? peek : null) ?? _browser.Tabs.FirstOrDefault(t => (t.Bench || Store.Testing) && t.Id.ToString("N").StartsWith(id.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

    private JsonObject Describe(Tab tab) => new()
    {
        ["id"] = tab.ShortId, ["url"] = tab.Url?.AbsoluteUri, ["title"] = tab.Title, ["name"] = tab.Name, ["loading"] = tab.Loading,
        ["bench"] = tab.Bench, ["private"] = tab.Private, ["active"] = tab == _browser.Selected, ["asleep"] = !tab.Live, ["icon"] = tab.Icon ?? "", ["pinned"] = tab.Pinned, ["letter"] = tab.Letter, ["group"] = _browser.Groups.FirstOrDefault(g => g.Id == tab.GroupId)?.Name, ["heard"] = _pages.Heard.GetValueOrDefault(tab)
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
        ["slidOut"] = _slidOut,
        ["columnWidth"] = _prefs.ColumnWidth,
        ["appearance"] = Root.ActualTheme.ToString().ToLowerInvariant(),
        ["find"] = FindBar.Visibility == Visibility.Visible,
        ["findText"] = FindText.Text,
        ["findCount"] = FindCount.Text,
        ["focused"] = Describe(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot)),
        ["findRaw"] = _finding is { } f ? new JsonArray(f.Find.MatchCount, f.Find.ActiveMatchIndex) : null,
        ["fullScreen"] = _fullScreen,
        ["panel"] = _panel.ToString().ToLowerInvariant(),
        ["littles"] = new JsonArray(_littles.Select(l => (JsonNode)new JsonObject { ["url"] = l.Tab.Url?.AbsoluteUri, ["title"] = l.Tab.Title }).ToArray()),
        ["peek"] = _peek is { } peek ? new JsonObject { ["id"] = peek.ShortId, ["url"] = peek.Url?.AbsoluteUri, ["title"] = peek.Title } : null,
        ["switcher"] = Switcher.Visibility == Visibility.Visible ? new JsonArray((SwitcherRows.ItemsSource as IEnumerable<PanelRow> ?? []).Select(r => (JsonNode)JsonValue.Create(r.Title)!).ToArray()) : null,
        ["asking"] = _asking is { } asking ? AskingText.Text : null,
        ["keeping"] = Keeping.Visibility == Visibility.Visible ? KeepingText.Text : null,
        ["logins"] = new JsonArray((LoginRows.ItemsSource as IEnumerable<PanelRow> ?? []).Where(_ => Logins.Visibility == Visibility.Visible).Select(r => (JsonNode)JsonValue.Create(r.Title)!).ToArray()),
        ["bookmarks"] = _bookmarks.Count,
        ["fieldNote"] = _fieldNote,
        ["notifications"] = new JsonArray(_posted.Select(p => (JsonNode)new JsonObject { ["site"] = p.Site, ["title"] = p.Title, ["body"] = p.Body }).ToArray()),
        ["panelRows"] = new JsonArray((PanelRows.ItemsSource as IEnumerable<PanelRow> ?? []).Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["detail"] = r.Detail }).ToArray()),
        ["selected"] = _browser.Selected?.ShortId,
        ["space"] = CurrentSpace.Name,
        ["column"] = new JsonArray(_row.Items.Select(i => (JsonNode)JsonValue.Create(i is GroupRow g ? "group " + g.Name + (g.Group.Collapsed ? " (folded)" : "") : i is Tab t ? t.ShortId : "?")!).ToArray()),
        ["pins"] = new JsonArray(_row.Pins.Select(p => (JsonNode)JsonValue.Create(p.ShortId)!).ToArray()),
        ["spaces"] = new JsonArray(_spaces.All.Select(s => (JsonNode)JsonValue.Create(s.Name)!).ToArray()),
        ["store"] = _browser.Store,
        ["panes"] = new JsonArray(_pages.Panes.Select(p => (JsonNode)new JsonObject { ["id"] = p.Tab.ShortId, ["x"] = p.Rect.X, ["y"] = p.Rect.Y, ["w"] = p.Rect.Width, ["h"] = p.Rect.Height }).ToArray()),
        ["tabs"] = _browser.Ordinary.Count(),
        ["windows"] = new JsonArray(new JsonObject { ["kind"] = "browser", ["title"] = Title, ["visible"] = AppWindow.IsVisible, ["frame"] = new JsonArray(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height) })
    };
}
