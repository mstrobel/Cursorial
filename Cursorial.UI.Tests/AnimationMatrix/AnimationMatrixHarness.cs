using Cursorial.Animation;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.AnimationMatrix;

/// <summary>
/// Shared harness for the animation-matrix sections (design doc §9). A minimal animatable element with
/// two independent <see cref="double"/> properties plus a property-change hook (for the reentrancy rows),
/// driven through <see cref="UITestHost"/> on a deterministic fake clock.
/// </summary>
internal sealed class Animatable : UIElement
{
    public static readonly StyledProperty<double> VProperty = UIProperty.Register<Animatable, double>(nameof(V));
    public static readonly StyledProperty<double> WProperty = UIProperty.Register<Animatable, double>(nameof(W));

    public double V => GetValue(VProperty);
    public double W => GetValue(WProperty);

    /// <summary>Reentrancy hook (§7): invoked from V's change notification with the new value (N61/N63/N65).</summary>
    public Action<double>? VChanged;

    /// <summary>Sets V's local base value (for the From-snapshot rows — N105/N120).</summary>
    public void SetV(double value) => SetValue(VProperty, value);

    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);
        if (ReferenceEquals(args.Property, VProperty))
            VChanged?.Invoke(GetValue(VProperty));
    }
}

/// <summary>
/// Counts <see cref="IAnimation{T}.ValueAt"/> invocations — for the "sampled exactly once per Tick" rows
/// (§1) — while delegating the value to an inner animation.
/// </summary>
internal sealed class CountingAnimation(IAnimation<double> inner) : IAnimation<double>
{
    public int SampleCount { get; private set; }

    public TimeSpan Duration => inner.Duration;

    public double ValueAt(TimeSpan elapsed)
    {
        SampleCount++;
        return inner.ValueAt(elapsed);
    }
}

internal static class Anim
{
    public static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>A shown host with one <see cref="Animatable"/> in a <see cref="StackPanel"/> root, run to idle.</summary>
    public static (UITestHost Host, StackPanel Root, Animatable A) Shown()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        var a = new Animatable();
        var root = new StackPanel();
        root.Children.Add(a);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, root, a);
    }

    public static AnimationScheduler Scheduler(this UITestHost host) => host.Application.AnimationScheduler;
}
