using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

// Markup gradients: a [brush=VALUE]…[/brush] tag, resolved by BrushMarkup — inline gradient syntax
// (linear:/radial:/conic: + a color list) or a named-brush registry. The opaque Tag channel keeps IBrush
// out of Rendering; DrawFormattedText reads the tag and brushes the run.
public class BrushMarkupTests
{
    private static FormattedText Format(string markup, TextMarkupOptions options, int width = 12) =>
        new TextFormatter().Format(TextMarkup.Parse(markup, options), width);

    [Fact]
    public void InlineLinearGradient_ColorsTheRunAcrossItsStrip()
    {
        var ft = Format("[brush=linear:#ff0000,#0000ff]ABCD[/brush]", BrushMarkup.Options());
        var b = DrawHarness.Render(12, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 12, 2), OutputCapabilities.None));

        var a = b[0, 0].Style.Foreground;   // strip start
        var d = b[3, 0].Style.Foreground;   // strip end
        Assert.True(a.Red > a.Blue, $"A should be red-dominant, was {a}");
        Assert.True(d.Blue > d.Red, $"D should be blue-dominant, was {d}");
    }

    [Fact]
    public void InlineGradient_AcceptsNamedColors()
    {
        // Named colors (palette-backed) parse and brush the run — not the default foreground.
        var ft = Format("[brush=radial:brightcyan,magenta]Z[/brush]", BrushMarkup.Options());
        var b = DrawHarness.Render(8, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 8, 2), OutputCapabilities.None));
        Assert.Equal("Z", b[0, 0].Grapheme);
        Assert.Equal(Color.FromPalette(14), b[0, 0].Style.Foreground);   // brightcyan, the center (offset 0) stop
    }

    [Fact]
    public void RegistryBrush_ResolvesByName()
    {
        var registry = new Dictionary<string, BrushedStyle>(StringComparer.OrdinalIgnoreCase)
        {
            ["solidred"] = new BrushedStyle(new SolidColorBrush(Color.FromRgb(255, 0, 0))),
        };
        var ft = Format("[brush=solidred]X[/brush]", BrushMarkup.Options(registry: registry), width: 8);
        var b = DrawHarness.Render(8, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 8, 2), OutputCapabilities.None));
        Assert.Equal(Color.FromRgb(255, 0, 0), b[0, 0].Style.Foreground);
    }

    [Fact]
    public void Brush_WithoutResolver_Throws()
    {
        // [brush] with no resolver (plain options) is a clear parse error, not silent.
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[brush=linear:red,blue]X[/brush]", TextMarkupOptions.Empty));
    }

    [Fact]
    public void Brush_UnknownValue_Throws()
    {
        // Not inline (no kind:) and not in the registry → rejected.
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[brush=nonexistent]X[/brush]", BrushMarkup.Options()));
    }

    [Fact]
    public void InlineGradient_NeedsAtLeastTwoColors()
    {
        Assert.Throws<FormatException>(() => TextMarkup.Parse("[brush=linear:#ff0000]X[/brush]", BrushMarkup.Options()));
    }

    [Fact]
    public void Brush_NestedInsideFg_PopsCorrectly()
    {
        // [fg=red]a[brush=…]b[/brush]c[/fg]: the brush wraps only "b" (and overrides the fg there); "a" and "c"
        // keep fg=red, confirming the tag scope pops cleanly when interleaved with a style tag.
        var ft = Format("[fg=red]a[brush=linear:#ff0000,#0000ff]b[/brush]c[/fg]", BrushMarkup.Options());
        var b = DrawHarness.Render(12, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 12, 2), OutputCapabilities.None));
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("c", b[2, 0].Grapheme);
        Assert.Equal(Color.FromPalette(1), b[0, 0].Style.Foreground);    // 'a' — fg=red
        Assert.Equal(Color.FromPalette(1), b[2, 0].Style.Foreground);    // 'c' — fg=red still applies after [/brush]
        Assert.NotEqual(Color.FromPalette(1), b[1, 0].Style.Foreground); // 'b' — brushed (overrides fg=red)
    }

    [Theory]
    [InlineData("#ff8800", 255, 136, 0)]
    [InlineData("#f80", 255, 136, 0)]
    public void MarkupColor_ParsesHex(string value, int r, int g, int b)
    {
        Assert.True(MarkupColor.TryParse(value, out var c));
        Assert.Equal((r, g, b), (c.Red, c.Green, c.Blue));
    }

    [Fact]
    public void MarkupColor_NamedPaletteAndInvalid()
    {
        Assert.True(MarkupColor.TryParse("red", out var red));
        Assert.Equal(Color.FromPalette(1), red);
        Assert.True(MarkupColor.TryParse("200", out var pal));
        Assert.Equal(Color.FromPalette(200), pal);
        Assert.False(MarkupColor.TryParse("notacolor", out _));
        Assert.False(MarkupColor.TryParse("", out _));
        Assert.False(MarkupColor.TryParse("300", out _));   // palette out of range
    }
}
