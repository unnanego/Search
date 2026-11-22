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
    /// A bench request for this window, answered on its own thread (App.Bench picks the window).
    public Task<JsonObject> Bench(JsonObject request) => OnUi(() => Answer(request));

    /// The bench turned off: its tabs go.
    public void CloseBenchTabs() { foreach (var tab in _browser.Tabs.Where(t => t.Bench && !t.Agent).ToList()) _browser.Close(tab); }

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
                return new JsonObject { ["tabs"] = new JsonArray(_browser.Tabs.Where(t => !t.Agent && (t.Bench || Store.Testing)).Select(t => (JsonNode)Describe(t)).ToArray()) };
            case "probe":
                return Probe();
            // Test runs only, as on the Mac: a key as the window takes it, and typing into the field or find.
            case "press" or "type" or "select" or "answer" or "keep" or "pick" or "panelaction" or "edge" or "keeplittle" or "keeppeek" or "spaces" or "addspace" or "space" or "route" or "extensions" or "extinstall" or "extconfirm" or "extpress" or "extpopup" or "extpanel" or "extremove" or "extenable" or "extpin" or "extsites" or "extreload" or "extupdate" or "notice" or "newwindow" or "closewindow" or "quit" or "addprofile" or "renameprofile" or "removeprofile" or "profiles" or "sync" or "importsources" or "import" or "agents" when !Store.Testing:
                return BenchServer.Error($"{Text("do")} only works on a --test run");
            // A window more, from this one (Ctrl+N), or this one closed; "window" in any request names the one to act in.
            case "newwindow":
                App.NewWindow(this);
                return new JsonObject { ["window"] = App.Windows.Count - 1 };
            case "closewindow":
                Close();
                return new JsonObject { ["windows"] = App.Windows.Count };
            // Profiles: one made (and a window of it), renamed, removed; the list with each one's windows.
            case "addprofile":
                if (Text("name") is not { } named || MakeProfile(named) is not { } profileWindow) return BenchServer.Error("addprofile needs a name");
                return new JsonObject { ["profile"] = SwiftJson.Uuid(profileWindow.Profile), ["window"] = App.Windows.IndexOf(profileWindow) };
            case "renameprofile":
                return Text("profile") is { } renaming && SwiftJson.Uuid(renaming) is { } rid && App.Shared.Profiles.Rename(rid, Text("name") ?? "") ? Profiles() : BenchServer.Error("no such profile, or no name");
            case "removeprofile":
                if (Text("profile") is not { } unwanted || SwiftJson.Uuid(unwanted) is not { } gone) return BenchServer.Error("removeprofile needs a profile");
                DeleteProfile(gone);
                return Profiles();
            case "profiles":
                return Profiles();
            // Sync: on with a folder ("folder"), off ("off"), a pass now ("now"); its state either way.
            case "sync":
                if (Text("folder") is { } syncFolder) App.Syncing.TurnOn(syncFolder);
                if (Flag("off")) App.Syncing.TurnOff();
                var pass = Flag("now") ? await App.Syncing.Now() : null;
                var sync = App.Shared.Sync;
                return new JsonObject { ["on"] = sync.On, ["folder"] = sync.Folder, ["device"] = sync.Device, ["problem"] = pass?.Problem ?? sync.Problem, ["ran"] = pass?.Ran, ["changed"] = pass?.Changed, ["peers"] = new JsonArray(sync.Peers.Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()) };
            // Import: what each browser in the test world's Import folder holds, and bringing some of it in, as the
            // sheet's "Bring them in" does ("what": bookmarks, history, passwords, tabs, sidebar, extensions).
            case "importsources":
                return new JsonObject { ["sources"] = new JsonArray(ImportSource.Installed().Select(s => { var p = s.Preview(); return (JsonNode)new JsonObject { ["name"] = s.Name, ["profiles"] = new JsonArray(s.Profiles.Select(x => (JsonNode)JsonValue.Create(x.Name)!).ToArray()), ["bookmarks"] = p.Bookmarks, ["places"] = p.Places, ["passwords"] = p.Passwords, ["appBound"] = p.AppBound, ["tabs"] = p.Session.Tabs, ["extensions"] = p.Extensions.Count, ["sidebar"] = s.SidebarCounts() is { } c ? c.Spaces : null }; }).ToArray()) };
            case "import":
                if (ImportSource.Installed().FirstOrDefault(s => s.Name == Text("source")) is not { } importing) return BenchServer.Error("no such browser");
                var bring = (request["what"] as JsonArray ?? []).Select(n => Enum.TryParse<Bringing>(n?.GetValue<string>(), ignoreCase: true, out var b) ? b : Bringing.None).Aggregate(Bringing.None, (all, b) => all | b);
                var heard = await BringIn(importing, Text("profile"), bring);
                Notice(heard);
                return new JsonObject { ["said"] = heard, ["accounts"] = _vault.All().Count };
            case "csv":
                if (!Store.Testing || Text("file") is not { } csvFile) return BenchServer.Error("csv needs a file, on a --test run");
                return new JsonObject { ["said"] = TakePasswordFile(csvFile), ["accounts"] = _vault.All().Count };
            // Coding agents: the server's state, its question answered as a click would ("answer": allow or deny,
            // "tabs", "always"), Stop pressed, or a program remembered as if it had passed its signature check.
            case "agents":
                if (Text("answer") is { } said) { if (_agentAsked is null) return BenchServer.Error("nothing is being asked"); _agentBoxes?.Tabs.SetValue(CheckBox.IsCheckedProperty, Flag("tabs")); _agentBoxes?.Always.SetValue(CheckBox.IsCheckedProperty, Flag("always")); AnswerAgent(said == "allow"); }
                if (Flag("stop")) App.Agents?.Stop();
                var state = App.Agents?.State ?? new JsonObject { ["running"] = false };
                state["asking"] = _agentAsked is not null;
                state["line"] = _agentLine.Visibility == Visibility.Visible ? _agentSaid.Text : null;
                state["agentTabs"] = _browser.Tabs.Count(t => t.Agent);
                return state;
            case "quit":
                DispatcherQueue.TryEnqueue(App.Quit);
                return new JsonObject { ["ok"] = true };
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
            case "extensions":
                return new JsonObject
                {
                    ["extensions"] = new JsonArray(_extensions.List.Items.Select(i => (JsonNode)new JsonObject
                    {
                        ["id"] = i.Id, ["name"] = i.Name, ["version"] = i.Version, ["enabled"] = i.Enabled, ["pinned"] = i.Pinned == true, ["fromStore"] = i.FromStore, ["source"] = i.Source,
                        ["error"] = _extensions.Errors.GetValueOrDefault(i.Id), ["button"] = _extensionButtons.ContainsKey(i.Id), ["sites"] = _extensions.SitesOf(i.Id).Mode.ToString().ToLowerInvariant()
                    }).ToArray()),
                    ["bar"] = ExtensionsBar.Visibility == Visibility.Visible, ["busy"] = new JsonArray(_extensions.Busy.Select(b => (JsonNode)b).ToArray())
                };
            case "extinstall":
                var ask = Flag("ask");
                if (Text("folder") is { } extFolder) { var adding = AddFolder(extFolder, ask); if (!ask) await adding; }
                else if (Text("store") is { } storeId) { var adding = AddFromStore(storeId, ask); if (!ask) await adding; }
                else return BenchServer.Error("extinstall needs a folder or a store id");
                return new JsonObject { ["ok"] = true, ["notice"] = LastNotice };
            case "extconfirm":
                if (_confirming is not { } confirming) return BenchServer.Error("nothing is being asked");
                confirming.TrySetResult(Flag("yes"));
                return new JsonObject { ["ok"] = true };
            case "extpress":
                if (Text("ext") is not { } pressed) return BenchServer.Error("extpress needs ext");
                var frontBefore = await FrontWindow();
                return new JsonObject { ["answer"] = (await Press(pressed, _extensionButtons.GetValueOrDefault(pressed) ?? _puzzle))?.DeepClone(), ["popup"] = PopupState(), ["front"] = frontBefore };
            case "extpopup":
                var popupState = PopupState();
                if (Text("eval") is { } popupEval && _popup is { } shown) popupState["value"] = await RunAny(shown.View.CoreWebView2, popupEval);
                return popupState;
            case "extpanel":
                if (Text("open") is { } sideId) await OpenSide(sideId);
                if (Flag("close")) CloseSide();
                var side = new JsonObject { ["open"] = _side is not null, ["ext"] = _side?.Id, ["url"] = _side?.View.CoreWebView2?.Source, ["width"] = SidePanel.ActualWidth };
                if (Text("eval") is { } sideEval && _side is { } open) side["value"] = await RunAny(open.View.CoreWebView2, sideEval);
                return side;
            case "extremove":
                if (Text("ext") is not { } removing) return BenchServer.Error("extremove needs ext");
                await _extensions.Remove(removing);
                return new JsonObject { ["ok"] = true };
            case "extenable":
                if (Text("ext") is not { } enabling) return BenchServer.Error("extenable needs ext");
                await _extensions.SetEnabled(enabling, !Flag("off"));
                return new JsonObject { ["ok"] = true };
            case "extpin":
                if (Text("ext") is not { } pinning) return BenchServer.Error("extpin needs ext");
                _extensions.SetPinned(pinning, !Flag("off"));
                return new JsonObject { ["ok"] = true };
            case "extsites":
                if (Text("ext") is not { } siting) return BenchServer.Error("extsites needs ext");
                var hosts = (request["hosts"] as JsonArray)?.Select(h => h?.GetValue<string>()).OfType<string>().ToList() ?? [];
                await _extensions.SetSites(siting, _extensions.SitesOf(siting) with { Mode = hosts.Count > 0 ? SiteMode.Some : SiteMode.All, Hosts = hosts });
                return new JsonObject { ["ok"] = true };
            case "extreload":
                if (Text("ext") is not { } reloading) return BenchServer.Error("extreload needs ext");
                await Reload(reloading);
                return new JsonObject { ["ok"] = true, ["notice"] = LastNotice };
            case "extupdate":
                await CheckNow();
                return new JsonObject { ["ok"] = true, ["notice"] = LastNotice };
            case "notice":
                return new JsonObject { ["notice"] = LastNotice, ["shown"] = Keeping.Visibility == Visibility.Visible };
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
            case "trustedclick" when Store.Testing:
                // A click the page sees as real (isTrusted), through the DevTools protocol, at the element's middle.
                if (Text("selector") is not { } trusted) return BenchServer.Error("trustedclick needs a selector");
                var clickCore = (await _pages.Ensure(found)).CoreWebView2;
                var where = await clickCore.ExecuteScriptAsync($"(()=>{{const el=document.querySelector({JsonSerializer.Serialize(trusted)});if(!el)return null;el.scrollIntoView({{block:'center'}});const r=el.getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]}})()");
                if (JsonNode.Parse(where) is not JsonArray { Count: 2 } point) return BenchServer.Error($"nothing matches {trusted}");
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await clickCore.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = point[0]!.GetValue<double>(), y = point[1]!.GetValue<double>(), button = "left", clickCount = 1 }));
                return new JsonObject { ["ok"] = true };
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
    private Tab? Find(string id) => (_peek is { } peek && peek.ShortId.StartsWith(id, StringComparison.OrdinalIgnoreCase) && Store.Testing ? peek : null) ?? _browser.Tabs.FirstOrDefault(t => !t.Agent && (t.Bench || Store.Testing) && t.Id.ToString("N").StartsWith(id.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

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
        ["focused"] = Root.XamlRoot is { } root ? Describe(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root)) : null,
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
        ["window"] = App.Windows.IndexOf(this),
        ["owner"] = _owner,
        ["profile"] = App.Shared.Profiles.Get(Profile).Name,
        ["windowTitle"] = Title,
        ["windows"] = new JsonArray(App.Windows.Select(w => (JsonNode)new JsonObject { ["kind"] = "browser", ["title"] = w.Title, ["visible"] = w.AppWindow.IsVisible, ["front"] = w == App.Acting, ["space"] = w.CurrentSpace.Name, ["tabs"] = w._browser.Ordinary.Count(), ["frame"] = new JsonArray(w.AppWindow.Position.X, w.AppWindow.Position.Y, w.AppWindow.Size.Width, w.AppWindow.Size.Height) }).ToArray())
    };

    private static JsonObject Profiles() => new()
    {
        ["profiles"] = new JsonArray(App.Shared.Profiles.List.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = SwiftJson.Uuid(p.Id), ["name"] = p.Name, ["windows"] = new JsonArray(App.Windows.Select((w, i) => (w, i)).Where(x => x.w.Profile == p.Id).Select(x => (JsonNode)JsonValue.Create(x.i)).ToArray()),
            ["spaces"] = new JsonArray(App.Shared.Spaces.Of(p.Id).Select(s => (JsonNode)JsonValue.Create(s.Name)!).ToArray())
        }).ToArray())
    };

    private JsonObject PopupState() => new()
    {
        ["open"] = _popup is not null, ["ext"] = _popup?.Id, ["url"] = _popup?.View.CoreWebView2?.Source, ["width"] = PopupCard.Width, ["height"] = PopupCard.Height,
        ["shown"] = PopupLayer.Visibility == Visibility.Visible && PopupCard.Opacity > 0
    };
}
