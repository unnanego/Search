using System.Text;
using System.Text.Json;
using Search.Core;

namespace Search.Core.Tests;

public class SpaceGradientTests
{
    private static void Near(double expected, double actual, double within = 1e-6) => Assert.InRange(actual, expected - within, expected + within);

    private static SpaceGradient Read(string json) { using var d = JsonDocument.Parse(json); return SpaceGradient.Read(d.RootElement); }

    [Fact]
    public void OkLabIsOttossons()
    {
        // His reference values for sRGB red, and white and black.
        var red = OkLab.FromRgb(1, 0, 0);
        Near(0.627955, red.L); Near(0.224863, red.A); Near(0.125846, red.B);
        Near(1, OkLab.FromRgb(1, 1, 1).L, 1e-4);
        Near(0, OkLab.FromRgb(1, 1, 1).Chroma, 1e-4);
        Assert.Equal("#000000", OkLab.FromRgb(0, 0, 0).Hex);
        Assert.Equal("#FF0000", red.Hex);
    }

    [Theory]
    [InlineData("#4285F4", "#4285F4")]
    [InlineData("4285f4", "#4285F4")]
    [InlineData("  #4FB3A9\t", "#4FB3A9")]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("#4285F4FF", null)]
    [InlineData("", null)]
    public void HexReadsAsTheMacReadsIt(string hex, string? back) => Assert.Equal(back, OkLab.FromHex(hex)?.Hex);

    [Fact]
    public void AColourOutsideSrgbLosesOnlyChroma()
    {
        var wild = new OkLab(0.7, 0.4, 0.0);
        var shown = OkLab.FromRgb(wild.Srgb);
        Near(0.7, shown.L, 2e-3);
        Assert.True(shown.Chroma < wild.Chroma);
        Near(0, Math.Atan2(shown.B, shown.A), 2e-2);
    }

    [Fact]
    public void ColoursAreHeldToTheWashsBand()
    {
        var black = OkLab.FromHex("#000000")!.Value.Held;
        var white = OkLab.FromHex("#FFFFFF")!.Value.Held;
        Assert.Equal((OkLab.LightnessLow, OkLab.LightnessHigh), (black.L, white.L));
        Near(OkLab.MostChroma, new OkLab(0.7, 0.3, 0.3).Held.Chroma);
        var calm = OkLab.FromLch(0.7, 0.05, 40);
        Assert.Equal(calm, calm.Held);
    }

    [Fact]
    public void TwoColoursThatReadAreAGradient()
    {
        Assert.False(new SpaceGradient([]).Usable);
        Assert.False(new SpaceGradient(["#FF0000", "nope"]).Usable);
        Assert.True(new SpaceGradient(["#FF0000", "nope", "#0000FF"]).Usable);
        Assert.Equal(3, new SpaceGradient(["#FF0000", "#00FF00", "#0000FF", "#FFFFFF"]).Drawn.Count);
        Assert.Null(new SpaceGradient(["x"]).Lead);
        Assert.Empty(new SpaceGradient(["#FF0000"]).Stops());
    }

    [Fact]
    public void StopsRunFromTheFirstColourToTheLast()
    {
        var gradient = new SpaceGradient(["#5E8FE0", "#F09A3E", "#D9609B"]);
        var stops = gradient.Stops();
        Assert.Equal(24, stops.Count);
        Assert.Equal((0d, 1d), (stops[0].Location, stops[^1].Location));
        Assert.Equal(gradient.Drawn[0].Srgb, stops[0].Colour);
        Assert.Equal(gradient.Drawn[2].Srgb, stops[^1].Colour);
        Assert.Equal(gradient.Lead, stops[0].Colour);
        Assert.Equal(8, gradient.Stops(count: 8).Count);
        // Blue to orange keeps its colour through the middle rather than greying.
        var (blue, orange) = (gradient.Drawn[0], gradient.Drawn[1]);
        Near((blue.Chroma + orange.Chroma) / 2, blue.Mix(orange, 0.5).Chroma);
        var middle = OkLab.Along(gradient.Drawn, 0.5);
        Near(orange.L, middle.L); Near(orange.A, middle.A); Near(orange.B, middle.B);
    }

    [Fact]
    public void ItRunsAcrossTheWholeWindow()
    {
        static void Is((UnitPoint Start, UnitPoint End) actual, double sx, double sy, double ex, double ey)
        {
            Near(sx, actual.Start.X); Near(sy, actual.Start.Y); Near(ex, actual.End.X); Near(ey, actual.End.Y);
        }
        Is(new SpaceGradient(["#000000", "#FFFFFF"], 90).Points(0, 0, 100, 100), 0, 0.5, 1, 0.5);
        Is(new SpaceGradient(["#000000", "#FFFFFF"], 180).Points(0, 0, 100, 100), 0.5, 0, 0.5, 1);
        Is(new SpaceGradient(["#000000", "#FFFFFF"], 0).Points(0, 0, 100, 100), 0.5, 1, 0.5, 0);
        Is(new SpaceGradient(["#000000", "#FFFFFF"]).Points(0, 0, 100, 100), 0, 0, 1, 1);
        // The column, 50 wide in a window 200 by 100: its part of the one gradient.
        Is(new SpaceGradient(["#000000", "#FFFFFF"], 90).Points(0, 0, 50, 100, 200, 100), 0, 0.5, 4, 0.5);
        Is(new SpaceGradient(["#000000", "#FFFFFF"], 90).Points(150, 0, 50, 100, 200, 100), -3, 0.5, 1, 0.5);
    }

    [Fact]
    public void AGradientAlwaysReads()
    {
        Assert.Equal(new SpaceGradient(["#4FB3A9", "#5E8FE0"], 45, true), Read("""{"colours":["#4FB3A9","#5E8FE0"],"angle":45,"grain":true}"""));
        Assert.Equal(new SpaceGradient([]), Read("5"));
        Assert.Equal(new SpaceGradient([]), Read("""{"colours":["#4FB3A9",1],"angle":"steep","grain":"yes"}"""));
        Assert.Equal(new SpaceGradient(["#4FB3A9"], 22.5), Read("""{"colours":["#4FB3A9"],"angle":22.5}"""));
    }

    [Fact]
    public void WritesTheMacsShape()
    {
        var json = SwiftJson.Encode(w => new SpaceGradient(["#4FB3A9", "#5E8FE0"], 22.5, true).Write(w));
        Assert.Equal("""{"colours":["#4FB3A9","#5E8FE0"],"angle":22.5,"grain":true}""", Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void TheChoicesAreTheMacs()
    {
        Assert.Equal(14, SpaceGradient.Swatches.Count);
        Assert.All(SpaceGradient.Swatches, s => Assert.Equal(s.Hex, OkLab.FromHex(s.Hex)!.Value.Hex));
        Assert.Equal(["Sunrise", "Lagoon", "Orchard", "Dusk", "Glacier", "Ember"], SpaceGradient.Presets.Select(p => p.Name));
        Assert.All(SpaceGradient.Presets, p => Assert.True(p.Gradient.Usable));
        var hex = SpaceGradient.Swatches.ToDictionary(s => s.Name, s => s.Hex);
        Assert.Equal([hex["Teal"], hex["Blue"]], SpaceGradient.Presets[1].Gradient.Colours);
        Assert.Equal(new SpaceGradient([hex["Blue"], hex["Violet"]]), SpaceGradient.Starting(1));
        Assert.Equal(new SpaceGradient([hex["Red"], hex["Amber"]]), SpaceGradient.Starting(4));
        Assert.Equal(SpaceGradient.Presets[1].Gradient, SpaceGradient.Starting(0));
        Assert.Equal(SpaceGradient.Presets[1].Gradient, SpaceGradient.Starting(9));
    }

    [Fact]
    public void StrengthWashesHarderAndStaysOnItsSide()
    {
        Assert.Equal((0.07, 0.11, 0.18, 0.22, 0.30, 0.40),
            (SpaceStrength.Subtle.Opacity(false), SpaceStrength.Subtle.Opacity(true), SpaceStrength.Rich.Opacity(false), SpaceStrength.Rich.Opacity(true), SpaceStrength.Vivid.Opacity(false), SpaceStrength.Vivid.Opacity(true)));
        var light = OkLab.FromHex("#E8F0FF")!.Value.Held;
        Assert.Equal(light, SpaceStrength.Subtle.Tone(light, dark: true));
        Assert.InRange(SpaceStrength.Vivid.Tone(light, dark: true).L, 0.48, 0.58);
        Assert.InRange(SpaceStrength.Rich.Tone(light, dark: true).L, 0, 0.72);
        Assert.InRange(SpaceStrength.Vivid.Tone(OkLab.FromHex("#203040")!.Value.Held, dark: false).L, 0.62, 1);
        // More chroma, but never past the band's for a colour already near it.
        var calm = OkLab.FromLch(0.7, 0.1, 250);
        Near(0.13, SpaceStrength.Vivid.Tone(calm, false).Chroma);
        Near(0.20, SpaceStrength.Vivid.Tone(OkLab.FromLch(0.7, 0.18, 250), false).Chroma);
        Assert.Equal((SpaceStrength.Rich, SpaceStrength.Vivid, SpaceStrength.Subtle, SpaceStrength.Subtle), (SpaceStrengths.Parse("rich"), SpaceStrengths.Parse("vivid"), SpaceStrengths.Parse("ultra"), SpaceStrengths.Parse(null)));
        Assert.Equal(("rich", "Vivid"), (SpaceStrength.Rich.Raw(), SpaceStrength.Vivid.Name()));
        Near(SpaceStrengths.GrainLight * 1.5, SpaceStrength.Vivid.Grain(false));
    }

    [Fact]
    public void ASpacesLook()
    {
        Assert.Null(Spaces.Wash(0, false));
        Assert.Null(Spaces.Wash(6, false));
        Assert.Null(Spaces.Wash(null, false));
        Assert.Equal(Spaces.WashDark, Spaces.Wash(1, true));
        Assert.Equal(Spaces.Colours[2], Spaces.Toned(2, SpaceStrength.Subtle, false));
        Assert.NotEqual(Spaces.Colours[2], Spaces.Toned(2, SpaceStrength.Vivid, true));
        Assert.Null(Spaces.Toned(9, SpaceStrength.Rich, false));

        var plain = new Space(Guid.NewGuid(), "Plain");
        Assert.Null(plain.Look);
        Assert.Null(plain.Tint);
        Assert.Equal("No colour", plain.LookName);
        Assert.Null(SpaceWash.Of(plain.Look, SpaceStrength.Vivid, false));
        var future = plain with { Colour = 12 };
        Assert.Null(future.Look);
        Assert.Null(future.Tint);
        var orange = plain with { Colour = 3 };
        Assert.Equal(new SpaceLook.Colour(3), orange.Look);
        Assert.Equal(Spaces.Colours[3], orange.Tint);
        Assert.Equal("Orange", orange.LookName);
        var lagoon = orange with { Gradient = SpaceGradient.Presets[1].Gradient with { Angle = 10 } };
        Assert.Equal((new SpaceLookChoice.Preset(1), "Lagoon"), (lagoon.LookChoice, lagoon.LookName));
        Assert.Equal(lagoon.Gradient!.Lead, lagoon.Tint);
        // A gradient that can't be drawn leaves the colour standing.
        Assert.Equal(new SpaceLook.Colour(3), (orange with { Gradient = new(["#FFFFFF"]) }).Look);

        var wash = SpaceWash.Of(lagoon.Look, SpaceStrength.Subtle, false)!;
        Assert.Null(wash.Colour);
        Assert.Equal((24, 10d, 0.07, 0d), (wash.Stops.Count, wash.Angle, wash.Opacity, wash.Grain));
        var solid = SpaceWash.Of(orange.Look, SpaceStrength.Rich, true)!;
        Assert.Equal(Spaces.Toned(3, SpaceStrength.Rich, true), solid.Colour);
        Assert.Equal((0, 0.22), (solid.Stops.Count, solid.Opacity));
    }
}
