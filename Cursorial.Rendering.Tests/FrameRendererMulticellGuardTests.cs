using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

// The multicell guard (FrameRenderer.ComputeFragmentGuardCells). Kitty OSC 66 sized text lives on
// the terminal as multicell blocks whose overwrite semantics are non-local: a write into a block's
// top-left cell erases the whole block, a write into any other top-row cell replaces it with
// spaces, and a write landing in a lower row makes the terminal SKIP the cursor past the block
// before printing (https://sw.kovidgoyal.net/kitty/text-sizing-protocol/). A minimal cell diff that
// partially overwrites such a region therefore tears neighboring cells it believed unchanged,
// leaves stale scaled glyphs standing, and desyncs the tracked cursor column — the observed
// "torn rows" / "right-displaced text" glitches after a composite-only slide (an Expander
// open/collapse re-centering a StackPanel: layout and raster stay valid, only the composition
// offset moves, so the diff sees zero changed cells).
public class FrameRendererMulticellGuardTests
{
    private static readonly Style Panel = Style.Default.WithBackground(Color.FromRgb(20, 30, 40));
    private static readonly Style Accent = Style.Default.WithBackground(Color.FromRgb(50, 60, 70));

    private const string PanelSgr = "48;2;20;30;40";
    private const string AccentSgr = "48;2;50;60;70";

    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static void FillPanel(CellBuffer buffer, in Style style)
    {
        for (int r = 0; r < buffer.Rows; r++)
            for (int c = 0; c < buffer.Columns; c++)
                buffer.Set(c, r, " ", style);
    }

    // A 6×2 fragment anchored at (2, 1) over a uniform panel, with the covered cells set to the
    // exact bg-only form the renderer snapshots for them — so a repositioned fragment leaves a
    // byte-identical cell grid behind and the diff alone would never revisit the old rect.
    private static (FrameRenderer Renderer, CellBuffer Buffer, SentinelFragment Fragment) CreateSlideScenario()
    {
        var renderer = new FrameRenderer();
        var buffer = new CellBuffer(20, 8);
        FillPanel(buffer, Panel);

        var fragment = new SentinelFragment(new Size(6, 2), "[SIZED]");
        buffer.AddFragment(2, 1, fragment);
        return (renderer, buffer, fragment);
    }

    [Fact]
    public void MovedFragment_CompositeOnlySlide_RewritesOldRectAndReEmitsAtNewAnchor()
    {
        var (renderer, buffer, fragment) = CreateSlideScenario();
        Render(renderer, buffer);

        // The alignment slide: the raster is unchanged and only the composition offset moves, so
        // the compositor de-registers the fragment at the old anchor and re-registers the SAME
        // instance two rows down (SceneCompositor.PassThroughFragments). The uniform panel means
        // the cell diff sees nothing changed anywhere.
        buffer.RemoveFragment(2, 1);
        buffer.AddFragment(2, 3, fragment);
        var output = Render(renderer, buffer);

        // (i) The old rect's cells are force-rewritten even though they compare equal — that write
        // is what erases the stale multicells on the terminal. The CUP to the old rect's SECOND
        // row is unique to the cell rewrite: fragment brackets only ever CUP to anchors ((2,1) for
        // the erase, (2,3) for the emit).
        Assert.Contains("\x1b[3;3H", output);

        // The old rect's top row is rewritten before its second row (row-major order). Per the
        // OSC 66 overwrite rules the top-row writes erase every multicell block in the rect, so
        // the lower-row writes land on ordinary cells and the terminal never cursor-skips them —
        // this ordering is what keeps the renderer's cursor-advance model truthful.
        int topRow = output.IndexOf("\x1b[2;3H", StringComparison.Ordinal);
        int secondRow = output.IndexOf("\x1b[3;3H", StringComparison.Ordinal);
        Assert.True(topRow >= 0 && topRow < secondRow, "The old rect's top row must be rewritten before its lower rows.");

        // (ii) The fragment re-emits at the new anchor exactly once, after the old rect scrub.
        Assert.Equal(1, Count(output, "[SIZED]"));
        int payload = output.IndexOf("[SIZED]", StringComparison.Ordinal);
        Assert.True(payload > secondRow, "The fragment must re-emit only after its old rect has been scrubbed.");

        int newAnchor = output.LastIndexOf("\x1b[4;3H", payload, StringComparison.Ordinal);
        Assert.True(newAnchor >= 0, "The fragment payload must be preceded by a cursor move to the new anchor.");
    }

    [Fact]
    public void SteadyFragment_UntouchedCells_EmitsNothing()
    {
        // (iii) The guard must not create churn: an unchanged fragment over untouched cells
        // re-emits neither the fragment nor any cell of its footprint.
        var (renderer, buffer, _) = CreateSlideScenario();
        Render(renderer, buffer);

        var output = Render(renderer, buffer);

        Assert.DoesNotContain("[SIZED]", output);
        Assert.DoesNotContain("48;2;", output);
    }

    [Fact]
    public void RemovedFragment_IdenticalCellsUnderneath_StillRewritesOldRect()
    {
        // Removal with a byte-identical cell grid underneath: the covered placeholders the front
        // buffer recorded equal the back cells, so a plain diff sees nothing — but the terminal
        // still shows the scaled glyphs. The whole old rect must rewrite anyway. (No compositor
        // ForceRepaint mark is involved; this is the renderer's own frame-over-frame accounting.)
        var (renderer, buffer, _) = CreateSlideScenario();
        Render(renderer, buffer);

        buffer.RemoveFragment(2, 1);
        var output = Render(renderer, buffer);

        Assert.Contains("\x1b[3;3H", output);       // second footprint row rewritten (no bracket CUPs there)
        Assert.Contains(PanelSgr, output);          // the rewrite paints the panel background back
        Assert.DoesNotContain("[SIZED]", output);   // the fragment itself is gone
    }

    [Fact]
    public void PartialOverlapWrite_ExpandsToWholeFootprint_AndReEmitsOnce()
    {
        // (iv) A single-cell write into a LOWER row of a live fragment's rect. Left alone, that
        // write would be cursor-skipped by the terminal (it lands inside a live multicell block's
        // lower row) and would blank neighbors the diff believed unchanged. The guard expands the
        // emission to the fragment's whole rect, top row first.
        var (renderer, buffer, _) = CreateSlideScenario();
        for (int c = 4; c <= 7; c++)
            buffer.Set(c, 1, " ", Accent);   // distinct bg on the rect's top-right, to observe its rewrite
        Render(renderer, buffer);

        buffer.Set(3, 2, " ", Style.Default.WithBackground(Color.FromRgb(200, 100, 50)));
        var output = Render(renderer, buffer);

        // The UNCHANGED top-row accent cells are rewritten (whole-footprint expansion) ...
        Assert.Contains(AccentSgr, output);

        // ... and before the changed lower-row cell, so the top-row writes have already erased the
        // multicell blocks by the time the lower row is touched.
        int accent = output.IndexOf(AccentSgr, StringComparison.Ordinal);
        int changed = output.IndexOf("48;2;200;100;50", StringComparison.Ordinal);
        Assert.True(accent >= 0 && changed > accent, "Top-row rewrite must precede the lower-row write.");

        // The fragment re-emits exactly once, after the cell pass repainted its footprint.
        Assert.Equal(1, Count(output, "[SIZED]"));
        Assert.True(output.IndexOf("[SIZED]", StringComparison.Ordinal) > changed,
                    "The fragment must re-emit after its footprint was repainted.");
    }

    [Fact]
    public void ShadowBand_OverlapsFragment_DamagedRegionRepaintsWhenTheShadowLifts()
    {
        // The popup-shadow scenario: a menu clears the fragment but its translucent shadow fringe
        // overlaps the rect's lower row. Shadow layers are deliberately NOT occluders (see
        // TopLevelSurface.CollectLayers), so the fragment stays registered while the shadow's
        // bg-only tint writes land inside its footprint. Frame B (shadow appears) and frame C
        // (shadow lifts) must each rewrite the whole footprint and re-emit the fragment — without
        // the guard, the partial band writes tear the multicells / erase the image band and the
        // damage persists after the menu closes.
        var (renderer, buffer, _) = CreateSlideScenario();
        for (int c = 4; c <= 7; c++)
            buffer.Set(c, 1, " ", Accent);
        var frameA = Render(renderer, buffer);
        Assert.Equal(1, Count(frameA, "[SIZED]"));

        // Frame B — the shadow band tints row 2 (the fragment's lower row) across columns 0..9.
        var shadow = Style.Default.WithBackground(Color.FromRgb(10, 15, 20));
        for (int c = 0; c <= 9; c++)
            buffer.Set(c, 2, " ", shadow);
        var frameB = Render(renderer, buffer);

        Assert.Contains(AccentSgr, frameB);          // untouched top row rewritten too (full footprint)
        Assert.Equal(1, Count(frameB, "[SIZED]"));   // fragment re-emits over the tint
        Assert.True(frameB.IndexOf("[SIZED]", StringComparison.Ordinal) >
                    frameB.IndexOf("48;2;10;15;20", StringComparison.Ordinal),
                    "The fragment must re-emit after the shadow band's cell writes.");

        // Frame C — the menu closes and the band reverts to the panel background.
        for (int c = 0; c <= 9; c++)
            buffer.Set(c, 2, " ", Panel);
        var frameC = Render(renderer, buffer);

        Assert.Contains(PanelSgr, frameC);           // the damaged band cells rewrite ...
        Assert.Contains(AccentSgr, frameC);          // ... as part of the whole footprint ...
        Assert.Equal(1, Count(frameC, "[SIZED]"));   // ... and the fragment repaints on top
    }

    [Fact]
    public void WriteAdjacentToFragmentRect_DoesNotExpandOrReEmit()
    {
        // Precision of the expansion: a narrow-glyph write immediately LEFT of the rect cannot
        // paint into it, so it must trigger neither a footprint rewrite nor a re-emission.
        var (renderer, buffer, _) = CreateSlideScenario();
        Render(renderer, buffer);

        buffer.Set(1, 1, "x", Style.Default);
        var output = Render(renderer, buffer);

        Assert.Contains("x", output);
        Assert.DoesNotContain("[SIZED]", output);
        Assert.DoesNotContain("\x1b[3;3H", output);   // no second-footprint-row rewrite
    }

    [Fact]
    public void ScrollDetection_SuppressedWhileCellsFragmentIsLive()
    {
        // An SU/SD physically slides the multicell glyphs a fragment committed to the terminal
        // away from the rect the renderer tracks for them — so scroll detection must stand down
        // whenever fragments are live, even when the shifted cell content would match. (The cells
        // here are staged so the covered placeholder matches the shifted content exactly; the old
        // overlay-only gate would have emitted the scroll.)
        var renderer = new FrameRenderer();
        var buffer = new CellBuffer(8, 6);
        FillPanel(buffer, Style.Default);
        for (int row = 0; row < 6; row++)
            buffer.Set(0, row, ((char) ('A' + row)).ToString(), Style.Default);
        buffer.AddFragment(6, 0, new SentinelFragment(new Size(1, 1), "[F]"));
        Render(renderer, buffer);

        for (int row = 0; row < 6; row++)
            buffer.Set(0, row, ((char) ('B' + row)).ToString(), Style.Default);   // everything shifts up one row
        var output = Render(renderer, buffer);

        Assert.DoesNotContain("\x1b[1S", output);   // no scroll command while the fragment is live
        Assert.Contains("G", output);               // the content still repaints via the plain diff
        Assert.DoesNotContain("[F]", output);       // the untouched fragment still doesn't churn
    }

    [Fact]
    public void SizedTextFragment_CompositeOnlySlide_ReEmitsOsc66AtTheNewAnchor()
    {
        // The reported glitch end-to-end at the renderer boundary, with the real OSC 66 fragment:
        // scale-2 text slides two rows down with a byte-identical cell grid. The old rect must be
        // scrubbed (erasing the stale multicells) before the OSC 66 payload re-emits at the new
        // anchor.
        var caps = OutputCapabilities.None with
                   {
                       TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
                   };

        var renderer = new FrameRenderer(caps);
        var buffer = new CellBuffer(20, 8);
        FillPanel(buffer, Style.Default);

        var fragment = new SizedTextFragment(new TextSizing(Scale: 2), "Title", Style.Default);
        buffer.AddFragment(2, 1, fragment);   // rect (2, 1) – (11, 2): 5 clusters × scale 2, 2 rows
        Render(renderer, buffer);

        buffer.RemoveFragment(2, 1);
        buffer.AddFragment(2, 3, fragment);
        var output = Render(renderer, buffer);

        Assert.Contains("\x1b]66;", output);
        Assert.Contains("s=2", output);
        Assert.Equal(1, Count(output, "Title"));

        Assert.Contains("\x1b[3;3H", output);   // the old rect's second row is rewritten
        Assert.True(output.IndexOf("\x1b[3;3H", StringComparison.Ordinal) <
                    output.IndexOf("Title", StringComparison.Ordinal),
                    "The stale multicell rows must be scrubbed before the OSC 66 payload re-emits.");
    }

    private sealed class SentinelFragment(Size size, string sentinel) : IBufferFragment
    {
        public FragmentLayer Layer => FragmentLayer.Cells;
        public Size GetSize() => size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
        {
            var bytes = Encoding.UTF8.GetBytes(sentinel);
            var dest = output.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            output.Advance(bytes.Length);
        }
    }
}
