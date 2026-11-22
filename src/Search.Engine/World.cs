using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace Search.Engine;

// Search's own scripts in a page, where the page can't reach them (WKContentWorld on the Mac). They run in an
// isolated world, through the DevTools protocol: the page shares their DOM but not their globals, can't see their
// listeners or their messages, and can't call the one way out, a binding that exists only in this world. That
// matters most for passwords, which are filled from here and must never pass through anything a page can read.
//
// Runtime.enable is never sent: sites that check for automation look for its side effects. Without it a binding
// can't be given to a world by name, so each new document's world is asked for by name (Page.createIsolatedWorld)
// and given the binding by its context. A script says something with `__searchSend({ kind: "...", ... })`, which
// holds what it says until the binding is there; the window hears it as Message(kind, body).
public sealed class World
{
    public const string Name = "search";
    /// One name per web view, so no page's world could ever be given another's.
    private readonly string _binding = "__search" + Guid.NewGuid().ToString("N")[..8];

    /// First in the world, before any of Search's scripts: the way out, and the queue in front of it.
    private string Prelude => $$"""
        (() => {
          const queue = [], out = () => globalThis[{{JsonSerializer.Serialize(_binding)}}];
          globalThis.__searchSend = message => { const text = JSON.stringify(message); typeof out() === 'function' ? out()(text) : queue.push(text); };
          globalThis.__searchReady = () => { while (queue.length) out()(queue.shift()); };
        })();
        """;

    private readonly CoreWebView2 _core;
    private string? _mainFrame;
    /// Held for as long as the world is: a receiver nothing refers to is collected, and its subscription with it.
    private CoreWebView2DevToolsProtocolEventReceiver? _calls;

    public event Action<string, JsonElement>? Message;

    /// The web view whose pages this world is in.
    public CoreWebView2 Core => _core;

    private World(CoreWebView2 core) => _core = core;

    public static async Task<World> Open(CoreWebView2 core, IEnumerable<string> scripts)
    {
        var world = new World(core);
        world._calls = core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
        world._calls.DevToolsProtocolEventReceived += (_, e) => world.OnBinding(e.ParameterObjectAsJson);
        // As soon as a new document's content starts to arrive, and again when it's parsed in case that was early.
        core.ContentLoading += (_, _) => _ = world.Attach();
        core.DOMContentLoaded += (_, _) => _ = world.Attach();
        // The Page domain has to be on for new-document scripts to run. It has no side effect a page can notice; the
        // Runtime domain, the one automation checks look for, stays off.
        await world.Call("Page.enable", new JsonObject());
        await world.Add(world.Prelude);
        foreach (var script in scripts) await world.Add(script);
        return world;
    }

    /// A script for every document from now on, and the one showing now. Returns its id, for Remove.
    public async Task<string?> Add(string source) => (await Call("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = source, ["worldName"] = Name, ["runImmediately"] = true }))?["identifier"]?.GetValue<string>();

    /// A script the next documents no longer get.
    public Task Remove(string id) => Call("Page.removeScriptToEvaluateOnNewDocument", new JsonObject { ["identifier"] = id });

    /// The binding given to this document's world, and what its scripts said while waiting sent on.
    private async Task Attach()
    {
        if (await Context() is not { } id) return;
        await Call("Runtime.addBinding", new JsonObject { ["name"] = _binding, ["executionContextId"] = id });
        await Call("Runtime.evaluate", new JsonObject { ["expression"] = "globalThis.__searchReady && __searchReady()", ["contextId"] = id });
    }

    /// Runs an expression in this world, in the page's main frame, and returns its value.
    public async Task<JsonNode?> Evaluate(string expression)
    {
        if (await Context() is not { } id) return null;
        var answer = await Call("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["contextId"] = id, ["returnByValue"] = true, ["awaitPromise"] = true });
        return answer?["result"]?["value"];
    }

    /// The world's context in the main frame. The main frame can change under a cross-site navigation: asked again once.
    private async Task<int?> Context()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _mainFrame ??= (await Call("Page.getFrameTree", new JsonObject()))?["frameTree"]?["frame"]?["id"]?.GetValue<string>();
            if (_mainFrame is null) return null;
            var context = await Call("Page.createIsolatedWorld", new JsonObject { ["frameId"] = _mainFrame, ["worldName"] = Name });
            if (context?["executionContextId"]?.GetValue<int>() is { } id) return id;
            _mainFrame = null;
        }
        return null;
    }

    private async Task<JsonNode?> Call(string method, JsonObject args)
    {
        try { return JsonNode.Parse(await _core.CallDevToolsProtocolMethodAsync(method, args.ToJsonString())); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }

    private void OnBinding(string json)
    {
        try
        {
            var call = JsonNode.Parse(json);
            if (call?["name"]?.GetValue<string>() != _binding || call["payload"]?.GetValue<string>() is not { } payload) return;
            if (JsonNode.Parse(payload) is not JsonObject body || body["kind"]?.GetValue<string>() is not { } kind) return;
            using var document = JsonDocument.Parse(body.ToJsonString());
            Message?.Invoke(kind, document.RootElement.Clone());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
    }
}
