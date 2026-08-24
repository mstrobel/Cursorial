using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// W3 <c>x:TypeArguments</c> end-to-end in the reflection lane (design doc
/// <c>xaml-conversion-routes.md</c> §1 W3 — XAML 2009 semantics: legal on ANY object element): the
/// element resolves CLOSED before attributes parse (the pre-scan), the definition resolves by exact
/// name then the backtick-arity form, arguments close recursively (intrinsics, nesting, the Cursorial
/// suffix extensions), members SUBSTITUTE (<c>Payload : T</c> → <c>double</c> feeds the route
/// machinery), and activation constructs the closed type. Error rows pin positioned diagnostics.
/// </summary>
public sealed class GenericHolder<T> : Control
{
    public T? Payload { get; set; }
    public List<T> Items { get; } = [];
}

public sealed class Section26_TypeArguments : LoaderTestBase
{
    [Fact] // XT1: the flagship — a closed generic element activates with SUBSTITUTED members
    public void XT1_ClosedElement_ActivatesWithSubstitutedMembers()
    {
        var holder = Load<GenericHolder<double>>(
            "<GenericHolder x:TypeArguments=\"x:Double\" Payload=\"0.5\"/>");

        Assert.Equal(0.5, holder.Payload); // Payload : T substituted to double — the ladder converted it
    }

    [Fact] // XT2: a framework-type argument from the default namespace
    public void XT2_FrameworkTypeArgument()
    {
        var holder = Load<GenericHolder<Border>>(
            "<GenericHolder x:TypeArguments=\"Border\"/>");

        Assert.IsType<GenericHolder<Border>>(holder);
    }

    [Fact] // XT3: NESTED closing — x:TypeArguments="scg:List(x:String)" (the System.Xaml parenthesized form)
    public void XT3_NestedClosing()
    {
        var holder = LoadRaw(
            "<GenericHolder xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:scg=\"using:System.Collections.Generic\" " +
            "x:TypeArguments=\"scg:List(x:String)\"/>");

        Assert.IsType<GenericHolder<List<string>>>(holder);
    }

    [Fact] // XT4 (Cursorial extension): the array suffix closes T = double[]
    public void XT4_ArraySuffixArgument()
    {
        var holder = Load("<GenericHolder x:TypeArguments=\"x:Double[]\"/>");
        Assert.IsType<GenericHolder<double[]>>(holder);
    }

    [Fact] // XT5 (Cursorial extension): the nullable suffix closes T = double?
    public void XT5_NullableSuffixArgument()
    {
        var holder = Load("<GenericHolder x:TypeArguments=\"x:Double?\"/>");
        Assert.IsType<GenericHolder<double?>>(holder);
    }

    [Fact] // XT6: implicit content fills the SUBSTITUTED collection member type-safely
    public void XT6_SubstitutedCollectionMember()
    {
        var holder = Load<GenericHolder<string>>(
            "<GenericHolder x:TypeArguments=\"x:String\">" +
              "<GenericHolder.Items><x:String>alpha</x:String><x:String>beta</x:String></GenericHolder.Items>" +
            "</GenericHolder>");

        Assert.Equal(["alpha", "beta"], holder.Items);
    }

    [Fact] // XT7: a malformed argument list is a positioned CUR1202 naming the grammar failure
    public void XT7_MalformedArguments_IsPositionedError()
    {
        var ex = ThrowsLoad("CUR1202", () => Load("<GenericHolder x:TypeArguments=\"scg:List(\"/>"));
        Assert.Contains("Malformed x:TypeArguments", ex.Message);
    }

    [Fact] // XT8: an unbound prefix in an argument is a positioned CUR2003
    public void XT8_UnboundPrefix_IsPositionedError()
        => ThrowsLoad("CUR2003", () => Load("<GenericHolder x:TypeArguments=\"nope:Widget\"/>"));

    [Fact] // XT9: an unresolvable closing (unknown argument type) is a positioned CUR2002 naming the form
    public void XT9_UnresolvableClosing_IsPositionedError()
    {
        var ex = ThrowsLoad("CUR2002", () => Load("<GenericHolder x:TypeArguments=\"x:NoSuchType\"/>"));
        Assert.Contains("Cannot close 'GenericHolder'", ex.Message);
    }

    [Fact] // XT10: x:TypeArguments on a NON-generic element is the same positioned failure (no definition)
    public void XT10_TypeArgumentsOnNonGeneric_IsPositionedError()
        => ThrowsLoad("CUR2002", () => Load("<Border x:TypeArguments=\"x:Double\"/>"));

    [Fact] // XT11 (the W3 flagship — the sweep critic's largest hole closed): KEYFRAMES are markup-
    // authorable — <Keyframe x:TypeArguments="x:Double"> closes, its substituted Value converts through
    // the ladder, the W1 Easing converter serves the segment easing, and the default-initialized
    // Keyframes list fills in place
    public void XT11_KeyframeMarkup_FillsTheTrack()
    {
        var sb = Load<Storyboard>(
            "<Storyboard>" +
              "<DoubleTrack TargetPath=\"Opacity\" Duration=\"0:0:0.4\">" +
                "<DoubleTrack.Keyframes>" +
                  "<Keyframe x:TypeArguments=\"x:Double\" Time=\"0:0:0.1\" Value=\"0.25\"/>" +
                  "<Keyframe x:TypeArguments=\"x:Double\" Time=\"0:0:0.4\" Value=\"1.0\" Easing=\"QuadOut\"/>" +
                "</DoubleTrack.Keyframes>" +
              "</DoubleTrack>" +
            "</Storyboard>");

        var track = Assert.IsType<DoubleTrack>(Assert.Single(sb.Children));
        Assert.Equal(2, track.Keyframes.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.1), track.Keyframes[0].Time);
        Assert.Equal(0.25, track.Keyframes[0].Value);
        Assert.Null(track.Keyframes[0].Easing);
        Assert.Equal(1.0, track.Keyframes[1].Value);
        Assert.Same(Cursorial.Animation.Easings.QuadOut, track.Keyframes[1].Easing);
    }
}
