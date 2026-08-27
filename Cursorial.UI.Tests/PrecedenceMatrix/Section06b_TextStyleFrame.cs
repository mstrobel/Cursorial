using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// TSF1 — the load-bearing arbitration for the "base whole text style" design (design-panel Approach C):
/// the base style drives the per-axis attached properties from a <see cref="ValueFrame"/> at
/// <see cref="BindingPriority.BaseTextStyle"/>, so an explicit per-axis <c>LocalValue</c> masks it and clearing
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

        // The explicit local SURVIVES the base — LocalValue beats the base frame (the differentiator).
        Assert.Equal(TextWeight.Bold, host.GetValue(TextElement.TextWeightProperty));
        Assert.Equal(BindingPriority.LocalValue, host.GetValueSource(TextElement.TextWeightProperty).Priority);

        // The untouched axis IS driven by the frame, at BaseTextStyle.
        Assert.True(host.GetValue(TextElement.InverseProperty));
        Assert.Equal(BindingPriority.BaseTextStyle, host.GetValueSource(TextElement.InverseProperty).Priority);

        // Clearing the explicit local re-promotes the frame's Faint — automatically, no manual re-sync.
        host.ClearValue(TextElement.TextWeightProperty);

        Assert.Equal(TextWeight.Faint, host.GetValue(TextElement.TextWeightProperty)); // ← the load-bearing assertion
        Assert.Equal(BindingPriority.BaseTextStyle, host.GetValueSource(TextElement.TextWeightProperty).Priority);
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

    [Fact]
    public void TSF2_PerAxisStyleSetter_BeatsTheBase()
    {
        // The whole reason for the BaseTextStyle tier: a per-axis STYLE setter wins over the base.
        var host = new Border();

        // A resting per-axis Style-lane setter: TextWeight = Bold.
        host.AddFrame(new TestValueFrame(10).With(TextElement.TextWeightProperty, TextWeight.Bold));

        // The base whole-style wants Faint + Inverse.
        host.SetValue(TextElement.BaseTextStyleProperty,
                      new BrushedStyle { Weight = TextWeight.Faint, Apply = TextAttributes.Inverse });

        // Style (100) beats BaseTextStyle (150) on the contested axis.
        Assert.Equal(TextWeight.Bold, host.GetValue(TextElement.TextWeightProperty));
        Assert.Equal(BindingPriority.Style, host.GetValueSource(TextElement.TextWeightProperty).Priority);

        // The axis the style leaves alone is still driven by the base.
        Assert.True(host.GetValue(TextElement.InverseProperty));
        Assert.Equal(BindingPriority.BaseTextStyle, host.GetValueSource(TextElement.InverseProperty).Priority);
    }

    [Fact]
    public void TSF3_BaseTextStyleLane_BeatsInherited_LosesToStyle()
    {
        // The tier's lane ordering on a generic INHERITING property (the text axes don't inherit, so
        // this pins BaseTextStyle above Inherited and below Style directly against the store).
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        Assert.Equal(BindingPriority.Inherited, leaf.GetValueSource(Pi).Priority);

        leaf.AddFrame(new TestValueFrame(10, priority: BindingPriority.BaseTextStyle).With(Pi, 6));
        Assert.Equal(6, leaf.GetValue(Pi));                                       // base beats inherited
        Assert.Equal(BindingPriority.BaseTextStyle, leaf.GetValueSource(Pi).Priority);

        leaf.AddFrame(new TestValueFrame(20).With(Pi, 7));                        // a Style-lane setter
        Assert.Equal(7, leaf.GetValue(Pi));                                       // Style beats base
        Assert.Equal(BindingPriority.Style, leaf.GetValueSource(Pi).Priority);
    }

    [Fact]
    public void TSF4_ValueDiagnostics_EnumerateTheBaseTextStyleRung()
    {
        var host = new Border();
        host.SetValue(TextElement.BaseTextStyleProperty, new BrushedStyle { Apply = TextAttributes.Inverse });

        // The value-stack enumeration includes the base rung.
        var stack = host.GetValueDiagnostics(TextElement.InverseProperty);
        Assert.Contains(stack, d => d.Priority == BindingPriority.BaseTextStyle && d.HasValue);

        // Under an explicit local, the stack shows the LocalValue rung AND still enumerates the base
        // rung underneath it (shadowed) — strongest-first, the base below local.
        host.SetValue(TextElement.InverseProperty, false);
        var shadowed = host.GetValueDiagnostics(TextElement.InverseProperty).ToList();
        Assert.Contains(shadowed, d => d.Priority == BindingPriority.LocalValue);
        Assert.Contains(shadowed, d => d.Priority == BindingPriority.BaseTextStyle);
        Assert.True(shadowed.FindIndex(d => d.Priority == BindingPriority.LocalValue) <
                    shadowed.FindIndex(d => d.Priority == BindingPriority.BaseTextStyle));
    }

    [Fact]
    public void TSF5_Explain_ReportsTheBaseTextStyleSource()
    {
        var host = new Border();
        host.SetValue(TextElement.BaseTextStyleProperty, new BrushedStyle { Apply = TextAttributes.Inverse });

        // A base-style frame is not a selector-matched rule, so Explain reports it on the generic
        // stronger-lane line ("<- BaseTextStyle") rather than a sort-key contributor breakdown.
        var explanation = StyleDiagnostics.Explain(host, TextElement.InverseProperty);
        Assert.Contains("BaseTextStyle", explanation);
    }
}
