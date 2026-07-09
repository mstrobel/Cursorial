using Cursorial.UI;
using Cursorial.UI.Themes;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §15 — the library-contributed selector-style leg (design doc §11.3a/§11.8, amended). A
/// dictionary registered into <see cref="ThemeContributions"/> now has its top-level <c>Styles</c> gathered
/// by the <see cref="StyleEngine"/> as a Theme-layer leg: ABOVE the BuiltIn framework leg, BELOW the app-theme
/// leg and <c>UIApplication.Styles</c>. Each test saves/restores the process-global contribution registry.
/// </summary>
public class Section15_ContributedStyles
{
    // A contribution carrying only a Styles slot (the selector-style leg under test).
    private static ResourceDictionary ContributionWith(params Style[] styles)
    {
        var slot = new Styles();
        foreach (var style in styles)
            slot.Add(style);
        return new ResourceDictionary { Styles = slot };
    }

    // Register contributions for the duration of `body`, restoring the registry afterward (the registry is
    // process-global + append-only, so leaking a contribution would pollute every later test).
    private static void WithContributions(Action body, params ResourceDictionary[] contributions)
    {
        var saved = ThemeContributions.Snapshot;
        try
        {
            foreach (var contribution in contributions)
                ThemeContributions.Register(contribution);
            body();
        }
        finally
        {
            ThemeContributions.RestoreForTests(saved);
        }
    }

    [Fact] // a contributed selector style matches an element and applies
    public void S185_ContributedStyle_MatchesAndApplies()
    {
        using var tree = ShowTree(show: false);
        WithContributions(() =>
        {
            tree.Host.ShowRoot(tree.Root);

            Assert.Equal(7, tree.A.GetValue(Widget.P));
            Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        }, ContributionWith(R("Widget", (Widget.P, 7))));
    }

    [Fact] // App.Styles (App layer) always wins over a contributed style (Theme layer)
    public void S186_AppStyle_OverridesContributedStyle()
    {
        using var tree = ShowTree(show: false);
        WithContributions(() =>
        {
            tree.App.Styles.Add(R("Widget", (Widget.P, 9)));
            tree.Host.ShowRoot(tree.Root);

            Assert.Equal(9, tree.A.GetValue(Widget.P)); // App beats the contributed leg
        }, ContributionWith(R("Widget", (Widget.P, 7))));
    }

    [Fact] // the app-theme leg wins over a contributed style (contributions sort below app.Theme)
    public void S187_AppThemeStyle_OverridesContributedStyle()
    {
        using var tree = ShowTree(show: false);
        WithContributions(() =>
        {
            tree.App.Theme = new ResourceDictionary { Styles = new Styles { R("Widget", (Widget.P, 8)) } };
            tree.Host.ShowRoot(tree.Root);

            Assert.Equal(8, tree.A.GetValue(Widget.P)); // app.Theme beats the contributed leg
        }, ContributionWith(R("Widget", (Widget.P, 7))));
    }

    [Fact] // among contributions, the later-registered wins a same-target tie (resource-tier "last wins")
    public void S188_LaterContribution_WinsTheTie()
    {
        using var tree = ShowTree(show: false);
        WithContributions(() =>
        {
            tree.Host.ShowRoot(tree.Root);

            Assert.Equal(2, tree.A.GetValue(Widget.P)); // second registration's slot sorts above the first
        }, ContributionWith(R("Widget", (Widget.P, 1))), ContributionWith(R("Widget", (Widget.P, 2))));
    }

    [Fact] // a contribution shipping only resources (no Styles) is skipped — no crash, no match
    public void S189_ContributionWithoutStyles_IsSkipped()
    {
        using var tree = ShowTree(show: false);
        WithContributions(() =>
        {
            tree.Host.ShowRoot(tree.Root);

            Assert.Equal(0, tree.A.GetValue(Widget.P));
            Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
        }, new ResourceDictionary()); // no Styles slot
    }

    [Fact] // a contribution registered AFTER the tree is live re-arms the engine app-wide
    public void S190_LateRegistration_ReArms()
    {
        using var tree = ShowTree(); // shown, no contribution yet
        Assert.Equal(0, tree.A.GetValue(Widget.P));

        WithContributions(() =>
        {
            tree.Host.RunUntilIdle();
            Assert.Equal(7, tree.A.GetValue(Widget.P)); // OnThemeStylesInvalidated re-matched the live tree
        }, ContributionWith(R("Widget", (Widget.P, 7))));
    }
}
