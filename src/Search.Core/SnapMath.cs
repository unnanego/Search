using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace Search.Core;

// Capture's arithmetic, apart from any page or screen (Snap.swift's SnapGeometry). The layer over the page (snap.js)
// says what was chosen in the page's CSS pixels; this turns that into the clip Chromium is asked to draw
// (Page.captureScreenshot), how big a picture it makes, where the PNG goes and how its clipboard copy is marked.
//
// Where it differs from the Mac's, it is because Chromium is asked differently: WebKit draws a rectangle in the web
// view's points, as you see it, where Chromium's clip is in CSS pixels of the document, whatever is scrolled or
// pinched, so the scroll is added rather than the pinch's offset taken away. The caps are the Mac's: the top 30,000
// CSS pixels of a page that keeps loading more as it is scrolled, and at most 64 million pixels (256 MB as a bitmap).
// One more is Chromium's own: a picture more than 16,384 pixels on a side (the GPU's largest texture) comes back
// broken, so a wider one is made smaller and a taller one is drawn in bands and stacked.

/// What was chosen: dragged across, clicked on, or the whole page.
public enum SnapKind { Area, Element, Page }

/// What the layer answers with (snap.js's choice), in CSS pixels. X, Y, Width and Height are in the layout viewport,
/// as getBoundingClientRect has them, and unused for a whole page; the rest is where the page stood as it was chosen.
public sealed record SnapChoice(SnapKind Kind)
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double ScrollX { get; init; }
    public double ScrollY { get; init; }
    /// The page's full size.
    public double DocumentWidth { get; init; }
    public double DocumentHeight { get; init; }
    /// The layout viewport's size: what is on screen without drawing beyond it.
    public double ViewWidth { get; init; }
    public double ViewHeight { get; init; }
    /// The pinch zoom (visualViewport.scale): a part is taken as you see it, a whole page at its own size.
    public double Pinch { get; init; } = 1;
    /// Device pixels a CSS pixel (devicePixelRatio): the screen's scale, and Chromium's page zoom with it.
    public double Ratio { get; init; } = 1;

    /// As the page posts it: { take: "area" | "element" | "page", x, y, w, h, sx, sy, dw, dh, iw, ih, vs, dpr }. Null
    /// for anything else.
    public static SnapChoice? From(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("take", out var take) || take.ValueKind != JsonValueKind.String) return null;
        SnapKind? kind = take.GetString() switch { "area" => SnapKind.Area, "element" => SnapKind.Element, "page" => SnapKind.Page, _ => null };
        if (kind is null) return null;
        double N(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : 0;
        return new SnapChoice(kind.Value)
        {
            X = N("x"), Y = N("y"), Width = N("w"), Height = N("h"), ScrollX = N("sx"), ScrollY = N("sy"), DocumentWidth = N("dw"), DocumentHeight = N("dh"),
            ViewWidth = N("iw"), ViewHeight = N("ih"), Pinch = N("vs") > 0 ? N("vs") : 1, Ratio = N("dpr") > 0 ? N("dpr") : 1
        };
    }
}

/// One Page.captureScreenshot clip: in the document's CSS pixels, from its top-left corner, and the scale to draw it at
/// (on top of the device's pixels a CSS pixel, which Chromium applies itself).
public readonly record struct SnapClip(double X, double Y, double Width, double Height, double Scale);

/// A band of a tall picture: its clip, and the rows of the picture it fills from `Top`.
public readonly record struct SnapBand(SnapClip Clip, int Top, int Rows);

/// What to ask Chromium for. `Clip` is the whole picture; `Bands` the captures that make it, one unless it is taller
/// than Chromium draws at once. `Beyond` is whether any of it is off screen (captureBeyondViewport). Width and Height
/// are the picture's pixels as planned; `Cropped` a page cut to its top.
public sealed record SnapPlan(SnapClip Clip, int Width, int Height, bool Beyond, bool Cropped, SnapBand[] Bands);

/// Where the PNG is written and how its clipboard copy is marked (see SnapMath.Marks).
public readonly record struct SnapKeep(string Path, bool Private);

public static class SnapMath
{
    /// The tallest picture taken, in CSS pixels at no zoom (the Mac's points): past it, a page that keeps loading more
    /// as it is scrolled has no end.
    public const double Tallest = 30_000;
    /// The most pixels a picture may have, about 256 MB as a bitmap.
    public const double Budget = 64_000_000;
    /// The most pixels Chromium draws on a side in one capture.
    public const int Side = 16_384;
    /// The rows of each band of a picture taller than Side.
    public const int Band = 8_192;
    /// Less than this across or down, in CSS pixels, and there is nothing there to take.
    public const double Smallest = 2;
    /// A press that moves less than this (in CSS pixels, either way) is a click, and a drag smaller than it is none
    /// (snap.js keeps the same numbers).
    public const double Drag = 4;

    /// The page, in the document's CSS pixels. A page that says it has no size is its viewport.
    public static (double X, double Y, double Width, double Height) Page(SnapChoice c) =>
        c.DocumentWidth > 0 && c.DocumentHeight > 0 ? (0, 0, c.DocumentWidth, c.DocumentHeight) : (c.ScrollX, c.ScrollY, c.ViewWidth, c.ViewHeight);

    /// Null when there is nothing there to take: a click on nothing, an area dragged off the page. `zoom` is the page
    /// zoom, for the Tallest cap only (devicePixelRatio has it for the pixels).
    public static SnapPlan? Plan(SnapChoice c, double zoom = 1, double tallest = Tallest, double budget = Budget, int side = Side, int band = Band)
    {
        var page = Page(c);
        var (x, y, w, h) = c.Kind == SnapKind.Page ? page : Intersect((c.X + c.ScrollX, c.Y + c.ScrollY, c.Width, c.Height), page);
        if (w < Smallest || h < Smallest) return null;
        // Outward to whole CSS pixels, as the Mac's .integral, but never past the page.
        double left = Math.Max(page.X, Math.Floor(x)), top = Math.Max(page.Y, Math.Floor(y));
        double right = Math.Min(page.X + page.Width, Math.Ceiling(x + w)), bottom = Math.Min(page.Y + page.Height, Math.Ceiling(y + h));
        (x, y, w, h) = (left, top, right - left, bottom - top);
        if (w < Smallest || h < Smallest) return null;

        var cropped = false;
        var zoomed = Math.Max(zoom, 0.01);
        if (h * zoomed > tallest) { h = Math.Floor(tallest / zoomed); cropped = true; }
        // A whole page at its own size, a part as you see it: pinched, it is bigger.
        var scale = c.Kind == SnapKind.Page ? 1 : Math.Max(c.Pinch, 0.01);
        var ratio = Math.Max(c.Ratio, 0.01);
        var pixels = w * h * Math.Pow(scale * ratio, 2);
        if (pixels > budget) scale *= Math.Sqrt(budget / pixels);
        if (w * scale * ratio > side) scale = side / (w * ratio);
        var each = scale * ratio;
        int width = Pixels(w * each), height = Pixels(h * each);

        var view = (c.ScrollX, c.ScrollY, c.ViewWidth, c.ViewHeight);
        var beyond = c.ViewWidth <= 0 || c.ViewHeight <= 0 || x < view.ScrollX || y < view.ScrollY || x + w > view.ScrollX + view.ViewWidth || y + h > view.ScrollY + view.ViewHeight;
        var clip = new SnapClip(x, y, w, h, scale);
        SnapBand[] bands;
        if (height <= side) bands = [new SnapBand(clip, 0, height)];
        else
        {
            bands = new SnapBand[(height + band - 1) / band];
            for (var i = 0; i < bands.Length; i++)
            {
                int from = i * band, rows = Math.Min(band, height - from);
                bands[i] = new SnapBand(clip with { Y = y + from / each, Height = rows / each }, from, rows);
            }
        }
        return new SnapPlan(clip, width, height, beyond, cropped, bands);
    }

    /// Whole pixels, a hair's rounding error forgiven, and never none.
    private static int Pixels(double value) => Math.Max(1, (int)Math.Floor(value + 1e-6));

    private static (double X, double Y, double Width, double Height) Intersect((double X, double Y, double Width, double Height) a, (double X, double Y, double Width, double Height) b)
    {
        double left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y), right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? (left, top, right - left, bottom - top) : (left, top, 0, 0);
    }

    /// A PNG's size, read from its header; null for anything that isn't one.
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature) || !png[12..16].SequenceEqual("IHDR"u8)) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(png[16..]), height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// "Capture 2026-11-17 at 14.03.22.png": a time, as the Mac names its own screenshots, and not the site, which a
    /// private tab's would give away in the downloads folder. `local` is the time on this computer's clock.
    public static string FileName(DateTime local) => "Capture " + local.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture) + ".png";

    /// Where the picture taken at `local` goes in `downloads`, " 2", " 3" and on rather than over one already there
    /// (Browser.free). A private tab's is saved too, as on the Mac: it names no site, and only its clipboard copy is
    /// marked to stay out of clipboard history.
    public static SnapKeep Keep(DateTime local, string downloads, bool @private) => new(Downloads.Free(FileName(local), downloads), @private);

    /// Windows' clipboard formats the copy carries besides its picture ("PNG" and a bitmap), each with its data: never
    /// to the cloud clipboard (the Mac keeps it on this Mac, off Universal Clipboard), and from a private tab not in
    /// Win+V's history and passed over by clipboard managers too (the Mac's org.nspasteboard.TransientType). Through
    /// WinRT, ClipboardContentOptions { IsRoamable = false, IsAllowedInHistory = !private } sets the first two.
    public static (string Format, byte[] Data)[] Marks(bool @private) => @private
        ? [(CloudFormat, Zero), (HistoryFormat, Zero), (MonitorFormat, Zero)]
        : [(CloudFormat, Zero)];

    public const string CloudFormat = "CanUploadToCloudClipboard";
    public const string HistoryFormat = "CanIncludeInClipboardHistory";
    public const string MonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
    /// A DWORD 0: "no" to the first two; the third goes by being there at all.
    private static byte[] Zero => [0, 0, 0, 0];

    /// What the window says once it's done (Browser.keep): copied, and saved to the folder named, or not saved; "The
    /// top of the page" for one cut short. The notice for a saved one offers to show the file in its folder.
    public static string Notice(bool cropped, string? savedTo)
    {
        var copied = cropped ? "The top of the page copied" : "Copied";
        return savedTo is null ? copied + ", but couldn't save it" : $"{copied} and saved to {Path.GetFileName(Path.TrimEndingDirectorySeparator(savedTo))}";
    }

    /// A choice with nothing there to take.
    public const string NothingThere = "Nothing there to capture";
    /// A picture Chromium wouldn't give.
    public const string Failed = "Couldn't capture this page";
}
