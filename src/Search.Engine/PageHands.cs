using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Search.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Search.Engine;

// What the bench and coding agents (AgentServer) share for working a page (PageHands.swift): the scripts that read it
// and touch one element of a read (page-hands.js, in Search's isolated world), real clicks and keys handed to its web
// view, and a picture of it with what must not be seen painted over. Nothing here decides who may do what: each
// caller does that for itself, and the agent's rules are the stricter ones.
//
// Input goes through the DevTools protocol (Input.dispatchMouseEvent and dispatchKeyEvent): the page sees it as real
// (isTrusted), it never moves your pointer or takes your keyboard, and it reaches only that page's renderer, never the
// app's own keys. Pictures are Chromium's own capture (Page.captureScreenshot), which works whether or not the tab is
// in front, blacked out here with Windows' imaging, not in the page.
public static class PageHands
{
    // The scripts, each the one function in page-hands.js called with JSON arguments.

    private static string Call(string verb, JsonObject args) => Scripts.Read("page-hands.js").TrimEnd().TrimEnd(';') + "(" + Scripts.Literal(verb) + ", " + args.ToJsonString() + ")";

    /// The page's visible controls and headings, with the element list kept under `key` for Touch.
    public static string Read(string key, int snap, int most, int longest, bool text) => Call("read", new() { ["key"] = key, ["snap"] = snap, ["most"] = most, ["longest"] = longest, ["text"] = text });

    /// One element of the last read under `key`: "click", "press", "scroll", "focus", "set", "submit" or "verify".
    /// `strict` refuses file fields. A null text for "verify" checks against what "focus" kept.
    public static string Touch(string key, int snap, int index, string how, string? text, bool replace, bool strict = false) =>
        Call("touch", new() { ["key"] = key, ["snap"] = snap, ["index"] = index, ["how"] = how, ["text"] = text, ["replace"] = replace, ["strict"] = strict });

    /// Whether wait_for's condition holds in the page: text, role, name.
    public static string Check(JsonObject condition) => Call("check", new() { ["condition"] = condition.DeepClone() });

    /// Where a picture must be blacked out, in CSS pixels of the viewport.
    public static string Secrets => Call("secrets", new());

    /// What a picture must not show made invisible for the time it is taken, and where it was, kept under `key`.
    public static string Hidden(string key) => Call("hidden", new() { ["key"] = key });

    /// What Hidden made invisible, as it was.
    public static string Shown(string key) => Call("shown", new() { ["key"] = key });

    /// The page scrolled by so many CSS pixels, or to its "top" or "bottom".
    public static string Scroll(double dx, double dy, string? to) => Call("scroll", new() { ["dx"] = dx, ["dy"] = dy, ["to"] = to });

    /// A script's answer, a JSON string, as an object; null when the page gave nothing back (no world, a script error).
    public static async Task<JsonObject?> Run(World world, string script) =>
        await world.Evaluate(script) is JsonValue value && value.TryGetValue<string>(out var text) ? Parse(text) : null;

    public static JsonObject? Parse(string text) { try { return JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; } }

    // Hands.

    /// A click at a point in the page's own top-left CSS pixels: the pointer moved there in the page alone, pressed and
    /// let go. `middle` for the middle button.
    public static async Task Click(CoreWebView2 core, double x, double y, bool middle = false)
    {
        var button = middle ? "middle" : "left";
        await Cdp(core, "Input.dispatchMouseEvent", new() { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y, ["button"] = "none" });
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await Cdp(core, "Input.dispatchMouseEvent", new() { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = type == "mousePressed" ? (middle ? 4 : 1) : 0, ["clickCount"] = 1 });
    }

    /// One key, down and up, into whatever in the page has the keyboard. A key held with ctrl or alt types nothing.
    public static async Task Key(CoreWebView2 core, AgentKey key)
    {
        await Focused(core);
        var types = key.Text.Length > 0 && (key.Modifiers & (AgentKey.Ctrl | AgentKey.Alt)) == 0;
        var down = new JsonObject { ["type"] = types ? "keyDown" : "rawKeyDown", ["key"] = key.Key, ["code"] = key.Code, ["windowsVirtualKeyCode"] = key.VirtualKey, ["nativeVirtualKeyCode"] = key.VirtualKey, ["modifiers"] = key.Modifiers };
        if (types) { down["text"] = key.Text; down["unmodifiedText"] = key.Text; }
        await Cdp(core, "Input.dispatchKeyEvent", down);
        await Cdp(core, "Input.dispatchKeyEvent", new() { ["type"] = "keyUp", ["key"] = key.Key, ["code"] = key.Code, ["windowsVirtualKeyCode"] = key.VirtualKey, ["nativeVirtualKeyCode"] = key.VirtualKey, ["modifiers"] = key.Modifiers });
    }

    /// Text typed a character at a time: each a key where a US keyboard has one, inserted as typed text where it hasn't
    /// (an accent, an emoji), newline as Enter and tab as Tab.
    public static async Task Type(CoreWebView2 core, string text)
    {
        await Focused(core);
        var each = StringInfo.GetTextElementEnumerator(text.Replace("\r\n", "\n"));
        while (each.MoveNext())
        {
            var character = (string)each.Current;
            if (AgentKey.Of(character) is { } key) await Key(core, key);
            else await Cdp(core, "Input.insertText", new() { ["text"] = character });
        }
    }

    /// The page told it has the keyboard, as it would in front, so a field it focused takes keys while it sits behind.
    private static Task Focused(CoreWebView2 core) => Cdp(core, "Emulation.setFocusEmulationEnabled", new() { ["enabled"] = true });

    /// A picture of the page as it is laid out, as PNG: the viewport `css` wide and high, `width` pixels across if
    /// asked for. Null when Chromium gave none.
    public static async Task<byte[]?> Picture(CoreWebView2 core, double cssWidth, double cssHeight, double? width)
    {
        var args = new JsonObject { ["format"] = "png" };
        if (width is > 0 && cssWidth > 0) args["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = cssWidth, ["height"] = cssHeight, ["scale"] = width.Value / cssWidth };
        var shot = await Cdp(core, "Page.captureScreenshot", args);
        if (shot?["data"]?.GetValue<string>() is not { } data) return null;
        try { var png = Convert.FromBase64String(data); return png.Length < 24 ? null : png; } catch (FormatException) { return null; }
    }

    /// A PNG with each box (left, top, width, height in CSS pixels of a viewport `across` CSS pixels wide) painted
    /// black. Null if it doesn't decode: a picture that can't be blacked out doesn't leave.
    public static async Task<(byte[] Png, int Width, int Height)?> BlackOut(byte[] png, IReadOnlyList<double[]> boxes, double across)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(png.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            if (boxes.Count == 0) return (png, width, height);
            var scale = across > 0 ? width / across : 1;
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            foreach (var box in boxes.Where(b => b.Length == 4))
            {
                // Outward to whole pixels, so a box's edge never leaves a sliver.
                int left = Math.Max(0, (int)Math.Floor(box[0] * scale)), top = Math.Max(0, (int)Math.Floor(box[1] * scale));
                int right = Math.Min(width, (int)Math.Ceiling((box[0] + box[2]) * scale)), bottom = Math.Min(height, (int)Math.Ceiling((box[1] + box[3]) * scale));
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        var at = (y * width + x) * 4;
                        (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (0, 0, 0, 255);
                    }
            }
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, decoder.DpiX, decoder.DpiY, pixels);
            await encoder.FlushAsync();
            output.Seek(0);
            using var read = output.AsStreamForRead();
            using var bytes = new MemoryStream();
            await read.CopyToAsync(bytes);
            return (bytes.ToArray(), width, height);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException) { return null; }
    }

    private static async Task<JsonNode?> Cdp(CoreWebView2 core, string method, JsonObject args)
    {
        try { return JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync(method, args.ToJsonString())); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException) { return null; }
    }
}
