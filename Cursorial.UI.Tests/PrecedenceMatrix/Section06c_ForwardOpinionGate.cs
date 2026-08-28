using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// TSF-FWD — the interaction between the per-axis text forwards (<see cref="TextElement.ForwardAllAxes"/>,
/// installed by a <c>ContentPresenter</c> during content realization) and a <c>BaseTextStyle</c> on the
/// realized part. A forward from a source with NO opinion on an axis must be TRANSPARENT (retract), so the
/// part's own <c>BaseTextStyle</c> shows through — it must not push the source's DEFAULT value at a stronger
/// lane and shadow the base.
/// </summary>
public class Section06c_ForwardOpinionGate
{
    [Fact]
    public void SourceWithNoOpinion_DoesNotShadow_PartBaseTextStyle_Weight()
    {
        var source = new Border(); // no opinion on any text axis
        var part = new Border();

        // The part's own base look: Bold.
        part.SetValue(TextElement.BaseTextStyleProperty, new BrushedStyle { Weight = TextWeight.Bold });
        Assert.Equal(TextWeight.Bold, part.GetValue(TextElement.TextWeightProperty)); // base drives it

        // The ContentPresenter-style forward from a source that has no TextWeight opinion.
        TextElement.ForwardAllAxes(part, source);

        // EXPECTED: the forward is transparent (source has no opinion), so the base's Bold survives.
        Assert.Equal(TextWeight.Bold, part.GetValue(TextElement.TextWeightProperty));
        Assert.Equal(BindingPriority.BaseTextStyle, part.GetValueSource(TextElement.TextWeightProperty).Priority);
    }

    [Fact]
    public void SourceWithNoOpinion_DoesNotShadow_PartBaseTextStyle_UnderlineBrush()
    {
        var source = new Border();
        var part = new Border();
        var brush = new SolidColorBrush(Color.FromRgb(10, 20, 30));

        part.SetValue(TextElement.BaseTextStyleProperty, new BrushedStyle { UnderlineColor = brush });
        Assert.Same(brush, part.GetValue(TextElement.UnderlineBrushProperty));

        TextElement.ForwardAllAxes(part, source);

        // The nullable-brush axis: the source's default is null. A forwarded null must not shadow the base brush.
        Assert.Same(brush, part.GetValue(TextElement.UnderlineBrushProperty));
    }

    [Fact]
    public void SourceWithRealOpinion_StillForwards_OverPartBaseTextStyle()
    {
        var source = new Border();
        var part = new Border();

        // The source has a REAL opinion (an explicit local Bold).
        source.SetValue(TextElement.TextWeightProperty, TextWeight.Bold);

        // The part's base look wants Faint.
        part.SetValue(TextElement.BaseTextStyleProperty, new BrushedStyle { Weight = TextWeight.Faint });

        TextElement.ForwardAllAxes(part, source);

        // The real control-cue forward wins over the part's base look (existing intent preserved).
        Assert.Equal(TextWeight.Bold, part.GetValue(TextElement.TextWeightProperty));
    }

    [Fact]
    public void ForwardRetractsAndRestores_AsSourceOpinionComesAndGoes()
    {
        var source = new Border();
        var part = new Border();

        part.SetValue(TextElement.BaseTextStyleProperty, new BrushedStyle { Weight = TextWeight.Faint });
        TextElement.ForwardAllAxes(part, source);

        // No source opinion yet → the base look (Faint) shows (the forward is retracted).
        Assert.Equal(TextWeight.Faint, part.GetValue(TextElement.TextWeightProperty));

        // The source GAINS an opinion → the forward re-produces and wins over the base.
        source.SetValue(TextElement.TextWeightProperty, TextWeight.Bold);
        Assert.Equal(TextWeight.Bold, part.GetValue(TextElement.TextWeightProperty));

        // The source clears → the forward retracts again and the base look restores automatically.
        source.ClearValue(TextElement.TextWeightProperty);
        Assert.Equal(TextWeight.Faint, part.GetValue(TextElement.TextWeightProperty));
    }
}
