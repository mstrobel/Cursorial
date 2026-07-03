using Cursorial.UI;
using Cursorial.Tests.UI.PrecedenceMatrix;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI;

/// <summary>
/// Non-matrix unit tests for the <see cref="ValueSource"/> diagnostics annotations (design doc §2.1
/// grafts; PD23): <c>BasePriority</c>, <c>IsAnimated</c>, <c>IsCoerced</c> are informational and
/// excluded from equality, which compares exactly the matrix-pinned (Priority, IsCurrentValue) pair.
/// </summary>
public class ValueSourceDiagnosticsTests
{
    [Fact]
    public void Equality_ComparesOnlyTheMatrixPinnedPair()
    {
        var plain = new ValueSource(BindingPriority.Style, IsCurrentValue: false);
        var annotated = new ValueSource(BindingPriority.Style, IsCurrentValue: false)
        {
            BasePriority = BindingPriority.Style,
            IsCoerced = true
        };

        Assert.Equal(plain, annotated); // PD23: annotations do not participate
        Assert.Equal(plain.GetHashCode(), annotated.GetHashCode());
        Assert.NotEqual(plain, new ValueSource(BindingPriority.Style, IsCurrentValue: true));
    }

    [Fact]
    public void AnimatedSource_ReportsIsAnimated_AndTheBaseLane()
    {
        var host = new Host();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        var source = host.GetValueSource(P);
        Assert.True(source.IsAnimated);
        Assert.Equal(BindingPriority.Animation, source.Priority);
        Assert.Equal(BindingPriority.LocalValue, source.BasePriority); // the lane underneath

        handle.Dispose();
        source = host.GetValueSource(P);
        Assert.False(source.IsAnimated);
        Assert.Equal(BindingPriority.LocalValue, source.BasePriority);
    }

    [Fact]
    public void AnimatedOverInherited_BaseLaneIsInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        leaf.BeginAnimation(Pi).SetValue(9);

        var source = leaf.GetValueSource(Pi);
        Assert.Equal(BindingPriority.Animation, source.Priority);
        Assert.Equal(BindingPriority.Inherited, source.BasePriority); // the walk-up base
    }

    [Fact]
    public void CoercedWrite_ReportsIsCoerced_UncoercedDoesNot()
    {
        var host = new Host();

        host.SetValue(Pc, 250); // clamped to 100
        Assert.True(host.GetValueSource(Pc).IsCoerced);

        host.SetValue(Pc, 50); // inside the clamp range
        Assert.False(host.GetValueSource(Pc).IsCoerced);
    }

    [Fact]
    public void InheritedSource_ReportsAnnotationsFalse()
    {
        var (root, _, leaf) = Chain();
        var handle = root.BeginAnimation(Pi);
        handle.SetValue(9);

        var source = leaf.GetValueSource(Pi);
        Assert.Equal(BindingPriority.Inherited, source.Priority);
        Assert.False(source.IsAnimated); // PD23: ancestor detail stays at the ancestor
        Assert.False(source.IsCoerced);
        Assert.Equal(BindingPriority.Inherited, source.BasePriority);
    }
}
