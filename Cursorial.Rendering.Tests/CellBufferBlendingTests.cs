using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

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
        var style = CellStyle.Default.WithForeground(Color.FromRgb(255, 0, 0));
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
        buf.Set(0, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(128, 128, 128)));

        // Push Multiply, set with half-gray foreground. The result should be ~1/4
        // (half-gray × half-gray = quarter-gray).
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgb(128, 128, 128)));

        Assert.Equal(Color.FromRgb(64, 64, 64), buf[0, 0].Style.Foreground);
        // The grapheme of the new cell should be the source's, not the previous one.
        Assert.Equal("y", buf[0, 0].Grapheme);
    }

    [Fact]
    public void Set_BlendsBackground()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(200, 200, 200)));

        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(128, 128, 128)));

        // 128 * 200 / 255 ≈ 100.
        Assert.Equal((byte) 100, buf[0, 0].Style.Background.Red);
    }

    [Fact]
    public void Set_NonColorStyleFieldsAreSourceVerbatim()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithAttributes(TextAttributes.Italic));

        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, "y", CellStyle.Default.WithAttributes(TextAttributes.Bold));

        // Source attributes win — no blending of non-color style fields.
        Assert.Equal(TextAttributes.Bold, buf[0, 0].Style.Attributes);
    }

    [Fact]
    public void Set_AfterPop_StopsBlending()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithForeground(Color.FromRgb(128, 128, 128)));

        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.PopBlendingMode();

        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgb(255, 255, 255)));
        Assert.Equal(Color.FromRgb(255, 255, 255), buf[0, 0].Style.Foreground);
    }

    // ---- Set: a Mode carried on the delta ----

    [Fact]
    public void Set_DeltaCarryingMode_ScopesItAndBlendsForegroundOverBackground()
    {
        var buf = new CellBuffer(3, 1);
        // The delta analog of Set_MultiplyMode_BlendsAgainstPreviousCell: the mode rides the DELTA
        // rather than a manual PushBlendingMode, and the overload scopes it to this one write.
        buf.Set(0, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(128, 128, 128)));

        var delta = PartialStyle.WithForeground(Color.FromRgb(128, 128, 128)) with { Mode = BlendingModes.Multiply };
        buf.Set(0, 0, "y", delta);

        // half-gray × half-gray = quarter-gray: the ink met the cell's BACKGROUND. The old ApplyTo
        // route would have combined it with the cell's Default foreground (inert) and kept half-gray.
        Assert.Equal(Color.FromRgb(64, 64, 64), buf[0, 0].Style.Foreground);
        Assert.Equal("y", buf[0, 0].Grapheme);

        // Scoped: the mode was pushed for this write and popped, leaving the stack clean.
        Assert.Same(BlendingModes.Default, buf.CurrentBlendingMode);
    }

    [Fact]
    public void Set_DeltaCarryingMode_EqualsAScopedPushOfTheModelessDelta()
    {
        var seed = CellStyle.Default.WithForeground(Color.FromRgb(200, 50, 100))
                                    .WithBackground(Color.FromRgb(0, 0, 255));
        var ink = PartialStyle.WithForeground(Color.FromRgb(255, 128, 64));

        var byDelta = new CellBuffer(3, 1);
        byDelta.Set(0, 0, "z", seed);
        byDelta.Set(0, 0, "z", ink with { Mode = BlendingModes.Screen });

        var byPush = new CellBuffer(3, 1);
        byPush.Set(0, 0, "z", seed);
        byPush.PushBlendingMode(BlendingModes.Screen);
        byPush.Set(0, 0, "z", ink);
        byPush.PopBlendingMode();

        // The contract: a Mode on the delta IS a scoped ambient push of that mode around the mode-less delta.
        Assert.Equal(byPush[0, 0], byDelta[0, 0]);
    }

    [Fact]
    public void Set_DeltaCarryingMode_BlendsAStatedBackgroundToo()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(200, 200, 200)));

        var delta = PartialStyle.WithBackground(Color.FromRgb(128, 128, 128)) with { Mode = BlendingModes.Multiply };
        buf.Set(0, 0, " ", delta);

        // 128 × 200 / 255 ≈ 100: the stated BACKGROUND blends against the cell's, not just the foreground —
        // BlendOver's background arm composites it with the scoped mode, exactly as a manual PushBlendingMode
        // would (see Set_BlendsBackground). An absent background still short-circuits to "leave it".
        Assert.Equal((byte) 100, buf[0, 0].Style.Background.Red);
    }

    [Fact]
    public void Set_DeltaCarryingMode_TranslucentBackground_AlphaCompositesOverAnRgbCellBackground()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 255)));   // opaque blue

        // A TRANSLUCENT background over an RGB-coloured cell background: the flat fold keeps it verbatim, and
        // BlendOver alpha-composites it against the cell (premultiplied). The stored result is opaque — alpha
        // is consumed at composite time — but the blend is still visible in the RGB it lands on.
        var delta = PartialStyle.WithBackground(Color.FromRgba(255, 0, 0, 128)) with { Mode = BlendingModes.SourceOver };
        buf.Set(0, 0, " ", delta);

        // red@50% over blue = (128, 0, 127), fully opaque.
        Assert.Equal(Color.FromRgb(128, 0, 127), buf[0, 0].Style.Background);
    }

    // ---- Fill: blending behavior ----

    [Fact]
    public void Fill_NoActiveMode_FastPathReplacesEveryCell()
    {
        var buf = new CellBuffer(3, 2);
        var fill = new Cell("x", CellKind.Single, CellStyle.Default.WithBackground(Color.FromRgb(100, 100, 100)));

        buf.Fill(fill);

        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 3; c++)
                Assert.Equal(fill, buf[c, r]);
        }
    }

    [Fact]
    public void Fill_MultiplyMode_BlendsAgainstEachCellIndividually()
    {
        var buf = new CellBuffer(3, 1);
        // Different existing backgrounds in each cell.
        buf.Set(0, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(255, 0, 0)));
        buf.Set(1, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(0, 255, 0)));
        buf.Set(2, 0, " ", CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 255)));

        buf.PushBlendingMode(BlendingModes.Multiply);
        // Fill with half-gray.
        buf.Fill(new Cell(" ", CellKind.Single, CellStyle.Default.WithBackground(Color.FromRgb(128, 128, 128))));

        // Each cell multiplied: half-gray * red = (128, 0, 0), etc.
        Assert.Equal((byte) 128, buf[0, 0].Style.Background.Red);
        Assert.Equal((byte) 0, buf[0, 0].Style.Background.Green);
        Assert.Equal((byte) 128, buf[1, 0].Style.Background.Green);
        Assert.Equal((byte) 128, buf[2, 0].Style.Background.Blue);
    }

    // ---- Alpha compositing ----

    [Fact]
    public void Set_AlphaZeroSource_LeavesBackdropColorUnchanged()
    {
        var buf = new CellBuffer(3, 1);
        // Alpha controls how much of the backdrop *cell's background* shows through the
        // newly painted foreground (the source's glyph fully replaces the prior glyph).
        // Seed the backdrop bg with the color we expect to see preserved.
        buf.Set(0, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(200, 100, 50)));

        // Fully transparent source foreground — backdrop bg shows through entirely.
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgba(0, 0, 0, 0)));

        Assert.Equal(Color.FromRgb(200, 100, 50), buf[0, 0].Style.Foreground);
        // The grapheme is still the source's — alpha controls color compositing only.
        Assert.Equal("y", buf[0, 0].Grapheme);
    }

    [Fact]
    public void Set_AlphaFullSource_ReplacesBackdropColor()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithForeground(Color.FromRgb(200, 100, 50)));
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgba(0, 0, 0, 255)));

        Assert.Equal(Color.FromRgb(0, 0, 0), buf[0, 0].Style.Foreground);
    }

    [Fact]
    public void Set_HalfAlphaSource_LinearlyBlendsWithBackdrop()
    {
        var buf = new CellBuffer(3, 1);
        // Backdrop bg = black; the source's foreground alpha-blends against it.
        buf.Set(0, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(0, 0, 0)));

        // Half-alpha white over black — expect mid-gray (~128).
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgba(255, 255, 255, 128)));

        var fg = buf[0, 0].Style.Foreground;
        Assert.InRange(fg.Red, (byte) 126, (byte) 130);
        Assert.InRange(fg.Green, (byte) 126, (byte) 130);
        Assert.InRange(fg.Blue, (byte) 126, (byte) 130);
    }

    [Fact]
    public void Set_StoredCellsAreAlwaysOpaque()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithForeground(Color.FromRgb(50, 50, 50)));
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgba(200, 200, 200, 128)));

        // Stored cell's alpha is normalized to 255 — alpha is consumed at composite time.
        Assert.Equal((byte) 255, buf[0, 0].Style.Foreground.Alpha);
    }

    [Fact]
    public void Set_AlphaCompositesUnderMultiplyMode()
    {
        var buf = new CellBuffer(3, 1);
        // Backdrop bg = light-gray — the alpha-blended source foreground composes against the
        // underlying cell's *background*, since the source's glyph replaces the prior glyph.
        buf.Set(0, 0, "x", CellStyle.Default.WithBackground(Color.FromRgb(200, 200, 200)));

        // Multiply with mid-gray gives (200 * 128/255) ≈ 100.
        // Half-alpha source: result = blended * 0.5 + backdrop * 0.5 = 100 * 0.5 + 200 * 0.5 ≈ 150.
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgba(128, 128, 128, 128)));

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
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromRgba(100, 100, 100, 128)));

        Assert.Equal(Color.FromRgb(100, 100, 100), buf[0, 0].Style.Foreground);
    }

    [Fact]
    public void Set_AlphaIgnoredWhenSourceIsPalette()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithForeground(Color.FromRgb(50, 50, 50)));

        // Palette source with explicit alpha — short-circuits to source (palette index).
        buf.Set(0, 0, "y", CellStyle.Default.WithForeground(Color.FromPalette(3).WithAlpha(128)));

        Assert.Equal(ColorKind.Palette, buf[0, 0].Style.Foreground.Kind);
        Assert.Equal((byte) 3, buf[0, 0].Style.Foreground.PaletteIndex);
    }

    // ---- Clear and indexer don't blend ----

    [Fact]
    public void Clear_IgnoresActiveBlendingMode()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithForeground(Color.FromRgb(255, 0, 0)));
        buf.PushBlendingMode(BlendingModes.Multiply);
        buf.Clear();

        Assert.Equal(default, buf[0, 0]);
    }

    [Fact]
    public void Indexer_IgnoresActiveBlendingMode()
    {
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", CellStyle.Default.WithForeground(Color.FromRgb(128, 128, 128)));
        buf.PushBlendingMode(BlendingModes.Multiply);

        var explicitCell = new Cell("z", CellKind.Single, CellStyle.Default.WithForeground(Color.FromRgb(255, 255, 255)));
        buf[0, 0] = explicitCell;

        Assert.Equal(explicitCell, buf[0, 0]);
    }

    // ---- Set and Fill apply ONE blending rule ----------------------------------------------

    private const string Glyph = "x";

    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Green = Color.FromRgb(0, 255, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Grey = Color.FromRgb(128, 128, 128);
    private static readonly Color TranslucentRed = Color.FromRgba(255, 0, 0, 128);

    /// <summary>
    /// Resolve <paramref name="source"/> over <paramref name="backdrop"/> twice — once through
    /// <see cref="CellBuffer.Set(int, int, ReadOnlySpan{char}, in CellStyle)"/>, once through <see cref="CellBuffer.Fill(in Cell)"/> — and hand back
    /// both stored cells. Identical buffer size, identical seeded backdrop (written through the indexer,
    /// which does not blend), identical active mode, identical grapheme and kind: the ONLY thing that can
    /// make the two differ is the blending rule each path applies. A non-blank grapheme keeps
    /// <see cref="CellBuffer.Set(int, int, ReadOnlySpan{char}, in CellStyle)"/> off its blank-rescue path, so both paths reduce to the blend alone.
    /// </summary>
    private static (Cell ViaSet, Cell ViaFill) BlendBothWays(CellStyle source, CellStyle backdrop, IBlendingMode mode)
    {
        var set = new CellBuffer(1, 1);
        set[0, 0] = new Cell(Glyph, CellKind.Single, backdrop);
        set.PushBlendingMode(mode);
        set.Set(0, 0, Glyph, source);

        var fill = new CellBuffer(1, 1);
        fill[0, 0] = new Cell(Glyph, CellKind.Single, backdrop);
        fill.PushBlendingMode(mode);
        fill.Fill(new Cell(Glyph, CellKind.Single, source));

        return (set[0, 0], fill[0, 0]);
    }

    /// <summary>
    /// THE invariant: the same source over the same backdrop under the same blending mode must produce
    /// the same cell through <see cref="CellBuffer.Set(int, int, ReadOnlySpan{char}, in CellStyle)"/> as through <see cref="CellBuffer.Fill(in Cell)"/>.
    /// One rule — <see cref="CellStyle.BlendOver"/> — encoded once. The two paths formerly held private
    /// copies of it that drifted apart on the underline colour; every channel is asserted here, not just
    /// the one that drifted, so a copy re-introduced anywhere in the style is caught.
    /// </summary>
    [Fact]
    public void SetAndFill_ResolveTheSameSourceOverTheSameBackdropIdentically()
    {
        CellStyle[] backdrops =
        [
            CellStyle.Default,
            CellStyle.Default.WithBackground(Blue).WithUnderlineColor(Green),
            CellStyle.Default.WithBackground(Grey).WithUnderlineColor(Red)
                             .WithAttributes(TextAttributes.Underline),
            CellStyle.Transparent
        ];

        CellStyle[] sources =
        [
            CellStyle.Default.WithForeground(Grey),
            CellStyle.Default.WithForeground(Grey).WithBackground(Grey),
            CellStyle.Default.WithUnderlineColor(TranslucentRed).WithUnderlineStyle(UnderlineStyle.Curly),
            CellStyle.Default.WithAttributes(TextAttributes.Underline),   // underlined, colour left Default
            CellStyle.Default.WithForeground(Red).WithBackground(Green).WithUnderlineColor(Blue)
                             .WithAttributes(TextAttributes.Underline | TextAttributes.Bold),
            CellStyle.Transparent
        ];

        IBlendingMode[] modes =
        [
            BlendingModes.Multiply, BlendingModes.Screen, BlendingModes.Lighten, BlendingModes.Plus
        ];

        foreach (var mode in modes)
        {
            foreach (var backdrop in backdrops)
            {
                foreach (var source in sources)
                {
                    var (viaSet, viaFill) = BlendBothWays(source, backdrop, mode);
                    string where = $"mode={mode.GetType().Name}, source={source}, backdrop={backdrop}";

                    Assert.True(viaSet.Style.Foreground == viaFill.Style.Foreground,
                                $"Foreground disagrees: Set={viaSet.Style.Foreground}, Fill={viaFill.Style.Foreground} — {where}");
                    Assert.True(viaSet.Style.Background == viaFill.Style.Background,
                                $"Background disagrees: Set={viaSet.Style.Background}, Fill={viaFill.Style.Background} — {where}");
                    Assert.True(viaSet.Style.UnderlineColor == viaFill.Style.UnderlineColor,
                                $"UnderlineColor disagrees: Set={viaSet.Style.UnderlineColor}, Fill={viaFill.Style.UnderlineColor} — {where}");
                    Assert.True(viaSet.Style.Attributes == viaFill.Style.Attributes,
                                $"Attributes disagree: Set={viaSet.Style.Attributes}, Fill={viaFill.Style.Attributes} — {where}");
                    Assert.True(viaSet.Style.UnderlineStyle == viaFill.Style.UnderlineStyle,
                                $"UnderlineStyle disagrees: Set={viaSet.Style.UnderlineStyle}, Fill={viaFill.Style.UnderlineStyle} — {where}");
                    Assert.True(viaSet.Style.Hyperlink == viaFill.Style.Hyperlink,
                                $"Hyperlink disagrees: Set={viaSet.Style.Hyperlink}, Fill={viaFill.Style.Hyperlink} — {where}");
                    Assert.True(viaSet == viaFill, $"cells disagree: Set={viaSet}, Fill={viaFill} — {where}");
                }
            }
        }
    }

    /// <summary>
    /// The guard <see cref="CellStyle.BlendOver"/> puts on the underline colour. A source that names no
    /// underline colour (<see cref="Color.Default"/>) AND carries no <see cref="TextAttributes.Underline"/>
    /// has no underline opinion at all, so the backdrop's underline colour survives untouched. Set the
    /// attribute and the source now HAS an underline, so its (defaulted) colour is what resolves and the
    /// backdrop's is dropped. Those are different branches; <see cref="CellBuffer.Fill(in Cell)"/>'s private
    /// copy of the rule had no guard and took neither.
    /// </summary>
    [Fact]
    public void Fill_UnderlineColourGuard_SeparatesNoUnderlineFromDefaultColouredUnderline()
    {
        var backdrop = CellStyle.Default.WithBackground(Blue).WithUnderlineColor(Green);

        var bare = CellStyle.Default.WithForeground(Grey);
        var underlined = CellStyle.Default.WithForeground(Grey).WithAttributes(TextAttributes.Underline);

        var (setBare, fillBare) = BlendBothWays(bare, backdrop, BlendingModes.Multiply);
        var (setUnderlined, fillUnderlined) = BlendBothWays(underlined, backdrop, BlendingModes.Multiply);

        // No underline anywhere in the source: nothing of its own to resolve, so the backdrop's colour is kept.
        Assert.Equal(Green, setBare.Style.UnderlineColor);
        Assert.Equal(Green, fillBare.Style.UnderlineColor);

        // Underlined with a Default colour: the source DOES have an underline, so its Default resolves
        // (Composite returns a non-RGB source verbatim) and the backdrop's colour is gone.
        Assert.Equal(Color.Default, setUnderlined.Style.UnderlineColor);
        Assert.Equal(Color.Default, fillUnderlined.Style.UnderlineColor);
    }

    /// <summary>
    /// What the underline colour composites AGAINST. Cell compositing resolves a translucent colour against
    /// what is physically behind the cell, and on a terminal cell that is the backdrop's BACKGROUND — an
    /// underline colour sits in front of nothing. <see cref="CellBuffer.Fill(in Cell)"/>'s private copy of the
    /// rule composited against the backdrop's UNDERLINE colour, which lands somewhere else entirely whenever
    /// the two differ and the source's underline colour is translucent enough for the target to matter.
    /// </summary>
    [Fact]
    public void Fill_TranslucentUnderlineColour_CompositesAgainstTheBackdropBackground()
    {
        var backdrop = CellStyle.Default.WithBackground(Blue).WithUnderlineColor(Green);
        var source = CellStyle.Default.WithUnderlineColor(TranslucentRed)
                                      .WithUnderlineStyle(UnderlineStyle.Curly)
                                      .WithAttributes(TextAttributes.Underline);

        var (viaSet, viaFill) = BlendBothWays(source, backdrop, BlendingModes.Multiply);

        // Multiply((255,0,0) × (0,0,255)) = (0,0,0), then half-alpha over the blue backdrop → (0,0,127).
        var againstBackground = Color.FromRgb(0, 0, 127);
        // Had it composited against the backdrop's underline colour (green) it would land here instead.
        var againstUnderlineColour = Color.FromRgb(0, 127, 0);

        Assert.Equal(againstBackground, viaSet.Style.UnderlineColor);
        Assert.Equal(againstBackground, viaFill.Style.UnderlineColor);
        Assert.NotEqual(againstUnderlineColour, viaFill.Style.UnderlineColor);
    }
}