using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Media;

/// <summary>
/// The underline rules <see cref="StyleDeltaTemplate"/> shares with <see cref="PartialStyle"/>. They are
/// separate implementations of one algebra — the template cannot delegate, because <c>Composed</c> keeps
/// only the mask — so the rules are pinned on both sides rather than assumed to travel.
/// </summary>
public class StyleDeltaTemplateTests
{
    /// <summary>A brush is all this file needs of one — <c>Cursorial.Drawing</c> sits above this assembly.</summary>
    private sealed class UniformBrush(Color color) : IBrush
    {
        public bool IsUniform => true;

        public Color ColorAt(int column, int row, Rect bounds) => color;
    }

    private static readonly IBrush Ink = new UniformBrush(Color.FromRgb(200, 30, 40));

    /// <summary>
    /// A shapeless underline states the FLAG and nothing else, so an earlier shape stands. "No opinion on
    /// the shape" must not read as "no shape".
    /// </summary>
    [Fact]
    public void ABareUnderline_DoesNotErase_AnEarlierShape()
    {
        var composed = default(StyleDeltaTemplate).Underlining(UnderlineStyle.Curly).Underlining();

        Assert.Equal(UnderlineStyle.Curly, composed.UnderlineShape);
    }

    /// <summary>
    /// A removal resets the shape, so a shapeless add AFTER one inherits the reset value rather than the
    /// base's. Composing must agree with applying in order; falling back unconditionally would let a base
    /// shape survive an intermediate removal.
    /// </summary>
    [Fact]
    public void ABareUnderline_AfterARemoval_DoesNotResurrectTheBaseShape()
    {
        var removeThenAdd = default(StyleDeltaTemplate).RemovingUnderline()
                                                       .Then(default(StyleDeltaTemplate).Underlining());

        var applied = removeThenAdd.Resolve(0, 0, new Rect(0, 0, 1, 1))
                                   .ApplyTo(default(CellStyle) with { UnderlineStyle = UnderlineStyle.Curly });

        Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(default, applied.UnderlineStyle);
    }

    /// <summary>A removal after a shape removes, and keeps no remnant to resurrect it.</summary>
    [Fact]
    public void ARemoval_AfterAShape_KeepsNoRemnant()
    {
        var composed = default(StyleDeltaTemplate).Underlining(UnderlineStyle.Curly).RemovingUnderline();

        Assert.Null(composed.UnderlineShape);
        Assert.True(composed.RemovedAttributes.HasFlag(TextAttributes.Underline));
    }

    /// <summary>
    /// <c>Underlining</c> has to carry its arguments itself, since <c>Composed</c> keeps only the mask —
    /// the one place the template cannot borrow <see cref="PartialStyle"/>'s implementation.
    /// </summary>
    [Fact]
    public void Underlining_CarriesBothItsShapeAndItsColour()
    {
        var t = default(StyleDeltaTemplate).Underlining(UnderlineStyle.Dotted, Ink);

        Assert.Equal(UnderlineStyle.Dotted, t.UnderlineShape);
        Assert.Same(Ink, t.UnderlineColor);
        Assert.True(t.AppliedAttributes.HasFlag(TextAttributes.Underline));
    }
}
