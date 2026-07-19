using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

using Style = Cursorial.UI.Style;
using Cursorial.UI.Controls;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §16 — <see cref="Style.RequiresCapabilities"/>: the <c>@media</c>-model capability gate
/// (the caps-mechanism review's verdict B). A required flag set gates the rule against the engine's
/// EFFECTIVE capability mask — the same fold the <c>caps-*</c> class stamp derives its names from — at
/// gather time, for selector and element-addressed channels alike. Unlike the <c>.caps-*</c> ancestor
/// classes, the gate is independent of subject position: a control shown AS a surface root matches like
/// any nested one (the top-level-element gap). Each required flag counts one class-like specificity unit
/// (the dropped <c>.caps-*</c> compound's exact weight).
/// </summary>
public class Section16_CapabilityRequirements
{
    private static Style RCaps(Selector selector, StyleCapabilities requires, params (UIProperty Property, object? Value)[] setters)
    {
        var style = new Style(selector) { RequiresCapabilities = requires };
        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));
        return style;
    }

    [Fact] // the gate follows the EFFECTIVE tier across flips — off → on → off, riding the tier restamp's re-match
    public void Requires_GatesOnEffectiveTier_AcrossFlips()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(RCaps(Selectors.Is<Widget>(), StyleCapabilities.Ansi16, (Widget.P, 7)));
        Assert.True(tree.Host.RunUntilIdle());

        Assert.NotEqual(7, tree.A.GetValue(Widget.P)); // default host is truecolor — the Ansi16 rule is gated OFF

        tree.App.RequestedColorTier = ColorDepth.Ansi16;
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(7, tree.A.GetValue(Widget.P));    // tier flip → restamp → re-match → gated ON

        tree.App.RequestedColorTier = ColorDepth.Truecolor;
        Assert.True(tree.Host.RunUntilIdle());
        Assert.NotEqual(7, tree.A.GetValue(Widget.P)); // and back OFF
    }

    [Fact] // THE top-level-element gap, closed: a control shown AS the surface root matches a capability-
           // gated rule — no ancestor class required (a .caps-* descendant selector can never do this).
    public void Requires_MatchesSubjectShownAsRoot()
    {
        var host = UIHeadlessHost.Create(null);
        using var _ = host;

        var widget = new Widget();
        host.Application.Styles.Add(RCaps(Selectors.Is<Widget>(), StyleCapabilities.Truecolor, (Widget.P, 9)));
        host.ShowRoot(widget);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(9, widget.GetValue(Widget.P)); // matched at ROOT subject position
    }

    [Fact] // each required flag counts one class-like unit: a Requires rule beats an unguarded twin
           // regardless of order, and ties with the class-form twin (declaration order then decides) —
           // the migration's bit-identical-sort-order guarantee.
    public void Requires_SpecificityMatchesTheDroppedClassCompound()
    {
        using var tree = ShowTree();
        // Unguarded first, Requires second — and also Requires first, unguarded second: Requires wins both
        // ways (classLike 1 vs 0), proving the +1 is real and order-independent.
        tree.App.Styles.Add(R("Widget", (Widget.P, 1)));
        tree.App.Styles.Add(RCaps(Selectors.Is<Widget>(), StyleCapabilities.Truecolor, (Widget.P, 2)));
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(2, tree.A.GetValue(Widget.P));

        // The class-form twin ties with the Requires form; the LATER declaration wins the tie.
        using var tree2 = ShowTree();
        tree2.App.Styles.Add(RCaps(Selectors.Is<Widget>(), StyleCapabilities.Truecolor, (Widget.Q, 3)));
        tree2.App.Styles.Add(R(".caps-truecolor Widget", (Widget.Q, 4)));
        Assert.True(tree2.Host.RunUntilIdle());
        Assert.Equal(4, tree2.A.GetValue(Widget.Q)); // tie on specificity → later declaration

        using var tree3 = ShowTree();
        tree3.App.Styles.Add(R(".caps-truecolor Widget", (Widget.Q, 4)));
        tree3.App.Styles.Add(RCaps(Selectors.Is<Widget>(), StyleCapabilities.Truecolor, (Widget.Q, 3)));
        Assert.True(tree3.Host.RunUntilIdle());
        Assert.Equal(3, tree3.A.GetValue(Widget.Q)); // symmetric — later declaration again
    }

    [Fact] // requirements compose by UNION (AND) across BasedOn: the derived rule needs BOTH flags.
    public void Requires_CombinesAcrossBasedOn()
    {
        using var tree = ShowTree();
        var baseStyle = new Style(Selectors.Is<Widget>()) { RequiresCapabilities = StyleCapabilities.Motion };
        var derived = new Style(Selectors.Is<Widget>()) { BasedOn = baseStyle, RequiresCapabilities = StyleCapabilities.Truecolor };
        derived.Setters.Add(new Setter(Widget.P, 11));
        tree.App.Styles.Add(derived);
        Assert.True(tree.Host.RunUntilIdle());

        Assert.Equal(11, tree.A.GetValue(Widget.P)); // default host: truecolor + motion — both present

        tree.App.RequestedColorTier = ColorDepth.Ansi256;
        Assert.True(tree.Host.RunUntilIdle());
        Assert.NotEqual(11, tree.A.GetValue(Widget.P)); // Truecolor flag lost — the union gate fails
    }

    [Fact] // the element-addressed channels gate identically: an EXPLICIT style with an unmet requirement
           // contributes nothing; with a met requirement it applies.
    public void Requires_GatesExplicitStyles()
    {
        using var tree = ShowTree();
        var gatedOff = new Style { RequiresCapabilities = StyleCapabilities.NoColor };
        gatedOff.Setters.Add(new Setter(Widget.P, 21));
        tree.A.Style = gatedOff;
        Assert.True(tree.Host.RunUntilIdle());
        Assert.NotEqual(21, tree.A.GetValue(Widget.P)); // NoColor unmet on the truecolor host

        var gatedOn = new Style { RequiresCapabilities = StyleCapabilities.Truecolor };
        gatedOn.Setters.Add(new Setter(Widget.P, 22));
        tree.B.Style = gatedOn;
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(22, tree.B.GetValue(Widget.P));
    }

    [Fact] // app back-compat: an APP-AUTHORED .caps-* DESCENDANT rule now matches a control shown as the
           // root — the RootElementHost above it carries the caps classes too (the additive host stamp),
           // giving root-level content a class-bearing proper ancestor without the app changing anything.
    public void AppAuthoredClassRule_MatchesRootSubject_ViaHostStamp()
    {
        var host = UIHeadlessHost.Create(null);
        using var _ = host;

        var widget = new Widget();
        host.Application.Styles.Add(R(".caps-truecolor Widget", (Widget.P, 41)));
        host.ShowRoot(widget);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(41, widget.GetValue(Widget.P)); // the host ancestor carries caps-truecolor
    }

    [Fact] // LANE preservation: a capability requirement makes the rule CONDITIONAL (ClassLike > 0 —
           // each flag counts one class-like unit), so it arbitrates at StyleTrigger exactly like its old
           // .caps-* class form — ABOVE the Template lane. Load-bearing for the occlusion rules that write
           // Occludes onto /template/ Borders: they must keep piercing template-authored part values.
    public void Requires_KeepsStyleTriggerLane_LikeTheClassForm()
    {
        var requiresForm = new Style(Selectors.Is<Widget>()) { RequiresCapabilities = StyleCapabilities.NoColor };
        requiresForm.Setters.Add(new Setter(Widget.P, 1));
        requiresForm.Seal();
        Assert.True(requiresForm.CompiledRules[0].IsConditional); // → BindingPriority.StyleTrigger

        var restingForm = new Style(Selectors.Is<Widget>());
        restingForm.Setters.Add(new Setter(Widget.P, 2));
        restingForm.Seal();
        Assert.False(restingForm.CompiledRules[0].IsConditional); // a bare type rule stays at Style
    }

    [Fact] // two TIER flags under AND semantics can never both hold — the contradiction throws at Seal
           // (the SD17 fail-fast precedent), checked on the COMBINED mask so a BasedOn-composed conflict
           // is caught too. "ansi16 OR nocolor" is two styles, not a comma list.
    public void Requires_ConflictingTiers_ThrowAtSeal()
    {
        var style = new Style(Selectors.Is<Widget>())
        { RequiresCapabilities = StyleCapabilities.Ansi16 | StyleCapabilities.NoColor };
        style.Setters.Add(new Setter(Widget.P, 1));
        Assert.Throws<InvalidOperationException>(() => style.Seal());

        var baseStyle = new Style(Selectors.Is<Widget>()) { RequiresCapabilities = StyleCapabilities.Ansi16 };
        var derived = new Style(Selectors.Is<Widget>()) { BasedOn = baseStyle, RequiresCapabilities = StyleCapabilities.NoColor };
        derived.Setters.Add(new Setter(Widget.P, 2));
        Assert.Throws<InvalidOperationException>(() => derived.Seal()); // the BasedOn union conflicts
    }

    [Fact] // Unicode is currently UNCONDITIONAL (the SD14 recorded deferral — no negotiated glyph source):
           // Requires(Unicode) passes on every host today, and starts gating for real when the deferral lifts.
    public void Requires_Unicode_CurrentlyUnconditional()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(RCaps(Selectors.Is<Widget>(), StyleCapabilities.Unicode, (Widget.P, 31)));
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(31, tree.A.GetValue(Widget.P));
    }
}
