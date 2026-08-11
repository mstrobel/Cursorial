using System.Runtime.CompilerServices;

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Media;

namespace Cursorial.Tests.UI.Properties;

/// <summary>
/// Sub-object observation: a styled property whose value is a (non-element) <see cref="UIObject"/>
/// auto-subscribes its owner, and a sub-property change routes through the owning slot's EFFECT
/// path — the sub-property's declared level clamped by the host slot's ceiling through the
/// Measure→Arrange→Render implication closure — firing the host property's flag effects only,
/// never its value-changed channels. Lifetime is leak-free: detach on replacement and on the
/// teardown sweep, both probed with <see cref="WeakReference"/>s.
/// </summary>
public class SubObjectObservationTests
{
    // ───────────────────────────── fixture types ─────────────────────────────

    /// <summary>A host with one slot per effect ceiling, plus every value-changed channel counted.</summary>
    private class EffectHost : UIElement
    {
        /// <summary>A render-ceiling brush slot (the TextBlock.Foreground shape).</summary>
        public static readonly StyledProperty<IBrush?> RenderBrushProperty =
            UIProperty.Register<EffectHost, IBrush?>("SubObsRenderBrush",
                changed: static (sender, _, _) => ((EffectHost)sender).ChangedCallbackCount++);

        /// <summary>A measure-ceiling brush slot — the mixed-declaration case.</summary>
        public static readonly StyledProperty<IBrush?> MeasureBrushProperty =
            UIProperty.Register<EffectHost, IBrush?>("SubObsMeasureBrush");

        /// <summary>A render-ceiling object slot (for non-brush sub-objects).</summary>
        public static readonly StyledProperty<object?> ObjectSlotProperty =
            UIProperty.Register<EffectHost, object?>("SubObsObjectSlot");

        /// <summary>A measure-ceiling object slot.</summary>
        public static readonly StyledProperty<object?> MeasureObjectSlotProperty =
            UIProperty.Register<EffectHost, object?>("SubObsMeasureObjectSlot");

        static EffectHost()
        {
            AffectsRender<EffectHost>(RenderBrushProperty, ObjectSlotProperty);
            AffectsMeasure<EffectHost>(MeasureBrushProperty, MeasureObjectSlotProperty);
        }

        /// <summary>Metadata <c>Changed</c> invocations for <see cref="RenderBrushProperty"/>.</summary>
        public int ChangedCallbackCount;

        /// <summary>Virtual <c>OnPropertyChanged</c> deliveries (any property).</summary>
        public int VirtualChannelCount;

        protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
        {
            base.OnPropertyChanged(in args);
            VirtualChannelCount++;
        }
    }

    /// <summary>A sub-object whose animatable knob declares <see cref="PropertyEffects.AffectsMeasure"/>.</summary>
    private sealed class MeasureDeclaringSub : UIObject
    {
        public static readonly StyledProperty<double> XProperty =
            UIProperty.Register<MeasureDeclaringSub, double>("SubObsMeasureX");

        static MeasureDeclaringSub() => AffectsMeasure<MeasureDeclaringSub>(XProperty);

        public double X { set => SetValue(XProperty, value); }
    }

    /// <summary>A sub-object whose property declares no effects at all.</summary>
    private sealed class EffectlessSub : UIObject
    {
        public static readonly StyledProperty<double> XProperty =
            UIProperty.Register<EffectlessSub, double>("SubObsEffectlessX");

        public double X { set => SetValue(XProperty, value); }
    }

    private sealed class CountingObserver<T> : IValueObserver<T>
    {
        public int Count;

        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
            => Count++;
    }

    private sealed class CountingUntypedObserver : IUntypedValueObserver
    {
        public int Count;

        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => Count++;
    }

    private static PhaseShiftedBrush PhaseRamp()
        => new(new LinearGradientBrush(Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255),
                                       spread: GradientSpread.Repeat));

    /// <summary>Runs a layout pass and clears the render bit, so every invalidation is this test's own.</summary>
    private static T Settled<T>(T element) where T : UIElement
    {
        element.Measure(new Size(20, 5));
        element.RenderDirty = false;
        element.CompositeDirty = false;
        return element;
    }

    // ───────────────────────── the mechanism (red when disabled) ─────────────────────────

    [Fact]
    public void PhaseTick_RepaintsTheHost_WithoutTouchingLayout()
    {
        var host = new EffectHost();
        var brush = PhaseRamp();
        host.SetValue(EffectHost.RenderBrushProperty, brush);
        Settled(host);

        brush.Phase = 0.5; // the sub-change: no reference on the host moved

        Assert.True(host.RenderDirty);      // Phase:AffectsRender through Foreground-shaped slot → repaint
        Assert.True(host.IsMeasureValid);   // a render-level sub-change never escalates to measure
    }

    [Fact]
    public void PhaseTick_FiresNoValueChangedChannels()
    {
        var host = new EffectHost();
        var brush = PhaseRamp();
        var typed = new CountingObserver<IBrush?>();
        var untyped = new CountingUntypedObserver();
        host.AddObserver(EffectHost.RenderBrushProperty, typed);
        host.AddObserver((UIProperty)EffectHost.RenderBrushProperty, untyped);

        host.SetValue(EffectHost.RenderBrushProperty, brush);
        Settled(host);

        // The reference SET fired every channel once — snapshot, then tick.
        var callbacks = host.ChangedCallbackCount;
        var virtuals = host.VirtualChannelCount;
        var typedCount = typed.Count;
        var untypedCount = untyped.Count;

        brush.Phase = 0.25;

        Assert.True(host.RenderDirty); // the flag effects fired…
        Assert.Equal(callbacks, host.ChangedCallbackCount);   // …but no metadata Changed —
        Assert.Equal(virtuals, host.VirtualChannelCount);     // callbacks compare old/new REFERENCES,
        Assert.Equal(typedCount, typed.Count);                // and no reference changed
        Assert.Equal(untypedCount, untyped.Count);
    }

    [Fact]
    public void Icon_PhaseTick_DoesNotReenterEffectiveBrushDerivation()
    {
        // The in-tree example the no-callback rule protects: Icon derives EffectiveIconBrush inside
        // IconBrush's changed callback via a reference compare. A phase tick must not re-enter it —
        // no host reference changed, so neither IconBrush's channels nor the derived direct
        // property's dispatch may fire.
        var icon = new Icon();
        var brush = PhaseRamp();
        var iconBrushChannel = new CountingUntypedObserver();
        var effectiveChannel = new CountingUntypedObserver();
        icon.AddObserver((UIProperty)Icon.IconBrushProperty, iconBrushChannel);
        icon.AddObserver(Icon.EffectiveIconBrushProperty, effectiveChannel);

        icon.SetValue(Icon.IconBrushProperty, brush);
        var iconBrushDispatches = iconBrushChannel.Count;
        var effectiveDispatches = effectiveChannel.Count;

        brush.Phase = 0.75;

        Assert.Equal(iconBrushDispatches, iconBrushChannel.Count);
        Assert.Equal(effectiveDispatches, effectiveChannel.Count);
    }

    // ───────────────────────── lattice-aware clamping ─────────────────────────

    [Fact]
    public void RenderSubChange_ThroughAMeasureCeilingSlot_TransmitsAsRenderOnly()
    {
        // The ceiling is a CLOSURE, not a bitmask: AffectsMeasure implies re-arrange and re-render,
        // so a Measure-ceiling slot still transmits a render-level sub-change — as render, without
        // escalating it to measure.
        var host = new EffectHost();
        var brush = PhaseRamp();
        host.SetValue(EffectHost.MeasureBrushProperty, brush);
        Settled(host);

        brush.Phase = 0.5;

        Assert.True(host.RenderDirty);
        Assert.True(host.IsMeasureValid);
    }

    [Fact]
    public void MeasureSubChange_ThroughARenderCeilingSlot_ClampsToRender()
    {
        var host = new EffectHost();
        var sub = new MeasureDeclaringSub();
        host.SetValue(EffectHost.ObjectSlotProperty, sub);
        Settled(host);

        sub.X = 1.0; // declares AffectsMeasure — but the host slot's ceiling is render

        Assert.True(host.RenderDirty);
        Assert.True(host.IsMeasureValid); // clamped: the slot promises at most a repaint
    }

    [Fact]
    public void MeasureSubChange_ThroughAMeasureCeilingSlot_InvalidatesMeasure_NotRenderDirectly()
    {
        // Closure ∩ closure = {M, A, R}, REDUCED to its generator before dispatch: exactly what an
        // ordinary AffectsMeasure change does — InvalidateMeasure, with re-render left to layout
        // (which re-rasters only when the arranged size actually changes).
        var host = new EffectHost();
        var sub = new MeasureDeclaringSub();
        host.SetValue(EffectHost.MeasureObjectSlotProperty, sub);
        Settled(host);

        sub.X = 1.0;

        Assert.False(host.IsMeasureValid);
        Assert.False(host.RenderDirty);
    }

    [Fact]
    public void EffectlessSubChange_InvalidatesNothing()
    {
        var host = new EffectHost();
        var sub = new EffectlessSub();
        host.SetValue(EffectHost.ObjectSlotProperty, sub);
        Settled(host);

        sub.X = 1.0;

        Assert.False(host.RenderDirty);
        Assert.True(host.IsMeasureValid);
    }

    [Fact]
    public void ClosureArithmetic_ExpandAndReduce()
    {
        // Expand: the upward closure along Measure ⇒ Arrange ⇒ Render and ParentMeasure ⇒ ParentArrange.
        Assert.Equal(PropertyEffects.AffectsMeasure | PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender,
                     PropertyEffectsClosure.Expand(PropertyEffects.AffectsMeasure));
        Assert.Equal(PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender,
                     PropertyEffectsClosure.Expand(PropertyEffects.AffectsArrange));
        Assert.Equal(PropertyEffects.AffectsRender, PropertyEffectsClosure.Expand(PropertyEffects.AffectsRender));
        Assert.Equal(PropertyEffects.AffectsParentMeasure | PropertyEffects.AffectsParentArrange,
                     PropertyEffectsClosure.Expand(PropertyEffects.AffectsParentMeasure));

        // The behavior bits never travel the sub-object path; Composite is its own lane, outside
        // the chain in both directions.
        Assert.Equal(PropertyEffects.AffectsRender,
                     PropertyEffectsClosure.Expand(PropertyEffects.AffectsRender | PropertyEffects.Inherits));
        Assert.Equal(PropertyEffects.AffectsComposite, PropertyEffectsClosure.Expand(PropertyEffects.AffectsComposite));
        Assert.Equal(PropertyEffects.None,
                     PropertyEffectsClosure.Expand(PropertyEffects.AffectsRender) &
                     PropertyEffectsClosure.Expand(PropertyEffects.AffectsComposite));

        // Reduce: back to the strongest generator per chain — what the dispatch actually fires.
        Assert.Equal(PropertyEffects.AffectsMeasure,
                     PropertyEffectsClosure.Reduce(
                         PropertyEffects.AffectsMeasure | PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender));
        Assert.Equal(PropertyEffects.AffectsArrange,
                     PropertyEffectsClosure.Reduce(PropertyEffects.AffectsArrange | PropertyEffects.AffectsRender));
        Assert.Equal(PropertyEffects.AffectsRender, PropertyEffectsClosure.Reduce(PropertyEffects.AffectsRender));
        Assert.Equal(PropertyEffects.AffectsParentMeasure,
                     PropertyEffectsClosure.Reduce(
                         PropertyEffects.AffectsParentMeasure | PropertyEffects.AffectsParentArrange));
    }

    // ───────────────────────── chaining and inheritance ─────────────────────────

    [Fact]
    public void ChainedWrappers_ForwardThroughTheMiddleObject()
    {
        // outer wraps inner wraps gradient: the inner's Phase tick reaches the host through the
        // outer's own watch (the base OnSubObjectPropertyChanged forward), re-clamped per hop.
        var inner = PhaseRamp();
        var outer = new PhaseShiftedBrush(inner);
        var host = new EffectHost();
        host.SetValue(EffectHost.RenderBrushProperty, outer);
        Settled(host);

        inner.Phase = 0.5;

        Assert.True(host.RenderDirty);
    }

    [Fact]
    public void InheritedSlot_FansOutToNonShadowedDescendants()
    {
        // TextElement.Foreground (attached, inheriting, global AffectsRender): a phase tick on the
        // ancestor's brush repaints the ancestor AND every descendant painting that same brush —
        // while a descendant with its own contribution is shadowed and stays quiet.
        var parent = new EffectHost();
        var child = new EffectHost();
        var shadowed = new EffectHost();
        child.SetInheritanceParent(parent);
        shadowed.SetInheritanceParent(parent);

        var brush = PhaseRamp();
        shadowed.SetValue(TextBlock.ForegroundProperty, new PhaseShiftedBrush(new SolidColorBrush(Colors.Default)));
        parent.SetValue(TextBlock.ForegroundProperty, brush);
        Settled(parent);
        Settled(child);
        Settled(shadowed);

        brush.Phase = 0.5;

        Assert.True(parent.RenderDirty);
        Assert.True(child.RenderDirty);
        Assert.False(shadowed.RenderDirty);
    }

    [Fact]
    public void ElementValuedSlots_AreNotWatched()
    {
        // Elements route their own effects through the tree — watching one would double-invalidate.
        var host = new EffectHost();
        var elementValue = new EffectHost();
        host.SetValue(EffectHost.ObjectSlotProperty, elementValue);
        Settled(host);

        elementValue.SetValue(EffectHost.RenderBrushProperty, new SolidColorBrush(Colors.Default));

        Assert.False(host.RenderDirty);
    }

    // ───────────────────────── lifetime: replacement, defer, teardown ─────────────────────────

    [Fact]
    public void ReplacedBrush_IsDetached_ReplacementIsAttached()
    {
        var host = new EffectHost();
        var first = PhaseRamp();
        var second = PhaseRamp();
        host.SetValue(EffectHost.RenderBrushProperty, first);
        host.SetValue(EffectHost.RenderBrushProperty, second);
        Settled(host);

        first.Phase = 0.5;
        Assert.False(host.RenderDirty); // the replaced brush no longer repaints this host

        second.Phase = 0.5;
        Assert.True(host.RenderDirty);
    }

    [Fact]
    public void ReplacedBrush_IsNotRetained()
    {
        var host = new EffectHost();
        var weakReplaced = AttachAndReplace(host);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakReplaced.IsAlive); // no record, no watcher, no store slot roots it
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndReplace(EffectHost host)
    {
        var replaced = PhaseRamp();
        host.SetValue(EffectHost.RenderBrushProperty, replaced);
        host.SetValue(EffectHost.RenderBrushProperty, PhaseRamp());
        return new WeakReference(replaced);
    }

    [Fact]
    public void DeferScope_NetNoChange_KeepsTheOriginalWatch()
    {
        // A → B → A inside a defer scope coalesces to no change (first old == last new): the watch
        // must still be on A — the value the store actually holds.
        var host = new EffectHost();
        var original = PhaseRamp();
        var transient = PhaseRamp();
        host.SetValue(EffectHost.RenderBrushProperty, original);
        Settled(host);

        using (host.DeferNotifications())
        {
            host.SetValue(EffectHost.RenderBrushProperty, transient);
            host.SetValue(EffectHost.RenderBrushProperty, original);
        }

        transient.Phase = 0.5;
        Assert.False(host.RenderDirty);

        original.Phase = 0.5;
        Assert.True(host.RenderDirty);
    }

    [Fact]
    public void Teardown_DetachesTheWatch()
    {
        var host = new EffectHost();
        var brush = PhaseRamp();
        host.SetValue(EffectHost.RenderBrushProperty, brush);
        Settled(host);

        host.TearDown();
        brush.Phase = 0.5;

        Assert.False(host.RenderDirty); // the swept host no longer hears the brush
    }

    [Fact]
    public void TornDownHost_IsNotRetainedByALongLivedBrush()
    {
        // The leak direction that matters: a shared, long-lived animated brush must not pin every
        // element that ever painted it. After the teardown sweep the brush holds no watcher.
        var brush = PhaseRamp();
        var weakHost = AttachAndTearDown(brush);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakHost.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndTearDown(PhaseShiftedBrush brush)
    {
        var host = new EffectHost();
        host.SetValue(EffectHost.RenderBrushProperty, brush);
        host.TearDown();
        return new WeakReference(host);
    }
}
