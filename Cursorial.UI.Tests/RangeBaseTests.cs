using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI;

/// <summary>
/// The shared <see cref="RangeBase"/> contract extracted from ScrollBar/ProgressBar (#109): Value clamped into
/// <c>[Minimum, max(Minimum, Maximum)]</c>, a range change re-clamps Value, <see cref="RangeBase.FilledFraction"/>,
/// the change virtuals fire, and derived controls override the inherited defaults (the M201 per-type-freeze fix is
/// what makes those inherited-default overrides land).
/// </summary>
public class RangeBaseTests
{
    private sealed class TestRange : RangeBase
    {
        public readonly List<(double Old, double New)> ValueChanges = [];
        public int MinChanges;
        public int MaxChanges;

        protected override void OnValueChanged(double oldValue, double newValue) => ValueChanges.Add((oldValue, newValue));
        protected override void OnMinimumChanged(double oldValue, double newValue) => MinChanges++;
        protected override void OnMaximumChanged(double oldValue, double newValue) => MaxChanges++;
    }

    [Fact]
    public void Value_IsClampedIntoTheRange()
    {
        var r = new TestRange { Minimum = 0, Maximum = 10 };
        r.Value = 50;
        Assert.Equal(10, r.Value); // clamped to Maximum
        r.Value = -5;
        Assert.Equal(0, r.Value);  // clamped to Minimum
    }

    [Fact]
    public void RangeChange_ReclampsValue()
    {
        var r = new TestRange { Minimum = 0, Maximum = 100, Value = 80 };
        r.Maximum = 50;            // shrinking the ceiling below the value
        Assert.Equal(50, r.Value); // re-clamped down
        r.Minimum = 60;            // raising the floor above the value
        Assert.Equal(60, r.Value); // re-clamped up
    }

    [Fact]
    public void InvertedRange_ClampsValueToMinimum()
    {
        var r = new TestRange { Minimum = 10, Maximum = 5 }; // Max < Min ⇒ clamp window is [10, max(10,5)] = [10,10]
        r.Value = 7;
        Assert.Equal(10, r.Value);
        Assert.Equal(0, r.FilledFraction); // empty/inverted range ⇒ 0
    }

    [Fact]
    public void FilledFraction_IsTheNormalizedValue()
    {
        var r = new TestRange { Minimum = 0, Maximum = 200, Value = 50 };
        Assert.Equal(0.25, r.FilledFraction, 3);
    }

    [Fact]
    public void ChangeVirtuals_Fire()
    {
        var r = new TestRange { Minimum = 0, Maximum = 10 };
        r.Value = 4;
        r.Value = 7;
        Assert.Equal([(0, 4), (4, 7)], r.ValueChanges);
        Assert.True(r.MaxChanges >= 1);
    }

    [Fact] // ScrollBar/ProgressBar override the inherited RangeBase defaults (Max 1, LargeChange 1).
    public void DerivedControls_OverrideInheritedDefaults()
    {
        var bar = new ScrollBar();
        Assert.Equal(0, bar.Maximum);     // ScrollBar override (RangeBase default is 1)
        Assert.Equal(0, bar.LargeChange); // ScrollBar override ⇒ falls back to ViewportSize

        var progress = new ProgressBar();
        Assert.Equal(100, progress.Maximum); // ProgressBar override
        Assert.Equal(0, progress.Minimum);   // inherited RangeBase default
    }

    [Fact] // ScrollBar and ProgressBar both ARE RangeBase (the shared base).
    public void ScrollBarAndProgressBar_DeriveFromRangeBase()
    {
        Assert.IsAssignableFrom<RangeBase>(new ScrollBar());
        Assert.IsAssignableFrom<RangeBase>(new ProgressBar());
    }
}
