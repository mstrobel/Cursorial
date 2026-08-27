using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// TSF1 — the load-bearing arbitration for the "base whole text style" design (design-panel Approach C):
/// the base style drives the per-axis attached properties from a <see cref="ValueFrame"/> at
/// <see cref="BindingPriority.Style"/>, so an explicit per-axis <c>LocalValue</c> masks it and clearing
/// that local re-promotes the frame's contribution automatically — the two-sided arbitration
/// <c>SetCurrentValue</c> (M119 clobber) could not deliver.
/// </summary>
public class Section06b_TextStyleFrame
{
    [Fact]
    public void TSF1_BaseStyleFrame_YieldsToExplicitLocal_AndRePromotesOnClear()
    {
        var host = new Border();

        // An explicit per-axis local FIRST — the scenario SCV would have clobbered (M119).
        host.SetValue(TextElement.TextWeightProperty, TextWeight.Bold);

        // The base whole-style arrives: Faint weight + Inverse applied.
        var bundle = new BrushedStyle { Weight = TextWeight.Faint, Apply = TextAttributes.Inverse };
        host.SetValue(TextElement.BaseTextStyleProperty, bundle);

        // The explicit local SURVIVES the base — LocalValue beats the Style frame (the differentiator).
        Assert.Equal(TextWeight.Bold, host.GetValue(TextElement.TextWeightProperty));
        Assert.Equal(BindingPriority.LocalValue, host.GetValueSource(TextElement.TextWeightProperty).Priority);

        // The untouched axis IS driven by the frame, at Style.
        Assert.True(host.GetValue(TextElement.InverseProperty));
        Assert.Equal(BindingPriority.Style, host.GetValueSource(TextElement.InverseProperty).Priority);

        // Clearing the explicit local re-promotes the frame's Faint — automatically, no manual re-sync.
        host.ClearValue(TextElement.TextWeightProperty);

        Assert.Equal(TextWeight.Faint, host.GetValue(TextElement.TextWeightProperty)); // ← the load-bearing assertion
        Assert.Equal(BindingPriority.Style, host.GetValueSource(TextElement.TextWeightProperty).Priority);
    }

    [Fact]
    public void TSF1b_BaseStyleFrame_RetractsToIdentity_LeavingTheAxesNative()
    {
        var host = new Border();

        host.SetValue(TextElement.BaseTextStyleProperty,
                      new BrushedStyle { Weight = TextWeight.Bold, Apply = TextAttributes.Inverse });
        Assert.Equal(TextWeight.Bold, host.GetValue(TextElement.TextWeightProperty));
        Assert.True(host.GetValue(TextElement.InverseProperty));

        // Back to Identity retracts the frame — the axes fall back to their native defaults.
        host.SetValue(TextElement.BaseTextStyleProperty, BrushedStyle.Identity);
        Assert.Equal(TextWeight.Normal, host.GetValue(TextElement.TextWeightProperty));
        Assert.Equal(BindingPriority.Default, host.GetValueSource(TextElement.TextWeightProperty).Priority);
        Assert.False(host.GetValue(TextElement.InverseProperty));
    }
}
