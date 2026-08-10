using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// The acceptance property for the delta write: for every (cell, delta) pair,
/// <c>Set(c, r, g, delta)</c> is BIT-IDENTICAL to <c>Set(c, r, g, Over(buffer, c, r, delta))</c>,
/// where <c>Over</c> is the fold the four glyph faces used to run at their own call sites before it
/// moved inside the write.
/// </summary>
/// <remarks>
/// <para>
/// The reference fold is written out HERE rather than called from production code. That is the whole
/// point: this file characterises the composite the delta overload replaced, so a later phase that
/// changes the fold has to change this test deliberately instead of having it agree with itself. (A
/// test that called <see cref="CellBuffer.FoldOntoCell"/> would pass for any fold whatsoever.)
/// </para>
/// <para>
/// "Bit-identical" is taken literally: every cell of the whole grid is compared, not just the one
/// written, because the write reaches its neighbours (wide-pair orphan repair) and the return value
/// is compared too.
/// </para>
/// </remarks>
public class CellBufferDeltaWriteTests
{
    // ---- the retired composite, restated ------------------------------------------------

    // GlyphPaint.Over(in CellBufferView, int, int, in PartialStyle), verbatim: fold onto the cell's
    // own style with the BACKGROUND spelled Color.Default, which is Set's own word for "leave the
    // background alone".
    private static CellStyle Over(CellBuffer buffer, int column, int row, in PartialStyle style)
        => style.ApplyTo(buffer[column, row].Style with { Background = Color.Default });

    private static CellStyle Over(in CellBufferView view, int column, int row, in PartialStyle style)
        => style.ApplyTo(view.Read(column, row).Style with { Background = Color.Default });

    // ---- the matrix ---------------------------------------------------------------------

    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Green = Color.FromRgb(0, 255, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Grey = Color.FromRgb(128, 128, 128);
    private static readonly Color TranslucentRed = Color.FromRgba(255, 0, 0, 128);

    private const int TargetColumn = 2;
    private const int TargetRow = 1;

    /// <summary>
    /// The states the destination cell can be in, each a name plus the seeding that produces it.
    /// Seeded through the public write paths so the buffer is in a state it could really reach —
    /// including the two half-a-wide-pair states, which is where the write touches its neighbours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cell holding a NON-OPAQUE foreground or underline colour is the state
    /// <see cref="Set_WithIdentityDelta_StillBlendsTheChannelsItDidNotState"/> shows divergence 1 in
    /// under the DEFAULT blending mode, and the original matrix reached no such state — every seed
    /// went through <c>Set</c> with an opaque colour and an RGB-or-default backdrop, which is
    /// <c>Color.Composite</c>'s normalize-to-alpha-255 path. Three seeds below reach it, by the two
    /// routes production reaches it by.
    /// </para>
    /// <para>
    /// Two seed through the RAW INDEXER, which stores the cell verbatim —
    /// <c>DrawingContext.FillRectangle</c> takes that path deliberately (its remarks: so "a
    /// translucent sampled colour is stored verbatim"). The third gets there through <c>Set</c>
    /// alone: on a surface whose blank is <see cref="CellStyle.Transparent"/> — the compositing
    /// scratch buffer this file already runs every case over — <c>Color.Composite</c> returns the
    /// BACKDROP verbatim for a transparent source, so a transparent foreground painted over a
    /// translucent background lands as that translucent background. The alpha survives because
    /// nothing composited it away.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Name, Action<CellBuffer> Seed)> CellStates()
    {
        yield return ("blank", _ => { });

        yield return ("opaque ink",
                      b => b.Set(TargetColumn, TargetRow, "x",
                                 CellStyle.Default.WithForeground(Red).WithBackground(Blue)
                                          .WithAttributes(TextAttributes.Bold | TextAttributes.Inverse)));

        yield return ("transparent",
                      b => b.Set(TargetColumn, TargetRow, "x", CellStyle.Transparent));

        yield return ("translucent background",
                      b => b.Set(TargetColumn, TargetRow, "x",
                                 CellStyle.Default.WithForeground(Green).WithBackground(TranslucentRed)));

        // Raw indexer, not Set: see the remarks above. A translucent FOREGROUND over an opaque
        // background is the state in which a declined foreground moves under SourceOver.
        yield return ("translucent foreground (raw)",
                      b => b[TargetColumn, TargetRow] =
                               new Cell("x", CellKind.Single,
                                        CellStyle.Default.WithForeground(TranslucentRed).WithBackground(Blue)));

        // ...and the same ink with a translucent underline colour, so the guarded underline arm sees
        // a non-opaque value too.
        yield return ("translucent underline colour (raw)",
                      b => b[TargetColumn, TargetRow] =
                               new Cell("x", CellKind.Single,
                                        CellStyle.Default.WithForeground(TranslucentRed)
                                                 .WithUnderlineColor(TranslucentRed)
                                                 .WithAttributes(TextAttributes.Underline)));

        // The same non-opaque foreground WITHOUT the raw indexer — two ordinary Set calls, which land
        // it only on the transparent-blank half of the SurfaceBlanks loop (see the remarks above). The
        // state divergence 1 lives in is not confined to the indexer path.
        yield return ("translucent foreground via Set",
                      b =>
                      {
                          b.Set(TargetColumn, TargetRow, "x",
                                CellStyle.Default.WithBackground(TranslucentRed));
                          b.Set(TargetColumn, TargetRow, "x", CellStyle.Transparent);
                      });

        yield return ("underlined, coloured underline",
                      b => b.Set(TargetColumn, TargetRow, "x",
                                 CellStyle.Default.WithUnderlineColor(Green)
                                          .WithUnderlineStyle(UnderlineStyle.Curly)
                                          .WithAttributes(TextAttributes.Underline)));

        // The guarded branch in BlendOver: an underline COLOUR with the flag off is a different arm
        // from the same colour with the flag on.
        yield return ("coloured underline, flag off",
                      b => b.Set(TargetColumn, TargetRow, "x",
                                 CellStyle.Default.WithUnderlineColor(Green)
                                          .WithUnderlineStyle(UnderlineStyle.Curly)));

        yield return ("hyperlinked",
                      b => b.Set(TargetColumn, TargetRow, "x",
                                 CellStyle.Default.WithHyperlink("https://example.invalid", "id")));

        // The three grapheme shapes the whitespace-over-non-whitespace branch discriminates on.
        yield return ("whitespace glyph",
                      b => b.Set(TargetColumn, TargetRow, " ",
                                 CellStyle.Default.WithBackground(Blue)));

        yield return ("durable empty",
                      b => b.Set(TargetColumn, TargetRow, CellBuffer.DurableEmptyGrapheme,
                                 CellStyle.Default.WithBackground(Blue)));

        yield return ("null glyph, styled",
                      b => b.Set(TargetColumn, TargetRow, null,
                                 CellStyle.Default.WithForeground(Red).WithBackground(Green)));

        // The target IS the leading half of a wide pair — a single-width write orphans its
        // continuation at TargetColumn + 1.
        yield return ("wide left half",
                      b => b.Set(TargetColumn, TargetRow, "中",
                                 CellStyle.Default.WithForeground(Red).WithBackground(Blue)));

        // The target IS a continuation — the write orphans the leading half at TargetColumn - 1.
        yield return ("wide continuation",
                      b => b.Set(TargetColumn - 1, TargetRow, "中",
                                 CellStyle.Default.WithForeground(Green).WithBackground(Grey)));
    }

    /// <summary>Every delta shape the type can express, plus the ones that used to be unsayable.</summary>
    private static IEnumerable<(string Name, PartialStyle Delta)> Deltas()
    {
        yield return ("identity", default);

        // Colour channels: absent, stated-opaque, stated-translucent, stated-TRANSPARENT and
        // stated-DEFAULT. The last is the channel the sentinel used to swallow.
        yield return ("foreground", PartialStyle.WithForeground(Red));
        yield return ("foreground translucent", PartialStyle.WithForeground(TranslucentRed));
        yield return ("foreground transparent", PartialStyle.WithForeground(Color.Transparent));
        yield return ("foreground default", PartialStyle.WithForeground(Color.Default));

        yield return ("background", PartialStyle.WithBackground(Blue));
        yield return ("background translucent", PartialStyle.WithBackground(TranslucentRed));
        yield return ("background transparent", PartialStyle.WithBackground(Color.Transparent));
        yield return ("background default", PartialStyle.WithBackground(Color.Default));

        yield return ("underline colour", PartialStyle.WithUnderlineColor(Green));
        yield return ("underline colour default", PartialStyle.WithUnderlineColor(Color.Default));

        yield return ("hyperlink", PartialStyle.WithHyperlink(new Hyperlink("https://example.test", "n")));
        yield return ("hyperlink cleared", PartialStyle.WithHyperlink(Hyperlink.None));

        // Every attribute mask shape: applied, removed, toggled, and the four decomposed axes.
        yield return ("applied", PartialStyle.WithApplied(TextAttributes.Inverse | TextAttributes.Strikethrough));
        yield return ("removed", PartialStyle.WithRemoved(TextAttributes.Inverse));
        yield return ("toggled", PartialStyle.WithToggled(TextAttributes.Inverse));
        yield return ("weight bold", PartialStyle.Weighted(TextWeight.Bold));
        yield return ("weight faint", PartialStyle.Weighted(TextWeight.Faint));
        yield return ("weight normal", PartialStyle.Weighted(TextWeight.Normal));
        yield return ("posture italic", PartialStyle.Postured(TextStyle.Italic));
        yield return ("posture normal", PartialStyle.Postured(TextStyle.Normal));
        yield return ("underline on", PartialStyle.WithUnderline());
        yield return ("underline curly", PartialStyle.WithUnderline(UnderlineStyle.Curly, Blue));
        yield return ("underline off", PartialStyle.WithoutUnderline());
        yield return ("shape only", new PartialStyle { UnderlineShape = UnderlineStyle.Double });

        // Blending rides on the delta and is applied by ApplyTo BEFORE the buffer's own stack blends
        // the folded style — two composites, in that order.
        yield return ("multiply carrier", PartialStyle.WithBlending(BlendingModes.Multiply));
        yield return ("shadow", PartialStyle.DefaultShadow);

        // Whole styles as deltas — the adapter the legacy call sites use.
        yield return ("from opaque style",
                      PartialStyle.From(CellStyle.Default.WithForeground(Red).WithBackground(Green)
                                                 .WithAttributes(TextAttributes.Bold)));
        yield return ("from transparent style", PartialStyle.From(CellStyle.Transparent));
        yield return ("from ink", PartialStyle.FromInk(CellStyle.Default.WithForeground(Blue)));

        // Several channels at once, mode included.
        yield return ("everything",
                      PartialStyle.WithForeground(Grey)
                                  .Then(PartialStyle.WithBackground(TranslucentRed))
                                  .Then(PartialStyle.WithUnderline(UnderlineStyle.Dotted, Green))
                                  .Then(PartialStyle.Weighted(TextWeight.Bold))
                                  .Then(PartialStyle.WithToggled(TextAttributes.Inverse))
                                  .Then(PartialStyle.WithBlending(BlendingModes.Screen)));
    }

    private static readonly string?[] Graphemes =
        [null, "", " ", "A", "中", CellBuffer.DurableEmptyGrapheme];

    private static readonly IBlendingMode?[] Modes =
        [null, BlendingModes.Multiply, BlendingModes.Screen];

    // Both surface blanks that matter: the terminal's default, and the transparent blank a
    // compositing scratch buffer (ChartPresenter) is built on.
    private static readonly CellStyle[] SurfaceBlanks = [CellStyle.Default, CellStyle.Transparent];

    private static CellBuffer Seeded(in CellStyle blank, Action<CellBuffer> seed, IBlendingMode? mode)
    {
        var buffer = new CellBuffer(6, 3, defaultStyle: blank);

        // Neighbours on the target's row, so a write that repairs a pair has something to repair
        // INTO and the comparison covers it.
        buffer.Set(0, TargetRow, "L", CellStyle.Default.WithForeground(Green).WithBackground(Red));
        buffer.Set(TargetColumn + 2, TargetRow, "R", CellStyle.Default.WithBackground(Grey));

        seed(buffer);

        if (mode is not null) buffer.PushBlendingMode(mode);
        return buffer;
    }

    private static void AssertSameGrid(CellBuffer expected, CellBuffer actual, string where)
    {
        for (int r = 0; r < expected.Rows; r++)
        for (int c = 0; c < expected.Columns; c++)
        {
            Assert.True(expected[c, r] == actual[c, r],
                        $"cell ({c},{r}) disagrees: composite={expected[c, r]}, delta={actual[c, r]} — {where}");
        }
    }

    // ---- the property -------------------------------------------------------------------

    [Fact]
    public void Set_WithDelta_IsIdenticalToOverThenSet()
    {
        int cases = 0;

        foreach (var blank in SurfaceBlanks)
        foreach (var mode in Modes)
        foreach (var (cellName, seed) in CellStates())
        foreach (var grapheme in Graphemes)
        foreach (var (deltaName, delta) in Deltas())
        {
            var viaComposite = Seeded(blank, seed, mode);
            var viaDelta = Seeded(blank, seed, mode);

            int expected = viaComposite.Set(TargetColumn, TargetRow, grapheme,
                                            Over(viaComposite, TargetColumn, TargetRow, delta));
            int actual = viaDelta.Set(TargetColumn, TargetRow, grapheme, delta);

            string where = $"blank={blank.Background}, mode={mode?.GetType().Name ?? "none"}, " +
                           $"cell={cellName}, grapheme={grapheme ?? "<null>"}, delta={deltaName}";

            Assert.True(expected == actual, $"return value disagrees: {expected} vs {actual} — {where}");
            AssertSameGrid(viaComposite, viaDelta, where);
            cases++;
        }

        // The matrix is the test; a silently-empty enumerable would pass vacuously.
        Assert.True(cases > 4000, $"matrix collapsed to {cases} cases");
    }

    [Fact]
    public void ViewSet_WithDelta_IsIdenticalToOverThenSet()
    {
        int cases = 0;

        // A window that is offset AND re-based, so the view's own clip and the local-coordinate
        // window both participate — the delta must fold over the cell the view maps to, not the one
        // at the same numbers on the backing buffer.
        foreach (var blank in SurfaceBlanks)
        foreach (var mode in Modes)
        foreach (var (cellName, seed) in CellStates())
        foreach (var grapheme in Graphemes)
        foreach (var (deltaName, delta) in Deltas())
        {
            var backingA = Seeded(blank, seed, mode);
            var backingB = Seeded(blank, seed, mode);

            var viewA = backingA.View(1, 0, 4, 3);
            var viewB = backingB.View(1, 0, 4, 3);

            int col = TargetColumn - 1;   // the same backing cell, in view-local coordinates

            int expected = viewA.Set(col, TargetRow, grapheme, Over(viewA, col, TargetRow, delta));
            int actual = viewB.Set(col, TargetRow, grapheme, delta);

            string where = $"view, blank={blank.Background}, mode={mode?.GetType().Name ?? "none"}, " +
                           $"cell={cellName}, grapheme={grapheme ?? "<null>"}, delta={deltaName}";

            Assert.True(expected == actual, $"return value disagrees: {expected} vs {actual} — {where}");
            AssertSameGrid(backingA, backingB, where);
            cases++;
        }

        Assert.True(cases > 4000, $"matrix collapsed to {cases} cases");
    }

    // The two clip answers a view gives that the buffer cannot: a coordinate outside the window is
    // dropped, and a wide glyph whose right half would leave the window degrades to a blank single.
    [Theory]
    [InlineData(-1, 0, "A")]
    [InlineData(0, -1, "A")]
    [InlineData(4, 0, "A")]
    [InlineData(0, 3, "A")]
    [InlineData(3, 0, "中")]
    public void ViewSet_WithDelta_MatchesTheCompositeAtTheClipEdges(int column, int row, string grapheme)
    {
        foreach (var (_, delta) in Deltas())
        {
            var backingA = new CellBuffer(6, 3);
            var backingB = new CellBuffer(6, 3);
            backingA.Write(0, 0, "abcdef", CellStyle.Default.WithBackground(Blue));
            backingB.Write(0, 0, "abcdef", CellStyle.Default.WithBackground(Blue));

            var viewA = backingA.View(1, 0, 4, 3);
            var viewB = backingB.View(1, 0, 4, 3);

            int expected = viewA.Set(column, row, grapheme, Over(viewA, column, row, delta));
            int actual = viewB.Set(column, row, grapheme, delta);

            Assert.Equal(expected, actual);
            AssertSameGrid(backingA, backingB, $"clip edge ({column},{row}) '{grapheme}'");
        }
    }

    // ---- Write folds per cell, not once per run -----------------------------------------

    [Fact]
    public void Write_WithDelta_IsTheSameAsSettingEachClusterInTurn()
    {
        foreach (var (name, delta) in Deltas())
        {
            var viaSets = new CellBuffer(6, 1);
            var viaWrite = new CellBuffer(6, 1);

            foreach (var b in new[] { viaSets, viaWrite })
            {
                b.Set(0, 0, "a", CellStyle.Default.WithForeground(Red));
                b.Set(1, 0, "b", CellStyle.Default.WithForeground(Green).WithAttributes(TextAttributes.Bold));
                b.Set(2, 0, "c", CellStyle.Default.WithBackground(Blue));
            }

            viaSets.Set(0, 0, "x", delta);
            viaSets.Set(1, 0, "y", delta);
            viaSets.Set(2, 0, "z", delta);

            int written = viaWrite.Write(0, 0, "xyz", delta);

            Assert.Equal(3, written);
            AssertSameGrid(viaSets, viaWrite, $"Write vs Sets — delta={name}");
        }
    }

    /// <summary>
    /// The hoist guard. A delta that states only the BACKGROUND leaves every cell's own foreground
    /// alone — so a run over three differently-inked cells keeps three different foregrounds. Folding
    /// the delta once before the loop would paint the first cell's answer across all three, which is
    /// invisible on a uniformly-styled row and the reason this row is not uniform.
    /// </summary>
    [Fact]
    public void Write_WithDelta_FoldsOntoEachCellRatherThanOnce()
    {
        var buffer = new CellBuffer(4, 1);
        buffer.Set(0, 0, "a", CellStyle.Default.WithForeground(Red));
        buffer.Set(1, 0, "b", CellStyle.Default.WithForeground(Green));
        buffer.Set(2, 0, "c", CellStyle.Default.WithForeground(Blue));

        buffer.Write(0, 0, "xyz", PartialStyle.WithBackground(Grey));

        Assert.Equal(Red, buffer[0, 0].Style.Foreground);
        Assert.Equal(Green, buffer[1, 0].Style.Foreground);
        Assert.Equal(Blue, buffer[2, 0].Style.Foreground);
        Assert.Equal(Grey, buffer[2, 0].Style.Background);
    }

    [Fact]
    public void ViewWrite_WithDelta_FoldsOntoEachCellRatherThanOnce()
    {
        var backing = new CellBuffer(6, 1);
        backing.Set(1, 0, "a", CellStyle.Default.WithForeground(Red));
        backing.Set(2, 0, "b", CellStyle.Default.WithForeground(Green));
        backing.Set(3, 0, "c", CellStyle.Default.WithForeground(Blue));

        backing.View(1, 0, 4, 1).Write(0, 0, "xyz", PartialStyle.WithBackground(Grey));

        Assert.Equal(Red, backing[1, 0].Style.Foreground);
        Assert.Equal(Green, backing[2, 0].Style.Foreground);
        Assert.Equal(Blue, backing[3, 0].Style.Foreground);
    }

    // ---- the whitespace-over-non-whitespace branch --------------------------------------

    /// <summary>
    /// <c>CellBuffer.Set</c>'s branch that keeps the previous cell's grapheme when a blank lands on it
    /// with a NON-OPAQUE background — which is precisely the UI's <see cref="CellStyle.Transparent"/>
    /// default. It reads the INCOMING style's background, so under a delta it sees the FOLDED one, and
    /// an absent background folds to <see cref="Color.Default"/> — which is opaque, so a delta that
    /// says nothing about the background does NOT take the branch. Both arms are pinned here because
    /// the difference is one <c>IsOpaque</c> away.
    /// </summary>
    [Fact]
    public void Set_WithDelta_PreservesTheGlyphUnderneathOnlyWhenTheStatedBackgroundIsTranslucent()
    {
        // Stated + translucent → the branch fires: the glyph underneath survives.
        var translucent = new CellBuffer(3, 1);
        translucent.Set(0, 0, "A", CellStyle.Default.WithForeground(Red));
        translucent.Set(0, 0, " ", PartialStyle.WithBackground(TranslucentRed));
        Assert.Equal("A", translucent[0, 0].Grapheme);

        // Stated + transparent → also non-opaque, same arm.
        var transparent = new CellBuffer(3, 1);
        transparent.Set(0, 0, "A", CellStyle.Default.WithForeground(Red));
        transparent.Set(0, 0, " ", PartialStyle.WithBackground(Color.Transparent));
        Assert.Equal("A", transparent[0, 0].Grapheme);

        // ABSENT → folds to Color.Default, which IS opaque: the space wins and the glyph is gone.
        var absent = new CellBuffer(3, 1);
        absent.Set(0, 0, "A", CellStyle.Default.WithForeground(Red));
        absent.Set(0, 0, " ", PartialStyle.WithForeground(Blue));
        Assert.Equal(" ", absent[0, 0].Grapheme);

        // Stated as Color.Default → indistinguishable from absent, by construction. This is the
        // sentinel the delta type exists to retire, surviving one layer down in Set's own blend.
        var statedDefault = new CellBuffer(3, 1);
        statedDefault.Set(0, 0, "A", CellStyle.Default.WithForeground(Red));
        statedDefault.Set(0, 0, " ", PartialStyle.WithBackground(Color.Default));
        Assert.Equal(" ", statedDefault[0, 0].Grapheme);
    }

    // ---- the OTHER base: a clear falls through to the surface's blank --------------------

    /// <summary>
    /// <c>Clear</c> / <c>ClearCells</c> take the SURFACE'S OWN BLANK as the base, not the destination
    /// cells and not <see cref="CellStyle.Default"/>. On a transparent scratch buffer a delta that
    /// declines the background must leave it TRANSPARENT — inheriting the terminal default there
    /// punches an opaque hole through a surface built to let content past.
    /// </summary>
    [Fact]
    public void Clear_WithDelta_FallsThroughToTheSurfaceBlank_NotToCellStyleDefault()
    {
        var scratch = new CellBuffer(3, 2, defaultStyle: CellStyle.Transparent);
        scratch.Set(0, 0, "A", CellStyle.Default.WithForeground(Red).WithBackground(Blue));

        scratch.Clear(PartialStyle.WithForeground(Green));

        Assert.Equal(Green, scratch[0, 0].Style.Foreground);
        Assert.Equal(Color.Transparent, scratch[0, 0].Style.Background);
        Assert.Equal(Color.Transparent, scratch[2, 1].Style.Background);
        Assert.Null(scratch[0, 0].Grapheme);

        var terminal = new CellBuffer(3, 2, defaultStyle: CellStyle.Default.WithBackground(Grey));
        terminal.Clear(PartialStyle.WithForeground(Green));

        Assert.Equal(Grey, terminal[0, 0].Style.Background);
    }

    [Fact]
    public void ClearCells_WithDelta_FallsThroughToTheSurfaceBlank_AndLeavesTheRestAlone()
    {
        var scratch = new CellBuffer(4, 2, defaultStyle: CellStyle.Transparent);
        scratch.Write(0, 0, "abcd", CellStyle.Default.WithForeground(Red).WithBackground(Blue));

        scratch.ClearCells(new Rect(1, 0, 2, 1), PartialStyle.WithForeground(Green));

        Assert.Equal(Green, scratch[1, 0].Style.Foreground);
        Assert.Equal(Color.Transparent, scratch[1, 0].Style.Background);
        Assert.Equal(Color.Transparent, scratch[2, 0].Style.Background);

        // Outside the rect, untouched.
        Assert.Equal("a", scratch[0, 0].Grapheme);
        Assert.Equal(Blue, scratch[0, 0].Style.Background);
        Assert.Equal("d", scratch[3, 0].Grapheme);
    }

    [Fact]
    public void ViewClear_WithDelta_FallsThroughToTheBackingBuffersBlank()
    {
        var scratch = new CellBuffer(6, 2, defaultStyle: CellStyle.Transparent);
        scratch.Write(0, 0, "abcdef", CellStyle.Default.WithForeground(Red).WithBackground(Blue));

        scratch.View(1, 0, 3, 1).Clear(PartialStyle.WithForeground(Green));

        Assert.Equal(Green, scratch[1, 0].Style.Foreground);
        Assert.Equal(Color.Transparent, scratch[1, 0].Style.Background);
        Assert.Equal(Blue, scratch[0, 0].Style.Background);      // outside the view
        Assert.Equal(Blue, scratch[4, 0].Style.Background);

        var byRect = new CellBuffer(6, 2, defaultStyle: CellStyle.Transparent);
        byRect.Write(0, 0, "abcdef", CellStyle.Default.WithForeground(Red).WithBackground(Blue));
        byRect.AsView().ClearCells(new Rect(1, 0, 3, 1), PartialStyle.WithForeground(Green));

        Assert.Equal(Green, byRect[1, 0].Style.Foreground);
        Assert.Equal(Color.Transparent, byRect[1, 0].Style.Background);
        Assert.Equal(Blue, byRect[0, 0].Style.Background);
    }

    // ---- recorded divergences ------------------------------------------------------------
    //
    // Three places where "a channel the delta declines is left alone" is NOT what the composite
    // does. All three are inherited verbatim from Over ∘ Set — the delta overload changed none of
    // them — and they are pinned with their actual values so that a later phase which fixes one has
    // to say so here.

    /// <summary>
    /// DIVERGENCE 1: an identity delta is not the identity. The fold leaves the cell's own foreground
    /// and underline colour in the style being written, and <c>Set</c> then blends the WHOLE style
    /// against the cell — so channels nobody stated are composited over the cell's own BACKGROUND.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The blend moves an unstated colour in two independent circumstances, and the second is why
    /// "invisible under SourceOver" — the characterisation this test used to carry — was wrong.
    /// </para>
    /// <list type="number">
    /// <item>
    /// Under a COMPOSITING mode, always: the mode's own colour math runs on every channel it is
    /// handed. rgb(255,0,0) over rgb(128,128,128) is rgb(128,0,0) under Multiply.
    /// </item>
    /// <item>
    /// Under SOURCE-OVER — the default — whenever the cell's own foreground or underline colour is
    /// NON-OPAQUE, because alpha compositing is exactly what SourceOver does not skip. The original
    /// matrix reached no such cell (see the remarks on <see cref="CellStates"/>), which is how the
    /// wrong characterisation survived; the states added there reach it both through the raw indexer
    /// and through plain <c>Set</c> calls on a transparent-blank surface.
    /// </item>
    /// </list>
    /// <para>
    /// The BACKGROUND is left alone in every mode, and that is a separate mechanism rather than an
    /// exception to this one: absence folds to <see cref="Color.Default"/>, and <c>BlendOver</c>'s
    /// Background arm short-circuits exactly that value straight to the backdrop without compositing.
    /// Attributes, underline shape and the hyperlink are left alone too — <c>BlendOver</c> rewrites
    /// the three colour channels and nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public void Set_WithIdentityDelta_StillBlendsTheChannelsItDidNotState()
    {
        var seed = CellStyle.Default.WithForeground(Red).WithBackground(Grey)
                            .WithUnderlineColor(Green).WithAttributes(TextAttributes.Underline);

        // (1) Opaque ink: SourceOver really does return it verbatim...
        var sourceOver = new CellBuffer(2, 1);
        sourceOver.Set(0, 0, "A", seed);
        sourceOver.Set(0, 0, "B", PartialStyle.Default);

        Assert.Equal(Red, sourceOver[0, 0].Style.Foreground);
        Assert.Equal(Green, sourceOver[0, 0].Style.UnderlineColor);

        // ...and a compositing mode does not.
        var multiply = new CellBuffer(2, 1);
        multiply.Set(0, 0, "A", seed);
        multiply.PushBlendingMode(BlendingModes.Multiply);
        multiply.Set(0, 0, "B", PartialStyle.Default);

        Assert.Equal(Color.FromRgb(128, 0, 0), multiply[0, 0].Style.Foreground);
        Assert.Equal(Color.FromRgb(0, 128, 0), multiply[0, 0].Style.UnderlineColor);

        // The background is left alone even here: absence folds to Color.Default, and BlendOver's
        // Background arm short-circuits exactly that value rather than compositing it.
        Assert.Equal(Grey, multiply[0, 0].Style.Background);

        // (2) NON-OPAQUE ink, seeded through the raw indexer — the falsifying case for "invisible
        // under SourceOver". No blending mode is pushed here; this is the DEFAULT path.
        var translucent = new CellBuffer(2, 1);
        translucent[0, 0] = new Cell("A", CellKind.Single,
                                     CellStyle.Default.WithForeground(TranslucentRed)
                                              .WithBackground(Blue)
                                              .WithUnderlineColor(TranslucentRed)
                                              .WithAttributes(TextAttributes.Underline));

        translucent.Set(0, 0, "B", PartialStyle.Default);

        // rgba(255,0,0,128) linearly composited over rgb(0,0,255): the delta stated neither channel.
        Assert.Equal(Color.FromRgb(128, 0, 127), translucent[0, 0].Style.Foreground);
        Assert.Equal(Color.FromRgb(128, 0, 127), translucent[0, 0].Style.UnderlineColor);
        Assert.Equal(Blue, translucent[0, 0].Style.Background);

        // The weaker form of the same thing: with no RGB backdrop to mix against, Composite still
        // normalizes the alpha away, so the stored colour is not the one the cell carried.
        var stripped = new CellBuffer(2, 1);
        stripped[0, 0] = new Cell("A", CellKind.Single, CellStyle.Default.WithForeground(TranslucentRed));
        stripped.Set(0, 0, "B", PartialStyle.Default);

        Assert.Equal(Red, stripped[0, 0].Style.Foreground);
        Assert.NotEqual(TranslucentRed, stripped[0, 0].Style.Foreground);

        // And the channels BlendOver does not touch survive an identity delta intact, in every case
        // above — the promise holds for them unconditionally.
        Assert.Equal(TextAttributes.Underline, translucent[0, 0].Style.Attributes);
    }

    /// <summary>
    /// DIVERGENCE 2: a delta that STATES <see cref="Color.Default"/> for the background is
    /// indistinguishable from one that declines the channel — the capability the nullable channel
    /// exists to provide ("paint the terminal's default background") does not survive the write. The
    /// fold spells absence as <see cref="Color.Default"/> and <c>BlendOver</c> re-reads it as a blend
    /// decision, so the sentinel this type retires one layer up is still load-bearing one layer down.
    /// </summary>
    [Fact]
    public void Set_WithDelta_CannotStateTheTerminalDefaultBackground()
    {
        var stated = new CellBuffer(2, 1);
        stated.Set(0, 0, "A", CellStyle.Default.WithBackground(Grey));
        stated.Set(0, 0, "B", PartialStyle.WithBackground(Color.Default));

        var absent = new CellBuffer(2, 1);
        absent.Set(0, 0, "A", CellStyle.Default.WithBackground(Grey));
        absent.Set(0, 0, "B", PartialStyle.WithForeground(Red));

        Assert.Equal(Grey, stated[0, 0].Style.Background);
        Assert.Equal(Grey, absent[0, 0].Style.Background);
    }

    /// <summary>
    /// DIVERGENCE 3: <c>BlendOver</c>'s guard on the underline colour cannot fire on the delta path.
    /// The guard is "the source names an underline colour OR carries the flag" — written to protect a
    /// backdrop's underline colour from a source with no underline opinion at all. A delta that
    /// declines the channel IS such a source, but the fold has already substituted the cell's own
    /// colour for it, so the guard sees an opinion where there is none and takes the composite arm
    /// with the flag either way. (The same guard is the one whose hand-copy drifted between
    /// <c>Set</c> and <c>Fill</c>; see the remarks on <c>CellBuffer.BlendStyle</c>.)
    /// </summary>
    [Fact]
    public void Set_WithDelta_CompositesTheUnderlineColourEvenWithTheFlagOff()
    {
        var flagOff = new CellBuffer(2, 1);
        flagOff.Set(0, 0, "A", CellStyle.Default.WithBackground(Grey).WithUnderlineColor(Green));
        flagOff.PushBlendingMode(BlendingModes.Multiply);
        flagOff.Set(0, 0, "B", PartialStyle.Default);

        var flagOn = new CellBuffer(2, 1);
        flagOn.Set(0, 0, "A", CellStyle.Default.WithBackground(Grey).WithUnderlineColor(Green)
                                      .WithAttributes(TextAttributes.Underline));
        flagOn.PushBlendingMode(BlendingModes.Multiply);
        flagOn.Set(0, 0, "B", PartialStyle.Default);

        Assert.Equal(Color.FromRgb(0, 128, 0), flagOff[0, 0].Style.UnderlineColor);
        Assert.Equal(Color.FromRgb(0, 128, 0), flagOn[0, 0].Style.UnderlineColor);
    }

    // The identity delta on a clear is exactly "clear to the surface's blank" — the same thing the
    // parameterless Clear does. Pins that the base is the blank and not the cells.
    [Fact]
    public void Clear_WithIdentityDelta_EqualsTheParameterlessClear()
    {
        foreach (var blank in SurfaceBlanks)
        {
            var viaDelta = new CellBuffer(3, 2, defaultStyle: blank);
            var viaPlain = new CellBuffer(3, 2, defaultStyle: blank);

            viaDelta.Write(0, 0, "abc", CellStyle.Default.WithForeground(Red));
            viaPlain.Write(0, 0, "abc", CellStyle.Default.WithForeground(Red));

            viaDelta.Clear(PartialStyle.Default);
            viaPlain.Clear();

            AssertSameGrid(viaPlain, viaDelta, $"identity clear, blank={blank.Background}");
        }
    }
}
