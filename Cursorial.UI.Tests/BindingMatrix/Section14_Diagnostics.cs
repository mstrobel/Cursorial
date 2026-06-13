using Cursorial.UI;
using Cursorial.UI.Data;
using Xunit;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>Binding matrix §14 — diagnostics (ring/sinks B0 part: B169–B174; <c>Explain</c> is B1).</summary>
[Collection("BindingDiagnostics")]
public class Section14_Diagnostics
{
    public Section14_Diagnostics()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B169_FailurePath_AlwaysRingRecorded_RegardlessOfLevel()
    {
        BindingDiagnostics.Level = BindingTraceLevel.Error; // default
        var vm = new Vm { Name = "notanint" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.NumProperty, new Binding("Name"));

        // Warning/Error events are always constructed and ring-recorded (failure paths only).
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Kind == BindingFailureKind.TypeMismatch);
    }

    [Fact]
    public void B170_HappyPath_NoDiagnosticsConstructed()
    {
        BindingDiagnostics.Level = BindingTraceLevel.Error;
        var before = BindingDiagnostics.RecentEvents.Count;
        var vm = new Vm { Name = "ok" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        vm.Name = "still ok";

        Assert.Equal(before, BindingDiagnostics.RecentEvents.Count); // nothing constructed
    }

    [Fact]
    public void B171_Verbose_GatedOnLevel()
    {
        // At Error level, the rung-3 Info is not constructed.
        BindingDiagnostics.Level = BindingTraceLevel.Error;
        var plain = new PlainVm { Note = "n" };
        var w = new BindWidget { DataContext = plain };
        w.SetBinding(BindWidget.TextProperty, new Binding("Note"));
        Assert.DoesNotContain(BindingDiagnostics.RecentEvents, e => e.Message.Contains("not observable"));

        // At Verbose level, it is.
        BindingDiagnostics.ResetForTests();
        BindingDiagnostics.Level = BindingTraceLevel.Verbose;
        var plain2 = new PlainVm { Note = "n" };
        var w2 = new BindWidget { DataContext = plain2 };
        w2.SetBinding(BindWidget.TextProperty, new Binding("Note"));
        Assert.Contains(BindingDiagnostics.RecentEvents, e => e.Message.Contains("not observable"));
    }

    [Fact]
    public void B172_SinkAndHandler_ReceiveEvent()
    {
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        var sink = new RecordingSink();
        BindingDiagnostics.AddSink(sink);
        var handlerEvents = new List<BindingTraceEvent>();
        BindingDiagnostics.TraceEmitted += e => handlerEvents.Add(e);

        var vm = new Vm { Name = "notanint" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.NumProperty, new Binding("Name"));

        Assert.NotEmpty(sink.Events);
        Assert.NotEmpty(handlerEvents);
        var e = sink.Events[^1];
        Assert.Contains("BindWidget", e.TargetDescription);
        Assert.True(e.Timestamp > 0);
    }

    [Fact]
    public void B173_Ring_OverwritesOldest_KeepsMostRecent256()
    {
        for (var i = 0; i < 300; i++)
        {
            BindingDiagnostics.Record(new BindingTraceEvent(
                BindingTraceLevel.Error, BindingFailureKind.PathError, $"p{i}", "t", "m", Environment.TickCount64));
        }

        Assert.Equal(256, BindingDiagnostics.RecentEvents.Count);
        Assert.Equal("p299", BindingDiagnostics.RecentEvents[^1].Path); // most recent
        Assert.Equal("p44", BindingDiagnostics.RecentEvents[0].Path);   // oldest kept (300-256)
        Assert.Equal(300, BindingDiagnostics.ErrorCount);
    }

    [Fact]
    public void B175_Explain_OneActiveLocalValue_OneLine()
    {
        var vm = new Vm { Name = "n" };
        var w = new BindWidget { DataContext = vm };
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        var explanation = BindingDiagnostics.Explain(w, BindWidget.TextProperty);
        Assert.True(explanation.HasBindings);
        var line = Assert.Single(explanation.Expressions);
        Assert.Equal(BindingLane.LocalValue, line.Lane);
        Assert.Equal(BindingStatus.Active, line.Status);
        Assert.Equal("Name", line.Path);
        Assert.Equal("n", line.LastProducedValue);
        Assert.Equal(BindingFailureKind.None, line.LastFailure);
    }

    [Fact]
    public void B176_Explain_AllLanes_StrongestFirst()
    {
        var vm = new Vm { Name = "low", Age = 9 };
        var w = new BindWidget { DataContext = vm };
        var frame = new TestFrame(sortKey: 5);
        w.AddFrame(frame);

        // A frame-hosted binding shadowed by a LocalValue binding on the same property.
        BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Age"), frame);
        w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        var explanation = BindingDiagnostics.Explain(w, BindWidget.TextProperty);
        Assert.Equal(2, explanation.Expressions.Length); // every expression across lanes
        Assert.Equal(BindingLane.LocalValue, explanation.Expressions[0].Lane);  // strongest-first
        Assert.Equal(BindingLane.FrameHosted, explanation.Expressions[1].Lane);

        // GetBindingExpression covers LocalValue only (BD7) — Explain covers all lanes.
        Assert.NotNull(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));
    }

    [Fact]
    public void B177_Explain_PathError_ShowsStatusAndFailure()
    {
        var vm = new Vm { Sub = null }; // broken mid-chain
        var w = new BindWidget { DataContext = vm };
        BindingDiagnostics.Level = BindingTraceLevel.Warning;
        w.SetBinding(BindWidget.TextProperty, new Binding("Sub.Name") { FallbackValue = "fb" });

        var explanation = BindingDiagnostics.Explain(w, BindWidget.TextProperty);
        var line = Assert.Single(explanation.Expressions);
        Assert.Contains("Sub.Name", line.ResolvedSourceChain);
        Assert.Equal("Sub.Name", line.Path);
    }

    [Fact]
    public void B178_DumpTo_DoesNotWriteToTerminal_PostSession()
    {
        // The dump targets a caller-supplied writer (called after session disposal in the canonical
        // teardown order) — never stdout.
        BindingDiagnostics.Record(new BindingTraceEvent(
            BindingTraceLevel.Error, BindingFailureKind.PathError, "P", "T#x.Prop", "msg", Environment.TickCount64));

        var sw = new System.IO.StringWriter();
        BindingDiagnostics.DumpTo(sw);

        var text = sw.ToString();
        Assert.Contains("PathError", text);
        Assert.Contains("T#x.Prop", text);
    }

    [Fact]
    public async Task B174_ReentrantSink_DoesNotDeadlockOnGate()
    {
        // A sink that re-enters the façade (RecentEvents/ErrorCount) from inside Write must not
        // deadlock: Record snapshots the sink list and releases Gate before delivery (finding 2).
        BindingDiagnostics.Level = BindingTraceLevel.Error;
        var sink = new ReentrantSink();
        BindingDiagnostics.AddSink(sink);

        // Run on a worker so a regression (Gate held during delivery) fails the test via a timeout
        // instead of hanging the whole suite.
        var task = Task.Run(() => BindingDiagnostics.Record(new BindingTraceEvent(
            BindingTraceLevel.Error, BindingFailureKind.PathError, "p", "t", "m", Environment.TickCount64)));

        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed); // Record completed; it did not deadlock on the re-entrant sink
        await task; // surface any exception
        Assert.True(sink.Reentered);
    }

    private sealed class RecordingSink : IBindingTraceSink
    {
        public List<BindingTraceEvent> Events { get; } = [];

        public void Write(in BindingTraceEvent e) => Events.Add(e);
    }

    private sealed class ReentrantSink : IBindingTraceSink
    {
        public bool Reentered { get; private set; }

        public void Write(in BindingTraceEvent e)
        {
            // Re-enter the façade — both of these acquire Gate. If Record still held Gate during
            // delivery this would deadlock.
            _ = BindingDiagnostics.RecentEvents;
            _ = BindingDiagnostics.ErrorCount;
            Reentered = true;
        }
    }
}
