using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Search.Core;
using Search.Engine;

namespace Search.Engine.Tests;

/// A coding agent's connection (AgentServer, AgentSession) over a real pipe of a test's own, to a stand-in for the
/// window: the question first and nothing before it, one agent at a time, a pause after a no, Stop, idle, its own
/// tabs and your tabs only if you said so. Pages aren't here (no web view): what needs one is refused as it would be.
public class AgentServerTests
{
    private sealed class MemoryVault : IAgentVault
    {
        public byte[]? Data;
        public bool Usable => true;
        public byte[]? Read() => Data;
        public bool Write(byte[] data) { Data = data; return true; }
        public void Erase() => Data = null;
    }

    /// The window, as far as a session can tell.
    private sealed class Host : IAgentHost
    {
        public event Action? Closed;
        public List<Tab> Row { get; } = [];
        public IReadOnlyList<Tab> Tabs => Row;
        public AgentAnswer Answer { get; set; } = new(Allow: true);
        public TimeSpan Thinks { get; set; } = TimeSpan.FromSeconds(0.6);
        public ConcurrentQueue<AgentQuestion> Asked { get; } = new();
        public ConcurrentQueue<AgentShown?> Shown { get; } = new();
        public ConcurrentQueue<Tab?> Marks { get; } = new();
        public Task<Tab> Open(Uri url) { var tab = new Tab { Bench = true, Agent = true, Url = url, Title = "Opened" }; Row.Add(tab); return Task.FromResult(tab); }
        public void Close(Tab tab) => Row.Remove(tab);
        public Task<World?> WorldOf(Tab tab, bool build) => Task.FromResult<World?>(null);
        public async Task<AgentAnswer> Ask(AgentQuestion question, CancellationToken gone) { Asked.Enqueue(question); await Task.Delay(Thinks, gone); return Answer; }
        public void Working(Tab? tab) => Marks.Enqueue(tab);
        public void Show(AgentShown? shown) => Shown.Enqueue(shown);
        public void Shut() => Closed?.Invoke();
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private int _n;
        private Client(NamedPipeClientStream pipe) { _pipe = pipe; _reader = new StreamReader(pipe, Encoding.UTF8); }

        public static async Task<Client> Connect(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return new Client(pipe);
        }

        public async Task Send(JsonObject request) { request["n"] = ++_n; await Raw(request.ToJsonString() + "\n"); }
        public async Task Raw(string text) { await _pipe.WriteAsync(Encoding.UTF8.GetBytes(text)); await _pipe.FlushAsync(); }

        /// The next line, or null once the pipe is closed.
        public async Task<JsonObject?> Next(double seconds = 10)
        {
            try { return await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(seconds)) is { } line ? JsonNode.Parse(line)!.AsObject() : null; }
            catch (IOException) { return null; }
        }

        public async Task<JsonObject> Ask(string verb, JsonObject? args = null)
        {
            var request = args ?? new JsonObject();
            request["do"] = verb;
            await Send(request);
            return await Next() ?? throw new IOException("closed");
        }

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }

    private static string Code(JsonObject? answer) => answer?["error"]?["code"]?.GetValue<string>() ?? "";

    private static (AgentServer Server, Host Host, string Pipe) Serve(AgentTrust? trust = null, Host? host = null)
    {
        var pipe = $"SearchAgentTests-{Guid.NewGuid():N}\\agent";
        host ??= new Host();
        var server = new AgentServer(pipe, () => host, trust ?? new AgentTrust(new MemoryVault()));
        server.Start();
        return (server, host, pipe);
    }

    private static async Task<Client> Allowed(string pipe)
    {
        var client = await Client.Connect(pipe);
        Assert.True((await client.Ask("hello", new() { ["client"] = "Claude Code" }))["allowed"]!.GetValue<bool>());
        return client;
    }

    [Fact]
    public async Task NothingIsReadUntilThePersonAnswers()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        await using var client = await Client.Connect(pipe);
        Assert.Equal("hello_first", Code(await client.Ask("open", new() { ["url"] = "https://example.test/" })));
        var clock = Stopwatch.StartNew();
        await client.Send(new JsonObject { ["do"] = "hello", ["client"] = "Claude\u0007 Code" });
        await client.Send(new JsonObject { ["do"] = "status" });
        var hello = await client.Next();
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.5), "answered by the person, not at once");
        Assert.Equal(2, hello!["n"]!.GetValue<int>());
        Assert.True(hello["allowed"]!.GetValue<bool>());
        Assert.False(hello["seesTabs"]!.GetValue<bool>());
        var status = await client.Next();
        Assert.Equal(3, status!["n"]!.GetValue<int>());
        Assert.Equal("Claude Code", status["client"]!.GetValue<string>());
        Assert.True(host.Asked.TryPeek(out var question));
        Assert.Equal("Claude Code", question.Client);
        Assert.Contains(Path.GetFileName(Environment.ProcessPath!), question.Program, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(question.NoAlways);
        Assert.Contains(host.Shown, s => s is { Client: "Claude Code", Auto: false });
        Assert.Equal("said_hello", Code(await client.Ask("hello")));
    }

    [Fact]
    public async Task ItWorksInTabsOfItsOwn()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        var yours = new Tab { Url = new Uri("https://yours.test/"), Title = "Yours" };
        host.Row.Add(yours);
        await using var client = await Allowed(pipe);
        Assert.Equal("refused_address", Code(await client.Ask("open", new() { ["url"] = "file:///C:/Windows/win.ini" })));
        Assert.Equal("refused_address", Code(await client.Ask("open", new() { ["url"] = "search://settings" })));
        var opened = await client.Ask("open", new() { ["url"] = "example.test/sign-in?pw=hunter2" });
        Assert.Equal("p1", opened["page"]!.GetValue<string>());
        Assert.Equal("https://example.test/sign-in?pw=redacted", opened["url"]!.GetValue<string>());
        Assert.True(host.Row[^1].Agent);
        var tabs = (await client.Ask("tabs"))["tabs"]!.AsArray();
        Assert.Single(tabs);
        Assert.True(tabs[0]!["mine"]!.GetValue<bool>());
        Assert.Equal("Opened", (await client.Ask("go", new() { ["page"] = "p1", ["url"] = "https://example.test/next" }))["title"]!.GetValue<string>());
        Assert.Equal(new Uri("https://example.test/next"), host.Row[^1].Url);
        Assert.Contains(host.Marks, m => m == host.Row[^1]);
        // What needs a page is refused as it would be without one.
        Assert.Equal("page", Code(await client.Ask("read", new() { ["page"] = "p1" })));
        Assert.Equal("stale_page", Code(await client.Ask("read", new() { ["page"] = "p9" })));
        Assert.Equal("bad_handle", Code(await client.Ask("click", new() { ["element"] = "button.go" })));
        Assert.Equal("stale_element", Code(await client.Ask("click", new() { ["element"] = "p1.s1.e0" })));
        Assert.Equal("bad_key", Code(await client.Ask("press", new() { ["page"] = "p1", ["key"] = "ctrl+v" })));
        Assert.Equal("bad_condition", Code(await client.Ask("wait_for", new() { ["page"] = "p1", ["selector"] = "input[value^=a]" })));
        Assert.Equal("no_verb", Code(await client.Ask("eval", new() { ["page"] = "p1", ["script"] = "1" })));
        Assert.Equal("p1", (await client.Ask("close", new() { ["page"] = "p1" }))["closed"]!.GetValue<string>());
        Assert.Equal("stale_page", Code(await client.Ask("read", new() { ["page"] = "p1" })));
        Assert.True((await client.Ask("bye"))["bye"]!.GetValue<bool>());
        Assert.Null(await client.Next());
        Assert.Same(yours, Assert.Single(host.Row));
        await Until(() => server.Session is null);
        Assert.NotEmpty(host.Shown);
        Assert.Null(host.Shown.Last());
    }

    [Fact]
    public async Task YourTabsAreSeenOnlyIfYouSaidAndNeverUsed()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        host.Answer = new AgentAnswer(Allow: true, SeesTabs: true);
        host.Row.Add(new Tab { Url = new Uri("https://yours.test/back?token=abc"), Title = "Yours" });
        host.Row.Add(new Tab { Url = new Uri("https://private.test/"), Private = true });
        host.Row.Add(new Tab { Url = new Uri("https://script.test/"), Bench = true });
        await using var client = await Client.Connect(pipe);
        Assert.True((await client.Ask("hello", new() { ["client"] = "Claude Code" }))["seesTabs"]!.GetValue<bool>());
        var tabs = (await client.Ask("tabs"))["tabs"]!.AsArray();
        var tab = Assert.Single(tabs)!.AsObject();
        Assert.False(tab["mine"]!.GetValue<bool>());
        Assert.Equal("https://yours.test/back?token=redacted", tab["url"]!.GetValue<string>());
        var page = tab["page"]!.GetValue<string>();
        Assert.Equal("not_yours", Code(await client.Ask("go", new() { ["page"] = page, ["url"] = "https://elsewhere.test/" })));
        Assert.Equal("not_yours", Code(await client.Ask("shot", new() { ["page"] = page })));
        Assert.Equal("not_yours", Code(await client.Ask("close", new() { ["page"] = page })));
        Assert.Equal("asleep", Code(await client.Ask("read", new() { ["page"] = page })));
        Assert.Equal("bad_condition", Code(await client.Ask("wait_for", new() { ["page"] = page, ["text"] = "x" })));
        Assert.Equal("bad_condition", Code(await client.Ask("wait_for", new() { ["page"] = page, ["role"] = "button" })));
        Assert.False(Code(await client.Ask("wait_for", new() { ["page"] = page, ["url"] = "token=a", ["timeout"] = 0.5 })) == "", "its secret is never something to wait on");
        Assert.True((await client.Ask("wait_for", new() { ["page"] = page, ["url"] = "/back", ["title"] = "Yours" }))["met"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WithoutTheTickYourTabsArentThere()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        host.Row.Add(new Tab { Url = new Uri("https://yours.test/"), Title = "Yours" });
        await using var client = await Allowed(pipe);
        Assert.Empty((await client.Ask("tabs"))["tabs"]!.AsArray());
    }

    [Fact]
    public async Task OneAgentAtATime()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        host.Thinks = TimeSpan.FromSeconds(30);
        await using var first = await Client.Connect(pipe);
        await first.Send(new JsonObject { ["do"] = "hello", ["client"] = "First" });
        await Until(() => !host.Asked.IsEmpty);
        await using var second = await Client.Connect(pipe);
        Assert.Equal("busy", Code(await second.Next()));
        Assert.Null(await second.Next());
    }

    [Fact]
    public async Task DontAllowThenNobodyAsksForAWhile()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        host.Answer = new AgentAnswer(Allow: false, SeesTabs: true, Always: true);
        await using var client = await Client.Connect(pipe);
        Assert.Equal("not_allowed", Code(await client.Ask("hello", new() { ["client"] = "Claude Code" })));
        Assert.Null(await client.Next());
        await using var again = await Client.Connect(pipe);
        Assert.Equal("not_now", Code(await again.Next()));
        Assert.Empty(server.Trust.Remembered);
    }

    [Fact]
    public async Task AQuestionWalkedAwayFromIsQuietToo()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        host.Thinks = TimeSpan.FromSeconds(30);
        var client = await Client.Connect(pipe);
        await client.Send(new JsonObject { ["do"] = "hello", ["client"] = "Claude Code" });
        await Until(() => !host.Asked.IsEmpty);
        await client.DisposeAsync();
        await Until(() => server.Session is null);
        await using var again = await Client.Connect(pipe);
        Assert.Equal("not_now", Code(await again.Next()));
    }

    [Fact]
    public async Task StopEndsItAndAsksAgainFromThenOn()
    {
        var trust = new AgentTrust(new MemoryVault());
        Assert.True(trust.RememberSimulated("Claude Code", "nothing passes this", testing: true));
        var (server, _, pipe) = Serve(trust);
        using var _s = server;
        await using var client = await Allowed(pipe);
        server.Stop();
        Assert.Null(await client.Next());
        Assert.True(trust.Remembered[0].Suspended, "Stop suspends what is remembered under its name");
        await using var again = await Client.Connect(pipe);
        Assert.Equal("not_now", Code(await again.Next()));
    }

    [Fact]
    public async Task TurningTheSettingOffEndsIt()
    {
        var (server, _, pipe) = Serve();
        await using var client = await Allowed(pipe);
        server.Dispose();
        Assert.Null(await client.Next());
        Assert.False(server.Running);
    }

    [Fact]
    public async Task ItsWindowClosingEndsIt()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        await using var client = await Allowed(pipe);
        host.Shut();
        Assert.Null(await client.Next());
    }

    [Fact]
    public async Task NoWindowNoAgent()
    {
        var pipe = $"SearchAgentTests-{Guid.NewGuid():N}\\agent";
        using var server = new AgentServer(pipe, () => null, new AgentTrust(new MemoryVault()));
        server.Start();
        await using var client = await Client.Connect(pipe);
        Assert.Equal("no_window", Code(await client.Ask("hello", new() { ["client"] = "Claude Code" })));
        Assert.Null(await client.Next());
    }

    [Fact]
    public async Task IdleIsLetGo()
    {
        var (server, _, pipe) = Serve();
        using var _s = server;
        server.IdleAfter = TimeSpan.FromSeconds(0.2);
        server.LetGoAfter = TimeSpan.FromSeconds(1);
        await using var client = await Allowed(pipe);
        Assert.Equal("idle", Code(await client.Next()));
        Assert.Null(await client.Next());
        // Let go isn't a no: the next one is asked straight away.
        await using var again = await Allowed(pipe);
    }

    [Fact]
    public async Task SayingNothingIsLetGo()
    {
        var (server, _, pipe) = Serve();
        using var _s = server;
        server.HelloWithin = TimeSpan.FromSeconds(0.3);
        await using var client = await Client.Connect(pipe);
        Assert.Null(await client.Next(5));
    }

    [Fact]
    public async Task TooMuchAtOnceIsLetGo()
    {
        var (server, host, pipe) = Serve();
        using var _ = server;
        host.Thinks = TimeSpan.FromSeconds(30);
        await using var client = await Client.Connect(pipe);
        await client.Raw(string.Concat(Enumerable.Repeat("{\"do\":\"status\"}\n", 40)));
        // Before hello, each is refused at once; after it, they wait for the answer and pile up.
        await client.Raw("{\"do\":\"hello\"}\n" + string.Concat(Enumerable.Repeat("{\"do\":\"status\"}\n", 40)));
        JsonObject? line;
        var codes = new List<string>();
        while ((line = await client.Next()) is not null) codes.Add(Code(line));
        Assert.Contains("too_much", codes);
    }

    [Fact]
    public async Task NotJsonIsSaidSo()
    {
        var (server, _, pipe) = Serve();
        using var _s = server;
        await using var client = await Client.Connect(pipe);
        await client.Raw("not json\n[1,2]\n");
        Assert.Equal("bad_request", Code(await client.Next()));
        Assert.Equal("bad_request", Code(await client.Next()));
    }

    [Fact]
    public async Task RememberedIsLetInWithoutAsking()
    {
        // What started this test run, as the agent's launcher would be read: a simulated entry for it, as the bench
        // makes in a test world, lets it in without the question.
        using var self = Process.GetCurrentProcess();
        if (AgentIdentity.Parent(self.SafeHandle) is not { } parentPid) return;
        using var parent = Process.GetProcessById(parentPid);
        var path = AgentIdentity.PathOf(parent.SafeHandle) ?? "";
        var (signed, signer) = AgentIdentity.Signature(path);
        if (!signed) return;
        var trust = new AgentTrust(new MemoryVault());
        trust.RememberSimulated("Claude Code", AgentLauncher.Requirement(Path.GetFileName(path), signer), testing: true);
        var (server, host, pipe) = Serve(trust);
        using var _ = server;
        await using var client = await Client.Connect(pipe);
        var hello = await client.Ask("hello", new() { ["client"] = "Claude Code" });
        Assert.True(hello["remembered"]!.GetValue<bool>());
        Assert.False(hello["seesTabs"]!.GetValue<bool>(), "your tabs never without a click of yours");
        Assert.Empty(host.Asked);
        Assert.Contains(host.Shown, s => s is { Auto: true, JustAdmitted: true });
        Assert.Equal(1, server.Admissions);
        // Another name is asked.
        await client.Ask("bye");
        await Until(() => server.Session is null);
        await using var other = await Client.Connect(pipe);
        Assert.True((await other.Ask("hello", new() { ["client"] = "Someone Else" }))["allowed"]!.GetValue<bool>());
        Assert.Single(host.Asked);
    }

    [Fact]
    public void ThePipeIsTheStoreFoldersOwn()
    {
        Assert.Equal(@"Search\agent", AgentServer.PipeFor(@"C:\Users\x\AppData\Local\Search"));
        Assert.Equal(@"Search (test)\agent", AgentServer.PipeFor(@"C:\Users\x\AppData\Local\Search (test)\"));
    }

    [Fact]
    public void TheSameWorldTwiceIsRefused()
    {
        var (server, _, pipe) = Serve();
        using var _s = server;
        using var twice = new AgentServer(pipe, () => null, new AgentTrust(new MemoryVault()));
        Assert.True(Record.Exception(twice.Start) is IOException or UnauthorizedAccessException);
    }

    private static async Task Until(Func<bool> done, double seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(20); }
    }
}
