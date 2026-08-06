using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// <see cref="SceneLayer.Damage"/>: a layer's claim that the one <see cref="Scene.RasterVersion"/> bump
/// it is reporting rewrote only these cells of its scene. Without it a version change costs the layer's
/// whole footprint, which is right for every ordinary scene (<see cref="Scene.Draw"/> re-rasters
/// wholesale) and ruinous for an intermediate surface, whose version bumps for a change anywhere in a
/// collapsed subtree.
/// </summary>
/// <remarks>
/// The claim is only believed when nothing else about the layer could have invalidated it — same scene,
/// same <see cref="CompositeParameters"/>, and a version exactly one ahead of the last composite. Each of
/// those has a test below, because every one of them fails <em>silently</em>: the target keeps a stale
/// cell, the front/back diff sees no difference, and nothing is emitted to correct it.
/// </remarks>
public class SceneLayerDamageTests
{
    private static readonly Color Backdrop = Color.FromRgb(0, 0, 255);
    private static readonly Color Body = Color.FromRgb(255, 0, 0);
    private static readonly Color Spot = Color.FromRgb(0, 255, 0);
    private static readonly Color OtherSpot = Color.FromRgb(255, 255, 0);

    private static readonly Rect SpotCell = new(3, 2, 1, 1);

    [Fact]
    public void Damage_NarrowsTheRecompositedRegionToTheCellsItNames()
    {
        var (compositor, buffer, view, scene) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(scene) };

        Paint(scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        buffer.ClearDirty();
        Paint(scene, OtherSpot);
        layers[0] = layers[0] with { Damage = SpotCell };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal([SpotCell], buffer.DirtyRegions);
    }

    [Fact]
    public void WithoutDamage_AVersionChangeStillCostsTheWholeFootprint()
    {
        // The unchanged behaviour, pinned beside the new one: null damage means "unknown", and unknown has
        // always meant the whole footprint. Every producer but an intermediate surface says null.
        var (compositor, buffer, view, scene) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(scene) };

        Paint(scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        buffer.ClearDirty();
        Paint(scene, OtherSpot);

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal([new Rect(0, 0, 12, 6)], buffer.DirtyRegions);
    }

    [Fact]
    public void Damage_IsTranslatedByTheLayerOffset()
    {
        // Damage is in the SCENE's coordinates — it describes cells of the scene, not of the target — so
        // the layer's placement is applied to it exactly as it is to the footprint.
        var (compositor, buffer, view, _) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(Scene.Create(6, 4), new CompositeParameters(offsetColumn: 4, offsetRow: 1)) };

        Paint(layers[0].Scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        buffer.ClearDirty();
        Paint(layers[0].Scene, OtherSpot);
        layers[0] = layers[0] with { Damage = SpotCell };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal([new Rect(7, 3, 1, 1)], buffer.DirtyRegions);
    }

    [Fact]
    public void Damage_OutsideTheLayersClip_CostsNothingAtAll()
    {
        // The cell the scene rewrote is one the target cannot see, so there is genuinely nothing to do —
        // and the compositor must say so (false, no dirty marks) rather than repaint the footprint for a
        // change that could not have reached it.
        var (compositor, buffer, view, _) = Fixture(12, 6);
        var clipped = new CompositeParameters(offsetColumn: 4, offsetRow: 1, clip: new Rect(4, 1, 2, 1));
        var layers = new[] { new SceneLayer(Scene.Create(6, 4), clipped) };

        Paint(layers[0].Scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        buffer.ClearDirty();
        Paint(layers[0].Scene, OtherSpot);            // SpotCell → target (7, 3), outside the clip
        layers[0] = layers[0] with { Damage = SpotCell };

        Assert.False(compositor.Composite(layers, view));
        Assert.Empty(buffer.DirtyRegions);
    }

    // ───────────────────────────── where the claim must not be believed ─────────────────────────────

    [Fact]
    public void Damage_IsIgnoredWhenTheLayerAlsoMoved()
    {
        // A move rewrites every cell of both the new footprint and the vacated one, whatever the scene's
        // own damage was — the damage says which cells of the SCENE changed, never where it lands.
        var (compositor, buffer, view, _) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(Scene.Create(6, 4)) };

        Paint(layers[0].Scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        buffer.ClearDirty();
        Paint(layers[0].Scene, OtherSpot);
        layers[0] = layers[0] with { Parameters = new CompositeParameters(offsetColumn: 2), Damage = SpotCell };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal([new Rect(0, 0, 8, 4)], buffer.DirtyRegions);   // vacated (0,0,6,4) ∪ new (2,0,6,4)
    }

    [Fact]
    public void Damage_IsIgnoredWhenTheVersionMovedByMoreThanOne()
    {
        // The damage describes the LAST composite into the scene. A reader that missed the one before it
        // would keep whatever that one rewrote — so more than one bump means the claim is incomplete.
        var (compositor, buffer, view, scene) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(scene) };

        Paint(scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        buffer.ClearDirty();
        Paint(scene, OtherSpot);
        Paint(scene, Spot);
        layers[0] = layers[0] with { Damage = SpotCell };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal([new Rect(0, 0, 12, 6)], buffer.DirtyRegions);
    }

    [Fact]
    public void Damage_IsIgnoredWhenTheSceneIdentityChanged()
    {
        // A different scene in the slot shares nothing with the one whose cells the damage describes —
        // including, after a pool round trip, its version numbering.
        var (compositor, buffer, view, scene) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(scene) };

        Paint(scene, Spot);
        Assert.True(compositor.Composite(layers, view));

        var replacement = Scene.Create(12, 6);
        Paint(replacement, OtherSpot);
        Assert.Equal(scene.RasterVersion, replacement.RasterVersion);   // identical version, different pixels

        buffer.ClearDirty();
        layers[0] = new SceneLayer(replacement) { Damage = SpotCell };

        Assert.True(compositor.Composite(layers, view));
        Assert.Equal([new Rect(0, 0, 12, 6)], buffer.DirtyRegions);
    }

    // ───────────────────────────── the output, not just the region ─────────────────────────────

    [Fact]
    public void ANarrowedComposite_LandsOnExactlyTheSameCellsAsAFullOne()
    {
        // The region is bookkeeping; this is the property that matters. A damage rect one cell too small
        // leaves a stale cell that the front/back diff will never report, so it survives on the terminal
        // until something unrelated repaints it.
        var (incremental, buffer, view, scene) = Fixture(12, 6);
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(offsetColumn: 2, offsetRow: 1, opacity: 128)) };

        Paint(scene, Spot);
        Assert.True(incremental.Composite(layers, view));

        foreach (var colour in new[] { OtherSpot, Spot, Color.FromRgb(9, 9, 9), OtherSpot })
        {
            Paint(scene, colour);
            layers[0] = layers[0] with { Damage = SpotCell };
            incremental.Composite(layers, view);

            var reference = new CellBuffer(12, 6);
            new SceneCompositor(CellStyle.Default.WithBackground(Backdrop))
                .Composite([new SceneLayer(scene, layers[0].Parameters)], reference.AsView());

            for (var row = 0; row < 6; row++)
            for (var column = 0; column < 12; column++)
                Assert.Equal(reference[column, row], buffer[column, row]);
        }
    }

    // ───────────────────────────── fixtures ─────────────────────────────

    private static (SceneCompositor Compositor, CellBuffer Buffer, CellBufferView View, Scene Scene) Fixture(int columns, int rows)
    {
        var buffer = new CellBuffer(columns, rows);
        return (new SceneCompositor(CellStyle.Default.WithBackground(Backdrop)), buffer, buffer.AsView(), Scene.Create(columns, rows));
    }

    /// <summary>
    /// Re-rasters <paramref name="scene"/>: the same body everywhere, <paramref name="spot"/> in
    /// <see cref="SpotCell"/>. Two calls that differ only in <paramref name="spot"/> bump
    /// <see cref="Scene.RasterVersion"/> once and change exactly that one cell — a producer whose honest
    /// damage is <see cref="SpotCell"/>.
    /// </summary>
    private static void Paint(Scene scene, Color spot)
    {
        scene.Invalidate();
        scene.Draw(context =>
                   {
                       context.FillRectangle(scene.Bounds, new SolidColorBrush(Body));
                       context.FillRectangle(SpotCell, new SolidColorBrush(spot));
                   });
    }
}
