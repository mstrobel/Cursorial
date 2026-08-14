using Cursorial.Animation;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Media;

using Color = Cursorial.Media.Color;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// The animated phase brush end-to-end: a <see cref="TextBlock"/> painted with a
/// <see cref="PhaseShiftedBrush"/> over a repeating gradient, its <see cref="PhaseShiftedBrush.Phase"/>
/// driven by <c>Cursorial.Animation</c> through the headless host's frame loop. Frames differ per
/// tick while the brush REFERENCE never changes — so every reference-keyed cache stays hot (the
/// cache-key census): the <see cref="FormattedTextCache"/> layout is reused (repaint without
/// reformat), and the steady-state tick path allocates nothing. The one deliberate per-tick cost —
/// <see cref="ScaledText"/>'s OSC 66 fragment rebuild under a non-uniform carrier — is pinned here
/// as honest, because the fragment's baked SGR backdrop genuinely changes as the phase advances.
/// </summary>
public sealed class AnimatedPhaseBrushTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static PhaseShiftedBrush PhaseRamp()
        => new(new LinearGradientBrush(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255),
                                       spread: GradientSpread.Repeat));

    private static (UIHeadlessHost Host, TextBlock Text, PhaseShiftedBrush Brush) ShownText()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var brush = PhaseRamp();
        var text = new TextBlock("ABCDEFGHIJ") { Foreground = brush };
        var root = new StackPanel();
        root.Children.Add(text);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, text, brush);
    }

    private static Color[] ForegroundRow(UIHeadlessHost host, int cells)
    {
        var colors = new Color[cells];
        for (var col = 0; col < cells; col++)
            colors[col] = host.GetCell(col, 0).Style.Foreground;
        return colors;
    }

    [Fact]
    public void AnimatedPhase_FramesDifferPerTick_LayoutIsNeverReformatted()
    {
        var (host, text, brush) = ShownText();
        using var _ = host;

        var formatsAfterFirstFrame = text.Cache.FullFormatCount + text.Cache.FastPathFormatCount;
        var previous = ForegroundRow(host, 10);

        brush.BeginAnimation(PhaseShiftedBrush.PhaseProperty, new DoubleAnimation(0.0, 1.0, Ms(1000)));

        for (var tick = 0; tick < 3; tick++)
        {
            host.AdvanceTime(Ms(160)); // phase +0.16 per tick over the repeating ramp

            var current = ForegroundRow(host, 10);
            Assert.NotEqual(previous, current); // the gradient visibly slid — a new frame per tick
            previous = current;
        }

        // The brush mutated IN PLACE: the element still holds the same reference…
        Assert.Same(brush, text.Foreground);

        // …so the layout cache never missed: repaint per tick, zero reformats (the carrier term is
        // reference-keyed and the reference never changed).
        Assert.Equal(formatsAfterFirstFrame, text.Cache.FullFormatCount + text.Cache.FastPathFormatCount);
    }

    [Fact]
    public void PhaseTickPath_AllocatesNothingSteadyState()
    {
        // The per-tick spine measured WITHOUT the frame pipeline around it: animated-lane write →
        // change dispatch on the brush → sub-object fan-out → lattice clamp → InvalidateVisual
        // (zone raster-dirty flag). Frame rendering itself is measured by nothing here — its
        // buffers belong to the frame pipeline, not to the brush.
        var (host, _, brush) = ShownText();
        using var _ = host;

        var handle = brush.BeginAnimation(PhaseShiftedBrush.PhaseProperty); // the raw Animation-lane producer
        handle.SetValue(0.001); // warm-up: entry, metadata, carrier pool, watcher delivery
        handle.SetValue(0.002);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 1_000; i++)
            handle.SetValue(i / 1_000.0);
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
    }

    [Fact]
    public void LayoutCache_CarrierHoldingThePhaseBrush_StillHitsAfterATick()
    {
        // The FormattedTextCache carrier term is reference-keyed (BrushedStyle's brushes compare by
        // reference): a phase tick must NOT miss the layout key — cached layout reused, repaint only.
        var text = new TextBlock();
        var cache = new FormattedTextCache(text, () => { });
        var brush = PhaseRamp();

        var request = new FormattedTextCache.LayoutRequest(
            "hello", MarkupLane: false, Columns: 20, MaxRows: null,
            WrapMode.NoWrap, TextAlignment.Left, TextTrimming.None,
            Carrier: new BrushedStyle { Foreground = brush });

        cache.StoreLayout(in request, FormattedText.Empty);

        brush.Phase = 0.7; // the tick: same reference, new sampling

        Assert.True(cache.TryGetLayout(in request, out var cached));
        Assert.Same(FormattedText.Empty, cached);

        // Control: a DIFFERENT brush instance in the carrier misses — the key really is the term
        // that a phase tick leaves untouched.
        var replaced = request with { Carrier = new BrushedStyle { Foreground = PhaseRamp() } };
        Assert.False(cache.TryGetLayout(in replaced, out _));
    }

    [Fact]
    public void ScaledText_AnimatedGradientCarrier_RebuildsTheFragmentPerTick_AndTheBackdropReallyChanged()
    {
        // ScaledText's staleness gate rebuilds unconditionally for !IsUniform carriers, so an
        // animated gradient on sized text rebuilds the OSC 66 fragment every paint. That is the
        // HONEST cost, pinned here: the fragment bakes ONE SGR backdrop — the carrier resolved at
        // the fragment's anchor — and a phase tick moves the gradient under that anchor, so the
        // emission genuinely differs; a phase-aware exemption would freeze stale ink. (The gate
        // also rebuilds between paints at an UNCHANGED phase — its seam has no anchor in scope to
        // key a resolved value on; see the report for the anchor-keyed refinement question.)
        var brush = PhaseRamp();
        var carrier = new BrushedStyle { Foreground = brush };
        Assert.False(carrier.IsUniform); // a phase-shifted gradient is non-uniform — the gate's input

        var content = new ScaledText("Hi", new TextSizing(Scale: 2));
        var buffer = new CellBuffer(40, 6);
        var caps = OutputCapabilities.None with
                   {
                       TextSizing = new TextSizingCapabilities(Width: true, Scale: true)
                   };

        content.Paint(buffer, 0, 0, carrier, caps);
        var first = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);

        brush.Phase = 0.5; // the tick

        content.Paint(buffer, 0, 0, carrier, caps);
        var second = Assert.IsType<SizedTextFragment>(buffer.Fragments[(0, 0)].Fragment);

        Assert.NotSame(first, second);                 // rebuilt per tick — the pinned cost…
        Assert.NotEqual(first.Style, second.Style);    // …and honestly so: the baked backdrop moved
    }
}
