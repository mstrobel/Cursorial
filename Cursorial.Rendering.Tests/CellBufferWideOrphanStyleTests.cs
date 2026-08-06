using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// When a write invalidates half of a wide glyph pair, the surviving half is stripped to a blank
/// single carrying the <b>pair's</b> <see cref="CellStyle"/>. Resetting it to <see cref="Cell.Blank"/>
/// would punch a differently-styled hole through whatever region the pair sat in (a selection tint,
/// a panel fill), and on a surface whose blank is not <see cref="CellStyle.Default"/> — an intermediate
/// group buffer, based on <see cref="CellStyle.Transparent"/> — it would leave an <em>opaque</em> cell
/// that occludes content the surface was supposed to let through.
/// <para>
/// Where that style comes from depends on which half survives. A surviving <b>wide-left</b> keeps
/// its own. A surviving <b>continuation</b> has none to keep — <see cref="CellBuffer"/> stores
/// <see cref="Cell.Kind"/> alone there — so it inherits the wide-left's, which the buffer must read
/// <em>before</em> the write that clobbers it. Getting that order wrong is invisible when both
/// halves are written in the same style, so every case below writes the clobbering cell in a
/// different color than the pair it destroys.
/// </para>
/// </summary>
public class CellBufferWideOrphanStyleTests
{
    private static readonly Color Red = Color.FromRgb(200, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 200);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    // The buffer's own notion of blank: a terminal that reported its default colors gives the
    // buffer a non-default base style, which Clear/Fill write and Style.Default does NOT match.
    private static CellBuffer BufferWithBaseStyle(Color foreground, Color background)
        => new(6, 1, TerminalCapabilities.None with
                     {
                         Output = OutputCapabilities.None with
                                  {
                                      Color = ColorCapabilities.None with
                                              {
                                                  DefaultForeground = foreground,
                                                  DefaultBackground = background
                                              }
                                  }
                     });

    private static void AssertBlankedInPlace(in Cell cell)
    {
        Assert.Equal(CellKind.Single, cell.Kind);
        Assert.True(string.IsNullOrEmpty(cell.Grapheme));
    }

    [Fact]
    public void Set_OverwriteWideLeft_OrphanedContinuationInheritsTheWideLeftsStyle()
    {
        var buf = new CellBuffer(6, 1);
        var tinted = CellStyle.Default.WithForeground(White).WithBackground(Red);
        buf.Set(1, 0, "中", tinted);

        buf.Set(1, 0, "a", CellStyle.Default.WithBackground(Blue)); // clobbers the left half

        // Red, not Blue: the orphan inherits the style of the wide-left that was there, not the one
        // the clobbering write is installing.
        AssertBlankedInPlace(buf[2, 0]);
        Assert.Equal(Red, buf[2, 0].Style.Background);
    }

    [Fact]
    public void Set_OverwriteContinuation_OrphanedWideLeftKeepsItsStyle()
    {
        var buf = new CellBuffer(6, 1);
        var tinted = CellStyle.Default.WithForeground(White).WithBackground(Red);
        buf.Set(1, 0, "中", tinted);

        buf.Set(2, 0, "a", CellStyle.Default.WithBackground(Blue)); // clobbers the continuation

        AssertBlankedInPlace(buf[1, 0]);
        Assert.Equal(Red, buf[1, 0].Style.Background);
    }

    [Fact]
    public void Indexer_OverwriteWideLeft_OrphanedContinuationInheritsTheWideLeftsStyle()
    {
        var buf = new CellBuffer(6, 1);
        var tinted = CellStyle.Default.WithForeground(White).WithBackground(Red);
        buf[1, 0] = new Cell("中", CellKind.WideLeft, tinted);

        buf[1, 0] = new Cell("a", CellKind.Single, CellStyle.Default.WithBackground(Blue));

        AssertBlankedInPlace(buf[2, 0]);
        Assert.Equal(Red, buf[2, 0].Style.Background);
    }

    [Fact]
    public void Indexer_OverwriteContinuation_OrphanedWideLeftKeepsItsStyle()
    {
        var buf = new CellBuffer(6, 1);
        var tinted = CellStyle.Default.WithForeground(White).WithBackground(Red);
        buf[1, 0] = new Cell("中", CellKind.WideLeft, tinted);

        buf[2, 0] = new Cell("a", CellKind.Single, CellStyle.Default.WithBackground(Blue));

        AssertBlankedInPlace(buf[1, 0]);
        Assert.Equal(Red, buf[1, 0].Style.Background);
    }

    [Fact]
    public void Set_WidePairOverNextPairsWideLeft_CascadedOrphanInheritsItsWideLeftsStyle()
    {
        // The pair written at (1,2) lands its continuation on the next pair's WideLeft at 2, so
        // THAT pair's continuation at 3 is orphaned two columns over — and its style has to be read
        // off the wide-left at 2 before the incoming pair's continuation overwrites it.
        var buf = new CellBuffer(6, 1);
        buf.Set(2, 0, "中", CellStyle.Default.WithBackground(Red));

        buf.Set(1, 0, "全", CellStyle.Default.WithBackground(Blue));

        AssertBlankedInPlace(buf[3, 0]);
        Assert.Equal(Red, buf[3, 0].Style.Background);
    }

    [Fact]
    public void Indexer_WideLeftOverNextPairsWideLeft_CascadedOrphanInheritsItsWideLeftsStyle()
    {
        var buf = new CellBuffer(6, 1);
        buf[2, 0] = new Cell("中", CellKind.WideLeft, CellStyle.Default.WithBackground(Red));

        buf[1, 0] = new Cell("全", CellKind.WideLeft, CellStyle.Default.WithBackground(Blue));

        AssertBlankedInPlace(buf[3, 0]);
        Assert.Equal(Red, buf[3, 0].Style.Background);
    }

    [Fact]
    public void Set_OrphanedHalf_InBufferWithNonDefaultBaseStyle_DoesNotAcquireDefaultStyle()
    {
        // A glyph painted with no background of its own inherits the buffer's base background.
        // Orphaning its partner must not swap that for Style.Default — the buffer's blank is the
        // base style, so a default-styled cell is a visible hole, not a blank.
        var background = Color.FromRgb(30, 30, 40);
        var buf = BufferWithBaseStyle(Color.FromRgb(220, 220, 220), background);
        buf.Set(1, 0, "中", CellStyle.Default.WithForeground(White));

        buf.Set(1, 0, "a", CellStyle.Default.WithForeground(White));

        AssertBlankedInPlace(buf[2, 0]);
        Assert.Equal(background, buf[2, 0].Style.Background);
        Assert.NotEqual(CellStyle.Default, buf[2, 0].Style);
    }

    [Fact]
    public void Set_OrphanedHalf_OnTransparentSurface_StaysTransparent()
    {
        // The group-compositing surface: SceneCompositor.ForIntermediate resets every cell to
        // Style.Transparent, and glyphs land on it with no background of their own. An orphaned
        // half reset to Style.Default would be OPAQUE (Color.Default is opaque), occluding
        // whatever the surface is later blended over. The orphan here is the CONTINUATION, which
        // carries no style of its own — so this is the case that fails if the inheritance is
        // dropped rather than sourced from the wide-left.
        var buf = TransparentSurface();
        buf.Set(1, 0, "中", CellStyle.Transparent.WithForeground(White));

        buf.Set(1, 0, "a", CellStyle.Transparent.WithForeground(White));

        AssertBlankedInPlace(buf[2, 0]);
        Assert.True(buf[2, 0].Style.Background.IsTransparent);
    }

    [Fact]
    public void Indexer_OrphanedHalf_OnTransparentSurface_StaysTransparent()
    {
        // The compositor writes through the raw indexer, so the indexer's orphan sites carry the
        // same obligation as Set's.
        var buf = TransparentSurface();
        buf[1, 0] = new Cell("中", CellKind.WideLeft, CellStyle.Transparent.WithForeground(White));

        buf[2, 0] = new Cell("a", CellKind.Single, CellStyle.Transparent.WithForeground(White));

        AssertBlankedInPlace(buf[1, 0]);
        Assert.True(buf[1, 0].Style.Background.IsTransparent);
    }

    [Theory]
    [InlineData(1)] // clobber the WideLeft
    [InlineData(2)] // clobber the continuation
    public void Set_OrphanedHalf_LeavesNoStrandedPairMember(int clobberedColumn)
    {
        var buf = new CellBuffer(6, 1);
        buf.Set(1, 0, "中", CellStyle.Default.WithBackground(Red));

        buf.Set(clobberedColumn, 0, "a", CellStyle.Default.WithBackground(Blue));

        // Style preservation must not smuggle the KIND through: no half-glyph may survive, or the
        // frame renderer emits into a wide glyph's right half.
        for (int c = 0; c < buf.Columns; c++)
        {
            var cell = buf[c, 0];
            if (cell.Kind == CellKind.WideLeft)
                Assert.Equal(CellKind.WideContinuation, buf[c + 1, 0].Kind);
            else if (cell.Kind == CellKind.WideContinuation)
                Assert.Equal(CellKind.WideLeft, buf[c - 1, 0].Kind);
        }

        Assert.Equal(CellKind.Single, buf[1, 0].Kind);
        Assert.Equal(CellKind.Single, buf[2, 0].Kind);
    }

    private static CellBuffer TransparentSurface()
    {
        var buf = new CellBuffer(6, 1);
        // Exactly what SceneCompositor's reset-to-base writes for an intermediate surface.
        buf.Fill(new Cell(null, CellKind.Single, CellStyle.Transparent));
        return buf;
    }
}
