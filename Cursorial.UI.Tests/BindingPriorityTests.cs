using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>
/// The priority ladder (design doc §2.2): ordering is the contract; the gapped wire values are the
/// recorded headroom decision for re-addable cut rungs (§2.9).
/// </summary>
public class BindingPriorityTests
{
    [Fact]
    public void Ladder_StrongToWeak_OrdersByNumericValue()
    {
        Assert.True(BindingPriority.Animation < BindingPriority.LocalValue);
        Assert.True(BindingPriority.LocalValue < BindingPriority.Style);
        Assert.True(BindingPriority.Style < BindingPriority.Inherited);
        Assert.True(BindingPriority.Inherited < BindingPriority.Default);
        Assert.True(BindingPriority.Default < BindingPriority.Unset);
    }

    [Fact]
    public void WireValues_ArePinnedWithAdditiveHeadroom()
    {
        // Recorded decision: gapped values so the cut WPF rungs (StyleTrigger, Template — design doc
        // §2.9) can be re-added between existing rungs without renumbering. Pinned here so any future
        // renumbering is a deliberate, reviewed act.
        Assert.Equal(-100, (int)BindingPriority.Animation);
        Assert.Equal(0, (int)BindingPriority.LocalValue);
        Assert.Equal(100, (int)BindingPriority.Style);
        Assert.Equal(200, (int)BindingPriority.Inherited);
        Assert.Equal(300, (int)BindingPriority.Default);
        Assert.Equal(int.MaxValue, (int)BindingPriority.Unset);
    }
}
