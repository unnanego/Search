using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using Search.Engine;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Search.Engine.Tests;

/// What an agent's hands are given to run in a page (page-hands.js, as PageHands fills it in), and the picture's
/// blacking out, which needs no page.
public class PageHandsTests
{
    [Fact]
    public void EachScriptIsTheOneFunctionCalledWithJson()
    {
        var read = PageHands.Read("__searchAgent", 3, 300, 20000, true);
        Assert.StartsWith("// How a coding agent reads a page", read);
        Assert.EndsWith("""})("read", {"key":"__searchAgent","snap":3,"most":300,"longest":20000,"text":true})""", read);
        Assert.Contains("""("secrets", {})""", PageHands.Secrets);
        Assert.Contains("""("scroll", {"dx":0,"dy":400,"to":null})""", PageHands.Scroll(0, 400, null));
        Assert.Contains("""("hidden", {"key":"k"})""", PageHands.Hidden("k"));
        Assert.Contains("""("shown", {"key":"k"})""", PageHands.Shown("k"));
    }

    [Fact]
    public void WhatAnAgentGivesGoesInAsJsonNeverAsScript()
    {
        var typed = "\"); globalThis.x = 1; (\"</script>" + (char)0x2028;
        var touch = PageHands.Touch("__searchAgent", 1, 2, "set", typed, replace: true, strict: true);
        var args = touch[(touch.LastIndexOf("(\"touch\", ", StringComparison.Ordinal) + "(\"touch\", ".Length)..^1];
        var parsed = JsonNode.Parse(args)!.AsObject();
        Assert.Equal(typed, parsed["text"]!.GetValue<string>());
        Assert.Equal("set", parsed["how"]!.GetValue<string>());
        Assert.True(parsed["strict"]!.GetValue<bool>());
        Assert.Contains("\"text\":null", PageHands.Touch("k", 1, 2, "verify", null, replace: true));
        var check = PageHands.Check(new JsonObject { ["text"] = "a\"b" });
        var condition = check[(check.LastIndexOf("(\"check\", ", StringComparison.Ordinal) + "(\"check\", ".Length)..^1];
        Assert.Equal("a\"b", JsonNode.Parse(condition)!["condition"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void TheScriptKnowsEveryVerbItIsCalledWith()
    {
        var script = Scripts.Read("page-hands.js");
        foreach (var verb in new[] { "read", "touch", "check", "secrets", "hidden", "shown", "scroll" }) Assert.Contains($"case '{verb}':", script);
        foreach (var how in new[] { "click", "press", "scroll", "focus", "set", "submit", "verify" }) Assert.Contains($"'{how}'", script);
        // Shared with forms.js, which marks what Search fills.
        Assert.Contains("globalThis.__searchSecrets", script);
        Assert.Contains("globalThis.__searchSecrets", Scripts.Read("forms.js"));
    }

    [Fact]
    public async Task BoxesArePaintedBlackInImagePixels()
    {
        var png = await Picture(100, 50, 255);
        // A viewport 50 CSS pixels wide, pictured 100 across: two image pixels to a CSS one.
        var black = await PageHands.BlackOut(png, new[] { new double[] { 10, 10, 20, 10 } }, across: 50);
        Assert.NotNull(black);
        Assert.Equal((100, 50), (black.Value.Width, black.Value.Height));
        var pixels = await Pixels(black.Value.Png);
        int At(int x, int y) => pixels[(y * 100 + x) * 4];
        Assert.Equal(0, At(20, 20));
        Assert.Equal(0, At(59, 39));
        Assert.Equal(255, At(19, 20));
        Assert.Equal(255, At(60, 39));
        Assert.Equal(255, At(20, 40));
    }

    [Fact]
    public async Task BoxesPastTheEdgeAreCutToIt()
    {
        var black = await PageHands.BlackOut(await Picture(40, 40, 255), new[] { new double[] { -10, -10, 100, 100 } }, across: 40);
        Assert.All(await Pixels(black!.Value.Png), (b, i) => Assert.Equal(i % 4 == 3 ? 255 : 0, b));
    }

    [Fact]
    public async Task NothingToBlackOutLeavesThePictureAsItIs()
    {
        var png = await Picture(8, 8, 128);
        Assert.Same(png, (await PageHands.BlackOut(png, Array.Empty<double[]>(), across: 8))!.Value.Png);
    }

    [Fact]
    public async Task APictureThatDoesntDecodeDoesntLeave() => Assert.Null(await PageHands.BlackOut("not a png at all, not at all"u8.ToArray(), new[] { new double[] { 0, 0, 1, 1 } }, across: 1));

    private static async Task<byte[]> Picture(int width, int height, byte grey)
    {
        var pixels = Enumerable.Range(0, width * height * 4).Select(i => i % 4 == 3 ? (byte)255 : grey).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var read = stream.AsStreamForRead();
        using var bytes = new MemoryStream();
        await read.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task<byte[]> Pixels(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }
}
