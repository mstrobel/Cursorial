using Cursorial.UI;
using Cursorial.UI.Data;
using Xunit;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §3 — the source change-notification ladder (B25–B36).</summary>
public class Section03_SourceLadder
{
    public Section03_SourceLadder()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B025_Inpc_Rung1_SubscribesAndReReads()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("a", w.Text);

        vm.Name = "b";
        Assert.Equal("b", w.Text);
    }

    [Fact]
    public void B026_Disposed_NoDelivery_HandlerRemoved()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        expr.Dispose();
        vm.Name = "c";
        Assert.NotEqual("c", w.Text); // no delivery after dispose
    }

#if DEBUG
    [Fact]
    public void B027_DebugLeakTracker_ReportsUndisposedExpression_ByPath()
    {
        BindingLeakTracker.ResetForTests();
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };

        // Install an INPC binding but never dispose / sweep the target's expression.
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("a", w.Text);

        // The window-close weak-target sweep reports the undisposed expression by path + install site
        // (the target is still alive ⇒ a real leak: a missed teardown).
        var leaks = BindingLeakTracker.Sweep();
        Assert.Contains(leaks, l => l.Path == "Name" && l.TargetDescription.Contains("BindWidget"));

        GC.KeepAlive(w);
        BindingLeakTracker.ResetForTests();
    }
#endif

    [Fact]
    public void B028_ChangedEvent_Rung2_Subscribed()
    {
        var plain = new PlainVm { Title = "init" };
        var w = new BindWidget { DataContext = plain };
        w.SetBinding(BindWidget.TextProperty, new Binding("Title"));
        Assert.Equal("init", w.Text);

        plain.Title = "x"; // raises TitleChanged
        Assert.Equal("x", w.Text);
    }

    [Fact]
    public void B029_ChangedEvent_RemovedOnDispose()
    {
        var plain = new PlainVm { Title = "init" };
        var w = new BindWidget { DataContext = plain };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Title"));

        expr.Dispose();
        plain.Title = "x";
        Assert.NotEqual("x", w.Text);
    }

    [Fact]
    public void B030_ChangedEvent_EventHandlerOfEventArgs_Matches()
    {
        var plain = new PlainVm { Count = 1 };
        var w = new BindWidget { DataContext = plain };
        w.SetBinding(BindWidget.NumProperty, new Binding("Count"));
        Assert.Equal(1, w.Num);

        plain.Count = 5; // raises CountChanged (EventHandler<EventArgs>)
        Assert.Equal(5, w.Num);
    }

    [Fact]
    public void B031_Rung3_OneTimeRead_RecordsInfo()
    {
        BindingDiagnostics.Level = BindingTraceLevel.Verbose;
        var plain = new PlainVm { Note = "n" };
        var w = new BindWidget { DataContext = plain };
        w.SetBinding(BindWidget.TextProperty, new Binding("Note"));

        Assert.Equal("n", w.Text); // one-time read

        // A bare Note mutation with no notification is not seen.
        plain.Note = "changed";
        Assert.Equal("n", w.Text);

        // A one-time Info diagnostic naming the un-observable hop was recorded.
        Assert.Contains(BindingDiagnostics.RecentEvents,
            e => e.Path == "Note" && e.Message.Contains("not observable"));
    }

    [Fact]
    public void B032_Rung3_MidChain_ReReadsOnParentChange()
    {
        // "Holder.Note": Holder is INPC-notified on vm; Note is a rung-3 (no INPC, no [Name]Changed) leaf.
        var vm = new Vm { Holder = new PlainHolder { Note = "first" } };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Holder.Note"));
        Assert.Equal("first", w.Text);

        // A bare Note mutation with no parent notify is NOT seen.
        vm.Holder!.Note = "ignored";
        Assert.Equal("first", w.Text);

        // Swapping the Holder (INPC parent change) re-reads the whole tail incl. Note.
        vm.Holder = new PlainHolder { Note = "second" };
        Assert.Equal("second", w.Text);
    }

    [Fact]
    public void B033_BothInpcAndChangedEvent_InpcWins()
    {
        var dual = new DualVm { Name = "a" };
        var w = new BindWidget { DataContext = dual };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("a", w.Text);

        dual.Name = "b"; // raises INPC (one subscription)
        Assert.Equal("b", w.Text);

        // The NameChanged event is NOT subscribed: a bare NameChanged raise does not re-read.
        // (Name is unchanged so no observable effect — assert no throw and value holds.)
        dual.RaiseNameChanged();
        Assert.Equal("b", w.Text);
    }

    [Fact]
    public void B034_Indexer_Incc_Replace_ReReads()
    {
        var vm = new Vm();
        vm.Tags.Add("x");
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Tags[0]"));
        Assert.Equal("x", w.Text);

        vm.Tags[0] = "y"; // INCC Replace
        Assert.Equal("y", w.Text);
    }

    [Fact]
    public void B035_Indexer_Incc_Insert_ReReadsBoundIndex()
    {
        var vm = new Vm();
        vm.Tags.Add("x");
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Tags[0]"));
        Assert.Equal("x", w.Text);

        vm.Tags.Insert(0, "z"); // INCC Insert shifting index 0
        Assert.Equal("z", w.Text);
    }

    [Fact]
    public void B036_MidChainIdentityChange_RewiresAndDropsStale()
    {
        var s1 = new Vm { Name = "p" };
        var s2 = new Vm { Name = "q" };
        var vm = new Vm { Sub = s1 };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Sub.Name"));
        Assert.Equal("p", w.Text);

        vm.Sub = s2; // intermediate identity change
        Assert.Equal("q", w.Text);

        // The old s1.Name handler was unsubscribed: a later s1 mutation does not deliver.
        s1.Name = "stale";
        Assert.Equal("q", w.Text);
    }
}
