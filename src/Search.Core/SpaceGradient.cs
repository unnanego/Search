using System.Globalization;
using System.Text.Json;

namespace Search.Core;

// A space's gradient (SpaceGradient.swift, the data and the colour maths; the drawing is the window's): two or three
// colours instead of one, the way Arc themes a space. A space without one is washed with its colour, or nothing,
// exactly as before, and reads and writes spaces.json as it always did: the key is simply not there. Removing it
// gives the space back its colour.
//
// The gradient takes the colour's place wherever the wash is drawn, at the same strength, and runs across the whole
// window, not each part on its own (see Points). The colours are mixed in OKLab keeping the chroma of the ends, so
// the middle of blue to orange is as coloured as they are rather than a grey, and each is held to the band of
// lightness the plain colours sit in. Everything here is plain values: what to paint is SpaceWash.Of.

/// A colour in gamma-encoded sRGB, each part 0…1.
public readonly record struct Rgb(double R, double G, double B)
{
    public string Hex => $"#{Byte(R):X2}{Byte(G):X2}{Byte(B):X2}";
    private static int Byte(double c) => (int)Math.Round(Math.Clamp(c, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

/// A point in a box, 0…1 across and down, as SwiftUI's UnitPoint.
public readonly record struct UnitPoint(double X, double Y);

/// One stop of a gradient as drawn: its colour, and where along the line it sits, 0…1.
public readonly record struct GradientStop(Rgb Colour, double Location);

public sealed record SpaceGradient
{
    public const double DefaultAngle = 135;
    public const int Most = 3;

    /// The colours in the order they run, "#RRGGBB" in sRGB: two or three.
    public IReadOnlyList<string> Colours { get; init; }
    /// The way it runs, in degrees as CSS has them: 0 up, 90 right, 180 down.
    public double Angle { get; init; }
    /// A faint, still grain over it.
    public bool Grain { get; init; }

    public SpaceGradient(IReadOnlyList<string> colours, double angle = DefaultAngle, bool grain = false) { Colours = colours; Angle = angle; Grain = grain; }

    public bool Equals(SpaceGradient? other) => other is not null && Colours.SequenceEqual(other.Colours) && Angle.Equals(other.Angle) && Grain == other.Grain;
    public override int GetHashCode() => HashCode.Combine(Colours.Count, Colours.FirstOrDefault(), Angle, Grain);

    /// The colours as drawn: the first three that read, each held to the wash's band of lightness and chroma.
    public IReadOnlyList<OkLab> Drawn => Colours.Select(OkLab.FromHex).OfType<OkLab>().Take(Most).Select(c => c.Held).ToList();

    /// Two colours or more that read; otherwise the space's colour stands.
    public bool Usable => Drawn.Count >= 2;

    /// What the gradient is drawn with: its colours mixed in OKLab at `count` stops, evenly spaced. Empty when it
    /// isn't usable.
    public IReadOnlyList<GradientStop> Stops(int count = 24, SpaceStrength strength = SpaceStrength.Subtle, bool dark = false)
    {
        var lab = Drawn.Select(c => strength.Tone(c, dark)).ToList();
        if (lab.Count < 2 || count < 2) return [];
        return Enumerable.Range(0, count).Select(step => { var t = step / (double)(count - 1); return new GradientStop(OkLab.Along(lab, t).Srgb, t); }).ToList();
    }

    /// Its first colour as drawn: for the space's icon and its menu.
    public Rgb? Lead => Drawn.Count > 0 ? Drawn[0].Srgb : null;

    /// Where it starts and ends, as unit points of a box at (x, y, width, height) in a window `roomWidth` by
    /// `roomHeight` (y down): the line through the window's middle at `Angle`, long enough that the window's corners
    /// lie on its ends, as in CSS. The column, the bars and the ground then each show their part of one gradient. No
    /// room (drawn off screen) is the box alone.
    public (UnitPoint Start, UnitPoint End) Points(double x, double y, double width, double height, double roomWidth = 0, double roomHeight = 0)
    {
        var (bx, by, bw, bh) = roomWidth > 0 && roomHeight > 0 ? (0d, 0d, roomWidth, roomHeight) : (x, y, width, height);
        var radians = Angle * Math.PI / 180;
        double dx = Math.Sin(radians), dy = -Math.Cos(radians);
        var length = Math.Abs(bw * dx) + Math.Abs(bh * dy);
        double midX = bx + bw / 2, midY = by + bh / 2;
        UnitPoint Unit(double px, double py) => new(width > 0 ? (px - x) / width : 0, height > 0 ? (py - y) / height : 0);
        return (Unit(midX - dx * length / 2, midY - dy * length / 2), Unit(midX + dx * length / 2, midY + dy * length / 2));
    }

    // Choices.

    /// The colours the editor offers: twelve hues at one lightness and chroma, so each washes as strongly as the next,
    /// and two quiet ones.
    public static readonly IReadOnlyList<(string Name, string Hex)> Swatches =
    [
        .. new (string, double)[] { ("Red", 25), ("Orange", 55), ("Amber", 85), ("Lime", 125), ("Green", 150), ("Teal", 185), ("Cyan", 215), ("Blue", 250), ("Indigo", 275), ("Violet", 300), ("Magenta", 330), ("Pink", 355) }
            .Select(h => (h.Item1, OkLab.FromLch(0.70, 0.14, h.Item2).Hex)),
        ("Graphite", OkLab.FromLch(0.62, 0.015, 260).Hex),
        ("Sand", OkLab.FromLch(0.78, 0.045, 75).Hex),
    ];

    private static string Swatch(string name) => Swatches.FirstOrDefault(s => s.Name == name).Hex ?? "#808080";

    public static readonly IReadOnlyList<(string Name, SpaceGradient Gradient)> Presets =
    [
        ("Sunrise", new([Swatch("Amber"), Swatch("Pink")])),
        ("Lagoon", new([Swatch("Teal"), Swatch("Blue")])),
        ("Orchard", new([Swatch("Lime"), Swatch("Teal")])),
        ("Dusk", new([Swatch("Indigo"), Swatch("Magenta"), Swatch("Orange")])),
        ("Glacier", new([Swatch("Cyan"), Swatch("Indigo")])),
        ("Ember", new([Swatch("Red"), Swatch("Amber")])),
    ];

    /// A first gradient for a space: its colour, if it has one, into the hue beside it; Lagoon otherwise.
    public static SpaceGradient Starting(int colour)
    {
        string[] from = ["", "Blue", "Green", "Orange", "Red", "Violet"], to = ["", "Violet", "Teal", "Pink", "Amber", "Pink"];
        return colour > 0 && colour < from.Length ? new([Swatch(from[colour]), Swatch(to[colour])]) : Presets[1].Gradient;
    }

    // In spaces.json: {"colours":["#RRGGBB",…],"angle":135,"grain":false}.

    /// Never fails: a gradient that doesn't read is no gradient (see Usable), and the space keeps its colour. Failing
    /// here would fail the whole list of spaces.
    public static SpaceGradient Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return new([]);
        var colours = SwiftJson.Has(e, "colours", out var c) && c.ValueKind == JsonValueKind.Array && c.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String)
            ? c.EnumerateArray().Select(i => i.GetString()!).ToList() : [];
        var angle = SwiftJson.Has(e, "angle", out var a) && SwiftJson.Number(a) is { } n && double.IsFinite(n) ? n : DefaultAngle;
        return new(colours, angle, SwiftJson.Has(e, "grain", out var g) && SwiftJson.Bool(g) == true);
    }

    public void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("colours");
        foreach (var colour in Colours) w.WriteStringValue(colour);
        w.WriteEndArray();
        w.WriteNumber("angle", Angle);
        w.WriteBoolean("grain", Grain);
        w.WriteEndObject();
    }
}

/// How strongly a space's colour or gradient washes the chrome. Subtle is the faint wash every space had; Rich and
/// Vivid are for whoever wants the colour seen. Missing from spaces.json is Subtle, so a list from before reads and
/// writes as it did. Stronger, the wash stays on its side of the window's appearance (see Tone), so the ink never has
/// to turn over.
public enum SpaceStrength { Subtle, Rich, Vivid }

public static class SpaceStrengths
{
    /// How much the grain shows, light and dark: faint enough to be felt, not seen as dots.
    public const double GrainLight = 0.035, GrainDark = 0.04;

    public static string Name(this SpaceStrength s) => s switch { SpaceStrength.Rich => "Rich", SpaceStrength.Vivid => "Vivid", _ => "Subtle" };
    /// As spaces.json has it: "subtle", "rich", "vivid".
    public static string Raw(this SpaceStrength s) => s switch { SpaceStrength.Rich => "rich", SpaceStrength.Vivid => "vivid", _ => "subtle" };
    /// Never fails: one a newer Search writes, or one that doesn't read, is Subtle.
    public static SpaceStrength Parse(string? raw) => raw switch { "rich" => SpaceStrength.Rich, "vivid" => SpaceStrength.Vivid, _ => SpaceStrength.Subtle };

    /// How much of the colour the chrome wears. Subtle is the colour's wash as it always was (Spaces.WashLight, WashDark).
    public static double Opacity(this SpaceStrength s, bool dark) => s switch
    {
        SpaceStrength.Rich => dark ? 0.22 : 0.18,
        SpaceStrength.Vivid => dark ? 0.40 : 0.30,
        _ => dark ? Spaces.WashDark : Spaces.WashLight,
    };

    /// The colour as this strength washes with it: more chroma, and its lightness held so the washed ground stays
    /// light in a light window (Vivid: no darker than 0.62) and deep in a dark one (Rich: no lighter than 0.72, Vivid:
    /// 0.48 to 0.58). Subtle is the colour as held. Never more chroma than the band allows a colour, however strong.
    public static OkLab Tone(this SpaceStrength s, OkLab colour, bool dark)
    {
        var (boost, low, high) = s switch
        {
            SpaceStrength.Rich => (1.15, 0d, dark ? 0.72 : 1),
            SpaceStrength.Vivid => (1.3, dark ? 0.48 : 0.62, dark ? 0.58 : 1),
            _ => (1d, 0d, 1d),
        };
        var chroma = colour.Chroma;
        var wanted = Math.Min(chroma * boost, Math.Max(chroma, OkLab.MostChroma));
        var scale = chroma > 1e-9 ? wanted / chroma : 1;
        return new(Math.Min(Math.Max(colour.L, low), high), colour.A * scale, colour.B * scale);
    }

    /// The grain, a touch stronger on a stronger colour, or it is lost in it.
    public static double Grain(this SpaceStrength s, bool dark) => (dark ? GrainDark : GrainLight) * s switch { SpaceStrength.Rich => 1.25, SpaceStrength.Vivid => 1.5, _ => 1 };
}

/// What a space washes the chrome with: one of Spaces.Colours, or its gradient. Null is nothing at all, the look every
/// space had before.
public abstract record SpaceLook
{
    public sealed record Colour(int Index) : SpaceLook;
    public sealed record Gradient(SpaceGradient Value) : SpaceLook;
}

/// What a space wears, as the picker names it: nothing, one of the colours, one of the ready-made gradients, or a
/// gradient of one's own.
public abstract record SpaceLookChoice
{
    public sealed record None : SpaceLookChoice;
    public sealed record Colour(int Index) : SpaceLookChoice;
    public sealed record Preset(int Index) : SpaceLookChoice;
    public sealed record Custom : SpaceLookChoice;
}

/// What the window paints behind the tabs for a space, as plain values: a colour (`Colour`) or a gradient (`Stops`,
/// running at `Angle`; `Gradient.Points` places it across the window), at `Opacity`, with grain at `Grain` (0 for
/// none). The chrome wears it — the column, the strip, the bookmarks bar, the ground around the page — never the page.
public sealed record SpaceWash(Rgb? Colour, IReadOnlyList<GradientStop> Stops, SpaceGradient? Gradient, double Opacity, double Grain)
{
    public double Angle => Gradient?.Angle ?? SpaceGradient.DefaultAngle;

    /// Null draws nothing at all, so a space without a colour looks exactly as every space did before.
    public static SpaceWash? Of(SpaceLook? look, SpaceStrength strength, bool dark) => look switch
    {
        SpaceLook.Colour c when Spaces.Wash(c.Index, dark) is not null && Spaces.Toned(c.Index, strength, dark) is { } colour => new(colour, [], null, strength.Opacity(dark), 0),
        SpaceLook.Gradient g => new(null, g.Value.Stops(strength: strength, dark: dark), g.Value, strength.Opacity(dark), g.Value.Grain ? strength.Grain(dark) : 0),
        _ => null,
    };
}

/// A colour in Björn Ottosson's OKLab: L from black (0) to white (1), A and B its two opponent axes. Even steps in it
/// look even, which sRGB's don't.
public readonly record struct OkLab(double L, double A, double B)
{
    /// The band a wash colour is held to: the plain colours' own. A near-black or near-white washed at their strength
    /// would move the ground as far as the grey of the live tab is from it.
    public const double LightnessLow = 0.56, LightnessHigh = 0.80, MostChroma = 0.20;

    public static OkLab FromLch(double lightness, double chroma, double hue)
    {
        var h = hue * Math.PI / 180;
        return new(lightness, chroma * Math.Cos(h), chroma * Math.Sin(h));
    }

    public static OkLab FromRgb(double r, double g, double b)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        double lr = Linear(r), lg = Linear(g), lb = Linear(b);
        var l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
        var m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
        var s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
        return new(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public static OkLab FromRgb(Rgb c) => FromRgb(c.R, c.G, c.B);

    /// "#RRGGBB" or "RRGGBB"; null for anything else.
    public static OkLab? FromHex(string hex)
    {
        var text = hex.Trim(' ', '\t', ' ');
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !text.All(char.IsAsciiHexDigit) || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
        return FromRgb(((value >> 16) & 0xFF) / 255d, ((value >> 8) & 0xFF) / 255d, (value & 0xFF) / 255d);
    }

    public double Chroma => Math.Sqrt(A * A + B * B);

    /// Linear sRGB, unclamped: outside 0…1 is outside what sRGB can show.
    public (double R, double G, double B) Linear
    {
        get
        {
            var l = Math.Pow(L + 0.3963377774 * A + 0.2158037573 * B, 3);
            var m = Math.Pow(L - 0.1055613458 * A - 0.0638541728 * B, 3);
            var s = Math.Pow(L - 0.0894841775 * A - 1.2914855480 * B, 3);
            return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        }
    }

    private bool InGamut
    {
        get
        {
            const double e = 1e-6;
            var (r, g, b) = Linear;
            return r >= -e && r <= 1 + e && g >= -e && g <= 1 + e && b >= -e && b <= 1 + e;
        }
    }

    /// Gamma-encoded sRGB. A colour sRGB can't show keeps its lightness and hue and loses only as much chroma as it must.
    public Rgb Srgb
    {
        get
        {
            var colour = this;
            if (!InGamut)
            {
                double low = 0, high = 1;
                for (var i = 0; i < 16; i++)
                {
                    var mid = (low + high) / 2;
                    if (new OkLab(L, A * mid, B * mid).InGamut) low = mid; else high = mid;
                }
                colour = new(L, A * low, B * low);
            }
            static double Encode(double c) { c = Math.Clamp(c, 0, 1); return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; }
            var (r, g, b) = colour.Linear;
            return new(Encode(r), Encode(g), Encode(b));
        }
    }

    public string Hex => Srgb.Hex;

    /// Held to the wash's band of lightness, and no more chroma than MostChroma.
    public OkLab Held
    {
        get
        {
            var scale = Chroma > MostChroma ? MostChroma / Chroma : 1;
            return new(Math.Clamp(L, LightnessLow, LightnessHigh), A * scale, B * scale);
        }
    }

    /// Between this and `other` at t: straight across OKLab for the hue, with the chroma going evenly from one end's to
    /// the other's, so the middle is no greyer than the ends.
    public OkLab Mix(OkLab other, double t)
    {
        var mixed = new OkLab(L + (other.L - L) * t, A + (other.A - A) * t, B + (other.B - B) * t);
        var wanted = Chroma + (other.Chroma - Chroma) * t;
        var has = mixed.Chroma;
        return has > 1e-4 ? mixed with { A = mixed.A * wanted / has, B = mixed.B * wanted / has } : mixed;
    }

    /// Along a run of colours, evenly spaced, at t in 0…1.
    public static OkLab Along(IReadOnlyList<OkLab> colours, double t)
    {
        if (colours.Count <= 1) return colours.Count == 1 ? colours[0] : new(0.7, 0, 0);
        var spot = Math.Clamp(t, 0, 1) * (colours.Count - 1);
        var at = Math.Min((int)spot, colours.Count - 2);
        return colours[at].Mix(colours[at + 1], spot - at);
    }
}
