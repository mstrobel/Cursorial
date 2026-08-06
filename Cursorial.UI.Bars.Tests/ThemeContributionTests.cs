using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Bars;

/// <summary>
/// The assembly theme-contribution tier (design doc §11.3a) end-to-end: the defect that motivated it — a
/// control theme that references its own brushes through <c>{DynamicResource}</c> — now resolves, because a
/// contribution is a real chain node (unlike the former per-type <c>Control.Theme</c> default). Also pins the
/// precedence (App overrides a contribution) and the subclass opt-in (<c>ControlThemeKey</c> override).
/// </summary>
/// <remarks>
/// These tests register a throwaway contribution into the process-global <see cref="ThemeContributions"/>
/// registry (the Bars module already registers globally by design, so this adds no new hazard). Keys are
/// test-unique (<c>BarsTest.*</c> / private test types) so the permanent registration never collides.
/// </remarks>
public sealed class ThemeContributionTests
{
    private const string InkKey = "BarsTest.Ink";
    private static readonly SolidColorBrush ContributedInk = new(Color.FromRgb(200, 50, 50));

    // A key that ALSO lives in CursorialTheme.BuiltIn (as an alias → TextBrush). Bars never render a Calendar, so
    // shadowing it process-wide is inert — it lets us assert the contribution wins over BuiltIn on a shared key.
    private static readonly SolidColorBrush BuiltInShadowInk = new(Color.FromRgb(7, 8, 9));

    // A test control whose theme is shipped ONLY via the contribution below — including a brush the theme
    // references through SetResource/{DynamicResource}, the exact thing the old Control.Theme default couldn't do.
    private class ContribControl : Control;

    // A subclass that opts into the base theme the idiomatic way (WPF DefaultStyleKey parity).
    private sealed class ContribSubControl : ContribControl
    {
        protected override object ControlThemeKey => typeof(ContribControl);
    }

    static ThemeContributionTests()
    {
        var contribution = new ResourceDictionary
        {
            [InkKey] = ContributedInk,
            [ThemeKeys.CalendarDayForeground] = BuiltInShadowInk, // a key also present in BuiltIn — contribution must win
            [typeof(ContribControl)] = new Style { Key = "BarsTest.ContribControl" }
                .SetResource(Control.BackgroundProperty, InkKey), // resolves ONLY through the contribution tier
        };
        ThemeContributions.Register(contribution);
    }

    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(16, 4), Capabilities = HeadlessCapabilities.KittyTruecolor });

    [Fact] // THE CLOSED GAP: a contributed control theme's DynamicResource reference to a contributed brush resolves.
    public void ContributedBrush_ReferencedByContributedControlTheme_Resolves()
    {
        using var host = NewHost();
        var control = new ContribControl();
        host.ShowRoot(new StackPanel { Children = { control } });
        host.RunUntilIdle();

        Assert.Same(ContributedInk, control.Background);
    }

    [Fact] // precedence: App.Resources (nearer the element) overrides the contribution on a shared key.
    public void App_OverridesContribution()
    {
        using var host = NewHost();
        var appInk = new SolidColorBrush(Color.FromRgb(10, 20, 30));
        host.Application.Resources[InkKey] = appInk;

        var control = new ContribControl();
        host.ShowRoot(new StackPanel { Children = { control } });
        host.RunUntilIdle();

        Assert.Same(appInk, control.Background); // app tier wins; the contribution is the fallback
    }

    [Fact] // a subclass opts into the base control theme by overriding ControlThemeKey (exact-key parity).
    public void Subclass_OptsIntoBaseTheme_ViaControlThemeKey()
    {
        using var host = NewHost();
        var control = new ContribSubControl();
        host.ShowRoot(new StackPanel { Children = { control } });
        host.RunUntilIdle();

        Assert.Same(ContributedInk, control.Background);
    }

    [Fact] // precedence, other half: a contribution OVERRIDES BuiltIn on a shared key (the runtime chain probes the
           // contribution tier BEFORE BuiltIn). Kills a reorder-the-probe mutation the earlier tests missed.
    public void Contribution_OverridesBuiltIn()
    {
        using var host = NewHost();
        var probe = new ContribControl();
        host.ShowRoot(new StackPanel { Children = { probe } });
        host.RunUntilIdle();

        // CalendarDayForeground is an alias→TextBrush in BuiltIn; the contribution supplies a DIRECT brush for the
        // same key and wins because the contribution tier sits above BuiltIn in the chain.
        Assert.True(probe.TryFindResource(ThemeKeys.CalendarDayForeground, out var value));
        Assert.Same(BuiltInShadowInk, value);
    }

    [Fact] // the tier is reachable through the {StaticResource} ambient scope too (not only the DynamicResource chain),
           // and overrides BuiltIn there as well — the two resolution paths must not diverge.
    public void Contribution_ResolvesVia_StaticResourceScope()
    {
        using var host = NewHost();
        var app = ResourceScopes.ForApplication();

        Assert.True(app.TryGetResource(InkKey, out var ink));
        Assert.Same(ContributedInk, ink);

        Assert.True(app.TryGetResource(ThemeKeys.CalendarDayForeground, out var shadow));
        Assert.Same(BuiltInShadowInk, shadow); // contribution beats BuiltIn on the StaticResource path too
    }

    [Fact] // a real bar control still themes after the migration off the per-control Control.Theme default.
    public void BarButton_StillThemed_ViaContributionTier()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Hi" };
        host.ShowRoot(new StackPanel { Children = { button } });
        host.RunUntilIdle();

        // The bar-button theme sets a Template; a themed BarButton has one applied (a bare, unthemed control does not).
        Assert.NotNull(button.Template);
    }
}
