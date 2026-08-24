using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Transitions in markup (W2b — design doc <c>xaml-conversion-routes.md</c> CR5/CR6/CR8/CR10): the sealed
/// transition leaves are element-constructible (parameterless ctors, init members), <c>Property</c> tokens
/// resolve AT PARSE against the lexical scope (unqualified → the enclosing Style <c>TargetType</c> first,
/// else the nearest enclosing object element; dotted → the owner, xmlns-aware; target-less positions
/// require the owner-qualified form), and a lone assignable property-element child ASSIGNS a collection
/// member instead of item-filling it. Error rows pin positioned diagnostics — a token never reaches a
/// runtime setter as a raw string.
/// </summary>
public sealed class Section23_TransitionsMarkup : LoaderTestBase
{
    [Fact] // XB1: the flagship — a transition child under the attached collection, unqualified token
    // resolving against the ENCLOSING ELEMENT (Border)
    public void XB1_TransitionChild_UnqualifiedToken_ResolvesAgainstHost()
    {
        var border = Load<Border>(
            "<Border>" +
              "<Transition.Transitions>" +
                "<DoubleTransition Property=\"Opacity\" Duration=\"0:0:0.1\"/>" +
              "</Transition.Transitions>" +
            "</Border>");

        var transitions = border.GetValue(Transition.TransitionsProperty);
        Assert.NotNull(transitions);
        var transition = Assert.IsType<DoubleTransition>(Assert.Single(transitions!));
        Assert.Same(UIElement.OpacityProperty, transition.Property); // the parse-resolved identity
        Assert.Equal(TimeSpan.FromSeconds(0.1), transition.Duration);
    }

    [Fact] // XB2: the owner-qualified form works in a TARGET-LESS position (a standalone resource)
    public void XB2_OwnerQualifiedToken_InStandaloneResource()
    {
        var panel = Load<StackPanel>(
            "<StackPanel>" +
              "<StackPanel.Resources>" +
                "<TransitionCollection x:Key=\"fades\">" +
                  "<DoubleTransition Property=\"UIElement.Opacity\" Duration=\"0:0:0.2\"/>" +
                "</TransitionCollection>" +
              "</StackPanel.Resources>" +
            "</StackPanel>");

        var fades = Assert.IsType<TransitionCollection>(panel.Resources!["fades"]);
        Assert.Same(UIElement.OpacityProperty, Assert.IsType<DoubleTransition>(Assert.Single(fades)).Property);
    }

    [Fact] // XB3 (CR8): a lone ASSIGNABLE property-element child assigns the collection member —
    // pre-W2b the parser always routed it to item-fill and the load failed
    public void XB3_ExplicitCollectionChild_Assigns()
    {
        var border = Load<Border>(
            "<Border><Transition.Transitions><TransitionCollection/></Transition.Transitions></Border>");

        var transitions = border.GetValue(Transition.TransitionsProperty);
        Assert.IsType<TransitionCollection>(transitions);
        Assert.Empty(transitions!);
    }

    [Fact] // XB4: the explicit collection carries its own children through the assignment
    public void XB4_ExplicitCollectionChild_WithTransitions()
    {
        var border = Load<Border>(
            "<Border>" +
              "<Transition.Transitions>" +
                "<TransitionCollection>" +
                  "<DoubleTransition Property=\"Opacity\"/>" +
                "</TransitionCollection>" +
              "</Transition.Transitions>" +
            "</Border>");

        var transitions = border.GetValue(Transition.TransitionsProperty);
        Assert.NotNull(transitions);
        Assert.Same(UIElement.OpacityProperty, Assert.IsType<DoubleTransition>(Assert.Single(transitions!)).Property);
    }

    [Fact] // XB5: an unqualified token whose nearest enclosing element cannot resolve it is a positioned
    // member-not-found naming THAT element (deterministic ambient — a Storyboard resource's tracks
    // owner-qualify or use TargetPath)
    public void XB5_UnqualifiedToken_UnresolvableOnAmbient_IsPositionedError()
        => ThrowsLoad("CUR2102", () => Load(
            "<Storyboard><DoubleTrack TargetProperty=\"Opacity\" To=\"1\"/></Storyboard>"));

    [Fact] // XB5b: the same track resolves with the owner-qualified form — the P2a papercut is closed
    public void XB5b_TrackTargetProperty_OwnerQualified_Resolves()
    {
        var sb = Load<Storyboard>(
            "<Storyboard><DoubleTrack TargetProperty=\"UIElement.Opacity\" To=\"1\"/></Storyboard>");

        Assert.Same(UIElement.OpacityProperty, Assert.IsType<DoubleTrack>(Assert.Single(sb.Children)).TargetProperty);
    }

    [Fact] // XB6: the Style-setter form — unqualified tokens resolve against the Style's TargetType
    // (the Window-theme-shaped authoring: style-provided transitions, lexical scope)
    public void XB6_StyleSetterTransitions_TargetTypeAmbient()
    {
        var style = Load<Style>(
            "<Style TargetType=\"Border\">" +
              "<Setter Property=\"Transition.Transitions\">" +
                "<TransitionCollection>" +
                  "<DoubleTransition Property=\"Opacity\" Duration=\"0:0:0.3\"/>" +
                "</TransitionCollection>" +
              "</Setter>" +
            "</Style>");

        var setter = Assert.Single(style.Setters);
        var collection = Assert.IsType<TransitionCollection>(setter.Value);
        Assert.Same(UIElement.OpacityProperty, Assert.IsType<DoubleTransition>(Assert.Single(collection)).Property);
    }

    [Fact] // XB7: a dotted token with an UNRESOLVABLE owner is a positioned error naming the owner
    public void XB7_DottedToken_UnknownOwner_IsPositionedError()
        => ThrowsLoad("CUR2002", () => Load(
            "<Border><Transition.Transitions><DoubleTransition Property=\"NoSuchType.Opacity\"/></Transition.Transitions></Border>"));

    [Fact] // XB8: a resolvable owner with an unknown member errors against the OWNER (not the ambient)
    public void XB8_DottedToken_UnknownMember_IsPositionedError()
        => ThrowsLoad("CUR2102", () => Load(
            "<Border><Transition.Transitions><DoubleTransition Property=\"UIElement.NoSuchProperty\"/></Transition.Transitions></Border>"));

    [Fact] // XB9: the full circle — markup-authored transitions ARM and RUN on a live element (the fill
    // window stays mutable through load, the attach edge seals + subscribes, a style flip ignites)
    public void XB9_MarkupTransitions_RunLive()
    {
        var border = Load<Border>(
            "<Border>" +
              "<Transition.Transitions>" +
                "<DoubleTransition Property=\"Opacity\" Duration=\"0:0:0.1\"/>" +
              "</Transition.Transitions>" +
            "</Border>");

        using var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(40, 10) });
        var root = new StackPanel();
        root.Children.Add(border);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.True(border.GetValue(Transition.TransitionsProperty)!.IsSealed); // armed at attach

        host.Application.Styles.Add(new Style(".dim").Set(UIElement.OpacityProperty, 0.25));
        border.Classes.Add("dim");

        Assert.Equal(1.0, border.Opacity);      // ignited FROM the old base — not snapped
        host.AdvanceTime(TimeSpan.FromMilliseconds(200));
        host.RunUntilIdle();
        Assert.Equal(0.25, border.Opacity);     // settled at the styled base
    }

    // ── Audit rows (XB10–XB13) ───────────────────────────────────────────────────────────────────

    [Fact] // XB10 (audit): a Style's TargetType must NOT leak into its template's parts — the deferred
    // boundary shadows the style-target fallback and the nearest PART is the ambient
    public void XB10_TemplateBody_PartWins_NotStyleTargetType()
    {
        var style = Load<Style>(
            "<Style TargetType=\"Button\">" +
              "<Setter Property=\"Template\">" +
                "<ControlTemplate>" +
                  "<ProgressBar>" +
                    "<Transition.Transitions>" +
                      "<DoubleTransition Property=\"IndeterminatePhase\" Duration=\"0:0:0.1\"/>" +
                    "</Transition.Transitions>" +
                  "</ProgressBar>" +
                "</ControlTemplate>" +
              "</Setter>" +
            "</Style>");

        // Pre-fix this threw CUR2102 "No member 'IndeterminatePhase' on 'Button'". The deferred slice
        // builds per instantiation — instantiate it and assert the token bound the PART's property.
        var template = Assert.IsType<Cursorial.UI.Controls.ControlTemplate>(Assert.Single(style.Setters).Value);
        var part = Assert.IsType<Cursorial.UI.Controls.ProgressBar>(
            template.Content!.Build(new Cursorial.UI.Controls.TemplateBuildContext(new Button(), new NameScopeDictionary())));
        var transition = Assert.IsType<DoubleTransition>(
            Assert.Single(part.GetValue(Transition.TransitionsProperty)!));
        Assert.Same(Cursorial.UI.Controls.ProgressBar.IndeterminatePhaseProperty, transition.Property);
    }

    [Fact] // XB11 (audit): a SELECTOR-only style's setter value has no lexical target — the designed
    // CUR2113 guidance (owner-qualify), never "No member on Setter"
    public void XB11_SelectorOnlyStyle_UnqualifiedToken_IsCUR2113()
    {
        var ex = ThrowsLoad("CUR2113", () => Load(
            "<Style Selector=\"Border.card\">" +
              "<Setter Property=\"Transition.Transitions\">" +
                "<TransitionCollection><DoubleTransition Property=\"Opacity\"/></TransitionCollection>" +
              "</Setter>" +
            "</Style>"));

        Assert.Contains("UIElement.Opacity", ex.Message); // the guidance names the fix
    }

    [Fact] // XB12 (audit): the resource-dictionary boundary is OPAQUE to the ambient walk — a keyed
    // entry parses host-independently (resolution against the RD host would make a shared resource's
    // meaning depend on where the dictionary happens to sit)
    public void XB12_ResourceEntry_UnqualifiedToken_IsCUR2113()
        => ThrowsLoad("CUR2113", () => Load(
            "<StackPanel>" +
              "<StackPanel.Resources>" +
                "<TransitionCollection x:Key=\"fades\"><DoubleTransition Property=\"Opacity\"/></TransitionCollection>" +
              "</StackPanel.Resources>" +
            "</StackPanel>"));

    [Fact] // XB13 (audit): a single-child IList<T>-only self-list (Styles implements ONLY the generic
    // interface) loads — the reflection provider's isSelfList now mirrors the Roslyn twin
    public void XB13_GenericOnlySelfList_SingleChild_Loads()
    {
        var panel = Load<StackPanel>(
            "<StackPanel>" +
              "<StackPanel.Resources>" +
                "<Styles x:Key=\"set\"><Style TargetType=\"Border\"/></Styles>" +
              "</StackPanel.Resources>" +
            "</StackPanel>");

        var set = Assert.IsType<Styles>(panel.Resources!["set"]);
        Assert.Single(set);
    }
}
