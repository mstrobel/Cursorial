using System;
using System.ComponentModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7 / punch #39 — permanent-detach teardown on window close: closing a window runs the terminal
/// sweep (value store evicted, binding expressions disposed) that a reversible detach does not, so a
/// live viewmodel no longer pins the closed window's subtree. A closed window is terminal (no re-show),
/// and content removed in a <c>Closed</c> handler is spared for reuse.
/// </summary>
public sealed class WindowTeardownTests
{
    private sealed class Vm : INotifyPropertyChanged
    {
        private string? _name = "initial";
        public string? Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;
    }

    private static (UIHeadlessHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    [Fact] // close disposes the window's own bindings — the VM's INPC subscription is released (no leak)
    public void Close_TearsDownWindowBindings_ReleasesViewModel()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var vm = new Vm();
        var window = new Window { DataContext = vm };
        window.SetBinding(Window.TitleProperty, new Binding(nameof(Vm.Name)));
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.True(vm.SubscriberCount >= 1);      // the binding wired and subscribed to the VM
        Assert.Equal("initial", window.Title);

        window.Close();
        Assert.Equal(0, vm.SubscriberCount);       // teardown disposed the binding — the VM is released
    }

    [Fact] // a closed window is terminal: re-showing throws instead of resurrecting a torn-down tree
    public void Close_MakesWindowTerminal_ReShowThrows()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var window = new Window();
        window.Show(wm);
        Assert.True(host.RunUntilIdle());
        window.Close();

        Assert.Throws<InvalidOperationException>(() => window.Show(wm));
        Assert.Throws<InvalidOperationException>(() => { _ = window.ShowDialogAsync(); });
    }

    [Fact] // the sweep reaches realized content: a local value on the content is evicted by teardown
    public void Close_TearsDownContentSubtree()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var content = new Border { Width = 7 };
        var window = new Window { Content = content };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(7, content.Width);            // local value present while attached

        window.Close();
        Assert.NotEqual(7, content.Width);         // store evicted by the teardown sweep → default
    }

    [Fact] // escape hatch: content removed in a Closed handler is SPARED — its store survives, it re-hosts
    public void Close_ContentRemovedInClosedHandler_IsSpared()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var content = new Border { Width = 7 };
        var window = new Window { Content = content };
        window.Closed += (_, _) => window.Content = null; // pull the content before the teardown sweep
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        window.Close();
        Assert.Equal(7, content.Width);            // spared — the store was never evicted

        // ...and it re-hosts on a fresh window with no "already attached" / torn-down fallout.
        var reuse = new Window { Content = content };
        reuse.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(7, content.Width);
        reuse.Close();
    }

    [Fact] // Hide() is reversible: the window stays alive (NOT torn down) and Show() brings it back
    public void Hide_KeepsWindowAlive_ShowReveals()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var vm = new Vm();
        var window = new Window { DataContext = vm };
        window.SetBinding(Window.TitleProperty, new Binding(nameof(Vm.Name)));
        window.Show(wm);
        host.RunUntilIdle();
        Assert.True(vm.SubscriberCount >= 1);

        window.Hide();
        host.RunUntilIdle();
        Assert.False(window.IsEffectivelyVisible);   // off display
        Assert.True(window.IsShown);                  // but still hosted/alive
        Assert.True(vm.SubscriberCount >= 1);         // NOT torn down — bindings intact

        window.Show(wm);                              // hidden→shown reveal path
        host.RunUntilIdle();
        Assert.True(window.IsEffectivelyVisible);
        Assert.Equal("initial", window.Title);        // the live tree returned, no rebuild

        // ...and Close after a Hide/Show cycle is still terminal.
        window.Close();
        Assert.Equal(0, vm.SubscriberCount);
        Assert.Throws<InvalidOperationException>(() => window.Show(wm));
    }

    [Fact] // a hidden window cannot become OR stay the active window; Hide hands activation off
    public void Hide_HandsOffActivation_AndCannotReactivateWhileHidden()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var a = new Window();
        var b = new Window();
        a.Show(wm);
        b.Show(wm);
        host.RunUntilIdle();
        Assert.Same(b, wm.ActiveWindow);      // last shown is active

        b.Hide();                              // hiding the active window
        host.RunUntilIdle();
        Assert.NotSame(b, wm.ActiveWindow);   // can't STAY active once hidden
        Assert.Same(a, wm.ActiveWindow);       // handed off to the visible peer

        Assert.False(b.Activate());            // can't BECOME active while hidden
        Assert.Same(a, wm.ActiveWindow);

        a.Close();
        b.Close();
    }

    // A control whose EFFECTIVE template is this one (Theme override wins the control theme, CD13).
    private static Style TemplateTheme(System.Func<Cursorial.UI.Controls.TemplateBuildContext, UIElement> build)
        => new Style().Set(Cursorial.UI.Controls.Control.TemplateProperty, new Cursorial.UI.Controls.ControlTemplate(build));

    [Fact] // ANY template-realized content is swept: a binding INSIDE a ControlTemplate disposes on close
    public void Close_TearsDownTemplateContent()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var vm = new Vm();
        var probe = new Button
        {
            DataContext = vm,
            Theme = TemplateTheme(_ =>
            {
                var text = new TextBlock();
                text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Vm.Name)));
                return text;
            }),
        };
        var window = new Window { Content = probe };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.True(vm.SubscriberCount >= 1);   // the template's binding wired to the VM
        window.Close();
        Assert.Equal(0, vm.SubscriberCount);     // the sweep reached into the template content and disposed it
    }

    [Fact] // recursion reaches DEEP template content — a bound leaf nested under panels is still swept
    public void Close_TearsDownNestedTemplateContent()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var vm = new Vm();
        var probe = new Button
        {
            DataContext = vm,
            Theme = TemplateTheme(_ =>
            {
                var leaf = new TextBlock();
                leaf.SetBinding(TextBlock.TextProperty, new Binding(nameof(Vm.Name)));
                return new StackPanel { Children = { new Border { Child = new StackPanel { Children = { leaf } } } } };
            }),
        };
        var window = new Window { Content = probe };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.True(vm.SubscriberCount >= 1);
        window.Close();
        Assert.Equal(0, vm.SubscriberCount);
    }

    [Fact] // template content is released whether the element is ATTACHED or DETACHED at teardown:
    // here it is unloaded from the window first, then torn down explicitly — the sweep still reaches it.
    public void TearDown_ReleasesTemplateContent_WhenDetached()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var vm = new Vm();
        var probe = new Button
        {
            DataContext = vm,
            Theme = TemplateTheme(_ =>
            {
                var text = new TextBlock();
                text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Vm.Name)));
                return text;
            }),
        };
        var window = new Window { Content = probe };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(vm.SubscriberCount >= 1);       // attached: the template binding wired

        window.Content = null;                       // UNLOAD (reversible detach) — binding survives
        Assert.True(host.RunUntilIdle());
        Assert.True(vm.SubscriberCount >= 1);       // detach does not dispose the binding

        probe.TearDown();                            // permanent sweep on a DETACHED element
        Assert.Equal(0, vm.SubscriberCount);         // template content released while unloaded
        window.Close();
    }

    [Fact] // a field-held ContextMenu (off-tree, own surface) is torn down via the owner's Control.OnTearDown
    public void Close_TearsDownFieldHeldContextMenu()
    {
        var (host, wm) = ShownRoot();
        using var _host = host;

        var vm = new Vm();
        var menu = new Cursorial.UI.Controls.ContextMenu { DataContext = vm };
        menu.SetBinding(Cursorial.UI.Controls.Control.MaxWidthProperty, new Binding(nameof(Vm.Name))); // subscribes to the VM
        // A NON-Control owner (Border : Decorator) proves UIElement — not just Control — owns the check.
        var owner = new Border();
        Cursorial.UI.Controls.ContextMenu.SetMenu(owner, menu);
        var window = new Window { Content = owner };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.True(vm.SubscriberCount >= 1);   // the menu's binding subscribed to the VM
        window.Close();
        // Owner Control.OnTearDown tears down its ContextMenu → ContextMenu.OnTearDown tears down its
        // off-tree popup; the menu's own bindings dispose. The whole off-tree cluster is released.
        Assert.Equal(0, vm.SubscriberCount);
    }
}
