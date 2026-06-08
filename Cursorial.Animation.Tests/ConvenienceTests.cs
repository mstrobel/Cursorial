using Cursorial.Animation;

namespace Cursorial.Tests.Animation;

public class ConvenienceTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void DoubleAnimation_IsAnAnimationOfDouble()
    {
        var a = new DoubleAnimation(0.0, 10.0, OneSecond);
        Assert.IsAssignableFrom<Animation<double>>(a);
        Assert.IsAssignableFrom<IAnimation<double>>(a);
        Assert.Equal(5.0, a.ValueAt(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void DoubleAnimation_HonorsEasing()
    {
        var a = new DoubleAnimation(0.0, 100.0, OneSecond, Easings.QuadIn);
        Assert.Equal(25.0, a.ValueAt(TimeSpan.FromSeconds(0.5)), 6);   // QuadIn(0.5) = 0.25
    }

    [Fact]
    public void Int32Animation_RoundsAndIsAnAnimationOfInt()
    {
        var a = new Int32Animation(0, 10, OneSecond);
        Assert.IsAssignableFrom<Animation<int>>(a);
        Assert.Equal(5, a.ValueAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(3, a.ValueAt(TimeSpan.FromSeconds(0.25)));   // 2.5 → 3 (away from zero)
    }

    [Fact]
    public void ConvenienceTypes_ComposeWithCombinators()
    {
        // A convenience animation flows through the generic combinators unchanged.
        IAnimation<double> once = new DoubleAnimation(0.0, 10.0, OneSecond).PingPong(1);
        Assert.Equal(TimeSpan.FromSeconds(2), once.Duration);
        Assert.Equal(10.0, once.ValueAt(OneSecond));            // peak at the turn
        Assert.Equal(0.0, once.ValueAt(TimeSpan.FromSeconds(2)));

        IAnimation<double> forever = new DoubleAnimation(0.0, 10.0, OneSecond).Loop();
        Assert.Equal(TimeSpan.MaxValue, forever.Duration);
        Assert.Equal(5.0, forever.ValueAt(TimeSpan.FromSeconds(1.5)));   // wraps perpetually
    }
}
