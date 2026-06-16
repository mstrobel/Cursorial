using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §13 — the watch-only surface (B157–B168), the Fork B <c>When</c>/<c>DataCondition</c>
/// seam. <see cref="IBindingWatch"/> has NO <c>Pause</c>/<c>Resume</c> (ledger B16).
/// </summary>
public class Section13_WatchSurface
{
    public Section13_WatchSurface()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B157_Watch_FollowsSource_SynchronousDelivery()
    {
        var vm = new Vm { IsDirty = false };
        var a = new BindWidget { DataContext = vm };
        var log = new List<object?>();
        var watch = BindingOperations.Watch(a, new Binding("IsDirty"), v => log.Add(v));

        Assert.Equal(false, watch.Value);
        vm.IsDirty = true;
        Assert.Equal(true, watch.Value);
        Assert.Equal(new object?[] { false, true }, log);

        // No store entry created for the watch (watch-only).
        Assert.Null(BindingOperations.GetBindingExpression(a, UIProperty.UnsetTargetProperty));
        GC.KeepAlive(watch);
    }

    [Fact]
    public void B158_Watch_UnresolvedPath_DeliversUnset()
    {
        var vm = new Vm { Sub = null };
        var a = new BindWidget { DataContext = vm };
        var watch = BindingOperations.Watch(a, new Binding("Sub.Name"), _ => { });
        Assert.Same(UIProperty.UnsetValue, watch.Value);
    }

    [Fact]
    public void B159_Watch_SelfSource()
    {
        var a = new BindWidget { Num = 0 };
        var watch = BindingOperations.Watch(a, new Binding("Num") { RelativeSource = RelativeSource.Self }, _ => { });

        a.Num = 4;
        Assert.Equal(4, watch.Value);
    }

    [Fact]
    public void B160_Watch_AncestorSource()
    {
        var root = new StackPanel();
        var paneA = new StackPanel();
        var a = new BindWidget();
        root.Children.Add(paneA);
        paneA.Children.Add(a);

        var watch = BindingOperations.Watch(a,
            new Binding("Spacing") { RelativeSource = RelativeSource.Ancestor<StackPanel>() }, _ => { });

        paneA.Spacing = 9;
        Assert.Equal(9, watch.Value);
    }

    [Fact]
    public void B161_Watch_DataContextChange_Rebinds()
    {
        var vm = new Vm { Name = "a" };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);
        var log = new List<object?>();
        var watch = BindingOperations.Watch(a, new Binding("Name"), v => log.Add(v));
        Assert.Equal("a", watch.Value);

        root.DataContext = new Vm { Name = "b" };
        Assert.Equal("b", watch.Value);
        Assert.Contains("b", log);
    }

    [Fact]
    public void B162_When_DrivenStyleRule_ArmedViaWatch_FlipsAcrossFrames()
    {
        // The styling integration (the deliberate-hole close): a When rule armed via Watch. The watch
        // delivers the boolean condition; the rule "activates" by arming a ValueFrame on the callback,
        // "deactivates" by retracting it. The bound property flips accordingly. (This is the exact
        // mechanism the styling engine plugs Watch into; the watch is the data half.)
        var vm = new Vm { IsDirty = false };
        var a = new BindWidget { DataContext = vm };

        TestFrame? activeFrame = null;
        var watch = BindingOperations.Watch(a, new Binding("IsDirty"), value =>
        {
            var met = value is true;
            if (met && activeFrame is null)
            {
                // rule activates: arm a frame that sets Num = 1
                var frame = new TestFrame(sortKey: 5);
                a.AddFrame(frame);
                a.BindInFrame(BindWidget.NumProperty, frame).SetValue(1);
                activeFrame = frame;
            }
            else if (!met && activeFrame is { } frame)
            {
                // rule deactivates: retract the frame
                a.RemoveFrame(frame);
                activeFrame = null;
            }
        });

        Assert.Equal(0, a.Num); // false ⇒ rule inactive

        vm.IsDirty = true;
        Assert.Equal(1, a.Num); // true ⇒ rule activates, sets Num

        vm.IsDirty = false;
        Assert.Equal(0, a.Num); // false ⇒ rule deactivates, Num promotes back to default
        GC.KeepAlive(watch);
    }

    [Fact]
    public void B163_Watch_StaysLiveAcrossDeactivation_NoPauseResume()
    {
        // The watcher is the re-activation predicate — it must stay live across a deactivation edge,
        // NOT be paused. IBindingWatch exposes no Pause/Resume (ledger B16).
        var vm = new Vm { IsDirty = true };
        var a = new BindWidget { DataContext = vm };
        var deliveries = new List<object?>();
        var watch = BindingOperations.Watch(a, new Binding("IsDirty"), deliveries.Add);

        // The interface offers no parking surface.
        Assert.DoesNotContain(typeof(IBindingWatch).GetMethods(), m => m.Name is "Pause" or "Resume");

        // After a "deactivation" the watch keeps delivering — it is what re-activates the rule.
        vm.IsDirty = false; // deactivation edge
        vm.IsDirty = true;  // the still-live watch fires the re-activation predicate

        Assert.Equal(new object?[] { true, false, true }, deliveries);
        GC.KeepAlive(watch);
    }

    [Fact]
    public void B167_Watch_VmFlipMidInputDrain_ParticipatesSameFrame()
    {
        using var host = Cursorial.UI.Testing.UITestHost.Create();
        var root = new BindWidget();
        var vm = new Vm { IsDirty = false };
        root.DataContext = vm;
        host.ShowRoot(root);
        host.RunFrame();

        var lastDelivered = new List<object?>();
        var watch = BindingOperations.Watch(root, new Binding("IsDirty"), lastDelivered.Add);

        // A VM-driven flip is delivered synchronously on the UI thread — a live When condition reaches
        // layout that frame (invariant 1).
        vm.IsDirty = true;
        Assert.Equal(true, watch.Value); // delivered synchronously, no frame needed to observe
        Assert.Contains(true, lastDelivered);
        GC.KeepAlive(watch);
    }

    [Fact]
    public void B164_Watch_DisposedOnDetach_TeardownBackstop()
    {
        var vm = new Vm { Name = "a" };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);
        var watch = BindingOperations.Watch(a, new Binding("Name"), _ => { });

        root.TearDown(); // the backstop: registry-tracked watches anchored on a are disposed
        vm.Name = "after";
        Assert.NotEqual("after", watch.Value); // unhooked
    }

    [Fact]
    public void B165_Watch_Dispose_Idempotent()
    {
        var vm = new Vm { Name = "a" };
        var a = new BindWidget { DataContext = vm };
        var watch = BindingOperations.Watch(a, new Binding("Name"), _ => { });

        watch.Dispose();
        var record = Record.Exception(watch.Dispose);
        Assert.Null(record);

        vm.Name = "x";
        Assert.NotEqual("x", watch.Value);
    }

    [Fact]
    public void B166_Watch_TeardownSweep_Backstop()
    {
        var vm = new Vm { Name = "a" };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);
        _ = BindingOperations.Watch(a, new Binding("Name"), _ => { });

        // The sweep disposes registry-tracked watches even if styling never disarms.
        var record = Record.Exception(root.TearDown);
        Assert.Null(record);
    }

    [Fact]
    public void B168_Watch_DeliversConvertedValue_NotABooleanVerdict()
    {
        var vm = new Vm { Age = 7 };
        var a = new BindWidget { DataContext = vm };
        var watch = BindingOperations.Watch(a, new Binding("Age"), _ => { });

        // The watch delivers the raw/converted value; the == Value compare is the styling engine's job.
        Assert.Equal(7, watch.Value);
    }
}
