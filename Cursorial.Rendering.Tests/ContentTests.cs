using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

public class ScaledTextTests
{
    private static OutputCapabilities WithTextSizing(bool width = true, bool scale = true)
        => OutputCapabilities.None with
           {
               TextSizing = new TextSizingCapabilities(Width: width, Scale: scale)
           };

    [Fact]
    public void Paint_WhenOsc66Supported_AttachesFragment()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2));
        var buffer = new CellBuffer(40, 6);

        var painted = content.Paint(buffer, 0, 0, default, WithTextSizing());

        Assert.NotEmpty(buffer.Fragments);
        Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);
        // OSC 66 size: 2 clusters × 1 (natural) × 2 (scale) = 4 columns, 2 rows.
        Assert.Equal(new Size(4, 2), painted.Size);
    }

    /// <summary>
    /// A footprint-identity fractional sizing (s=1:n=1:d=2) still emits OSC 66 — it must not be
    /// optimised into the cell walk.
    /// </summary>
    /// <remarks>Migrated from the characterisation corpus (sized-block-fractional): the routing half —
    /// TextSizing.IsSupported's Numerator/Denominator clause keeps the fragment path even when the
    /// fraction never changes the footprint. The writer's n/d bytes are pinned in TextSizingWriterTests,
    /// the unchanged footprint in TextFormatterMetricsTests.</remarks>
    [Fact]
    public void Paint_FootprintIdentityFraction_StillRoutesToTheFragment()
    {
        var content = new ScaledText("half", new TextSizing(Scale: 1, Numerator: 1, Denominator: 2));
        var buffer = new CellBuffer(24, 3);

        var painted = content.Paint(buffer, 0, 0, default, WithTextSizing());

        Assert.NotEmpty(buffer.Fragments);
        var fragment = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);
        Assert.Equal(1, fragment.Sizing.Numerator);
        Assert.Equal(2, fragment.Sizing.Denominator);

        // Footprint identity: 4 clusters × 1 row, the same cells plain text would take — and the cell
        // walk did NOT run: the anchor cell holds no glyph.
        Assert.Equal(new Size(4, 1), painted.Size);
        Assert.True(string.IsNullOrEmpty(buffer[0, 0].Grapheme));
    }

    [Fact]
    public void Paint_WhenOsc66Unsupported_FallsBackToShadowedFont()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2));
        var buffer = new CellBuffer(80, 8);

        var painted = content.Paint(buffer, 0, 0, default, OutputCapabilities.None);

        // No fragment should have been attached — fallback paints cells directly.
        Assert.Empty(buffer.Fragments);

        // The fallback for scale=2 is the ShadowFont, which is 2 rows tall.
        Assert.Equal(2, painted.Rows);
        Assert.True(painted.Columns > 0);

        // Some cells should have ink — proof the font path painted something.
        bool anyInk = false;

        for (int r = 0; r < painted.Rows && !anyInk; r++)
        {
            for (int c = 0; c < painted.Columns && !anyInk; c++)
            {
                if (!string.IsNullOrEmpty(buffer[c, r].Grapheme))
                    anyInk = true;
            }
        }

        Assert.True(anyInk);
    }

    [Fact]
    public void Paint_WhenOsc66Unsupported_FallsBackToFigletFont()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 5));
        var buffer = new CellBuffer(80, 8);

        var painted = content.Paint(buffer, 0, 0, default, OutputCapabilities.None);

        // No fragment should have been attached — fallback paints cells directly.
        Assert.Empty(buffer.Fragments);

        // The fallback for scale=5 is the bundled Small FIGlet font, which is 5 rows tall.
        Assert.Equal(5, painted.Rows);
        Assert.True(painted.Columns > 0);

        // Some cells should have ink — proof the font path painted something.
        bool anyInk = false;

        for (int r = 0; r < painted.Rows && !anyInk; r++)
        {
            for (int c = 0; c < painted.Columns && !anyInk; c++)
            {
                if (!string.IsNullOrEmpty(buffer[c, r].Grapheme))
                    anyInk = true;
            }
        }

        Assert.True(anyInk);
    }

    [Fact]
    public void Paint_NormalScale_FallsBackToMonospace()
    {
        // No sized request — default sizing is "normal text". Even with OSC 66 support, the
        // fragment's IsSupported returns false (no sub-feature exercised) so we hit the
        // fallback.
        var content = new ScaledText("Hi");
        var buffer = new CellBuffer(10, 2);

        var painted = content.Paint(buffer, 0, 0, default, WithTextSizing());

        Assert.Empty(buffer.Fragments);
        Assert.Equal(new Size(2, 1), painted.Size);
        Assert.Equal("H", buffer[0, 0].Grapheme);
        Assert.Equal("i", buffer[1, 0].Grapheme);
    }

    [Fact]
    public void Paint_ExplicitFallbackFontIsHonored()
    {
        // Pass the Mini font explicitly so we don't depend on Standard's exact dimensions.
        var content = new ScaledText("Hi", new TextSizing(Scale: 2), FigletFonts.Mini);
        var buffer = new CellBuffer(40, 8);

        var painted = content.Paint(buffer, 0, 0, default, OutputCapabilities.None);

        Assert.Empty(buffer.Fragments);
        Assert.Equal(FigletFonts.Mini.Height, painted.Rows);
    }
}

public class ShadowedFontTests
{
    [Fact]
    public void Measure_IncludesShadowOffset()
    {
        var inner = FigletFonts.Mini;
        var shadowed = new ShadowedFont(inner, offset: (2, 1));

        var innerSize = inner.Measure("X");
        var shadowedSize = shadowed.Measure("X");

        Assert.Equal(innerSize.Columns + 2, shadowedSize.Columns);
        Assert.Equal(innerSize.Rows + 1, shadowedSize.Rows);
    }

    [Fact]
    public void Paint_DrawsShadowFirstThenGlyph()
    {
        // Build a tiny 1x1 font where 'X' is a single 'X' character. Shadow at offset (1, 0)
        // means shadow lands at column 1; glyph at column 0. Both written.
        var glyph = new FigletGlyph('X', ["X"], '$');

        var font = new FigletFont("test", '$', 1, FigletLayoutMode.None,
                                  new Dictionary<uint, FigletGlyph> { ['X'] = glyph });

        var shadowStyle = CellStyle.Default.WithForeground(Color.FromRgb(100, 100, 100));
        var fgStyle = CellStyle.Default.WithForeground(Color.FromRgb(255, 255, 255));

        var shadowed = new ShadowedFont(font, offset: (1, 0), shadowStyle: shadowStyle);
        var buffer = new CellBuffer(5, 1);

        shadowed.Paint(buffer, 0, 0, "X", PartialStyle.FromInk(fgStyle));

        // Glyph at column 0 wins (painted last with foreground style).
        Assert.Equal("X", buffer[0, 0].Grapheme);
        Assert.Equal(fgStyle.Foreground, buffer[0, 0].Style.Foreground);

        // Shadow at column 1 — was painted first, never overwritten by the glyph (whose right
        // edge is column 0).
        Assert.Equal("X", buffer[1, 0].Grapheme);
        Assert.Equal(shadowStyle.Foreground, buffer[1, 0].Style.Foreground);
    }

    [Fact]
    public void Paint_EmptyText_NoOp()
    {
        var shadowed = new ShadowedFont(FigletFonts.Mini);
        var buffer = new CellBuffer(10, 5);

        Assert.Equal(Size.Empty, shadowed.Paint(buffer, 0, 0, "", default(PartialStyle)));
    }

    // ───────────────────────── the shadow as a DELTA ─────────────────────────
    //
    // The shadow used to be a whole CellStyle, which forced two defects this section pins the absence of.
    // It had to state channels a shadow has no opinion about, so the paint hand-patched the underline shape
    // and the attribute word back off the base before it could composite; and it had nowhere to put a
    // blending mode, so the paint recovered one by comparing the style it held against `CellStyle.DefaultShadow`
    // — which meant any caller who passed an equal-looking style silently inherited MULTIPLY.

    private static readonly Color Amber = Color.FromRgb(220, 160, 40);
    private static readonly Color Teal = Color.FromRgb(0, 128, 128);

    /// <summary>A 4x2 board with a teal backdrop, painted "ab" at (0,0) over an amber-backed base.</summary>
    /// <remarks>
    /// Both backgrounds are load-bearing for the MODE tests. The shadow's ink is half-alpha black, so it
    /// composites against the BASE's background to a mid tone (rather than to black, which is a fixed point
    /// of every mode and would hide the mode entirely), and the buffer's own backdrop then has to be a real
    /// colour for the mode's blend to be an RGB-on-RGB one.
    /// </remarks>
    private static CellBuffer ShadowBoard(ShadowedFont face, CellStyle baseStyle)
    {
        var buffer = new CellBuffer(4, 2);
        buffer.Fill(Cell.Blank with { Style = CellStyle.Default.WithBackground(Teal) });

        // Restated whole, sentinel read as at the product call sites: the base must survive over the teal
        // board rather than fall through to it.
        face.Paint(buffer, 0, 0, "ab",
                   baseStyle.Background.IsDefault ? BrushedStyle.FromInk(baseStyle) : BrushedStyle.From(baseStyle),
                   new Rect(0, 0, 4, 2));

        return buffer;
    }

    /// <summary>The one cell only the shadow reaches: glyphs land at (0,0)-(1,0), the shadow at (1,1)-(2,1).</summary>
    private static Cell ShadowOnlyCell(CellBuffer buffer) => buffer[2, 1];

    [Fact]
    public void TheDefaultShadowCarriesItsOwnBlendingMode()
    {
        Assert.Same(BlendingModes.Multiply, PartialStyle.DefaultShadow.Mode);
        Assert.Same(BlendingModes.Multiply, ShadowedFont.Default.ShadowBlendingMode);
        Assert.Same(BlendingModes.Multiply, new ShadowedFont(MonospaceFont.Default).ShadowBlendingMode);
    }

    /// <summary>
    /// A shadow delta that states its own <see cref="PartialStyle.Mode"/> composites under it, and the
    /// constructor's explicit mode still beats both. This is the path that used to hang off the sentinel
    /// comparison — the mode was recovered by testing the style for equality with the default shadow — so
    /// it is the one most likely to have silently reverted to <see cref="BlendingModes.Default"/>.
    /// </summary>
    [Fact]
    public void AnExplicitShadowModeOverridesTheDefault_AndTheConstructorArgumentOverridesTheDelta()
    {
        var baseStyle = CellStyle.Default.WithForeground(Color.FromRgb(255, 255, 255)).WithBackground(Amber);

        var multiplied = ShadowOnlyCell(ShadowBoard(new ShadowedFont(MonospaceFont.Default, (1, 1)), baseStyle));
        var screened = ShadowOnlyCell(ShadowBoard(
            new ShadowedFont(MonospaceFont.Default, (1, 1), PartialStyle.DefaultShadow with { Mode = BlendingModes.Screen }),
            baseStyle));
        var overridden = ShadowOnlyCell(ShadowBoard(
            new ShadowedFont(MonospaceFont.Default, (1, 1), PartialStyle.DefaultShadow, BlendingModes.Screen),
            baseStyle));

        // Same ink, three modes: only the composite differs, so the grapheme has to survive all three.
        Assert.Equal("b", multiplied.Grapheme);
        Assert.Equal("b", screened.Grapheme);
        Assert.Equal("b", overridden.Grapheme);

        // MULTIPLY darkens the mid-tone shadow ink against the teal backdrop; SCREEN lightens it. If the
        // delta's mode stopped reaching the composite, both would collapse onto the same frame.
        Assert.Equal(Color.FromRgb(0, 40, 10), multiplied.Style.Foreground);
        Assert.Equal(Color.FromRgb(110, 168, 138), screened.Style.Foreground);

        // ...and the constructor argument wins over the delta's own mode, not the other way round.
        Assert.Equal(screened.Style.Foreground, overridden.Style.Foreground);
    }

    /// <summary>
    /// The shadow states three colour channels and a mode, and nothing else — so the base's underline SHAPE
    /// and its attribute flags reach the shadow cells untouched. That is what absence MEANS in a delta, and
    /// it is what the paint used to fake by copying both back off the base by hand before compositing.
    /// </summary>
    [Fact]
    public void TheShadowLeavesTheBasesAttributesAndUnderlineShapeAlone()
    {
        var baseStyle = CellStyle.Default
                                 .WithForeground(Color.FromRgb(255, 255, 255))
                                 .WithAttributes(TextAttributes.Bold | TextAttributes.Italic |
                                                 TextAttributes.Underline | TextAttributes.Strikethrough)
                                 .WithUnderlineStyle(UnderlineStyle.Curly);

        var shadow = ShadowOnlyCell(ShadowBoard(new ShadowedFont(MonospaceFont.Default, (1, 1)), baseStyle));

        Assert.Equal("b", shadow.Grapheme);
        Assert.Equal(baseStyle.Attributes, shadow.Style.Attributes);
        Assert.Equal(UnderlineStyle.Curly, shadow.Style.UnderlineStyle);

        // The colour channels the shadow DOES state are still its own — otherwise "leaves the rest alone"
        // would be trivially true of a delta that left everything alone.
        Assert.NotEqual(baseStyle.Foreground, shadow.Style.Foreground);
    }

    /// <summary>
    /// The face's compatibility mask survives the move to a delta: INVERSE would light the cells the shadow
    /// exists to darken, and OVERLINE would rule a line above the glyph the shadow is cast from. Both are
    /// forced off over WHATEVER the base carries, which is one act where the whole-style form needed a
    /// hand-built union and a mask over it.
    /// </summary>
    [Fact]
    public void ForbiddenAttributesNeverReachTheShadow_WhoeverStatedThem()
    {
        var fromTheBase = ShadowOnlyCell(ShadowBoard(
            new ShadowedFont(MonospaceFont.Default, (1, 1)),
            CellStyle.Default.WithAttributes(TextAttributes.Bold | TextAttributes.Inverse | TextAttributes.Overline)));

        var fromTheShadowStyle = ShadowOnlyCell(ShadowBoard(
            new ShadowedFont(MonospaceFont.Default, (1, 1),
                             CellStyle.Default.WithForeground(Color.FromRgb(60, 60, 60))
                                      .WithAttributes(TextAttributes.Inverse | TextAttributes.Overline | TextAttributes.Blink)),
            CellStyle.Default.WithAttributes(TextAttributes.Bold)));

        Assert.Equal(TextAttributes.Bold, fromTheBase.Style.Attributes);
        Assert.Equal(TextAttributes.Bold | TextAttributes.Blink, fromTheShadowStyle.Style.Attributes);
    }

    /// <summary>
    /// A shadow style's attribute word ADDS to the glyph's rather than replacing it — the union the paint
    /// used to spell by hand, now the delta's own encoding.
    /// </summary>
    [Fact]
    public void AShadowStylesAttributesJoinTheBases_TheyDoNotReplaceThem()
    {
        var shadow = ShadowOnlyCell(ShadowBoard(
            new ShadowedFont(MonospaceFont.Default, (1, 1),
                             CellStyle.Default.WithForeground(Color.FromRgb(60, 60, 60))
                                      .WithAttributes(TextAttributes.Faint)),
            CellStyle.Default.WithAttributes(TextAttributes.Italic)));

        Assert.Equal(TextAttributes.Italic | TextAttributes.Faint, shadow.Style.Attributes);
    }
}