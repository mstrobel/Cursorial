using Cursorial.Animation;

namespace Cursorial.Tests.Animation;

public class EasingTests
{
    // Sample points and oracle values generated from the easings.net reference formulas (Python).
    private static readonly double[] T = [0.0, 0.25, 0.5, 0.75, 1.0];

    // (name, easing, expected@T). Pinned to the reference; tolerance 1e-7 absorbs Pow-vs-multiply ULPs.
    public static IEnumerable<object[]> Catalog()
    {
        yield return ["QuadIn", Easings.QuadIn, new[] { 0.0, 0.0625, 0.25, 0.5625, 1.0 }];
        yield return ["QuadOut", Easings.QuadOut, new[] { 0.0, 0.4375, 0.75, 0.9375, 1.0 }];
        yield return ["QuadInOut", Easings.QuadInOut, new[] { 0.0, 0.125, 0.5, 0.875, 1.0 }];
        yield return ["CubicIn", Easings.CubicIn, new[] { 0.0, 0.015625, 0.125, 0.421875, 1.0 }];
        yield return ["CubicOut", Easings.CubicOut, new[] { 0.0, 0.578125, 0.875, 0.984375, 1.0 }];
        yield return ["CubicInOut", Easings.CubicInOut, new[] { 0.0, 0.0625, 0.5, 0.9375, 1.0 }];
        yield return ["QuartIn", Easings.QuartIn, new[] { 0.0, 0.00390625, 0.0625, 0.31640625, 1.0 }];
        yield return ["QuartOut", Easings.QuartOut, new[] { 0.0, 0.68359375, 0.9375, 0.99609375, 1.0 }];
        yield return ["QuartInOut", Easings.QuartInOut, new[] { 0.0, 0.03125, 0.5, 0.96875, 1.0 }];
        yield return ["SineIn", Easings.SineIn, new[] { 0.0, 0.0761204675, 0.2928932188, 0.6173165676, 1.0 }];
        yield return ["SineOut", Easings.SineOut, new[] { 0.0, 0.3826834324, 0.7071067812, 0.9238795325, 1.0 }];
        yield return ["SineInOut", Easings.SineInOut, new[] { 0.0, 0.1464466094, 0.5, 0.8535533906, 1.0 }];
        yield return ["ExpoIn", Easings.ExpoIn, new[] { 0.0, 0.0055242717, 0.03125, 0.1767766953, 1.0 }];
        yield return ["ExpoOut", Easings.ExpoOut, new[] { 0.0, 0.8232233047, 0.96875, 0.9944757283, 1.0 }];
        yield return ["ExpoInOut", Easings.ExpoInOut, new[] { 0.0, 0.015625, 0.5, 0.984375, 1.0 }];
        yield return ["BackIn", Easings.BackIn, new[] { 0.0, -0.0641365625, -0.0876975, 0.1825903125, 1.0 }];
        yield return ["BackOut", Easings.BackOut, new[] { 0.0, 0.8174096875, 1.0876975, 1.0641365625, 1.0 }];
        yield return ["BackInOut", Easings.BackInOut, new[] { 0.0, -0.0996818437, 0.5, 1.0996818437, 1.0 }];
    }

    [Theory]
    [MemberData(nameof(Catalog))]
    public void Easing_MatchesOracle(string name, Easing easing, double[] expected)
    {
        for (int i = 0; i < T.Length; i++)
            Assert.True(Math.Abs(easing(T[i]) - expected[i]) < 1e-7,
                $"{name}({T[i]}) = {easing(T[i])}, expected {expected[i]}");
    }

    [Theory]
    [MemberData(nameof(Catalog))]
    public void Easing_PinsEndpoints(string name, Easing easing, double[] expected)
    {
        _ = expected;
        // Every standard easing maps 0→0 and 1→1 exactly, even the overshooting ones.
        Assert.True(Math.Abs(easing(0.0)) < 1e-12, $"{name}(0) should be 0");
        Assert.True(Math.Abs(easing(1.0) - 1.0) < 1e-12, $"{name}(1) should be 1");
    }

    [Fact]
    public void Linear_IsIdentity()
    {
        Assert.Equal(0.0, Easings.Linear(0.0));
        Assert.Equal(0.25, Easings.Linear(0.25));
        Assert.Equal(1.0, Easings.Linear(1.0));
    }

    [Fact]
    public void BackEasings_OvershootTheUnitRange()
    {
        Assert.True(Easings.BackIn(0.25) < 0.0);     // anticipation dips below 0
        Assert.True(Easings.BackOut(0.75) > 1.0);    // settle overshoots past 1
    }
}
