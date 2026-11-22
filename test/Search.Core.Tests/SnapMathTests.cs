using System.Text.Json;
using Search.Core;

namespace Search.Core.Tests;

/// Capture's arithmetic (SnapMath, after SnapGeometryTests.swift): what the page chose, in its CSS pixels, as the clip
/// Chromium is asked to draw, with no page or screen; and the file and clipboard marks it is kept with.
public class SnapMathTests
{
    private static SnapChoice Tall(SnapKind kind) => new(kind) { DocumentWidth = 1200, DocumentHeight = 5000, ViewWidth = 1200, ViewHeight = 800 };

    [Fact]
    public void AnAreaIsWhatWasDraggedWhereThePageIsScrolled()
    {
        var plan = SnapMath.Plan(Tall(SnapKind.Area) with { X = 10, Y = 20, Width = 300, Height = 200, ScrollY = 800, Ratio = 2 })!;
        Assert.Equal(new SnapClip(10, 820, 300, 200, 1), plan.Clip);
        Assert.Equal((600, 400), (plan.Width, plan.Height));
        Assert.False(plan.Beyond);
        Assert.False(plan.Cropped);
        Assert.Equal(new[] { new SnapBand(plan.Clip, 0, 400) }, plan.Bands);
    }

    [Fact]
    public void APartIsTakenOutwardToWholePixels()
    {
        var plan = SnapMath.Plan(Tall(SnapKind.Element) with { X = 10.4, Y = 20.6, Width = 99.2, Height = 50 })!;
        Assert.Equal(new SnapClip(10, 20, 100, 51, 1), plan.Clip);
    }

    [Fact]
    public void APinchedPartIsTakenAsYouSeeIt()
    {
        var plan = SnapMath.Plan(Tall(SnapKind.Area) with { X = 60, Y = 50, Width = 100, Height = 100, Pinch = 2, Ratio = 1.5 })!;
        Assert.Equal(new SnapClip(60, 50, 100, 100, 2), plan.Clip);
        Assert.Equal((300, 300), (plan.Width, plan.Height));
    }

    [Fact]
    public void AnElementIsCutToThePageAndDrawnBeyondTheScreen()
    {
        // Wider than the page and starting above it: only the page's part, some of it above what's on screen.
        var plan = SnapMath.Plan(Tall(SnapKind.Element) with { X = -20, Y = -400, Width = 1300, Height = 700, ScrollY = 100 })!;
        Assert.Equal(new SnapClip(0, 0, 1200, 400, 1), plan.Clip);
        Assert.True(plan.Beyond);
    }

    [Fact]
    public void TheWholePageIsFromItsTopWhateverTheScroll()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { ScrollY = 1234, DocumentWidth = 1200, DocumentHeight = 3000, ViewWidth = 1200, ViewHeight = 800, Ratio = 2 })!;
        Assert.Equal(new SnapClip(0, 0, 1200, 3000, 1), plan.Clip);
        Assert.Equal((2400, 6000), (plan.Width, plan.Height));
        Assert.True(plan.Beyond);
        Assert.False(plan.Cropped);
    }

    [Fact]
    public void AWholePageIsTakenAtItsOwnSizeWhateverThePinch()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { DocumentWidth = 1000, DocumentHeight = 2000, Pinch = 2, Ratio = 2 })!;
        Assert.Equal(1, plan.Clip.Scale);
        Assert.Equal((2000, 4000), (plan.Width, plan.Height));
    }

    [Fact]
    public void APageThatSaysItHasNoSizeIsItsViewport()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { ScrollY = 50, ViewWidth = 800, ViewHeight = 600 })!;
        Assert.Equal(new SnapClip(0, 50, 800, 600, 1), plan.Clip);
        Assert.False(plan.Beyond);
    }

    [Fact]
    public void AnEndlessPageGivesItsTopInBands()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { DocumentWidth = 800, DocumentHeight = 100_000 })!;
        Assert.Equal(SnapMath.Tallest, plan.Clip.Height);
        Assert.True(plan.Cropped);
        Assert.Equal(30_000, plan.Height);
        // Taller than Chromium draws at once: bands of 8,192 rows, end to end.
        Assert.Equal(4, plan.Bands.Length);
        Assert.Equal(plan.Height, plan.Bands.Sum(b => b.Rows));
        Assert.Equal(new[] { 0, 8192, 16384, 24576 }, plan.Bands.Select(b => b.Top));
        Assert.Equal(8192, plan.Bands[1].Clip.Y);
        Assert.Equal(30_000 - 24_576, plan.Bands[3].Clip.Height);
        Assert.All(plan.Bands, b => Assert.Equal((0d, 800d, 1d), (b.Clip.X, b.Clip.Width, b.Clip.Scale)));
    }

    [Fact]
    public void APageZoomedInReachesTheTallestSooner()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { DocumentWidth = 800, DocumentHeight = 25_000 }, zoom: 1.5)!;
        Assert.Equal(20_000, plan.Clip.Height);
        Assert.True(plan.Cropped);
    }

    [Fact]
    public void BandsAtAScaleCoverTheClipExactly()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { DocumentWidth = 1000, DocumentHeight = 12_000, Ratio = 1.5 })!;
        Assert.Equal(18_000, plan.Height);
        Assert.Equal(3, plan.Bands.Length);
        Assert.Equal(plan.Clip.Height, plan.Bands.Sum(b => b.Clip.Height), 6);
        Assert.Equal(plan.Bands[0].Clip.Y + plan.Bands[0].Clip.Height, plan.Bands[1].Clip.Y, 6);
    }

    [Fact]
    public void APictureTooBigToHoldIsMadeSmaller()
    {
        // 2000 × 20000 × 2² = 160 M pixels, past the 64 M budget.
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { DocumentWidth = 2000, DocumentHeight = 20_000, Ratio = 2 })!;
        Assert.True(plan.Clip.Scale < 1);
        Assert.InRange((double)plan.Width * plan.Height, SnapMath.Budget * 0.99, SnapMath.Budget);
        Assert.Equal(2000, plan.Clip.Width);
    }

    [Fact]
    public void APictureWiderThanChromiumDrawsIsMadeNarrower()
    {
        var plan = SnapMath.Plan(new SnapChoice(SnapKind.Page) { DocumentWidth = 20_000, DocumentHeight = 400 })!;
        Assert.Equal(SnapMath.Side, plan.Width);
        Assert.Equal(SnapMath.Side / 20_000d, plan.Clip.Scale, 9);
    }

    [Fact]
    public void NothingThereIsNothingTaken()
    {
        Assert.Null(SnapMath.Plan(Tall(SnapKind.Area) with { Y = -900, Width = 100, Height = 100 }));
        Assert.Null(SnapMath.Plan(Tall(SnapKind.Element) with { X = 5, Y = 5, Width = 1, Height = 40 }));
        Assert.Null(SnapMath.Plan(new SnapChoice(SnapKind.Page)));
    }

    [Fact]
    public void TheChoiceAsThePagePostsIt()
    {
        using var posted = JsonDocument.Parse("""{"kind":"snap.chosen","id":3,"take":"element","x":1.5,"y":2,"w":30,"h":40,"sx":0,"sy":600,"dw":1200,"dh":5000,"iw":1185,"ih":790,"vs":1,"dpr":1.25}""");
        Assert.Equal(new SnapChoice(SnapKind.Element) { X = 1.5, Y = 2, Width = 30, Height = 40, ScrollY = 600, DocumentWidth = 1200, DocumentHeight = 5000, ViewWidth = 1185, ViewHeight = 790, Ratio = 1.25 }, SnapChoice.From(posted.RootElement));
        using var bare = JsonDocument.Parse("""{"take":"page","vs":0,"dpr":"2"}""");
        Assert.Equal(new SnapChoice(SnapKind.Page), SnapChoice.From(bare.RootElement));
        using var other = JsonDocument.Parse("""{"take":"screen"}""");
        Assert.Null(SnapChoice.From(other.RootElement));
        using var none = JsonDocument.Parse("null");
        Assert.Null(SnapChoice.From(none.RootElement));
    }

    [Fact]
    public void TheFileIsNamedForTheTimeNotTheSite() => Assert.Equal("Capture 2026-11-18 at 11.06.40.png", SnapMath.FileName(new DateTime(2026, 11, 18, 11, 6, 40)));

    [Fact]
    public void APictureNeverGoesOverOneAlreadyThereAndAPrivateTabsIsSavedToo()
    {
        var folder = Path.Combine(Path.GetTempPath(), "search-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var at = new DateTime(2026, 11, 18, 11, 6, 40);
            Assert.Equal(new SnapKeep(Path.Combine(folder, "Capture 2026-11-18 at 11.06.40.png"), false), SnapMath.Keep(at, folder, @private: false));
            File.WriteAllBytes(Path.Combine(folder, "Capture 2026-11-18 at 11.06.40.png"), []);
            Assert.Equal(new SnapKeep(Path.Combine(folder, "Capture 2026-11-18 at 11.06.40 2.png"), true), SnapMath.Keep(at, folder, @private: true));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ACopyNeverRoamsAndAPrivateTabsStaysOutOfHistory()
    {
        Assert.Equal(new[] { SnapMath.CloudFormat }, SnapMath.Marks(@private: false).Select(m => m.Format));
        var marks = SnapMath.Marks(@private: true);
        Assert.Equal(new[] { SnapMath.CloudFormat, SnapMath.HistoryFormat, SnapMath.MonitorFormat }, marks.Select(m => m.Format));
        Assert.All(marks, m => Assert.Equal(new byte[4], m.Data));
        Assert.Equal("ExcludeClipboardContentFromMonitorProcessing", SnapMath.MonitorFormat);
    }

    [Fact]
    public void ThePicturesSizeIsReadFromItsHeader()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x02, 0x58, 0, 0, 0x01, 0x90, 8, 6, 0, 0, 0];
        Assert.Equal((600, 400), SnapMath.PngSize(png));
        Assert.Null(SnapMath.PngSize("not a picture at all, not at all"u8));
        Assert.Null(SnapMath.PngSize(png.AsSpan(0, 20)));
    }

    [Fact]
    public void TheNoticeSaysWhereItWent()
    {
        Assert.Equal("Copied and saved to Downloads", SnapMath.Notice(false, @"C:\Users\me\Downloads\"));
        Assert.Equal("The top of the page copied and saved to Downloads", SnapMath.Notice(true, @"C:\Users\me\Downloads"));
        Assert.Equal("Copied, but couldn't save it", SnapMath.Notice(false, null));
    }
}
