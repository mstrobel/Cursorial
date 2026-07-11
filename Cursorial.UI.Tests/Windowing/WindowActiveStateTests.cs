// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Style = Cursorial.UI.Style; // disambiguate from the Cursorial.Output.Style SGR record

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7 — the <c>:active-window</c> interaction state (design doc §0.3; "S4 writes it"). <see cref="Window"/>'s
/// active/inactive flip is the <em>one</em> producer of <see cref="InteractionState.ActiveWindow"/>, so the
/// chrome can pen the active window's border differently (<c>Pens.Double</c> when <c>:active-window</c>). These
/// pin that <see cref="Window.SetActiveInternal"/> mirrors the bit (white-box) and that the matcher consumes it
/// through a real <c>Window:active-window</c> style rule (end-to-end).
/// </summary>
public sealed class WindowActiveStateTests
{
    private static (UIHeadlessHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    private static bool HasActiveWindowState(Window window)
        => (window.InteractionStateInternal & InteractionState.ActiveWindow) != 0;

    [Fact] // the active window carries InteractionState.ActiveWindow; the deactivated one loses it; handoff restores it
    public void SetActiveInternal_FlipsActiveWindowInteractionState()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var a = host.NewWindow(width: 20, height: 8);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(a.IsActive);
        Assert.True(HasActiveWindowState(a)); // the sole producer of :active-window fired

        var b = host.NewWindow(width: 20, height: 8);
        b.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(b.IsActive);
        Assert.True(HasActiveWindowState(b));
        Assert.False(a.IsActive);
        Assert.False(HasActiveWindowState(a)); // deactivation cleared the bit (no stale :active-window)

        b.Close();                       // close the top window → activation hands back to a
        Assert.True(host.RunUntilIdle());
        Assert.True(a.IsActive);
        Assert.True(HasActiveWindowState(a)); // the handoff re-lit the bit
    }

    [Fact] // a real Window:active-window style rule wins on the active window and reverts on deactivation (end-to-end)
    public void ActiveWindowPseudoClass_DrivesStyle()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var resting = new SolidColorBrush(Color.FromRgb(20, 20, 20));
        var active = new SolidColorBrush(Color.FromRgb(200, 200, 200));

        var baseStyle = new Style(Selectors.OfType<Window>());
        baseStyle.Setters.Add(new Setter(Control.BackgroundProperty, resting));
        var activeStyle = new Style(Selectors.OfType<Window>().PseudoClass("active-window"));
        activeStyle.Setters.Add(new Setter(Control.BackgroundProperty, active));
        host.Application.Styles.Add(baseStyle);
        host.Application.Styles.Add(activeStyle); // :active-window beats the unguarded base (one extra :pseudo)

        var a = host.NewWindow(width: 20, height: 8);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.Same(active, a.Background); // the active rule won

        var b = host.NewWindow(width: 20, height: 8);
        b.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.Same(active, b.Background);  // the newly-active window picks up the active rule
        Assert.Same(resting, a.Background); // …and the deactivated one reverts to the base rule
    }
}
