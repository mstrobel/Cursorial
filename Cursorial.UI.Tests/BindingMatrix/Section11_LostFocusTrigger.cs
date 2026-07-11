using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §11 — the <c>LostFocus</c> trigger (B131–B138). Two distinct flush sources: S3's
/// routed <see cref="UIElement.LostFocusEvent"/> (physical focus moving off) and
/// <c>InputDispatcher.EditCommitRequested</c> (terminal focus-out, focus RETAINED). Terminal
/// focus-out raises no <c>LostFocusEvent</c>.
/// </summary>
public class Section11_LostFocusTrigger
{
    public Section11_LostFocusTrigger()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    private static FocusChangedEventArgs LostFocusArgs(UIElement a) =>
        new(UIElement.LostFocusEvent, a, a, null, FocusNavigationMethod.Tab);

    [Fact]
    public void B131_LostFocus_DirtyEdit_NotYetWritten()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit"; // no focus change
        Assert.Equal("a", vm.Name); // SourceDirty set; not yet written
    }

    [Fact]
    public void B132_LostFocus_RoutedEvent_FlushesPendingEdit()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit";
        Assert.Equal("a", vm.Name);

        w.RaiseEvent(LostFocusArgs(w)); // S3 routed LostFocusEvent — physical move-off
        Assert.Equal("edit", vm.Name);  // flushed
    }

    [Fact]
    public void B133_TerminalFocusOut_EditCommitPulse_Flushes_FocusRetained()
    {
        using var host = UIHeadlessHost.Create();
        var root = new StackPanel();
        var w = new BindWidget { Focusable = true };
        var vm = new Vm { Name = "a" };
        w.DataContext = vm;
        root.Children.Add(w);
        host.ShowRoot(root);
        host.RunFrame();

        host.Application.FocusManager.SetFocus(w);
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit";
        Assert.Equal("a", vm.Name);

        var lostFocusRaises = 0;
        w.AddHandler(UIElement.LostFocusEvent, (_, _) => lostFocusRaises++);

        host.SendInput(new FocusEvent { HasFocus = false, Timestamp = default }); // terminal focus-out → EditCommitRequested(w)
        host.RunFrame();

        Assert.Equal("edit", vm.Name);                          // the pulse flushed the pending edit
        Assert.Same(w, host.Application.FocusManager.FocusedElement); // keyboard focus RETAINED
        Assert.Equal(0, lostFocusRaises);                       // no LostFocusEvent raised
    }

    [Fact]
    public void B134_LostFocus_CrossScopePhysicalMove_Flushes()
    {
        // A physical focus move (raises LostFocusEvent on the element) flushes — where WPF's
        // logical-focus trigger would not (recorded divergence §6.11.6).
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit";
        w.RaiseEvent(LostFocusArgs(w)); // physical-focus move (to a menu/toolbar, a separate scope)
        Assert.Equal("edit", vm.Name);
    }

    [Fact]
    public void B136_LostFocus_NothingDirty_NoWrite()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit";
        w.RaiseEvent(LostFocusArgs(w)); // flush
        Assert.Equal("edit", vm.Name);

        var writes = 0;
        vm.PropertyChanged += (_, _) => writes++;

        w.RaiseEvent(LostFocusArgs(w)); // focus leaves again, nothing dirty
        Assert.Equal(0, writes);        // no re-write
    }

    [Fact]
    public void B137_LostFocus_DetachedBeforeFocusLeaves_NoSpuriousFlush()
    {
        var vm = new Vm { Name = "a" };
        var root = new BindWidget { DataContext = vm };
        var w = new BindWidget();
        root.AddChild(w);
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit"; // dirty
        Assert.Equal("a", vm.Name);

        root.TearDown(); // permanent detach disposes the expression + unhooks LostFocus
        Assert.True(expr.IsDisposed);

        var writes = 0;
        vm.PropertyChanged += (_, _) => writes++;
        w.RaiseEvent(LostFocusArgs(w)); // a later spurious event must not flush
        Assert.Equal(0, writes);
    }

    [Fact]
    public void B138_Explicit_DoesNotFlushOnLostFocus()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });

        w.Text = "edit";
        w.RaiseEvent(LostFocusArgs(w));
        Assert.Equal("a", vm.Name); // Explicit does NOT flush on LostFocus

        expr.UpdateSource();
        Assert.Equal("edit", vm.Name); // only UpdateSource flushes it
    }

    [Fact]
    public void B135_LostFocusEvent_AlwaysFrameworkTracked_NoCapabilityFallback()
    {
        // DEV-corrected (matrix B135): the routed LostFocusEvent is a FRAMEWORK event, always
        // available (focus is framework-tracked, not a terminal capability). The spec's
        // "falls back to PropertyChanged when the routed event is unavailable" path is therefore
        // unreachable in Cursorial — the LostFocus trigger always rides the routed event + pulse.
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });

        w.Text = "edit";
        Assert.Equal("a", vm.Name); // NOT flushed on the keystroke (would be, under a PropertyChanged fallback)

        w.RaiseEvent(LostFocusArgs(w));
        Assert.Equal("edit", vm.Name); // the routed event flushes — proof the trigger is the real LostFocus
    }
}
