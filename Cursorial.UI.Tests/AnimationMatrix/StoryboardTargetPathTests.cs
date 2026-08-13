using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Media;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

using Color = Cursorial.Media.Color;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Storyboard target PROPERTY PATHS (design doc §9.3, task #26): an AnimationTrack can target a
// property INSIDE a sub-object's value through a PropertyPath — the SAME grammar Cursorial's
// {Binding Path=...} uses (Cursorial.UI.Data.PropertyPath / BindingPath), NOT a second dialect.
// This makes an inline-declared sub-object (a PhaseShiftedBrush written straight into Foreground)
// animatable without promoting it to a resource. The path resolves ONCE at Begin, holding the
// terminal sub-object; the terminal segment is the animated StyledProperty<T>.
public sealed class StoryboardTargetPathTests
{
    private static PhaseShiftedBrush PhaseRamp()
        => new(new LinearGradientBrush(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255),
                                       spread: GradientSpread.Repeat));

    private static (UIHeadlessHost Host, StackPanel Root, TextBlock Text, PhaseShiftedBrush Brush) ShownConsumer()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        var brush = PhaseRamp();
        var text = new TextBlock("ABCDEFGHIJ") { Foreground = brush }; // the sub-object, declared INLINE (no resource)
        var root = new StackPanel();
        root.Children.Add(text);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, root, text, brush);
    }

    [Fact] // (1) the inline sub-object animates VIA THE PATH: a bare-identifier path "Foreground.Phase"
           // (the same grammar {Binding Path=Foreground.Phase} parses) walks the scope's Foreground to
           // the inline brush and advances its Phase — no Target/resource, no TargetProperty
    public void TargetPath_InlineSubObject_PhaseAdvances()
    {
        var (host, _, text, brush) = ShownConsumer();
        using var _ = host;

        Assert.Equal(0.0, brush.Phase); // baseline: nothing has driven it

        var storyboard = new Storyboard
        {
            Children =
            {
                new DoubleTrack
                {
                    TargetPath = "Foreground.Phase", // string form → PropertyPath (the binding grammar)
                    From = 0.0,
                    To = 1.0,
                    Duration = Ms(1000),
                    Repeat = RepeatBehavior.Forever
                }
            }
        };

        storyboard.Begin(text); // scope = text; base = scope; path walks Foreground → Phase

        host.AdvanceTime(Ms(250));

        Assert.Equal(0.25, brush.Phase, 3);      // the path resolved to the inline brush and drives its Phase
        Assert.Same(brush, text.Foreground);     // mutated IN PLACE — same reference, no brush promoted/allocated
    }

    [Fact] // (2) terminal-type mismatch fails at Seal (the compile-time-checked PropertyPath form bakes
           // the terminal UIProperty, so its type is known before any element): a double-typed terminal
           // under an Int32Track is rejected with a message naming the terminal property and its type
    public void TargetPath_TerminalTypeMismatch_ThrowsAtSeal()
    {
        var storyboard = new Storyboard
        {
            Children =
            {
                new Int32Track // T = int, but the terminal Phase is a StyledProperty<double>
                {
                    TargetPath = new PropertyPath(TextBlock.ForegroundProperty, PhaseShiftedBrush.PhaseProperty),
                    To = 1
                }
            }
        };

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 4) });
        using var _ = host;
        var text = new TextBlock("x") { Foreground = PhaseRamp() };
        host.ShowRoot(new StackPanel { Children = { text } });
        host.RunUntilIdle();

        var ex = Assert.Throws<InvalidOperationException>(() => storyboard.Begin(text)); // Begin seals first
        Assert.Contains("PhaseShiftedBrush.Phase", ex.Message); // names the terminal property…
        Assert.Contains("Double", ex.Message);                  // …and its actual value type
        Assert.Contains("match", ex.Message);

        // Mutation control: the SAME path under the matching DoubleTrack seals cleanly (the check keys on
        // the type mismatch, not on "a path is present").
        var matched = new Storyboard
        {
            Children =
            {
                new DoubleTrack
                {
                    TargetPath = new PropertyPath(TextBlock.ForegroundProperty, PhaseShiftedBrush.PhaseProperty),
                    To = 1.0,
                    Duration = Ms(500)
                }
            }
        };
        matched.Begin(text); // does not throw
    }

    [Fact] // (3) path retirement on scope detach: a path-targeted animation is a storyboard child, so the
           // group retires it by SCOPE when the scope element detaches (the always-safe lane), whatever the
           // path resolved to — exactly the Target-form contrast in SubObjectDetachStopTests, now via a path
    public void TargetPath_ScopeDetach_Retires()
    {
        var (host, root, text, brush) = ShownConsumer();
        using var _ = host;

        var storyboard = new Storyboard
        {
            Children =
            {
                new DoubleTrack
                {
                    TargetPath = new PropertyPath(TextBlock.ForegroundProperty, PhaseShiftedBrush.PhaseProperty),
                    From = 0.0,
                    To = 1.0,
                    Duration = Ms(1000),
                    Repeat = RepeatBehavior.Forever
                }
            }
        };

        storyboard.Begin(text);
        host.AdvanceTime(Ms(100));
        Assert.True(brush.Phase > 0.0);                     // animating while the scope is attached

        root.Children.Remove(text);                         // scope detach retires the whole group

        Assert.Equal(0.0, brush.Phase);                     // retracted; base resurfaced
        Assert.False(host.Scheduler().HasActiveAnimations); // idle gate unpinned (no orphaned advance)

        host.AdvanceTime(Ms(200));
        Assert.Equal(0.0, brush.Phase);                     // and STAYS still
    }

    [Fact] // (4) DEPTH > 1: a three-segment path descends THROUGH an intermediate sub-object hop
           // (Foreground → outer brush → its Brush → inner brush) to animate the INNER brush's Phase,
           // proving the walk reads each intermediate value to reach the next object — the outer brush's
           // own Phase is untouched, so the terminal really resolved on the inner object, not the outer
    public void TargetPath_MultiSegment_DescendsToInnerSubObject()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;

        var inner = PhaseRamp();
        var outer = new PhaseShiftedBrush(inner);           // outer.Brush = inner (a sub-object of a sub-object)
        var text = new TextBlock("ABCDEFGHIJ") { Foreground = outer };
        host.ShowRoot(new StackPanel { Children = { text } });
        host.RunUntilIdle();

        var storyboard = new Storyboard
        {
            Children =
            {
                new DoubleTrack
                {
                    // TextBlock.Foreground → PhaseShiftedBrush.Brush → PhaseShiftedBrush.Phase (the inner one)
                    TargetPath = new PropertyPath(
                        TextBlock.ForegroundProperty,
                        PhaseShiftedBrush.BrushProperty,
                        PhaseShiftedBrush.PhaseProperty),
                    From = 0.0,
                    To = 1.0,
                    Duration = Ms(1000),
                    Repeat = RepeatBehavior.Forever
                }
            }
        };

        storyboard.Begin(text);
        host.AdvanceTime(Ms(250));

        Assert.Equal(0.25, inner.Phase, 3); // the INNER brush's Phase advanced — the walk descended two hops
        Assert.Equal(0.0, outer.Phase);     // the OUTER brush's Phase is untouched — terminal resolved on inner
        Assert.Same(inner, outer.Brush);    // still the same inner reference (in-place)
    }
}
