// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Buffers;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The frame loop's phase order and routing contracts (design doc §10.5): input drain → jobs →
/// styling flush → animation tick → layout → render; resize/device-response events never reach
/// the dispatch target; default gestures; the out-of-band control-sequence channel; render gating.
/// </summary>
public sealed class FrameLoopTests
{
    [Fact]
    public void PhaseOrder_IsNormative()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        var log = new List<string>();
        var probe = new SeamProbe(log);
        app.InputDispatchTarget = probe;
        app.StyleHooks = probe;
        app.AnimationDriver = probe;

        var element = new Probe(4, 1) { Log = log, LogName = "el" };
        host.ShowRoot(element);

        app.Dispatcher.Post(() => log.Add("job"));
        host.SendKey(Key.Character, text: "x");
        host.RunFrame();

        Assert.Equal(
            ["begin-frame", "dispatch", "job", "flush", "tick", "flush", "tick-newly-started", "measure:el", "arrange:el", "render:el", "update-hover"],
            log);
    }

    [Fact]
    public void InputEvents_ReachDispatchTarget_InArrivalOrder()
    {
        using var host = UITestHost.Create();
        var probe = new SeamProbe([]);
        host.Application.InputDispatchTarget = probe;

        host.SendKey(Key.Character, text: "a");
        host.SendKey(Key.Character, text: "b");
        host.SendMouseMove(3, 4);
        host.RunFrame();

        Assert.Equal(3, probe.Dispatched.Count);
        Assert.Equal("a", ((KeyEvent)probe.Dispatched[0]).Text.ToString());
        Assert.Equal("b", ((KeyEvent)probe.Dispatched[1]).Text.ToString());
        Assert.IsType<MouseEvent>(probe.Dispatched[2]);
    }

    [Fact]
    public void ResizeAndDeviceResponse_NeverReachDispatchTarget()
    {
        using var host = UITestHost.Create();
        var probe = new SeamProbe([]);
        host.Application.InputDispatchTarget = probe;

        DeviceResponseEvent? routed = null;
        using var registration = host.Application.RegisterDeviceResponseSink(e => routed = e);

        host.SendResize(100, 30);
        host.SendInput(new DeviceResponseEvent
        {
            Kind = DeviceResponseKind.Unknown,
            Payload = new byte[] { 0x1b },
            Timestamp = host.Time.GetUtcNow()
        });
        host.RunFrame();

        Assert.Empty(probe.Dispatched);
        Assert.NotNull(routed);
    }

    [Fact]
    public void DeviceResponseSink_Unregisters()
    {
        using var host = UITestHost.Create();
        var count = 0;
        var registration = host.Application.RegisterDeviceResponseSink(_ => count++);

        host.SendInput(new DeviceResponseEvent { Kind = DeviceResponseKind.Unknown, Payload = default, Timestamp = host.Time.GetUtcNow() });
        host.RunFrame();
        Assert.Equal(1, count);

        registration.Dispose();
        host.SendInput(new DeviceResponseEvent { Kind = DeviceResponseKind.Unknown, Payload = default, Timestamp = host.Time.GetUtcNow() });
        host.RunFrame();
        Assert.Equal(1, count);
    }

    [Fact]
    public void UnhandledCtrlC_TriggersShutdown()
    {
        using var host = UITestHost.Create();
        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        host.RunFrame();

        Assert.True(host.Application.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void HandledCtrlC_DoesNotShutDown()
    {
        using var host = UITestHost.Create();
        host.Application.InputDispatchTarget = new SeamProbe([]) { DispatchResult = InputDispatchResult.DispatchedHandled };

        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        host.RunFrame();

        Assert.False(host.Application.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void QueueControlSequence_EmitsAfterDelta_ForcesFlushOnEmptyDelta()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());
        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length); // clean frame: zero bytes

        host.Application.QueueControlSequence(static w => w.Write("]0;title"u8));
        host.RunFrame();

        var bytes = host.LastFrameBytes.ToArray();
        Assert.True(bytes.Length > 0); // control sequence forced a flush despite the empty delta
        Assert.Contains("]0;title", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void RequestRender_ForcesAFrame_CleanTreeEmitsNothing()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());

        var probe = (Probe)host.Application.RootElement!;
        var renders = probe.RenderCount;
        host.Application.RequestRender();
        host.RunFrame();

        Assert.Equal(renders, probe.RenderCount);   // no re-raster — the tree was clean
        Assert.Equal(0, host.LastFrameBytes.Length); // diff renderer found nothing to emit
    }

    [Fact]
    public void InvalidateVisual_ReRasters_AndEmits()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var probe = new Probe(4, 1) { FillGlyph = "X" };
        host.ShowRoot(probe);
        Assert.True(host.RunUntilIdle());

        var renders = probe.RenderCount;
        probe.FillGlyph = "Y";
        probe.InvalidateVisual();
        host.RunFrame();

        Assert.Equal(renders + 1, probe.RenderCount);
        Assert.True(host.LastFrameBytes.Length > 0);
        Assert.StartsWith("YYYY", host.GetRowText(0));
    }

    [Fact]
    public void CompositeOnlyChange_TriggersRenderWithoutReRaster()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var probe = new Probe(4, 1) { FillGlyph = "X", Opacity = 0.5 }; // sub-1 opacity ⇒ own boundary
        var hostPanel = new Host();
        hostPanel.Add(probe);
        host.ShowRoot(hostPanel);
        Assert.True(host.RunUntilIdle());

        var renders = probe.RenderCount;
        probe.RenderOffsetColumn = 2; // AffectsComposite — parameters-only (invariant 3)
        Assert.True(host.Application.WindowManager!.HasDirtyVisuals); // the composite flag gates Phase 6
        host.RunFrame();

        Assert.Equal(renders, probe.RenderCount); // zero Render calls — the slide is pure composite
        Assert.True(host.LastFrameBytes.Length > 0);
        Assert.StartsWith("  XXXX", host.GetRowText(0));
    }

    [Fact]
    public void StreamEnd_ShutsDownLoop()
    {
        using var host = UITestHost.Create();
        host.Terminal.CompleteInput(); // EOF — the "terminal closed" path

        // The pump observes EOF asynchronously; spin a few frames on the wall clock.
        for (var i = 0; i < 100 && !host.Application.Dispatcher.ShutdownToken.IsCancellationRequested; i++)
        {
            Thread.Sleep(1);
            host.RunFrame();
        }

        Assert.True(host.Application.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void CurrentFrameTime_AdvancesWithFakeClock()
    {
        using var host = UITestHost.Create();
        host.RunFrame();
        var first = host.Application.CurrentFrameTime;

        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        host.RunFrame();
        var second = host.Application.CurrentFrameTime;

        Assert.Equal(first.FrameNumber + 1, second.FrameNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(100), second.Elapsed - first.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(100), second.Delta);
    }

    /// <summary>One probe implementing all three P1 no-op seams, logging phase entries.</summary>
    private sealed class SeamProbe(List<string> log) : IInputDispatchTarget, IStyleFrameHooks, IAnimationFrameDriver
    {
        public List<InputEvent> Dispatched { get; } = [];

        public InputDispatchResult DispatchResult { get; init; } = InputDispatchResult.NotUIInput;

        public InputDispatchResult Dispatch(InputEvent inputEvent)
        {
            log.Add("dispatch");
            Dispatched.Add(inputEvent);
            return DispatchResult;
        }

        public void UpdateHover() => log.Add("update-hover");

        public void OnCapabilitiesChanged(Terminal.TerminalCapabilities capabilities) => log.Add("caps");

        public void FlushPendingActivations() => log.Add("flush");

        public bool HasPendingActivations => false;

        public void BeginFrame(in FrameTime time) => log.Add("begin-frame");

        public void Tick() => log.Add("tick");

        public void TickNewlyStarted() => log.Add("tick-newly-started");

        public bool HasActiveAnimations => false;

        public void Shutdown() => log.Add("shutdown");
    }
}
