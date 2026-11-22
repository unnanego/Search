using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Search.Core;
using Search.Engine;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Search.Engine.Tests;

/// Capture's page side with no page: what the layer (snap.js) says and how Snap hears it, and a tall picture's bands
/// stacked into one.
public class SnapTests
{
    private static JsonElement Body(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    [Fact]
    public void AChoiceIsHeardOnlyFromTheLayerRaised()
    {
        Assert.True(Snap.Hear("snap.chosen", Body("""{"kind":"snap.chosen","id":2,"take":"area","x":10,"y":20,"w":300,"h":200,"sx":0,"sy":800,"dw":1200,"dh":5000,"iw":1200,"ih":800,"vs":1,"dpr":2}"""), 2, out var choice));
        Assert.Equal(new SnapChoice(SnapKind.Area) { X = 10, Y = 20, Width = 300, Height = 200, ScrollY = 800, DocumentWidth = 1200, DocumentHeight = 5000, ViewWidth = 1200, ViewHeight = 800, Ratio = 2 }, choice);
        // Put away: heard, with nothing taken.
        Assert.True(Snap.Hear("snap.off", Body("""{"kind":"snap.off","id":2}"""), 2, out choice));
        Assert.Null(choice);
        // A choice that isn't one is nothing taken too.
        Assert.True(Snap.Hear("snap.chosen", Body("""{"kind":"snap.chosen","id":2,"take":"screen"}"""), 2, out choice));
        Assert.Null(choice);
        // An earlier layer's, one with no id, or another script's: not this one's answer.
        Assert.False(Snap.Hear("snap.off", Body("""{"kind":"snap.off","id":1}"""), 2, out _));
        Assert.False(Snap.Hear("snap.chosen", Body("""{"kind":"snap.chosen","take":"page"}"""), 2, out _));
        Assert.False(Snap.Hear("curtain.off", Body("""{"kind":"curtain.off","id":2}"""), 2, out _));
    }

    [Fact]
    public void TheLayerSaysWhatSnapHears()
    {
        var script = Scripts.Read("snap.js");
        Assert.StartsWith("// Capture's layer over the page", script);
        foreach (var said in new[] { "kind: 'snap.chosen'", "kind: 'snap.off'", "globalThis.__searchSnap", "__searchSend(" }) Assert.Contains(said, script);
        foreach (var take in new[] { "'area'", "'element'", "'page'" }) Assert.Contains($"choice({take}", script);
        // Every number SnapChoice.From reads.
        foreach (var key in new[] { "x:", "y:", "w:", "h:", "sx:", "sy:", "dw:", "dh:", "iw:", "ih:", "vs:", "dpr:" }) Assert.Contains(key, script);
        // The Mac's bar, a closed shadow root, and Escape heard only from a real key.
        foreach (var text in new[] { "Drag an area or click an element", "Whole Page", "'esc'", "mode: 'closed'", "e.isTrusted" }) Assert.Contains(text, script);
        Assert.Contains($"const drag = {SnapMath.Drag};", script);
    }

    [Fact]
    public async Task BandsAreStackedTopFirst()
    {
        var png = await Snap.Stack(new[] { await Picture(6, 3, 10), await Picture(6, 2, 200) });
        Assert.NotNull(png);
        Assert.Equal((6, 5), SnapMath.PngSize(png));
        var pixels = await Pixels(png);
        Assert.Equal(10, pixels[(2 * 6 + 5) * 4]);
        Assert.Equal(200, pixels[(3 * 6) * 4]);
        Assert.Equal(200, pixels[(4 * 6 + 5) * 4]);
    }

    [Fact]
    public async Task BandsOfDifferentWidthsAreCutToTheNarrowest()
    {
        var png = await Snap.Stack(new[] { await Picture(5, 2, 50), await Picture(4, 2, 90) });
        Assert.Equal((4, 4), SnapMath.PngSize(png));
    }

    [Fact]
    public async Task ABandThatDoesntDecodeIsNoPicture() => Assert.Null(await Snap.Stack(new[] { await Picture(2, 2, 0), "not a png at all, not at all"u8.ToArray() }));

    [Fact]
    public void OnlyAnEndWithNoPictureHasANotice()
    {
        Assert.Equal("Nothing there to capture", new SnapResult(SnapEnd.NothingThere).Notice);
        Assert.Equal("Couldn't capture this page", new SnapResult(SnapEnd.Failed).Notice);
        Assert.Null(new SnapResult(SnapEnd.Cancelled).Notice);
        Assert.Null(new SnapResult(SnapEnd.Taken).Notice);
    }

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
