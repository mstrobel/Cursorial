using System.Globalization;
using System.Reflection;

using Cursorial.Media;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
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
        var registry = new Dictionary<string, ScopedBrush>(StringComparer.OrdinalIgnoreCase)
        {
            ["solidred"] = new ScopedBrush(new SolidColorBrush(Color.FromRgb(255, 0, 0))),
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
        Assert.Equal(ColorPalette.Ansi256[200], pal);
        Assert.False(MarkupColor.TryParse("notacolor", out _));
        Assert.False(MarkupColor.TryParse("", out _));
        Assert.False(MarkupColor.TryParse("300", out _));   // palette out of range
    }

    // ───────────────────────── MarkupColor: the two parse paths must agree ─────────────────────────

    /// <summary>
    /// The invariant: for every token both entry points can parse, the brush produced by
    /// <see cref="MarkupColor.TryParseBrush(string, out IBrush)"/> carries exactly the
    /// <see cref="Color"/> that <see cref="MarkupColor.TryParse(string, out Color)"/> returns — the same
    /// value AND the same <see cref="ColorKind"/> — and a token neither can parse fails on both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This already drifted once. The flattening of indices ≥ 16 through <see cref="ColorPalette.Ansi256"/>
    /// was added on the color side only, which silently made <see cref="BrushPalette.Ansi256"/> unreachable
    /// from <see cref="BrushMarkup"/>: the <c>&lt; 16</c> arm covered every survivor, so the intern table was
    /// bypassed and every token allocated a fresh brush. Nobody noticed until an audit traced reachability.
    /// Both paths now share one core and one <c>TryParseBrush</c>; these tests are what stop them diverging
    /// again — including the reference-identity assertions, which is the property the routing exists to get
    /// and which no value comparison can see.
    /// </para>
    /// <para>
    /// The 16-index split is load-bearing in two directions, so both are pinned:
    /// </para>
    /// <list type="bullet">
    /// <item>Index &lt; 16 must stay <see cref="ColorKind.Palette"/>, because <c>ResourceBrushResolver</c>
    /// redirects exactly that shape to <c>ThemeKeys.Ansi*</c> so a theme can restyle it. Flattening these to
    /// RGB would break the redirect and quietly un-theme <c>[fg=red]</c> / <c>[fg=1]</c>.</item>
    /// <item>Index ≥ 16 must become <see cref="ColorKind.Rgb"/>, because palette colors cannot participate in
    /// alpha blending or group opacity. Leaving them palette-kind would make <c>[fg=200]</c> opt out of
    /// compositing.</item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]    // the boundary, below
    public void MarkupColor_PaletteIndexBelow16_StaysPaletteKindOnBothPaths(int index)
    {
        var (color, brush) = AssertBothPathsAgree(index.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(ColorKind.Palette, color.Kind);
        Assert.Equal(index, (int) color.PaletteIndex);
        Assert.Same(SolidColorBrush.FromPalette(index), brush);   // the ANSI-16 intern table
    }

    /// <inheritdoc cref="MarkupColor_PaletteIndexBelow16_StaysPaletteKindOnBothPaths"/>
    [Theory]
    [InlineData(16)]    // the boundary, at
    [InlineData(17)]
    [InlineData(200)]
    [InlineData(255)]
    public void MarkupColor_PaletteIndex16AndAbove_FlattensToRgbOnBothPaths(int index)
    {
        var (color, brush) = AssertBothPathsAgree(index.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(ColorKind.Rgb, color.Kind);
        Assert.Equal(ColorPalette.Ansi256[index], color);
        Assert.Same(BrushPalette.Ansi256[index], brush);         // the xterm-256 intern table
    }

    /// <inheritdoc cref="MarkupColor_PaletteIndexBelow16_StaysPaletteKindOnBothPaths"/>
    [Theory]
    [MemberData(nameof(NamedColorTokens))]
    public void MarkupColor_NamedColor_AgreesOnBothPaths(string name, byte index)
    {
        var expectedColor = index < 16 ? Color.FromPalette(index) : ColorPalette.Ansi256[index];
        var expectedBrush = index < 16 ? SolidColorBrush.FromPalette(index) : BrushPalette.Ansi256[index];

        foreach (string spelling in new[] { name, name.ToUpperInvariant() })   // names are case-insensitive
        {
            var (color, brush) = AssertBothPathsAgree(spelling);

            Assert.Equal(expectedColor.Kind, color.Kind);
            Assert.Equal(expectedColor, color);
            Assert.Same(expectedBrush, brush);   // a name is an alias for its index, so it interns like one
        }
    }

    /// <inheritdoc cref="MarkupColor_PaletteIndexBelow16_StaysPaletteKindOnBothPaths"/>
    [Theory]
    [InlineData("#f80")]
    [InlineData("#ff8800")]
    [InlineData("#FF8800")]
    [InlineData("#ff880080")]   // the alpha spelling — the brush must carry the alpha, not drop it
    public void MarkupColor_Hex_AgreesOnBothPaths(string token)
    {
        var (color, _) = AssertBothPathsAgree(token);
        Assert.Equal(ColorKind.Rgb, color.Kind);
    }

    /// <inheritdoc cref="MarkupColor_PaletteIndexBelow16_StaysPaletteKindOnBothPaths"/>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("notacolor")]
    [InlineData("256")]         // palette index out of range
    [InlineData("300")]
    [InlineData("-1")]
    [InlineData("1abc")]
    [InlineData("red ")]        // no trimming — a name is matched verbatim
    [InlineData("#")]
    [InlineData("#ff")]
    [InlineData("#gggggg")]     // accepted length, bad digits
    [InlineData("#ff88000")]    // 7 digits — no accepted spelling
    public void MarkupColor_UnparseableToken_FailsOnBothPaths(string? token)
    {
        Assert.False(MarkupColor.TryParse(token, out var color, out var colorError));
        Assert.False(MarkupColor.TryParseBrush(token, out var brush, out var brushError));

        Assert.Equal(default(Color), color);
        Assert.Null(brush);

        // Both route through the same core, so they must agree on *why* it failed, not merely *that* it did.
        Assert.Equal(colorError?.GetType(), brushError?.GetType());
    }

    [Fact]
    public void MarkupColor_PaletteBrushBelow16_IsInterned()
    {
        Assert.True(MarkupColor.TryParseBrush("9", out var first));
        Assert.True(MarkupColor.TryParseBrush("9", out var second));

        Assert.Same(first, second);
        Assert.Same(SolidColorBrush.FromPalette(9), first);
    }

    [Fact]
    public void MarkupColor_PaletteBrush16AndAbove_IsInterned()
    {
        // The regression this file exists for: with the ≥ 16 flattening on the color path only, TryParseBrush's
        // `< 16` arm covered every survivor, BrushPalette.Ansi256 went unreachable, and every [fg=200] allocated
        // a fresh brush. Value assertions all still passed.
        Assert.True(MarkupColor.TryParseBrush("200", out var first));
        Assert.True(MarkupColor.TryParseBrush("200", out var second));

        Assert.Same(first, second);
        Assert.Same(BrushPalette.Ansi256[200], first);
    }

    [Fact]
    public void MarkupColor_HexBrush_IsAllowedToAllocate()
    {
        // Stated rather than left unstated: hex tokens have no intern table, so each parse allocates. If one is
        // ever added, retiring this assertion is the deliberate act that records the decision.
        Assert.True(MarkupColor.TryParseBrush("#ff8800", out var first));
        Assert.True(MarkupColor.TryParseBrush("#ff8800", out var second));

        Assert.NotSame(first, second);
    }

    // The named-color table is private, so reflect it rather than restating the list here: a name added to
    // MarkupColor is then covered automatically, and names are the tokens most likely to get special-cased on
    // one path only.
    public static TheoryData<string, byte> NamedColorTokens()
    {
        var field = typeof(MarkupColor).GetField("NamedColors", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException(
                        "MarkupColor.NamedColors was renamed or removed; this test would otherwise cover nothing.");

        if (field.GetValue(null) is not Dictionary<string, byte> { Count: > 0 } names)
            throw new InvalidOperationException("MarkupColor.NamedColors is not a non-empty Dictionary<string, byte>.");

        var data = new TheoryData<string, byte>();
        foreach (var (name, index) in names) data.Add(name, index);
        return data;
    }

    // Parses `token` through both entry points and asserts they agree: same success, and on success the brush's
    // declared Color is exactly the Color TryParse returned — same value and same ColorKind.
    private static (Color Color, IBrush Brush) AssertBothPathsAgree(string token)
    {
        Assert.True(MarkupColor.TryParse(token, out var color), $"TryParse rejected '{token}'");
        Assert.True(MarkupColor.TryParseBrush(token, out var brush), $"TryParseBrush rejected '{token}'");

        // Every arm of TryParseBrush's switch yields a solid brush. A future arm that doesn't would have no single
        // declared color to compare, so pin the shape here rather than silently skipping the comparison.
        var solid = Assert.IsType<SolidColorBrush>(brush);

        Assert.Equal(color.Kind, solid.Color.Kind);
        Assert.Equal(color, solid.Color);

        return (color, brush!);
    }
}
