using Cursorial.Animation;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Media;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

using Color = Cursorial.Media.Color;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Sub-object detach-stop (design doc §9.6's sub-object lane): a STANDALONE animation begun
// directly against a non-element UIObject (a PhaseShiftedBrush's Phase — the marquee brush) must
// retire when the target's LAST tree-participating consumer detaches, exactly as an animation
// targeting the element itself would. Retirement follows TREE participation, not object liveness
// (see AnimationScheduler.OnSubObjectDetached). The storyboard lane was always safe (groups retire
// by scope, whatever their children target) — pinned here for contrast via the track Target form.
public sealed class SubObjectDetachStopTests
{
    private static PhaseShiftedBrush PhaseRamp()
        => new(new LinearGradientBrush(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255),
                                       spread: GradientSpread.Repeat));

    private static (UIHeadlessHost Host, StackPanel Root, TextBlock Text, PhaseShiftedBrush Brush) ShownConsumer()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var brush = PhaseRamp();
        var text = new TextBlock("ABCDEFGHIJ") { Foreground = brush };
        var root = new StackPanel();
        root.Children.Add(text);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, root, text, brush);
    }

    [Fact] // the #25 leak: the sole consumer's detach retires the brush animation silently
           // (previously the orphaned Phase advanced forever — 0.128 → 0.328 after abandonment —
           // and permanently pinned the idle gate)
    public void StandaloneOnBrush_SoleConsumerDetach_Retires()
    {
        var (host, root, text, brush) = ShownConsumer();
        using var _ = host;

        var handle = brush.BeginAnimation(PhaseShiftedBrush.PhaseProperty,
            new DoubleAnimation(0.0, 1.0, Ms(1000)).Loop());
        host.AdvanceTime(Ms(128));
        Assert.Equal(0.128, brush.Phase, 3); // animating while consumed

        root.Children.Remove(text); // the brush's only consumer leaves the tree

        Assert.Equal(AnimationState.Stopped, handle.State);  // retired on the detach walk, no Completed
        Assert.Equal(0.0, brush.Phase);                      // retracted; base resurfaced
        Assert.False(host.Scheduler().HasActiveAnimations);  // the idle gate is unpinned

        host.AdvanceTime(Ms(200));
        Assert.Equal(0.0, brush.Phase); // and STAYS still — no orphaned advance
    }

    [Fact] // the sharing pin: a brush consumed by TWO attached elements keeps animating when only ONE
           // detaches (retire-on-first would freeze a still-visible marquee); the LAST detach retires
    public void StandaloneOnBrush_SharedByTwoConsumers_SurvivesFirstDetach_RetiresOnLast()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;

        var brush = PhaseRamp();
        var a = new TextBlock("AAAA") { Foreground = brush };
        var b = new TextBlock("BBBB") { Foreground = brush };
        var root = new StackPanel();
        root.Children.Add(a);
        root.Children.Add(b);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var handle = brush.BeginAnimation(PhaseShiftedBrush.PhaseProperty,
            new DoubleAnimation(0.0, 1.0, Ms(1000)).Loop());
        host.AdvanceTime(Ms(100));

        root.Children.Remove(a); // first consumer detaches — b still paints this brush

        Assert.Equal(AnimationState.Running, handle.State);
        var afterFirstDetach = brush.Phase;
        host.AdvanceTime(Ms(100));
        Assert.True(brush.Phase > afterFirstDetach); // MUST keep animating: one live consumer remains

        root.Children.Remove(b); // the LAST consumer detaches — now the brush leaves the tree

        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0.0, brush.Phase);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // contrast (the maintainer's track Target form): the storyboard lane was ALREADY safe —
           // the group retires by SCOPE on detach, whatever its children target
    public void Storyboard_TrackTargetingBrush_ScopeDetach_Retires()
    {
        var (host, root, text, brush) = ShownConsumer();
        using var _ = host;

        var storyboard = new Storyboard
        {
            Children =
            {
                new DoubleTrack
                {
                    Target = brush,
                    TargetProperty = PhaseShiftedBrush.PhaseProperty,
                    From = 0.0,
                    To = 1.0,
                    Duration = Ms(1000),
                    Repeat = RepeatBehavior.Forever
                }
            }
        };

        storyboard.Begin(text);
        host.AdvanceTime(Ms(100));
        Assert.True(brush.Phase > 0.0);

        root.Children.Remove(text); // scope detach retires the whole group — the safe lane

        Assert.Equal(0.0, brush.Phase);                     // retracted; base resurfaced
        Assert.False(host.Scheduler().HasActiveAnimations); // idle gate unpinned
    }
}
