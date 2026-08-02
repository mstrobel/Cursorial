using System.Buffers;
using System.Text;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Drawing;

// End-to-end coverage of the multicell guard through the real compositing path: a retained target,
// a scene whose raster never changes, and a Cells-layer fragment riding SceneCompositor's
// fragment pass-through. A composite-only slide (an Expander opening below a VerticalAlignment=
// Center StackPanel: layout and raster stay VALID, only the composition offset moves) re-anchors
// the fragment with ZERO changed cells in the diff — the exact shape that left stale OSC 66
// multicells (and torn/displaced rows) on kitty until the renderer learned frame-over-frame
// fragment accounting (FrameRenderer.ComputeFragmentGuardCells).
public class SceneCompositorSlideRenderTests
{
    private static readonly Style Panel = Style.Default.WithBackground(Color.FromRgb(20, 30, 40));

    private static SceneLayer Layer(Scene scene, int offsetColumn = 0, int offsetRow = 0) =>
        new(scene, new CompositeParameters(offsetColumn, offsetRow));

    private static string Render(FrameRenderer renderer, CellBuffer target)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(target, w);
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

    [Fact]
    public void CompositeOnlySlide_RewritesTheVacatedFragmentRect_AndReEmitsAtTheNewAnchor()
    {
        // A 10×6 scene: a uniform panel with a 2×2 fragment at scene (3, 2). The panel is taller
        // than the slide distance, so after the slide the fragment's OLD screen rect is covered by
        // the same panel cells it had before — byte-identical, invisible to the cell diff.
        var fragment = new SentinelFragment(new Size(2, 2), "[SLIDE]");
        using var scene = Scene.Create(10, 6);
        scene.Draw(ctx => ctx.DrawContent(
            new Rect(0, 0, 10, 6), new PanelWithFragment(fragment, 3, 2, Panel), OutputCapabilities.None));

        var target = new CellBuffer(12, 10);
        var compositor = new SceneCompositor(Style.Default);
        var renderer = new FrameRenderer();

        compositor.Composite([Layer(scene, offsetRow: 1)], target.AsView());
        var frame1 = Render(renderer, target);                     // fragment at target (3, 3)
        Assert.Equal(1, Count(frame1, "[SLIDE]"));

        // The slide: the scene raster is untouched (same RasterVersion) — only the composite
        // offset moves. The compositor re-registers the same fragment instance at (3, 5).
        compositor.Composite([Layer(scene, offsetRow: 3)], target.AsView());
        var frame2 = Render(renderer, target);

        // The vacated rect (rows 3–4) must be rewritten even though its cells compare equal —
        // the CUP to its SECOND row is unique to the cell rewrite (fragment brackets only CUP to
        // the anchors: (3,3) for the erase, (3,5) for the emit).
        Assert.Contains("\x1b[5;4H", frame2);

        // The fragment re-emits at the new anchor exactly once, after the old rect scrub.
        Assert.Equal(1, Count(frame2, "[SLIDE]"));
        int payload = frame2.IndexOf("[SLIDE]", StringComparison.Ordinal);
        Assert.True(frame2.IndexOf("\x1b[5;4H", StringComparison.Ordinal) < payload,
                    "The stale rect must be scrubbed before the fragment re-emits.");
        Assert.True(frame2.LastIndexOf("\x1b[6;4H", payload, StringComparison.Ordinal) >= 0,
                    "The fragment payload must be preceded by a cursor move to the new anchor.");
    }

    [Fact]
    public void TranslucentShadowLayer_GrazingTheFragment_RepaintsItOnEveryTransition()
    {
        // The popup-shadow bug through the real layer stack: frame A shows the fragment; frame B
        // adds a translucent shadow band (a separate NON-occluding scene layer, exactly how
        // TopLevelSurface.CollectLayers emits shadow fringes) overlapping the fragment's lower
        // row; frame C removes it. The shadow's bg-only tint writes land inside the footprint, so
        // both transitions must rewrite the WHOLE footprint and re-emit the fragment — otherwise
        // the band stays damaged after the menu closes (multicells torn, image band erased).
        var accent = Style.Default.WithBackground(Color.FromRgb(50, 60, 70));
        var fragment = new SentinelFragment(new Size(2, 2), "[SIZED]");
        using var scene = Scene.Create(12, 6);
        scene.Draw(ctx => ctx.DrawContent(
            new Rect(0, 0, 12, 6), new PanelWithFragment(fragment, 3, 2, Panel, accentCell: (4, 2)), OutputCapabilities.None));

        // A 6×1 translucent black band — bg-only cells, alpha 120 — grazing the rect's lower row.
        using var shadow = Scene.Create(6, 1);
        shadow.Draw(_ => { });
        var shadowStyle = Style.Default.WithBackground(Color.FromRgb(0, 0, 0).WithAlpha(120));
        var shadowView = shadow.Buffer.AsView();
        for (int c = 0; c < 6; c++)
            shadowView[c, 0] = new Cell(null, CellKind.Single, shadowStyle);

        var target = new CellBuffer(12, 6);
        var compositor = new SceneCompositor(Style.Default);
        var renderer = new FrameRenderer();

        compositor.Composite([new SceneLayer(scene, new CompositeParameters()) { SurfaceZ = 0 }], target.AsView());
        var frameA = Render(renderer, target);   // fragment rect: rows 2–3, columns 3–4
        Assert.Equal(1, Count(frameA, "[SIZED]"));

        // Frame B — the shadow layer appears over the rect's lower row. IsOccluder stays false
        // (the designed contract: a translucent tint must never crop or suppress the fragment),
        // so the fragment remains registered while the tint writes graze its footprint.
        compositor.Composite(
        [
            new SceneLayer(scene, new CompositeParameters()) { SurfaceZ = 0 },
            new SceneLayer(shadow, new CompositeParameters(1, 3)) { SurfaceZ = 1, IsOccluder = false },
        ], target.AsView());
        var frameB = Render(renderer, target);

        Assert.Equal(1, Count(frameB, "[SIZED]"));   // re-emitted over the tint ...
        Assert.Contains("48;2;50;60;70", frameB);    // ... after the whole footprint rewrote,
                                                     // including the untouched accent cell in the top row

        // Frame C — the shadow lifts; the damaged band must repaint and the fragment re-emit.
        compositor.Composite([new SceneLayer(scene, new CompositeParameters()) { SurfaceZ = 0 }], target.AsView());
        var frameC = Render(renderer, target);

        Assert.Equal(1, Count(frameC, "[SIZED]"));
        Assert.Contains("48;2;50;60;70", frameC);    // whole-footprint rewrite again
        Assert.Contains("48;2;20;30;40", frameC);    // the band's panel background is restored
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

    // An IContent that fills its bounds with a " "-glyph panel (so the composited cells compare
    // equal to the renderer's covered-cell placeholders) and registers a fragment at a fixed
    // scene anchor. An optional accent cell carries a distinct background so tests can observe
    // whole-footprint rewrites of otherwise-unchanged cells.
    private sealed class PanelWithFragment(
        IBufferFragment fragment, int fragmentColumn, int fragmentRow, Style fill,
        (int Column, int Row)? accentCell = null) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => fragment.GetSize();

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
        {
            for (int r = bounds.Row; r < bounds.RowEnd; r++)
                for (int c = bounds.Column; c < bounds.ColumnEnd; c++)
                    buffer.Set(c, r, " ", fill);

            if (accentCell is { } accent)
                buffer.Set(accent.Column, accent.Row, " ", Style.Default.WithBackground(Color.FromRgb(50, 60, 70)));

            buffer.AddFragment(fragmentColumn, fragmentRow, fragment, fill);
            var size = fragment.GetSize();
            return new Rect(fragmentColumn, fragmentRow, size.Columns, size.Rows);
        }
    }
}
