// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

using Style = Cursorial.UI.Style;
using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P9-W1 — the themed window chrome (C4, punch 36). The interim hardcoded <c>Template</c> default on the
/// <see cref="Window"/> type is gone; the chrome is now a <c>Theme.Window</c> control theme in
/// <c>CursorialTheme.BuiltIn</c>, resolved through the control-theme chain at the weakest
/// <c>StyleLayer.ControlTheme</c> — so an app <see cref="Window"/> style overrides it (the win the
/// b13fc2a Template-lane precedence fix unblocked). The borderless active/inactive look + hit-test roles are
/// covered by <c>WindowTests</c>/<c>WindowInputTests</c>/<c>WindowResizeMoveTests</c> (all green after the move).
/// </summary>
public sealed class WindowChromeTests
{
    private static (UIHeadlessHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    [Fact] // the window's chrome comes from the theme: a shown window gets a non-null Template + renders chrome
    public void Chrome_ResolvesFromTheme_NotAHardcodedDefault()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var window = host.NewWindow(
            title: "Hi",
            content: "Body",
            windowStartupLocation: WindowStartupLocation.Manual,
            left: 5, top: 3, width: 20, height: 8
        );
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        // The template resolved from the control-theme chain (no Template default on the Window type anymore).
        Assert.NotNull(window.Template);
        // The chrome rendered: the title band (row Top) is the accent fill, distinct from the body row below it.
        Assert.NotEqual(host.GetCell(7, 3).Style.Background, host.GetCell(7, 4).Style.Background);
    }

    [Fact] // a custom app theme WITHOUT a Theme.Window entry still gets chrome — BuiltIn is the chain backstop
    public void Chrome_CustomThemeWithoutWindowEntry_FallsBackToBuiltIn()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        // A custom app theme that does not theme Window at all. The control-theme chain must still resolve
        // Theme.Window from CursorialTheme.BuiltIn (the unconditional final backstop), so a custom-themed
        // app keeps window chrome — the regression guard for removing the hardcoded Template default.
        host.Application.Theme = new ResourceDictionary();
        Assert.True(host.RunUntilIdle());

        var window = host.NewWindow(
            title: "Hi",
            content: "Body",
            windowStartupLocation: WindowStartupLocation.Manual,
            left: 5, top: 3, width: 20, height: 8
        );
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.NotNull(window.Template);                                                       // resolved from BuiltIn
        Assert.NotEqual(host.GetCell(7, 3).Style.Background, host.GetCell(7, 4).Style.Background); // chrome rendered
    }

    [Fact] // an app Window style with a custom Template OVERRIDES the themed chrome (ControlTheme is the weakest layer)
    public void Chrome_AppTemplateStyle_OverridesThemedChrome()
    {
        var customColor = Color.FromRgb(123, 45, 67);
        var customTemplate = new ControlTemplate(_ => new Border { Background = new SolidColorBrush(customColor) });
        var (host, wm) = ShownRoot();
        using var _ = host;

        // A page-level Window style replaces the whole chrome — proving the BuiltIn chrome sits at the
        // (weakest) ControlTheme layer and an app style wins (CD30; the App layer beats ControlTheme).
        host.Application.Styles.Add(new Style(Selectors.OfType<Window>())
            .Set(Control.TemplateProperty, customTemplate));

        var window = host.NewWindow(

            title: "Hi",
            windowStartupLocation: WindowStartupLocation.Manual,
            left: 5, top: 3, width: 20, height: 8
        );
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        // The app style's Template won over the BuiltIn Theme.Window control theme (the C4 + precedence payoff).
        Assert.Same(customTemplate, window.Template);
        // And it actually applied: a body cell shows the custom fill colour, not the themed accent/panel.
        Assert.Equal(customColor, host.GetCell(7, 5).Style.Background);
    }

    [Fact] // W7 #1: re-templating a LIVE window unhooks the old chrome's active-look handlers — they must not fire after
    public void Chrome_ReTemplate_UnhooksOldActiveLookHandlers()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var window = host.NewWindow(
            windowStartupLocation: WindowStartupLocation.Manual,
            left: 2, top: 2, width: 20, height: 8,
            content: "Body"
        );
        window.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(window.IsActive);
        Assert.True(window.InteractionStateInternal.HasFlag(InteractionState.ActiveWindow));

        var oldTitleBar = window.GetTemplatePart<Border>("PART_TitleBar");
        Assert.NotNull(oldTitleBar);

        // Sanity: while active, the active look installed an AccentBrush DynamicResource on the title bar (its
        // resource provenance, even though the custom template's bar doesn't resolve the brush to a cell).
        Assert.Equal(ThemeKeys.WindowTitleBarActiveBackground,
                     ResourceDiagnostics.GetResourceKey(oldTitleBar!, Border.BackgroundProperty));

        // Re-template the live window (chrome-less): OnTemplateDetaching must unhook the active-look handlers,
        // then the discarded title bar tears down — evicting its resource producer (provenance → null).
        window.Template = new ControlTemplate(ctx =>
        {
            var presenter = new ContentPresenter();
            ctx.RegisterName("PART_ContentPresenter", presenter);
            return presenter;
        });
        Assert.True(host.RunUntilIdle());
        Assert.Null(ResourceDiagnostics.GetResourceKey(oldTitleBar!, Border.BackgroundProperty)); // torn down

        // Force the original window to DEACTIVATE: a second window steals activation. The discriminator —
        // a LEAKED Deactivated handler fires ApplyActiveLook(false), re-installing a SurfaceBrush DynamicResource
        // on the dead title bar (provenance becomes non-null); the unhook leaves it untouched (still null).
        var second = host.NewWindow(
            windowStartupLocation: WindowStartupLocation.Manual,
            left: 28, top: 2, width: 10, height: 5,
            content: "two"
        );
        second.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.False(window.IsActive); // deactivated by the second window's activation

        Assert.Null(ResourceDiagnostics.GetResourceKey(oldTitleBar!, Border.BackgroundProperty));
    }
}
