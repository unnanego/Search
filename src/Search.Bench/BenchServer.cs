using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Search.Bench;

// A way for a script on this machine to drive the browser you already have open (Bench.swift), with the same wire
// protocol so ./bench, bench-mcp and the Python tests carry over: one JSON object per line in, one per line out, one
// request per connection. `{"do": verb, ...}` asks; a flat object answers, `{"error": "..."}` when it can't.
//
// The Mac listens on an AF_UNIX socket file; here it is a named pipe, \\.\pipe\<store folder name>\bench, so a test
// world's pipe is its own as its folder is. AF_UNIX socket files on Windows can outlive their socket in a state no
// one can delete or bind again, which a fixed path can't survive. A pipe leaves nothing on disk, and CurrentUserOnly
// lets only a process running as the same Windows user connect: the Mac's peer-uid check.
public sealed class BenchServer : IDisposable
{
    private const int MaxRequest = 4_000_000;
    private readonly string _name;
    private readonly Func<JsonObject, Task<JsonObject>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private bool _running;

    public BenchServer(string name, Func<JsonObject, Task<JsonObject>> handle) { _name = name; _handle = handle; }

    /// The pipe's name for a store folder: "Search" → \\.\pipe\Search\bench.
    public static string PipeFor(string folder) => Path.GetFileName(folder.TrimEnd('\\', '/')) + "\\bench";

    public bool Running => _running;

    /// Fails if another process already answers on this pipe (the same world open twice).
    public void Start()
    {
        if (_running) return;
        var first = Listen(first: true);
        _running = true;
        _ = Accept(first);
    }

    private NamedPipeServerStream Listen(bool first) => new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));

    private async Task Accept(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(_stop.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { await pipe.DisposeAsync(); return; }
            var connected = pipe;
            // The next caller gets a fresh instance while this one is answered.
            try { pipe = Listen(first: false); } catch (IOException) { _ = Serve(connected); return; }
            _ = Serve(connected);
        }
        await pipe.DisposeAsync();
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        var line = await ReadLine(pipe);
        JsonObject answer;
        if (line is null) answer = Error("request too long");
        else if (TryParse(line) is not { } request) answer = Error("not a JSON object");
        else
        {
            var limit = request["do"] is JsonValue verb && verb.TryGetValue<string>(out var v) && v == "wait" ? (request["seconds"] is JsonValue s && s.TryGetValue<double>(out var sec) ? sec : 20) + 5 : 25;
            var work = _handle(request);
            answer = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(limit))) == work ? await work : Error($"no answer within {limit:0} s");
        }
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n"));
            await pipe.FlushAsync();
            pipe.WaitForPipeDrain();
        }
        catch (IOException) { }
    }

    private static async Task<string?> ReadLine(Stream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            int n;
            try { n = await stream.ReadAsync(chunk); } catch (IOException) { break; }
            if (n == 0) break;
            var newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
            buffer.Write(chunk, 0, newline >= 0 ? newline : n);
            if (newline >= 0) break;
            if (buffer.Length > MaxRequest) return null;
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonObject? TryParse(string line) { try { return JsonNode.Parse(line) as JsonObject; } catch (JsonException) { return null; } }

    public static JsonObject Error(string message) => new() { ["error"] = message };

    public void Dispose() { _stop.Cancel(); _running = false; }
}
