using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class CellBufferBlendingTests
{
    // ---- Stack ----

    [Fact]
    public void CurrentBlendingMode_EmptyStack_ReturnsDefault()
    {
        var buf = new CellBuffer(2, 2);
        Assert.Same(BlendingModes.Default, buf.CurrentBlendingMode);
    }

    [Fact]
    public void Push_ThenCurrentBlendingMode_ReturnsPushedMode()
    {
        var buf = new CellBuffer(2, 2);
        buf.PushBlendingMode(BlendingModes.Multiply);
        Assert.Same(BlendingModes.Multiply, buf.CurrentBlendingMode);
    }

    [Fact]
    public void PushTwice_TopOfStackIsCurrent()
    {
        var buf = new CellBuffer(2, 2);
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.PushBlendingMode(BlendingModes.Screen);
        Assert.Same(BlendingModes.Screen, buf.CurrentBlendingMode);
    }

    [Fact]
    public void Pop_RestoresPreviousMode()
    {
        var buf = new CellBuffer(2, 2);
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.PushBlendingMode(BlendingModes.Screen);

        var popped = buf.PopBlendingMode();

        Assert.Same(BlendingModes.Screen, popped);
        Assert.Same(BlendingModes.Multiply, buf.CurrentBlendingMode);
    }

    [Fact]
    public void Pop_EmptyStack_Throws()
    {
        var buf = new CellBuffer(2, 2);
        Assert.Throws<InvalidOperationException>(buf.PopBlendingMode);
    }

    [Fact]
    public void Push_NullMode_Throws()
    {
        var buf = new CellBuffer(2, 2);
        Assert.Throws<ArgumentNullException>(() => buf.PushBlendingMode(null!));
    }

    // ---- Set: blending behavior ----

    [Fact]
    public void Set_NoActiveMode_StoresSourceVerbatim()
    {
        var buf = new CellBuffer(3, 1);
        var style = Style.Default.WithForeground(Color.FromRgb(255, 0, 0));
        buf.Set(0, 0, "x", style);

        Assert.Equal(Color.FromRgb(255, 0, 0), buf[0, 0].Style.Foreground);
    }

    [Fact]
    public void Set_MultiplyMode_BlendsAgainstPreviousCell()
    {
        var buf = new CellBuffer(3, 1);
        // Lay down a half-gray cell first. The foreground composes against the underlying
        // cell's *background* (not foreground — the source's glyph replaces whatever
        // foreground content was there), so seed the bg with half-gray too.
        buf.Set(0, 0, "x", Style.Default.WithBackground(Color.FromRgb(128, 128, 128)));

        // Push Multiply, set with half-gray foreground. The result should be ~1/4
        // (half-gray × half-gray = quarter-gray).
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgb(128, 128, 128)));

        Assert.Equal(Color.FromRgb(64, 64, 64), buf[0, 0].Style.Foreground);
        // The grapheme of the new cell should be the source's, not the previous one.
        Assert.Equal("y", buf[0, 0].Grapheme);
    }

    [Fact]
    public void Set_BlendsBackground()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, " ", Style.Default.WithBackground(Color.FromRgb(200, 200, 200)));

        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, " ", Style.Default.WithBackground(Color.FromRgb(128, 128, 128)));

        // 128 * 200 / 255 ≈ 100.
        Assert.Equal((byte) 100, buf[0, 0].Style.Background.Red);
    }

    [Fact]
    public void Set_NonColorStyleFieldsAreSourceVerbatim()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithAttributes(TextAttributes.Italic));

        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, "y", Style.Default.WithAttributes(TextAttributes.Bold));

        // Source attributes win — no blending of non-color style fields.
        Assert.Equal(TextAttributes.Bold, buf[0, 0].Style.Attributes);
    }

    [Fact]
    public void Set_AfterPop_StopsBlending()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(128, 128, 128)));

        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.PopBlendingMode();

        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgb(255, 255, 255)));
        Assert.Equal(Color.FromRgb(255, 255, 255), buf[0, 0].Style.Foreground);
    }

    // ---- Fill: blending behavior ----

    [Fact]
    public void Fill_NoActiveMode_FastPathReplacesEveryCell()
    {
        var buf = new CellBuffer(3, 2);
        var fill = new Cell("x", CellKind.Single, Style.Default.WithBackground(Color.FromRgb(100, 100, 100)));

        buf.Fill(fill);

        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 3; c++)
                Assert.Equal(fill, buf[r, c]);
        }
    }

    [Fact]
    public void Fill_MultiplyMode_BlendsAgainstEachCellIndividually()
    {
        var buf = new CellBuffer(3, 1);
        // Different existing backgrounds in each cell.
        buf.Set(0, 0, " ", Style.Default.WithBackground(Color.FromRgb(255, 0, 0)));
        buf.Set(0, 1, " ", Style.Default.WithBackground(Color.FromRgb(0, 255, 0)));
        buf.Set(0, 2, " ", Style.Default.WithBackground(Color.FromRgb(0, 0, 255)));

        buf.PushBlendingMode(BlendingModes.Multiply);
        // Fill with half-gray.
        buf.Fill(new Cell(" ", CellKind.Single, Style.Default.WithBackground(Color.FromRgb(128, 128, 128))));

        // Each cell multiplied: half-gray * red = (128, 0, 0), etc.
        Assert.Equal((byte) 128, buf[0, 0].Style.Background.Red);
        Assert.Equal((byte) 0, buf[0, 0].Style.Background.Green);
        Assert.Equal((byte) 128, buf[0, 1].Style.Background.Green);
        Assert.Equal((byte) 128, buf[0, 2].Style.Background.Blue);
    }

    // ---- Alpha compositing ----

    [Fact]
    public void Set_AlphaZeroSource_LeavesBackdropColorUnchanged()
    {
        var buf = new CellBuffer(3, 1);
        // Alpha controls how much of the backdrop *cell's background* shows through the
        // newly painted foreground (the source's glyph fully replaces the prior glyph).
        // Seed the backdrop bg with the color we expect to see preserved.
        buf.Set(0, 0, "x", Style.Default.WithBackground(Color.FromRgb(200, 100, 50)));

        // Fully transparent source foreground — backdrop bg shows through entirely.
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgba(0, 0, 0, 0)));

        Assert.Equal(Color.FromRgb(200, 100, 50), buf[0, 0].Style.Foreground);
        // The grapheme is still the source's — alpha controls color compositing only.
        Assert.Equal("y", buf[0, 0].Grapheme);
    }

    [Fact]
    public void Set_AlphaFullSource_ReplacesBackdropColor()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(200, 100, 50)));
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgba(0, 0, 0, 255)));

        Assert.Equal(Color.FromRgb(0, 0, 0), buf[0, 0].Style.Foreground);
    }

    [Fact]
    public void Set_HalfAlphaSource_LinearlyBlendsWithBackdrop()
    {
        var buf = new CellBuffer(3, 1);
        // Backdrop bg = black; the source's foreground alpha-blends against it.
        buf.Set(0, 0, "x", Style.Default.WithBackground(Color.FromRgb(0, 0, 0)));

        // Half-alpha white over black — expect mid-gray (~128).
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgba(255, 255, 255, 128)));

        var fg = buf[0, 0].Style.Foreground;
        Assert.InRange(fg.Red, (byte) 126, (byte) 130);
        Assert.InRange(fg.Green, (byte) 126, (byte) 130);
        Assert.InRange(fg.Blue, (byte) 126, (byte) 130);
    }

    [Fact]
    public void Set_StoredCellsAreAlwaysOpaque()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(50, 50, 50)));
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgba(200, 200, 200, 128)));

        // Stored cell's alpha is normalized to 255 — alpha is consumed at composite time.
        Assert.Equal((byte) 255, buf[0, 0].Style.Foreground.Alpha);
    }

    [Fact]
    public void Set_AlphaCompositesUnderMultiplyMode()
    {
        var buf = new CellBuffer(3, 1);
        // Backdrop bg = light-gray — the alpha-blended source foreground composes against the
        // underlying cell's *background*, since the source's glyph replaces the prior glyph.
        buf.Set(0, 0, "x", Style.Default.WithBackground(Color.FromRgb(200, 200, 200)));

        // Multiply with mid-gray gives (200 * 128/255) ≈ 100.
        // Half-alpha source: result = blended * 0.5 + backdrop * 0.5 = 100 * 0.5 + 200 * 0.5 ≈ 150.
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgba(128, 128, 128, 128)));

        var fg = buf[0, 0].Style.Foreground;
        Assert.InRange(fg.Red, (byte) 145, (byte) 155);
    }

    [Fact]
    public void Set_AlphaIgnoredWhenBackdropIsDefault()
    {
        var buf = new CellBuffer(3, 1);
        // Backdrop cell at (0,0) is default-constructed → foreground is Color.Default.

        // Half-alpha source — we don't know default's RGB so the source wins (with alpha
        // normalized to 255 on the stored cell).
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromRgba(100, 100, 100, 128)));

        Assert.Equal(Color.FromRgb(100, 100, 100), buf[0, 0].Style.Foreground);
    }

    [Fact]
    public void Set_AlphaIgnoredWhenSourceIsPalette()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(50, 50, 50)));

        // Palette source with explicit alpha — short-circuits to source (palette index).
        buf.Set(0, 0, "y", Style.Default.WithForeground(Color.FromPalette(3).WithAlpha(128)));

        Assert.Equal(ColorKind.Palette, buf[0, 0].Style.Foreground.Kind);
        Assert.Equal((byte) 3, buf[0, 0].Style.Foreground.PaletteIndex);
    }

    // ---- Clear and indexer don't blend ----

    [Fact]
    public void Clear_IgnoresActiveBlendingMode()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(255, 0, 0)));
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Clear();

        Assert.Equal(default, buf[0, 0]);
    }

    [Fact]
    public void Indexer_IgnoresActiveBlendingMode()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(128, 128, 128)));
        buf.PushBlendingMode(BlendingModes.Multiply);

        var explicitCell = new Cell("z", CellKind.Single, Style.Default.WithForeground(Color.FromRgb(255, 255, 255)));
        buf[0, 0] = explicitCell;

        Assert.Equal(explicitCell, buf[0, 0]);
    }
}