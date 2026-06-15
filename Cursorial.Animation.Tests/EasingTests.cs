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

    // ───────────────────────────── A2 easings (Elastic / Bounce / CubicBezier) ─────────────────────────────

    public static IEnumerable<object[]> A2Easings()
    {
        yield return ["ElasticIn", Easings.ElasticIn];
        yield return ["ElasticOut", Easings.ElasticOut];
        yield return ["ElasticInOut", Easings.ElasticInOut];
        yield return ["BounceIn", Easings.BounceIn];
        yield return ["BounceOut", Easings.BounceOut];
        yield return ["BounceInOut", Easings.BounceInOut];
    }

    [Theory]
    [MemberData(nameof(A2Easings))]
    public void A2Easing_PinsEndpoints(string name, Easing easing)
    {
        Assert.True(Math.Abs(easing(0.0)) < 1e-12, $"{name}(0) should be 0");
        Assert.True(Math.Abs(easing(1.0) - 1.0) < 1e-12, $"{name}(1) should be 1");
    }

    [Fact]
    public void Elastic_Springs_PastTheUnitRange()
    {
        Assert.True(Easings.ElasticIn(0.25) < 0.0);  // accelerating spring dips below 0
        Assert.True(Easings.ElasticOut(0.2) > 1.0);  // decaying spring overshoots past 1
    }

    [Fact]
    public void Bounce_StaysWithinTheUnitRange()
    {
        // Bounce never overshoots — it settles with decaying bounces inside [0, 1].
        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
            Assert.InRange(Easings.BounceOut(t), 0.0, 1.0);
            Assert.InRange(Easings.BounceIn(t), 0.0, 1.0);
            Assert.InRange(Easings.BounceInOut(t), 0.0, 1.0);
        }
    }

    [Fact]
    public void CubicBezier_LinearControlPoints_IsIdentity()
    {
        var bezier = Easings.CubicBezier(0.0, 0.0, 1.0, 1.0); // P1=(0,0), P2=(1,1) ⇒ x==y for all u
        foreach (var x in new[] { 0.0, 0.2, 0.5, 0.7, 1.0 })
            Assert.True(Math.Abs(bezier(x) - x) < 1e-6, $"identity bezier({x}) = {bezier(x)}");
    }

    [Fact]
    public void CubicBezier_PinsEndpoints_AndIsMonotonic()
    {
        var ease = Easings.CubicBezier(0.25, 0.1, 0.25, 1.0); // the CSS "ease" curve
        Assert.Equal(0.0, ease(0.0));
        Assert.Equal(1.0, ease(1.0));

        var prev = ease(0.0);
        for (var x = 0.05; x <= 1.0; x += 0.05)
        {
            var y = ease(x);
            Assert.True(y >= prev - 1e-9, $"ease should be monotonic in x; dipped at {x}");
            prev = y;
        }

        Assert.True(ease(0.5) > 0.5); // front-loaded — half the time, more than half the progress
    }

    [Fact]
    public void CubicBezier_XControlPointsOutOfRange_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Easings.CubicBezier(1.5, 0.0, 0.5, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Easings.CubicBezier(0.5, 0.0, -0.2, 1.0));
    }

    [Theory]
    [InlineData("Linear")]
    [InlineData("CubicOut")]
    [InlineData("elasticinout")] // case-insensitive
    [InlineData("BounceOut")]
    public void TryParse_CatalogName_Resolves(string name)
    {
        Assert.True(Easings.TryParse(name, out var easing));
        Assert.NotNull(easing);
        Assert.True(Math.Abs(easing!(1.0) - 1.0) < 1e-9); // every catalog curve pins its endpoint
    }

    [Fact]
    public void TryParse_CubicBezierFunctional_Resolves()
    {
        Assert.True(Easings.TryParse("cubic-bezier(0, 0, 1, 1)", out var identity));
        Assert.True(Math.Abs(identity!(0.5) - 0.5) < 1e-6);

        Assert.True(Easings.TryParse("cubic-bezier(0.42,0,0.58,1)", out var easeInOut));
        Assert.Equal(0.0, easeInOut!(0.0));
        Assert.Equal(1.0, easeInOut(1.0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NotAnEasing")]
    [InlineData("cubic-bezier(1,2,3)")]       // wrong arity
    [InlineData("cubic-bezier(1.5,0,0.5,1)")] // x1 out of [0,1]
    [InlineData("cubic-bezier(a,b,c,d)")]     // non-numeric
    public void TryParse_Invalid_ReturnsFalse(string? text)
    {
        Assert.False(Easings.TryParse(text, out var easing));
        Assert.Null(easing);
    }
}
