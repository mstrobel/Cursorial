using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
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

        var painted = content.Paint(buffer, 0, 0, CellStyle.Default, WithTextSizing());

        Assert.NotEmpty(buffer.Fragments);
        Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);
        // OSC 66 size: 2 clusters × 1 (natural) × 2 (scale) = 4 columns, 2 rows.
        Assert.Equal(new Size(4, 2), painted.Size);
    }

    [Fact]
    public void Paint_WhenOsc66Unsupported_FallsBackToShadowedFont()
    {
        var content = new ScaledText("Hi", new TextSizing(Scale: 2));
        var buffer = new CellBuffer(80, 8);

        var painted = content.Paint(buffer, 0, 0, CellStyle.Default, OutputCapabilities.None);

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

        var painted = content.Paint(buffer, 0, 0, CellStyle.Default, OutputCapabilities.None);

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

        var painted = content.Paint(buffer, 0, 0, CellStyle.Default, WithTextSizing());

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

        var painted = content.Paint(buffer, 0, 0, CellStyle.Default, OutputCapabilities.None);

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

        shadowed.Paint(buffer, 0, 0, "X", fgStyle);

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

        Assert.Equal(Size.Empty, shadowed.Paint(buffer, 0, 0, "", CellStyle.Default));
    }
}