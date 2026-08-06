using Cursorial.Animation;
using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

// Animation-matrix §9 (interpolator registry, N71–N78) + the signed MarginsInterpolator (N76, AD13).
// In the Drawing test assembly so the Cursorial.Drawing module initializer has registered its value types.
public class InterpolatorRegistryTests
{
    [Fact] // N71/N72: the Animation primitives are pre-seeded
    public void For_AnimationPrimitives_PreSeeded()
    {
        Assert.Same(DoubleInterpolator.Instance, Interpolator.For<double>());
        Assert.Same(Int32Interpolator.Instance, Interpolator.For<int>());
        Assert.Same(ColorInterpolator.Instance, Interpolator.For<Color>());
    }

    [Fact] // N73: the Drawing value types auto-register via the module initializer
    public void For_DrawingTypes_AutoRegistered()
    {
        Assert.Same(SizeInterpolator.Instance, Interpolator.For<Size>());
        Assert.Same(RectInterpolator.Instance, Interpolator.For<Rect>());
        Assert.Same(MarginsInterpolator.Instance, Interpolator.For<Margins>());
        Assert.NotNull(Interpolator.For<IBrush>());
        Assert.NotNull(Interpolator.For<CompositeParameters>());
    }

    [Fact] // N74: an unregistered type throws a helpful message
    public void For_UnknownType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Interpolator.For<UnregisteredValue>());
        Assert.Contains("No interpolator is registered", ex.Message);
        Assert.False(Interpolator.IsRegistered<UnregisteredValue>());
        Assert.Null(Interpolator.ForOrNull<UnregisteredValue>());
    }

    [Fact] // N75: a custom registration wins (a unique test-local type keeps the process-global registry clean)
    public void Register_Custom_Wins()
    {
        var custom = new RegisteredValueInterpolator();
        Interpolator.Register(custom);
        Assert.True(Interpolator.IsRegistered<RegisteredValue>());
        Assert.Same(custom, Interpolator.For<RegisteredValue>());
    }

    [Fact] // N76/AD13: MarginsInterpolator is per-side, rounded, and SIGNED (no zero-clamp)
    public void Margins_InterpolatesSigned_PerSide()
    {
        var mid = MarginsInterpolator.Instance.Interpolate(new Margins(2, -1, 0, 3), new Margins(6, 3, 0, -1), 0.5);
        Assert.Equal(new Margins(4, 1, 0, 1), mid);

        // A side that crosses zero stays negative — not clamped (the size family clamps; margins do not).
        var neg = MarginsInterpolator.Instance.Interpolate(new Margins(0, 0, 0, 0), new Margins(-4, -2, 0, 0), 0.5);
        Assert.Equal(-2, neg.Left);
        Assert.Equal(-1, neg.Top);
    }

    private readonly struct UnregisteredValue;

    private readonly struct RegisteredValue;

    private sealed class RegisteredValueInterpolator : IInterpolator<RegisteredValue>
    {
        public RegisteredValue Interpolate(RegisteredValue from, RegisteredValue to, double progress) => from;
    }
}
