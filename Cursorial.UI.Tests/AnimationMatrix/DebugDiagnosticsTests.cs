#if DEBUG
using Cursorial.Animation;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// The DEBUG-only authoring diagnostics (design doc §9.6 leak tracker, §9.9 perpetual-on-AffectsMeasure).
// Gated to DEBUG — the diagnostics compile out in Release, so the whole file is conditional.
public sealed class DebugDiagnosticsTests
{
    [Fact] // §9.9: a perpetual animation on a layout-affecting property warns
    public void PerpetualOnLayoutProperty_Warns()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var warnings = CaptureWarnings(() =>
            a.BeginAnimation(Animatable.MProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop()));

        Assert.Contains(warnings, m => m.Contains("measure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // §9.9: a FINITE animation on a layout property does NOT warn (only perpetual ones)
    public void FiniteOnLayoutProperty_NoWarn()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var warnings = CaptureWarnings(() =>
            a.BeginAnimation(Animatable.MProperty, new DoubleAnimation(0.0, 10.0, Ms(100))));

        Assert.DoesNotContain(warnings, m => m.Contains("measure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // §9.9: a perpetual animation on a NON-layout property does NOT warn
    public void PerpetualOnNonLayoutProperty_NoWarn()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var warnings = CaptureWarnings(() =>
            a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop()));

        Assert.DoesNotContain(warnings, m => m.Contains("measure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // §9.6: an animation whose target never enters the tree warns after the leak threshold
    public void NeverAttachedTarget_WarnsAsLeak()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        host.ShowRoot(new StackPanel());
        host.RunUntilIdle();

        var orphan = new Animatable(); // constructed, never added to a tree
        var warnings = CaptureWarnings(() =>
        {
            orphan.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop());
            host.RunFrames(130); // past the 120-frame leak threshold
        });

        Assert.Contains(warnings, m => m.Contains("never-attached", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // §9.6, the sub-object lane: an animation on a never-watched sub-object (a brush no
           // element consumes) warns after the leak threshold, same as a never-attached element's
    public void NeverWatchedSubObjectTarget_WarnsAsLeak()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        host.ShowRoot(new StackPanel());
        host.RunUntilIdle();

        var orphan = new Cursorial.UI.Media.PhaseShiftedBrush(); // constructed, never consumed by any element's brush slot
        var warnings = CaptureWarnings(() =>
        {
            orphan.BeginAnimation(
                Cursorial.UI.Media.PhaseShiftedBrush.PhaseProperty,
                new DoubleAnimation(0.0, 1.0, Ms(100)).Loop());
            host.RunFrames(130); // past the 120-frame leak threshold
        });

        Assert.Contains(warnings, m => m.Contains("never-watched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // §9.6: a sub-object consumed by an attached element never trips the leak tracker
    public void WatchedSubObjectTarget_NoLeakWarning()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;

        var brush = new Cursorial.UI.Media.PhaseShiftedBrush();
        var text = new TextBlock("X") { Foreground = brush };
        var root = new StackPanel();
        root.Children.Add(text);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var warnings = CaptureWarnings(() =>
        {
            brush.BeginAnimation(
                Cursorial.UI.Media.PhaseShiftedBrush.PhaseProperty,
                new DoubleAnimation(0.0, 1.0, Ms(100)).Loop());
            host.RunFrames(150);
        });

        Assert.DoesNotContain(warnings, m => m.Contains("never-watched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // §9.6: an attached target never trips the leak tracker
    public void AttachedTarget_NoLeakWarning()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var warnings = CaptureWarnings(() =>
        {
            a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop());
            host.RunFrames(150);
        });

        Assert.DoesNotContain(warnings, m => m.Contains("never-attached", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> CaptureWarnings(Action action)
    {
        var warnings = new List<string>();
        void Handler(string m) => warnings.Add(m);
        AnimationDiagnostics.Warning += Handler;
        try
        {
            action();
        }
        finally
        {
            AnimationDiagnostics.Warning -= Handler;
        }
        return warnings;
    }
}
#endif
