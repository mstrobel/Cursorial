using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Media;

/// <summary>
/// The underline rules <see cref="BrushedStyle"/> shares with <see cref="PartialStyle"/>. They are
/// separate implementations of one algebra — the template cannot delegate, because <c>Composed</c> keeps
/// only the mask — so the rules are pinned on both sides rather than assumed to travel.
/// </summary>
public class BrushedStyleTests
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(200, 30, 40));

    /// <summary>
    /// A shapeless underline states the FLAG and nothing else, so an earlier shape stands. "No opinion on
    /// the shape" must not read as "no shape".
    /// </summary>
    [Fact]
    public void ABareUnderline_DoesNotErase_AnEarlierShape()
    {
        var composed = default(BrushedStyle).Underlining(UnderlineStyle.Curly).Underlining();

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
        var removeThenAdd = default(BrushedStyle).RemovingUnderline()
                                                       .Then(default(BrushedStyle).Underlining());

        var applied = removeThenAdd.Resolve(0, 0, new Rect(0, 0, 1, 1))
                                   .ApplyTo(default(CellStyle) with { UnderlineStyle = UnderlineStyle.Curly });

        Assert.True(applied.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(default, applied.UnderlineStyle);
    }

    /// <summary>A removal after a shape removes, and keeps no remnant to resurrect it.</summary>
    [Fact]
    public void ARemoval_AfterAShape_KeepsNoRemnant()
    {
        var composed = default(BrushedStyle).Underlining(UnderlineStyle.Curly).RemovingUnderline();

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
        var t = default(BrushedStyle).Underlining(UnderlineStyle.Dotted, Ink);

        Assert.Equal(UnderlineStyle.Dotted, t.UnderlineShape);
        Assert.Same(Ink, t.UnderlineColor);
        Assert.True(t.AppliedAttributes.HasFlag(TextAttributes.Underline));
    }

    // ---- XAML-authoring setters: write-only Apply/Remove/Toggle + init Weight/Posture ----
    // Each mirrors the corresponding fluent method over Identity; the boolean write surface rejects the
    // axis flags (Bold/Faint/Italic/Underline) exactly as PartialStyle does.

    [Fact]
    public void Apply_Setter_EquivalentToFluent()
    {
        var s = new BrushedStyle { Apply = TextAttributes.Inverse | TextAttributes.Strikethrough };
        Assert.Equal(TextAttributes.Inverse | TextAttributes.Strikethrough, s.AppliedAttributes);
        Assert.Equal(BrushedStyle.Identity.Applying(TextAttributes.Inverse | TextAttributes.Strikethrough), s);
    }

    [Fact]
    public void Remove_Setter_EquivalentToFluent()
    {
        var s = new BrushedStyle { Remove = TextAttributes.Blink };
        Assert.Equal(TextAttributes.Blink, s.RemovedAttributes);
        Assert.Equal(BrushedStyle.Identity.Removing(TextAttributes.Blink), s);
    }

    [Fact]
    public void Toggle_Setter_EquivalentToFluent()
    {
        var s = new BrushedStyle { Toggle = TextAttributes.Inverse };
        Assert.Equal(TextAttributes.Inverse, s.ToggledAttributes);
        Assert.Equal(BrushedStyle.Identity.Toggling(TextAttributes.Inverse), s);
    }

    [Theory]
    [InlineData(TextWeight.Bold)]
    [InlineData(TextWeight.Faint)]
    [InlineData(TextWeight.Normal)]
    public void Weight_Setter_RoundTrips_EquivalentToFluent(TextWeight w)
    {
        var s = new BrushedStyle { Weight = w };
        Assert.Equal(w, s.Weight);
        Assert.Equal(BrushedStyle.Identity.Weighing(w), s);
    }

    [Theory]
    [InlineData(TextStyle.Italic)]
    [InlineData(TextStyle.Normal)]
    public void Posture_Setter_RoundTrips_EquivalentToFluent(TextStyle p)
    {
        var s = new BrushedStyle { Posture = p };
        Assert.Equal(p, s.Posture);
        Assert.Equal(BrushedStyle.Identity.Posturing(p), s);
    }

    [Theory]
    [InlineData(TextAttributes.Bold)]
    [InlineData(TextAttributes.Faint)]
    [InlineData(TextAttributes.Italic)]
    [InlineData(TextAttributes.Underline)]
    public void AxisFlags_RejectedByTheWriteOnlyBooleanSetters(TextAttributes f)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushedStyle { Apply = f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushedStyle { Remove = f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushedStyle { Toggle = f });
    }

    [Fact]
    public void Setters_ComposeInOneInitializer_AlongsideBrushChannels()
    {
        var s = new BrushedStyle
                {
                    Foreground = Ink,
                    Apply      = TextAttributes.Inverse,
                    Remove     = TextAttributes.Blink,
                    Weight     = TextWeight.Bold,
                    Posture    = TextStyle.Italic,
                };

        Assert.Same(Ink, s.Foreground);
        Assert.Equal(TextWeight.Bold, s.Weight);
        Assert.Equal(TextStyle.Italic, s.Posture);
        Assert.True(s.AppliedAttributes.HasFlag(TextAttributes.Inverse));
        Assert.True(s.RemovedAttributes.HasFlag(TextAttributes.Blink));
    }
}
