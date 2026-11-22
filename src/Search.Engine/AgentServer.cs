using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Search.Core;

namespace Search.Engine;

// A coding agent on this PC (Claude Code) using the window you already have open, when you let it (AgentControl.swift).
//
// Off unless switched on in Settings › About › Developer, and the switch leaves a mark beside the setting
// (SwitchMark): a setting some other program wrote is put back to off at launch. On, Search listens on a named pipe,
// \\.\pipe\<store folder name>\agent, as the bench does (BenchServer, and why a pipe rather than a socket file): its
// ACL lets only you connect, and the other end is checked again for your user before a byte is read, and opened by
// the pid the pipe names. Never on the network. `search-mcp` at the root of the repository is the client an agent runs.
//
// Each connection asks first. Its first line says who it is, and the window shows "<name> wants to use Search" with
// Allow and Don't Allow; nothing else it sends is looked at until you answer, and only a click in that window
// answers. Allowed, it lasts as long as that connection does, or until Stop, or its window closes, or it asks nothing
// for five minutes. The next connection asks again, unless you ticked "Always allow" for it (AgentTrust). One agent at a
// time; after Don't Allow, Stop, or a question left unanswered, nobody asks again for a few seconds.
//
// The wire protocol is the Mac's: one JSON object per line each way, {"do": verb, "n": number, ...}, every answer
// carrying the request's n; {"error": {"code", "message", "hint"}} when it won't. The verbs are AgentSession's.
public sealed class AgentServer : IDisposable
{
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly SynchronizationContext _ui;
    private bool _running, _disposed;

    internal Func<IAgentHost?> Front { get; }
    internal AgentGate Gate { get; } = new();
    public AgentTrust Trust { get; }

    /// `front`: the window an agent that says hello will work in (the one in front), or null with none open. `ui`: the
    /// window's thread, where sessions run and the host is called; a thread of the server's own if none is given.
    public AgentServer(string name, Func<IAgentHost?> front, AgentTrust trust, SynchronizationContext? ui = null)
    {
        _name = name;
        Front = front;
        Trust = trust;
        _ui = ui ?? new AgentLoop();
    }

    /// The pipe's name for a store folder: "Search" → \\.\pipe\Search\agent.
    public static string PipeFor(string folder) => Path.GetFileName(folder.TrimEnd('\\', '/')) + "\\agent";

    public bool Running => _running;
    /// The one connection there is, asking or allowed.
    public AgentSession? Session { get; private set; }
    /// A session began or ended, or changed how it stands: for Settings and the bench.
    public event Action? Changed;
    /// Where what happens is written, one line each, never with what a page says.
    public Action<string> Log { get; set; } = line => System.Diagnostics.Debug.WriteLine("agent: " + line);
    /// How many agents were let in without asking: for the bench.
    public int Admissions { get; internal set; }

    // Shorter in a test run, for the bench.
    public TimeSpan IdleAfter { get; set; } = AgentRules.Idle;
    public TimeSpan LetGoAfter { get; set; } = AgentRules.LetGo;
    public TimeSpan HelloWithin { get; set; } = AgentRules.HelloWithin;

    /// Fails with IOException or UnauthorizedAccessException if another process already answers on this pipe (the same
    /// world open twice), as BenchServer does.
    public void Start()
    {
        if (_running || _disposed) return;
        var first = Listen(first: true);
        _running = true;
        _ = Accept(first);
        Log("listening");
    }

    /// Stop: the column's foot, the strip, the row, the menu. Stopped, it is never let in again without asking, until
    /// "Always allow" is ticked for it again.
    public void Stop() => OnUi(() => Session?.End("stopped from the window"));

    /// A tab an agent's tab opened (Pages.Opened): its own too.
    public void Adopted(Tab tab, Tab opener) => OnUi(() => Session?.Adopt(tab, opener));

    /// The setting turned off: the session ends and nothing more is listened to.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnUi(() => Session?.End("the setting was turned off"));
        _stop.Cancel();
        if (_running) Log("closed");
        _running = false;
    }

    /// For the bench's `agents`.
    public JsonObject State => Session is { } s ? s.State(_running) : new JsonObject { ["running"] = _running, ["connected"] = false, ["admissions"] = Admissions };

    private NamedPipeServerStream Listen(bool first) => new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));

    private async Task Accept(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { await pipe.DisposeAsync(); return; }
            var connected = pipe;
            // The next caller gets a fresh instance while this one is looked at.
            try { pipe = Listen(first: false); } catch (IOException) { Arrived(connected); return; }
            Arrived(connected);
        }
        await pipe.DisposeAsync();
    }

    /// Only you: the pipe's ACL already says so; this says it again, by the user the connecting process runs as, and
    /// opens that process before anything it sends is read.
    private void Arrived(NamedPipeServerStream pipe)
    {
        var peer = AgentIdentity.Peer(pipe.SafePipeHandle);
        if (peer is null) { pipe.Dispose(); Log("refused a connection from another user, or one that couldn't be told"); return; }
        var program = AgentIdentity.Program(peer);
        OnUi(() =>
        {
            if ((_disposed ? AgentRefusal.Stopped : Gate.Admit(Session is not null, DateTimeOffset.UtcNow)) is { } refusal)
            {
                _ = Refuse(pipe, peer, refusal);
                Log("refused a connection: " + refusal.Code);
                return;
            }
            Session = new AgentSession(this, pipe, peer, program);
            Log("connected: " + program);
            Session.Begin();
            Changed?.Invoke();
        });
    }

    /// One small answer to a connection that never becomes a session.
    private static async Task Refuse(NamedPipeServerStream pipe, AgentPeer peer, AgentRefusal refusal)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(refusal.Said.ToJsonString() + "\n"), limit.Token).ConfigureAwait(false);
            await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { await pipe.DisposeAsync(); peer.Dispose(); }
    }

    internal void Ended(AgentSession session, AgentState was, bool stopped)
    {
        if (Session != session) return;
        Session = null;
        Gate.Ended(was, stopped, DateTimeOffset.UtcNow);
        Changed?.Invoke();
    }

    internal void Touched() => Changed?.Invoke();

    /// On the window's thread: at once when already there, posted otherwise.
    internal void OnUi(Action work)
    {
        if (SynchronizationContext.Current == _ui) { Safely(work); return; }
        _ui.Post(_ => Safely(work), null);
    }

    private void Safely(Action work) { try { work(); } catch (Exception e) { Log("failed: " + e); } }
}

/// A thread of the server's own to run sessions on, when it is given no window's thread (the tests).
internal sealed class AgentLoop : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = new();

    public AgentLoop()
    {
        var thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (work, state) in _queue.GetConsumingEnumerable())
                try { work(state); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
        }) { IsBackground = true, Name = "Agents" };
        thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Agents' thread is only posted to.");
    public override SynchronizationContext CreateCopy() => this;
}
