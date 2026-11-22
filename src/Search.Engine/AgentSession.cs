using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Search.Core;

namespace Search.Engine;

// One agent's connection (AgentSession and the verbs in AgentControl.swift). Everything here runs on the window's
// thread (AgentServer's SynchronizationContext); the pipe is read and written on the thread pool.
//
// What it can do is the list of verbs below, each checked here, not in the client: no script or CSS of its own runs in
// a page, only Search's (page-hands.js), in Search's own world. Values of password, card and one-time-code fields never
// leave the page, and pictures black those out, every frame, and whatever can't be read. Secret-looking values are
// taken out of the addresses it is given. No address but http and https. Keys and clicks go to its page's renderer,
// never through the window, so none of Search's shortcuts hears them, and ctrl-letters are refused. Requests are paced
// to something like a person's speed, and each step checks it is still allowed.
//
//   hello {client}                       → {allowed, seesTabs[, remembered]} once you answer
//   status                               → {allowed, seesTabs, client}
//   tabs                                 → {tabs: [{page, url, title, loading, mine}]}
//   open {url[, timeout]}                → the new tab of its own, once loaded
//   go {page, url[, timeout]}, back {page}, forward {page}, close {page}
//   read {page[, max_nodes, max_text, text]} → {nodes: [{element, role, name, value | redacted, box, …}], text, …}
//   click {element[, timeout]}, type {element, text[, replace, keys, submit, timeout]}, press {page, key}
//   scroll {page, dx, dy | to} or {element}, wait_for {page, text | role | name | url | title[, gone, timeout]}
//   shot {page[, width]}                 → {png (base64), width, height, blackedOut, page}
//   bye
public sealed class AgentSession
{
    /// Where the element lists live in each page, apart from the bench's.
    private const string Key = "__searchAgent";
    /// Past this much unsent, a client isn't reading, and is let go.
    private const int MostUnsent = 64 << 20;
    private const int MostUnread = 1_000_000, MostWaiting = 32;

    private readonly AgentServer _server;
    private readonly NamedPipeServerStream _pipe;
    private readonly AgentPeer _peer;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _gone = new();
    private readonly List<byte> _bytes = new();
    private readonly Queue<JsonObject> _inbox = new();
    private readonly AgentPace _pace = new();
    private readonly Dictionary<string, Guid> _pages = new();
    private readonly HashSet<Guid> _own = new();
    private readonly Dictionary<string, int> _snaps = new();
    private IAgentHost? _host;
    private Action<JsonObject>? _hello;
    private bool _greeted, _working, _ended, _drop;
    private long _unsent;
    private int _counter;
    private DateTimeOffset _stirred = DateTimeOffset.UtcNow;
    private Tab? _workingOn;
    private string? _admittedBy;

    /// Which program connected, as Windows says: "python.exe, started by claude.exe".
    public string Program { get; }
    /// What it calls itself.
    public string Client { get; private set; } = "";
    public AgentConsent Consent { get; } = new();
    /// The program that started it, as its signature says, once checked at hello; null if it couldn't be.
    public AgentLauncher? Launcher { get; private set; }
    /// Let in without asking: remembered (AgentTrust).
    public bool Auto { get; private set; }
    public bool JustAdmitted { get; private set; }
    /// Working now: a request in hand, or one a moment ago.
    public bool Busy { get; private set; } = true;
    /// When the question went up.
    public DateTimeOffset Asked { get; private set; }
    public int OwnTabs => _own.Count;

    /// Why "Always allow" isn't offered, or null when it is.
    public string? NoAlways => !_server.Trust.Usable ? "this copy of Search has nowhere safe to keep it" : Launcher is null ? "Search couldn't tell which program started it" : Launcher.Unrememberable;

    internal AgentSession(AgentServer server, NamedPipeServerStream pipe, AgentPeer peer, string program)
    {
        _server = server;
        _pipe = pipe;
        _peer = peer;
        Program = program;
    }

    private bool Current => _server.Session == this && !_ended;
    /// Whether it may still act: the one there is, and allowed. Asked again before every step that touches a page, so
    /// nothing it asked for runs after Stop.
    private bool Live => Current && Consent.State == AgentState.Allowed;

    internal void Begin()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
        // A client that connects and says nothing doesn't keep the only place: hello within a few seconds.
        _ = Later(_server.HelloWithin, () => { if (!_greeted) End("it said nothing"); });
    }

    // Reading and writing.

    private async Task ReadLoop()
    {
        var chunk = new byte[65536];
        while (true)
        {
            int n;
            try { n = await _pipe.ReadAsync(chunk).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { n = 0; }
            if (n <= 0) { _server.OnUi(() => End("the connection closed")); return; }
            var got = chunk[..n];
            _server.OnUi(() => Received(got));
        }
    }

    private void Received(byte[] got)
    {
        if (!Current) return;
        _bytes.AddRange(got);
        int newline;
        while ((newline = _bytes.IndexOf((byte)'\n')) >= 0)
        {
            var line = _bytes.GetRange(0, newline).ToArray();
            _bytes.RemoveRange(0, newline + 1);
            if (line.Length == 0 || line is [(byte)'\r']) continue;
            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { request = null; }
            if (request is null) Say(new AgentRefusal("bad_request", "Not a JSON object").Said);
            else _inbox.Enqueue(request);
        }
        // A line that never ends is not a request, and a pile of them is not something a person's pace explains.
        if (_bytes.Count > MostUnread || _inbox.Count > MostWaiting)
        {
            Say(new AgentRefusal("too_much", "Too much at once").Said);
            End("it sent too much");
            return;
        }
        Pump();
    }

    private void Say(JsonObject answer)
    {
        if (_server.Session != this || _drop) return;
        var line = Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n");
        if (Interlocked.Add(ref _unsent, line.Length) > MostUnsent) { _drop = true; End("it stopped reading"); return; }
        _outbox.Writer.TryWrite(line);
    }

    /// As fast as the client reads; once ended, what is still to be said goes if the pipe takes it within a moment.
    private async Task WriteLoop()
    {
        using var closing = new CancellationTokenSource();
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_drop) break;
                if (_ended) closing.CancelAfter(TimeSpan.FromSeconds(2));
                await _pipe.WriteAsync(line, closing.Token).ConfigureAwait(false);
                await _pipe.FlushAsync(closing.Token).ConfigureAwait(false);
                Interlocked.Add(ref _unsent, -line.Length);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { _server.OnUi(() => End("the connection closed")); }
        finally
        {
            await _pipe.DisposeAsync();
            _peer.Dispose();
        }
    }

    // Taking requests.

    /// The next request, once the one before has its answer, at the pace.
    private void Pump()
    {
        if (!Current || _working || !_inbox.TryPeek(out var request)) return;
        var verb = AgentRules.Text(request, "do") ?? "";
        var verdict = Consent.Verdict(verb);
        if (!verdict.Go && verdict.Refused is null) return;
        _inbox.Dequeue();
        if (verdict.Refused is { } code)
        {
            Say(Reply(new AgentRefusal(code, AgentRules.RefusalText(code)).Said, request));
            _server.Log($"{verb} refused: {code}");
            Pump();
            return;
        }
        _working = true;
        if (verb != "hello") { _stirred = DateTimeOffset.UtcNow; if (!Busy) { Busy = true; Show(); } }
        var delay = verb == "hello" ? 0 : _pace.Delay(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _ = Later(TimeSpan.FromSeconds(delay), () => _ = Take(verb, request));
    }

    private async Task Take(string verb, JsonObject request)
    {
        var answered = false;
        void Answer(JsonObject said)
        {
            if (answered) return;
            answered = true;
            var code = (said["error"] as JsonObject)?["code"]?.GetValue<string>() ?? "ok";
            _server.Log($"{verb} {AgentRules.Text(request, "page") ?? AgentRules.Text(request, "element") ?? ""} {code}");
            Say(Reply(said, request));
            _working = false;
            _stirred = DateTimeOffset.UtcNow;
            Pump();
        }
        if (!Current) return;
        if (verb == "hello") { Hello(request, Answer); return; }
        // One answer, always: a page that never replies doesn't hold the agent, or the next request, for ever.
        var patience = AgentRules.Patience(request);
        _ = Later(patience, () => Answer(new AgentRefusal("no_answer", $"No answer within {patience.TotalSeconds:0} s").Said));
        JsonObject result;
        try { result = await Run(verb, request); }
        catch (AgentRefusal refusal) { result = refusal.Said; }
        catch (Exception e) { result = new AgentRefusal("internal", e.Message).Said; }
        Answer(result);
    }

    private static JsonObject Reply(JsonObject answer, JsonObject request)
    {
        if (request["n"] is { } n) answer["n"] = n.DeepClone();
        return answer;
    }

    /// Work done on the window's thread after a while.
    private static async Task Later(TimeSpan wait, Action then)
    {
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        then();
    }

    // The question.

    private void Hello(JsonObject request, Action<JsonObject> answer)
    {
        _greeted = true;
        Client = AgentRules.ClientName(AgentRules.Text(request, "client"));
        _host = _server.Front();
        if (_host is null) { answer(new AgentRefusal("no_window", "Search has no window open").Said); End("no window"); return; }
        _host.Closed += OnClosed;
        // Answered when you answer, or when it is found remembered.
        _hello = answer;
        _ = Greet();
    }

    private void OnClosed() => _server.OnUi(() => End("the window closed"));

    /// Who started it, by its signature, away from the window's thread; then let in at once if you said to always
    /// allow it and it still is what you said that to, or the question goes up.
    private async Task Greet()
    {
        var trust = _server.Trust;
        var requirements = trust.Requirements(Client);
        var seen = await Task.Run(() => AgentIdentity.Examine(_peer, requirements));
        if (!Current || Consent.State != AgentState.Connected || _host is not { } host) return;
        Launcher = seen?.Launcher;
        Consent.Hello();
        Asked = DateTimeOffset.UtcNow;
        if (AgentTrust.Match(Client, Launcher, seen?.Passed ?? [], trust.Remembered, Store.Testing, DateTimeOffset.UtcNow) is { } entry)
        {
            Auto = true;
            _admittedBy = entry.Id;
            // Your tabs never without a click of yours in the question.
            Consent.Answer(allow: true, seesTabs: false);
            trust.Used(entry with { Path = Launcher?.Path ?? entry.Path });
            _server.Log($"allowed automatically: {Client}, started by {Launcher?.Name} signed by {Launcher?.Signer}");
            _server.Admissions++;
            JustAdmitted = true;
            _ = Later(TimeSpan.FromSeconds(5), () => { if (!Current) return; JustAdmitted = false; Show(); });
            Said(new JsonObject { ["allowed"] = true, ["seesTabs"] = false, ["remembered"] = true });
            Began();
            Pump();
            return;
        }
        if (requirements.Count > 0) _server.Log($"{Client} is remembered, but what started it didn't pass: asking");
        _server.Touched();
        AgentAnswer said;
        while (true)
        {
            try { said = await host.Ask(new AgentQuestion(Client, Program, Launcher, NoAlways, Asked), _gone.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { _server.Log("the question failed: " + e.Message); End("the question couldn't be asked"); return; }
            if (!Current || Consent.State != AgentState.Asking) return;
            // A click in its first half second was meant for whatever was there before: asked again.
            if (DateTimeOffset.UtcNow - Asked > AgentRules.Settling) break;
            await Task.Delay(AgentRules.Settling);
            if (!Current || Consent.State != AgentState.Asking) return;
        }
        Decide(said);
    }

    private void Decide(AgentAnswer answer)
    {
        Consent.Answer(answer.Allow, answer.SeesTabs);
        _server.Log((answer.Allow ? "allowed" : "not allowed") + (answer.Allow && answer.SeesTabs ? ", with your tabs" : ""));
        // "Always allow" ticked again: whatever Stop suspended under its name may let it in again.
        if (answer.Allow && answer.Always) _server.Trust.Resume(Client);
        // Remembered only as ticked, and only a program that can be: the box isn't there otherwise.
        if (answer.Allow && answer.Always && NoAlways is null && Launcher is { } launcher && _server.Trust.Remember(Client, launcher))
            _server.Log($"always allowed from now on: {Client}, started by {launcher.Name} signed by {launcher.Signer}");
        if (answer.Allow)
        {
            Said(new JsonObject { ["allowed"] = true, ["seesTabs"] = Consent.SeesTabs });
            Began();
            Pump();
        }
        else
        {
            Said(new AgentRefusal("not_allowed", "You were not allowed to use Search").Said);
            // Said, then gone: the answer is on its way before the pipe is let go.
            End("not allowed");
        }
    }

    private void Said(JsonObject answer) { var hello = _hello; _hello = null; hello?.Invoke(answer); }

    /// Allowed: Stop goes up in its window's chrome, kept honest about whether it is working.
    private void Began()
    {
        _stirred = DateTimeOffset.UtcNow;
        Busy = true;
        Show();
        _ = Watch();
    }

    private async Task Watch()
    {
        while (Live)
        {
            await Task.Delay(Store.Testing ? TimeSpan.FromSeconds(0.5) : TimeSpan.FromSeconds(2));
            if (!Live) return;
            var (busy, letGo) = AgentRules.Quiet(_working, DateTimeOffset.UtcNow - _stirred, _server.IdleAfter, _server.LetGoAfter);
            if (Busy != busy) { Busy = busy; Show(); }
            if (!busy) Working(null);
            if (!letGo) continue;
            // Said first, so the client knows to ask again when it next needs Search, rather than taking it for Stop.
            Say(new AgentRefusal("idle", "Let go after a while with nothing asked", "Connect again when you next need Search.").Said);
            End("idle");
            return;
        }
    }

    private void Show()
    {
        _host?.Show(Live ? new AgentShown(Client, Program, Consent.SeesTabs, Auto, JustAdmitted, Busy) : null);
        _server.Touched();
    }

    /// The mark goes to the tab it is working in now, and off the one before.
    private void Working(Tab? tab)
    {
        if (_workingOn == tab) return;
        _workingOn = tab;
        _host?.Working(tab);
    }

    internal void Adopt(Tab tab, Tab opener) { if (_own.Contains(opener.Id)) _own.Add(tab.Id); }

    // Ending.

    internal void End(string why)
    {
        if (!Current) return;
        _ended = true;
        var was = Consent.State;
        var stopped = why.StartsWith("stopped", StringComparison.Ordinal);
        // Stopped, let in without asking or not: never let in again without asking, until "Always allow" is ticked again.
        if (stopped && was == AgentState.Allowed) { _server.Trust.Suspend(Client, _admittedBy); _server.Log($"stopped: {Client} is asked about again from now on"); }
        Said(AgentRefusal.Stopped.Said);
        Consent.End();
        _gone.Cancel();
        _inbox.Clear();
        if (_host is { } host)
        {
            host.Closed -= OnClosed;
            if (_workingOn is not null) host.Working(null);
            host.Show(null);
        }
        _workingOn = null;
        _server.Ended(this, was, stopped);
        // What was said last goes if the pipe takes it now; nothing waits.
        _outbox.Writer.TryComplete();
        _server.Log("ended: " + why);
    }

    // The verbs.

    private async Task<JsonObject> Run(string verb, JsonObject request)
    {
        if (_host is not { } host) { End("the window closed"); throw new AgentRefusal("stopped", "Its window was closed"); }
        switch (verb)
        {
            case "status": return new JsonObject { ["allowed"] = true, ["seesTabs"] = Consent.SeesTabs, ["client"] = Client };
            case "tabs": return Tabs(host);
            case "open":
            {
                var url = AgentRules.Address(AgentRules.Text(request, "url")) ?? throw BadAddress;
                var tab = await host.Open(url);
                if (!Live) throw AgentRefusal.Stopped;
                _own.Add(tab.Id);
                Working(tab);
                return await Settle(tab, AgentRules.Seconds(request, 20));
            }
            case "go":
            {
                var tab = TabOf(request, AgentUse.Drive);
                var url = AgentRules.Address(AgentRules.Text(request, "url")) ?? throw BadAddress;
                tab.Go(url);
                await Task.Delay(200);
                return await Settle(tab, AgentRules.Seconds(request, 20));
            }
            case "back" or "forward":
            {
                var tab = TabOf(request, AgentUse.Drive);
                await WorldOf(tab);
                tab.Do(verb == "back" ? TabCommand.Back : TabCommand.Forward);
                await Task.Delay(300);
                return await Settle(tab, AgentRules.Seconds(request, 20));
            }
            case "close":
            {
                var tab = TabOf(request, AgentUse.Drive);
                var handle = AgentRules.Text(request, "page") ?? "";
                if (_workingOn == tab) Working(null);
                host.Close(tab);
                _own.Remove(tab.Id);
                Forget(handle);
                return new JsonObject { ["closed"] = handle };
            }
            case "read": return await Read(request);
            case "click": return await Click(request);
            case "type": return await Type(request);
            case "press":
            {
                var tab = TabOf(request, AgentUse.Drive);
                var key = AgentRules.Key(AgentRules.Text(request, "key")) ?? throw new AgentRefusal("bad_key", "Not a key this can press",
                    "Enter, Tab, Escape, Backspace, an arrow, or one character, after shift+, alt+ or ctrl+. Never the Windows key, never ctrl and a letter.");
                var world = await WorldOf(tab);
                if (!Live) throw AgentRefusal.Stopped;
                await PageHands.Key(world.Core, key);
                await Task.Delay(300);
                return await Settle(tab, 10);
            }
            case "scroll": return await Scroll(request);
            case "wait_for": return await WaitFor(request);
            case "shot": return await Shot(request);
            case "bye":
                _ = Later(TimeSpan.FromMilliseconds(1), () => End("it said goodbye"));
                return new JsonObject { ["bye"] = true };
            default: throw new AgentRefusal("no_verb", $"Search doesn't do “{verb}” for an agent");
        }
    }

    private static AgentRefusal BadAddress => new("refused_address", "Only http and https addresses", "Search's own pages, Settings, extensions and files are not for agents.");

    private string Handle(Tab tab)
    {
        foreach (var (handle, id) in _pages) if (id == tab.Id) return handle;
        var made = "p" + ++_counter;
        _pages[made] = tab.Id;
        return made;
    }

    private void Forget(string handle) { _pages.Remove(handle); _snaps.Remove(handle); }

    /// The tab a request's page handle names, if it may be used that way. A private tab is never found, and a tab of
    /// yours is read at most.
    private Tab TabOf(JsonObject request, AgentUse use)
    {
        var handle = AgentRules.Text(request, "page") ?? "";
        var found = _pages.TryGetValue(handle, out var id) ? _host?.Tabs.FirstOrDefault(t => t.Id == id) : null;
        var refusal = found is null ? "stale_page" : AgentRules.Refusal(found, _own.Contains(found.Id), Consent, use);
        if (refusal is null) { Working(found); return found!; }
        if (refusal == "not_yours") throw new AgentRefusal("not_yours", "That tab is the person's own: it can be read, never used", "Open the address in a tab of your own with open_tab.");
        if (_pages.ContainsKey(handle)) Forget(handle);
        throw new AgentRefusal("stale_page", $"No page {(handle.Length == 0 ? "given" : $"“{handle}”")}", "Use list_tabs or open_tab.");
    }

    private (Tab Tab, string Page, int Snap, int Index) ElementOf(JsonObject request)
    {
        if (AgentRules.Element(AgentRules.Text(request, "element")) is not { } element) throw new AgentRefusal("bad_handle", "An element handle looks like p1.s3.e12, from read_page");
        var tab = TabOf(new JsonObject { ["page"] = element.Page }, AgentUse.Drive);
        if (!_snaps.TryGetValue(element.Page, out var snap) || snap != element.Snap) throw new AgentRefusal("stale_element", "That handle is from an earlier read_page", "Call read_page again.");
        return (tab, element.Page, element.Snap, element.Index);
    }

    /// A tab's world: its own tab's built if need be; one of yours read as it is, and never woken for it.
    private async Task<World> WorldOf(Tab tab)
    {
        if (!Live || _host is not { } host) throw AgentRefusal.Stopped;
        var own = _own.Contains(tab.Id);
        var world = await host.WorldOf(tab, build: own);
        if (!Live) throw AgentRefusal.Stopped;
        return world ?? throw (own ? new AgentRefusal("page", "The page gave nothing back") : new AgentRefusal("asleep", "That tab is asleep, and isn't woken for an agent"));
    }

    /// A script of Search's, in Search's world, on the tab's page.
    private async Task<JsonObject> Page(Tab tab, string script)
    {
        var world = await WorldOf(tab);
        var answer = await PageHands.Run(world, script);
        if (!Live) throw AgentRefusal.Stopped;
        if (answer is null) throw new AgentRefusal("page", "The page gave nothing back");
        if (answer["error"] is JsonValue error && error.TryGetValue<string>(out var code))
            throw new AgentRefusal(code, AgentRules.Text(answer, "message") ?? "", code is "stale_element" or "detached" ? "Call read_page again." : "");
        return answer;
    }

    private JsonObject Describe(Tab tab) => new() { ["page"] = Handle(tab), ["url"] = AgentRules.Url(tab.Url?.AbsoluteUri ?? ""), ["title"] = tab.Title, ["loading"] = tab.Loading };

    /// Once the page has stopped loading, or the time is up.
    private async Task<JsonObject> Settle(Tab tab, double seconds)
    {
        var limit = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 0, 30));
        while (true)
        {
            if (!tab.Loading && tab.Url is not null)
            {
                // A beat for the document's own scripts to settle.
                await Task.Delay(250);
                return Describe(tab);
            }
            if (DateTimeOffset.UtcNow >= limit) { var late = Describe(tab); late["timeout"] = true; return late; }
            await Task.Delay(100);
        }
    }

    private JsonObject Tabs(IAgentHost host)
    {
        var tabs = host.Tabs;
        var live = tabs.Select(t => t.Id).ToHashSet();
        foreach (var (handle, id) in _pages.ToList()) if (!live.Contains(id)) Forget(handle);
        _own.IntersectWith(live);
        var shown = tabs.Where(t => AgentRules.Refusal(t, _own.Contains(t.Id), Consent, AgentUse.Read) is null).Select(t =>
        {
            var described = Describe(t);
            described["mine"] = _own.Contains(t.Id);
            return (JsonNode)described;
        }).ToArray();
        return new JsonObject { ["tabs"] = new JsonArray(shown) };
    }

    private async Task<JsonObject> Read(JsonObject request)
    {
        var tab = TabOf(request, AgentUse.Read);
        var handle = Handle(tab);
        var snap = _snaps.GetValueOrDefault(handle) + 1;
        var read = await Page(tab, PageHands.Read(Key, snap, AgentRules.MostNodes(request), AgentRules.LongestText(request), AgentRules.Flag(request, "text") ?? true));
        _snaps[handle] = snap;
        var own = _own.Contains(tab.Id);
        foreach (var node in (read["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var index = node["e"] is JsonValue e && e.TryGetValue<int>(out var i) ? i : -1;
            node.Remove("e");
            // Your tabs are read, never touched: no handles to touch them by.
            if (own) node["element"] = $"{handle}.s{snap}.e{index}";
            if (AgentRules.Text(node, "href") is { } href) node["href"] = AgentRules.Url(href);
            // A frame named by its address, when it has no title.
            if (AgentRules.Text(node, "role") == "iframe" && AgentRules.Text(node, "name") is { } named) node["name"] = AgentRules.Scrubbed(named);
        }
        read["page"] = handle;
        read["url"] = AgentRules.Url(AgentRules.Text(read, "url"));
        return read;
    }

    private async Task<JsonObject> Click(JsonObject request)
    {
        var (tab, page, snap, index) = ElementOf(request);
        var before = tab.Url;
        var seen = await Page(tab, PageHands.Touch(Key, snap, index, "click", null, replace: true, strict: true));
        if (seen["at"] is not JsonArray { Count: 2 } at || at[0] is not JsonValue x || at[1] is not JsonValue y) throw new AgentRefusal("not_clicked", "The page had nowhere to click");
        var world = await WorldOf(tab);
        await PageHands.Click(world.Core, x.GetValue<double>(), y.GetValue<double>());
        await Task.Delay(300);
        var settled = await Settle(tab, AgentRules.Seconds(request, 10));
        settled["page"] = page;
        settled["navigated"] = tab.Url != before;
        return settled;
    }

    private async Task<JsonObject> Type(JsonObject request)
    {
        var (tab, _, snap, index) = ElementOf(request);
        var text = AgentRules.Text(request, "text") ?? "";
        if (text.Length > 10000) text = text[..(char.IsHighSurrogate(text[9999]) ? 9999 : 10000)];
        if (!AgentRules.Typeable(text)) throw new AgentRefusal("bad_text", "No control characters but tab and newline", "Use press for Enter, Tab or Escape.");
        var replace = AgentRules.Flag(request, "replace") ?? true;
        Task<JsonObject> Touch(string how, string? with) => Page(tab, PageHands.Touch(Key, snap, index, how, with, replace, strict: true));
        async Task<JsonObject> Finish(JsonObject typed)
        {
            if (AgentRules.Flag(request, "submit") != true) return typed;
            await Touch("submit", "");
            await Task.Delay(300);
            var state = await Settle(tab, AgentRules.Seconds(request, 10));
            foreach (var (key, value) in state) if (!typed.ContainsKey(key)) typed[key] = value?.DeepClone();
            return typed;
        }
        if (AgentRules.Flag(request, "keys") != true) return await Finish(await Touch("set", text));
        // Real key events, into the field once focused.
        var focused = await Touch("focus", text);
        var world = await WorldOf(tab);
        await PageHands.Type(world.Core, text);
        await Task.Delay(300);
        return await Finish(await Touch("verify", AgentRules.Text(focused, "want")));
    }

    private async Task<JsonObject> Scroll(JsonObject request)
    {
        if (request.ContainsKey("element"))
        {
            var (tab, _, snap, index) = ElementOf(request);
            return await Page(tab, PageHands.Touch(Key, snap, index, "scroll", null, replace: true, strict: true));
        }
        var page = TabOf(request, AgentUse.Drive);
        return await Page(page, PageHands.Scroll(AgentRules.Number(request, "dx") ?? 0, AgentRules.Number(request, "dy") ?? 0, AgentRules.Text(request, "to")));
    }

    private async Task<JsonObject> WaitFor(JsonObject request)
    {
        var tab = TabOf(request, AgentUse.Read);
        var timeout = AgentRules.Seconds(request, 10);
        var condition = AgentRules.Condition(request, _own.Contains(tab.Id));
        if (condition.None)
        {
            var settled = await Settle(tab, timeout);
            return settled.ContainsKey("timeout") ? throw new AgentRefusal("timeout", $"Still loading after {timeout:0} s") : settled;
        }
        var began = DateTimeOffset.UtcNow;
        while (true)
        {
            if (!Live) throw AgentRefusal.Stopped;
            JsonObject? seen = null;
            // On your tab nothing is asked of the page: its address and title are Search's to know.
            if (condition.Page.Count == 0) seen = new JsonObject { ["met"] = true, ["url"] = tab.Url?.AbsoluteUri ?? "", ["title"] = tab.Title };
            else
                try { seen = await Page(tab, PageHands.Check(condition.Page)); }
                catch (AgentRefusal refusal) when (refusal.Code is not ("bad_condition" or "stopped")) { }
            if (seen is not null && condition.Met(AgentRules.Flag(seen, "met") == true, AgentRules.Text(seen, "url") ?? "", AgentRules.Text(seen, "title") ?? ""))
                return new JsonObject { ["met"] = true, ["page"] = Handle(tab), ["url"] = AgentRules.Url(AgentRules.Text(seen, "url")), ["title"] = AgentRules.Text(seen, "title") ?? "", ["waitedMs"] = (int)(DateTimeOffset.UtcNow - began).TotalMilliseconds };
            if ((DateTimeOffset.UtcNow - began).TotalSeconds >= timeout) throw new AgentRefusal("timeout", $"Not met within {timeout:0} s", "Nothing was repeated. read_page to see where the page is.");
            await Task.Delay(250);
        }
    }

    /// A picture of its own tab only, with every secret field and whatever can't be read blacked out before it leaves:
    /// hidden in the page for the time of the picture, so a scroll between measuring and taking it can't uncover
    /// anything, and the boxes painted over as well.
    private async Task<JsonObject> Shot(JsonObject request)
    {
        var tab = TabOf(request, AgentUse.Drive);
        var width = AgentRules.ShotWidth(request);
        const string hidden = Key + "Hidden";
        var world = await WorldOf(tab);
        // Put back whatever happens, alive or not: the page is yours too.
        void PutBack() => _ = world.Evaluate(PageHands.Shown(hidden));
        try
        {
            var found = await Page(tab, PageHands.Hidden(hidden));
            double across = AgentRules.Number(found, "width") ?? 0, high = AgentRules.Number(found, "height") ?? 0;
            var png = await PageHands.Picture(world.Core, across, high, width) ?? throw new AgentRefusal("no_picture", "Chromium gave no picture");
            var boxes = (found["boxes"] as JsonArray ?? []).OfType<JsonArray>().Where(b => b.Count == 4).Select(b => b.Select(v => v?.GetValue<double>() ?? 0).ToArray()).ToArray();
            var black = await PageHands.BlackOut(png, boxes, across) ?? throw new AgentRefusal("no_picture", "No picture");
            if (!Live) throw AgentRefusal.Stopped;
            return new JsonObject { ["png"] = Convert.ToBase64String(black.Png), ["width"] = black.Width, ["height"] = black.Height, ["blackedOut"] = boxes.Length, ["page"] = Handle(tab) };
        }
        finally { PutBack(); }
    }

    internal JsonObject State(bool running) => new()
    {
        ["running"] = running, ["connected"] = true, ["state"] = Consent.State.ToString().ToLowerInvariant(), ["client"] = Client, ["program"] = Program, ["seesTabs"] = Consent.SeesTabs,
        ["own"] = _own.Count, ["auto"] = Auto, ["justAdmitted"] = JustAdmitted, ["admissions"] = _server.Admissions, ["offersAlways"] = NoAlways is null, ["noAlways"] = NoAlways ?? "",
        ["busy"] = Busy, ["workingOn"] = _workingOn?.ShortId ?? "",
        ["launcher"] = Launcher is { } l ? new JsonObject { ["path"] = l.Path, ["signer"] = l.Signer, ["signed"] = l.Signed } : new JsonObject()
    };
}
