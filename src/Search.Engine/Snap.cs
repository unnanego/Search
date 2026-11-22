using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Search.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Search.Engine;

/// How a capture ended: a picture, put away with nothing taken (Escape, the command again, another page, another
/// capture), a choice with nothing there (a click on nothing, an area off the page), or no picture from Chromium.
public enum SnapEnd { Taken, Cancelled, NothingThere, Failed }

/// What Take gives back. Png, Width and Height (the picture's own pixels, as decoded) only when Taken; Cropped for a
/// page cut to its top (SnapMath.Tallest). Notice is what the window says for an end with no picture, if anything.
public sealed record SnapResult(SnapEnd End, SnapKind? Kind = null, byte[]? Png = null, int Width = 0, int Height = 0, bool Cropped = false)
{
    public string? Notice => End switch { SnapEnd.NothingThere => SnapMath.NothingThere, SnapEnd.Failed => SnapMath.Failed, _ => null };
}

// Capture, the page's side (Snap.swift's Browser.toggleSnap, stopSnapping and snap). One light layer over a tab's page
// (snap.js), put in only when it's asked for, in Search's own world (World.cs) where the page can't see it or what it
// says. It answers with what was chosen and is out of the page by then; two frames later, so it's gone from what is
// painted too, Chromium draws the choice through the DevTools protocol (Page.captureScreenshot): in bands stacked here
// when it's taller than Chromium draws at once (SnapMath). Copying the picture and saving it are the window's: see
// SnapMath.Keep, Marks and Notice.
//
// One layer at a time, per window: a second Take puts the first away with nothing taken, as do Stop (Escape heard by
// the window, the command again, another tab), Escape or esc in the page, a navigation, and the tab closing or sleeping.
public sealed class Snap
{
    private sealed class Raised(Tab tab, int id)
    {
        public Tab Tab { get; } = tab;
        public int Id { get; } = id;
        public TaskCompletionSource<SnapChoice?> Chosen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Pages _pages;
    private Raised? _up;
    private int _count;

    /// Two frames drawn, or a quarter of a second if the page draws none: the layer, taken out, is gone from the paint.
    private const string Settle = "new Promise(done => { let over = false; const go = () => { if (!over) { over = true; done(true); } }; requestAnimationFrame(() => requestAnimationFrame(go)); setTimeout(go, 250); })";

    public Snap(Pages pages)
    {
        _pages = pages;
        pages.Released += tab => { if (_up is { } up && up.Tab == tab) End(up, null); };
    }

    /// Whether the layer is up, and on which tab's page.
    public bool Live => _up is not null;
    public Tab? Tab => _up?.Tab;

    /// The layer up over the tab's page until something is chosen or it's put away, then the picture. `accent` is the
    /// outline's colour as "r, g, b" (Windows' accent colour; its blue if null). Never throws for anything the page or
    /// Chromium does: a tab with no page, or a layer that couldn't go up, is Failed. Cancelling `cancel` is Stop.
    public async Task<SnapResult> Take(Tab tab, string? accent = null, CancellationToken cancel = default)
    {
        Stop();
        if (_pages.WorldOf(tab) is not { } world) return new(SnapEnd.Failed);
        var up = _up = new Raised(tab, ++_count);
        void Heard(string kind, JsonElement body) { if (Hear(kind, body, up.Id, out var choice)) End(up, choice); }
        void Navigating(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e) => Down(up);
        world.Message += Heard;
        world.Core.NavigationStarting += Navigating;
        using var stop = cancel.Register(() => Down(up), useSynchronizationContext: true);
        try
        {
            var raised = await world.Evaluate(Scripts.Read("snap.js") + $"\n;globalThis.__searchSnap.on({up.Id}, {Scripts.Literal(accent)})");
            if (raised is not JsonValue value || !value.TryGetValue<bool>(out var on) || !on)
            {
                var ended = up.Chosen.Task.IsCompleted;
                End(up, null);
                return new(ended ? SnapEnd.Cancelled : SnapEnd.Failed);
            }
            if (await up.Chosen.Task is not { } choice) return new(SnapEnd.Cancelled);
            return await Capture(world, choice);
        }
        finally
        {
            world.Message -= Heard;
            world.Core.NavigationStarting -= Navigating;
            End(up, null);
        }
    }

    /// The layer put away with nothing taken. False if none was up.
    public bool Stop()
    {
        if (_up is not { } up) return false;
        Down(up);
        return true;
    }

    private void Down(Raised up)
    {
        if (up.Chosen.Task.IsCompleted) return;
        _ = _pages.Evaluate(up.Tab, $"globalThis.__searchSnap && __searchSnap.off({up.Id})");
        End(up, null);
    }

    private void End(Raised up, SnapChoice? choice)
    {
        if (_up == up) _up = null;
        up.Chosen.TrySetResult(choice);
    }

    /// A message from the layer raised as `id`: true if it's its answer, with the choice, or null for one put away (or
    /// one that isn't a choice at all). Another layer's, or anything else, is false.
    internal static bool Hear(string kind, JsonElement body, int id, out SnapChoice? choice)
    {
        choice = null;
        if (kind is not ("snap.chosen" or "snap.off") || body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("id", out var said) || said.ValueKind != JsonValueKind.Number || !said.TryGetInt32(out var heard) || heard != id) return false;
        if (kind == "snap.chosen") choice = SnapChoice.From(body);
        return true;
    }

    private static async Task<SnapResult> Capture(World world, SnapChoice choice)
    {
        await world.Evaluate(Settle);
        var core = world.Core;
        // A whole page as Chromium has it laid out: what it can draw.
        if (choice.Kind == SnapKind.Page && await Cdp(core, "Page.getLayoutMetrics", new JsonObject()) is { } metrics
            && metrics["cssContentSize"] is JsonObject content && Number(content["width"]) is double width and > 0 && Number(content["height"]) is double height and > 0)
            choice = choice with { DocumentWidth = width, DocumentHeight = height };
        if (SnapMath.Plan(choice) is not { } plan) return new(SnapEnd.NothingThere, choice.Kind);
        var shots = new List<byte[]>();
        foreach (var band in plan.Bands)
        {
            if (await Shot(core, band.Clip, plan.Beyond) is not { } shot) return new(SnapEnd.Failed, choice.Kind);
            shots.Add(shot);
        }
        var png = shots.Count == 1 ? shots[0] : await Stack(shots);
        if (png is null || SnapMath.PngSize(png) is not { } size) return new(SnapEnd.Failed, choice.Kind);
        return new(SnapEnd.Taken, choice.Kind, png, size.Width, size.Height, plan.Cropped);
    }

    /// One clip drawn by Chromium, as PNG; null if it gave none. Beyond the viewport only when some of it is off screen:
    /// drawing beyond lays the page out as tall as it is for a moment, which a page's 100vh notices.
    private static async Task<byte[]?> Shot(CoreWebView2 core, SnapClip clip, bool beyond)
    {
        var args = new JsonObject
        {
            ["format"] = "png", ["captureBeyondViewport"] = beyond, ["fromSurface"] = true,
            ["clip"] = new JsonObject { ["x"] = clip.X, ["y"] = clip.Y, ["width"] = clip.Width, ["height"] = clip.Height, ["scale"] = clip.Scale }
        };
        if ((await Cdp(core, "Page.captureScreenshot", args))?["data"]?.GetValue<string>() is not { } data) return null;
        try { var png = Convert.FromBase64String(data); return SnapMath.PngSize(png) is null ? null : png; } catch (FormatException) { return null; }
    }

    /// Bands of one picture, top first, as one PNG: as wide as the narrowest, as tall as all of them. Null if any
    /// doesn't decode.
    internal static async Task<byte[]?> Stack(IReadOnlyList<byte[]> bands)
    {
        try
        {
            var decoded = new List<(byte[] Pixels, int Width, int Height)>();
            foreach (var band in bands)
            {
                using var input = new InMemoryRandomAccessStream();
                await input.WriteAsync(band.AsBuffer());
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                decoded.Add((pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight));
            }
            if (decoded.Count == 0) return null;
            int width = decoded.Min(d => d.Width), height = decoded.Sum(d => d.Height), row = 0;
            var all = new byte[(long)width * height * 4];
            foreach (var (pixels, across, high) in decoded)
                for (var y = 0; y < high; y++, row++) System.Buffer.BlockCopy(pixels, y * across * 4, all, row * width * 4, width * 4);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, all);
            await encoder.FlushAsync();
            output.Seek(0);
            using var read = output.AsStreamForRead();
            using var bytes = new MemoryStream();
            await read.CopyToAsync(bytes);
            return bytes.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or OverflowException) { return null; }
    }

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static async Task<JsonNode?> Cdp(CoreWebView2 core, string method, JsonObject args)
    {
        try { return JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync(method, args.ToJsonString())); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException) { return null; }
    }
}
