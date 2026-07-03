// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// S4 per-window focus activation: the <see cref="WindowManager"/> drives <c>FocusManager.OnWindowActivated</c>
/// / <c>OnWindowDeactivated</c> + <c>AccessKeyManager.OnWindowActivated</c> as the active window changes (via
/// <see cref="WindowManager.ActiveWindowChanged"/>, wired in <c>UIApplication.ComposeSystems</c>). Before this
/// wiring a freshly-activated window sat with NO focus — its content never auto-focused and Enter never reached
/// its <see cref="UIControls.Button.IsDefault"/> button (the surface-root binding was unreachable from the stale
/// host active root).
/// </summary>
public sealed class WindowActivationFocusTests
{
    private static (UITestHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new UIControls.StackPanel());
        host.RunUntilIdle();
        return (host, host.Application.WindowManager!);
    }

    private static Window WindowWith(UIElement content) => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = 2, Top = 2, Width = 30, Height = 10,
        Content = content,
    };

    [Fact] // activating a window auto-focuses its first tab stop + makes it the focus ActiveRoot
    public void ActivatingWindow_AutoFocusesFirstTabStop_AndBecomesActiveRoot()
    {
        var (host, wm) = ShownRoot();
        using var scope = host;

        var tb = new UIControls.TextBox { Width = 10, Height = 1 };
        var panel = new UIControls.StackPanel();
        panel.Children.Add(tb);
        panel.Children.Add(new UIControls.Button { Content = "OK", Width = 6, Height = 1 });

        var w = WindowWith(panel);
        w.Show(wm);
        host.RunUntilIdle();

        Assert.Same(w, wm.ActiveWindow);
        Assert.Same(tb, host.Application.FocusManager.FocusedElement); // first tab stop auto-focused
        Assert.Same(w, host.Application.FocusManager.ActiveRoot);      // the window is the active root
    }

    [Fact] // the user's report: Enter activates the window's default button from focused content
    public void DefaultButton_FiresOnEnter_FromFocusedWindowContent()
    {
        var (host, wm) = ShownRoot();
        using var scope = host;

        var tb = new UIControls.TextBox { Width = 10, Height = 1 };
        var btn = new UIControls.Button { IsDefault = true, Content = "OK", Width = 6, Height = 1 };
        var panel = new UIControls.StackPanel();
        panel.Children.Add(tb);
        panel.Children.Add(btn);

        var w = WindowWith(panel);
        w.Show(wm);
        host.RunUntilIdle();

        var clicks = 0;
        btn.Click += (_, _) => clicks++;

        Assert.True(tb.Focus());
        host.SendKey(Key.Enter);
        host.RunFrame();
        Assert.True(clicks >= 1);
    }

    [Fact] // the regressed case: Enter activates the default button even with NO focused element in the window
    public void DefaultButton_FiresOnEnter_WithNoFocus()
    {
        var (host, wm) = ShownRoot();
        using var scope = host;

        var btn = new UIControls.Button { IsDefault = true, Content = "OK", Width = 6, Height = 1 };
        var panel = new UIControls.StackPanel();
        panel.Children.Add(btn);

        var w = WindowWith(panel);
        w.Show(wm);
        host.RunUntilIdle();

        var clicks = 0;
        btn.Click += (_, _) => clicks++;

        host.Application.FocusManager.ClearFocus(); // no focused element; ActiveRoot is still the window
        host.RunFrame();
        host.SendKey(Key.Enter);
        host.RunFrame();
        Assert.True(clicks >= 1);
    }

    [Fact] // closing the active window deactivates it and restores the app root as the active focus root
    public void ClosingWindow_RestoresAppRootActivation()
    {
        var (host, wm) = ShownRoot();
        using var scope = host;
        var appRoot = host.Application.FocusManager.ActiveRoot; // the shown app root
        Assert.NotNull(appRoot);

        var w = WindowWith(new UIControls.StackPanel());
        w.Show(wm);
        host.RunUntilIdle();
        Assert.Same(w, host.Application.FocusManager.ActiveRoot);

        w.Close();
        host.RunUntilIdle();

        Assert.Null(wm.ActiveWindow);
        Assert.Same(appRoot, host.Application.FocusManager.ActiveRoot); // app root reactivated on deactivation
    }

    [Fact] // user-found: focus a ListBox item (a NESTED focus scope), open a window/dialog then close it — focus must
           // restore to the ITEM, not the app root's stale DIRECT-scope memory. The items host records the item's
           // memory, which the root scope does not mirror (the two scope memories are independent); OnWindowDeactivated
           // captures the live focused element so re-activation is exact.
    public void DeactivateReactivate_RestoresNestedScopeFocus_NotStaleRootMemory()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        using var scope = host;

        var list = new UIControls.ListBox { ItemsSource = new[] { "a", "b", "c" }, Width = 20, Height = 6 };
        var other = new UIControls.Button { Content = "Other", Width = 8, Height = 1 };
        var appRoot = new UIControls.StackPanel();
        appRoot.Children.Add(list);
        appRoot.Children.Add(other);
        host.ShowRoot(appRoot);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        var wm = host.Application.WindowManager!;

        other.Focus();          // seed the app-root scope memory with the WRONG element
        host.RunUntilIdle();

        var item1 = (UIControls.ListBoxItem) list.ItemContainerGenerator.ContainerFromIndex(1)!;
        item1.Focus();          // focus a list item — recorded on the items-host nested scope, not the root
        host.RunUntilIdle();
        Assert.Same(item1, focus.FocusedElement);

        var dialog = WindowWith(new UIControls.Button { Content = "OK", Width = 6, Height = 1 });
        dialog.Show(wm);        // deactivates the app root — captures item1 as its exact restore target
        host.RunUntilIdle();
        Assert.Same(dialog, wm.ActiveWindow);

        dialog.Close();         // reactivates the app root
        host.RunUntilIdle();

        Assert.Same(item1, focus.FocusedElement); // restored to the list item, NOT `other` (the root's stale memory)
    }
}
