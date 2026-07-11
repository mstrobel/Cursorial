// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Buffers;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        host.RunFrame();

        Assert.True(host.Application.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void HandledCtrlC_DoesNotShutDown()
    {
        using var host = UIHeadlessHost.Create();
        host.Application.InputDispatchTarget = new SeamProbe([]) { DispatchResult = InputDispatchResult.DispatchedHandled };

        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        host.RunFrame();

        Assert.False(host.Application.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void QueueControlSequence_EmitsAfterDelta_ForcesFlushOnEmptyDelta()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
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
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
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
    public void RequestFullRedraw_IdleApp_ReEmitsEverything_WithoutReRaster()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
        var probe = new Probe(4, 1) { FillGlyph = "X" };
        host.ShowRoot(probe);
        Assert.True(host.RunUntilIdle());
        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length); // quiescent baseline: no spurious renders

        var renders = probe.RenderCount;
        var rowBefore = host.GetRowText(0);

        // The method must ARM a render itself: the external-corruption scenario it serves has
        // nothing framework-side dirty, so without the arm this frame would emit nothing.
        host.Application.RequestFullRedraw();
        host.RunFrame();

        var bytes = System.Text.Encoding.ASCII.GetString(host.LastFrameBytes.ToArray());
        Assert.Contains("[2J", bytes);         // clear screen — the full-resync signature
        Assert.Contains("XXXX", bytes);              // every non-blank cell re-emitted …
        Assert.Equal(renders, probe.RenderCount);    // … WITHOUT re-rastering any zone (renderer-level reset only)
        Assert.Equal(rowBefore, host.GetRowText(0)); // the frame buffer itself is undisturbed

        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length); // one-shot: the next frame is a clean diff again
    }

    [Fact]
    public void RequestFullRedraw_CrossThread_MarshalsToUiThread_AndRedraws()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());
        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length);

        WorkerThread.Run(() =>
        {
            host.Application.RequestFullRedraw(); // off-thread: must post, never touch the renderer here
            return 0;
        });

        host.RunFrame(); // the posted job runs in Phase 2; the SAME frame's Phase 6 emits the full resync

        var bytes = System.Text.Encoding.ASCII.GetString(host.LastFrameBytes.ToArray());
        Assert.Contains("[2J", bytes);
        Assert.Contains("XXXX", bytes);
    }

    [Fact]
    public void CtrlL_OnAppRoot_TriggersFullRedraw()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());
        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length);

        host.SendKey(Key.Character, KeyModifiers.Control, "l"); // the conventional terminal redraw chord
        host.RunFrame();

        var bytes = System.Text.Encoding.ASCII.GetString(host.LastFrameBytes.ToArray());
        Assert.Contains("[2J", bytes);
        Assert.Contains("XXXX", bytes);
    }

    [Fact]
    public void CtrlL_WhileWindowFocused_TriggersFullRedraw()
    {
        // A focused window's key route ends at ITS surface root — the chord must be installed
        // per active root, or Ctrl+L dies the moment a dialog opens.
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());

        var window = host.NewWindow(content: new Probe(4, 1) { FillGlyph = "W" }, left: 10, top: 5);
        window.Show(host.Application.WindowManager!);
        Assert.True(host.RunUntilIdle());
        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length); // quiescent with the window active + focused

        host.SendKey(Key.Character, KeyModifiers.Control, "l");
        host.RunFrame();

        var bytes = System.Text.Encoding.ASCII.GetString(host.LastFrameBytes.ToArray());
        Assert.Contains("[2J", bytes);
    }

    [Fact]
    public void CtrlL_Binding_IsIdempotentAcrossActivations()
    {
        // Window switching re-activates the same roots repeatedly; the redraw binding must not
        // accumulate (EnsureRedrawBinding adds exactly one per root, ever).
        using var host = UIHeadlessHost.Create();
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());

        var wm = host.Application.WindowManager!;
        var w1 = host.NewWindow(content: new Probe(2, 1) { FillGlyph = "A" }, left: 2, top: 2);
        var w2 = host.NewWindow(content: new Probe(2, 1) { FillGlyph = "B" }, left: 20, top: 2);
        w1.Show(wm);
        Assert.True(host.RunUntilIdle());
        w2.Show(wm);
        Assert.True(host.RunUntilIdle());

        // Bounce activation between the two windows — each re-activation runs the ensure path.
        for (int i = 0; i < 3; i++)
        {
            w1.Activate();
            Assert.True(host.RunUntilIdle());
            w2.Activate();
            Assert.True(host.RunUntilIdle());
        }

        Assert.Equal(1, CountRedrawBindings(w1));
        Assert.Equal(1, CountRedrawBindings(w2));

        static int CountRedrawBindings(UIElement root)
        {
            int count = 0;
            foreach (var binding in root.InputBindings)
            {
                if (binding is KeyBinding { Gesture: KeyGesture { Key: Key.Character, Modifiers: KeyModifiers.Control } })
                    count++;
            }
            return count;
        }
    }

    [Fact]
    public void InvalidateVisual_ReRasters_AndEmits()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
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
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
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
        using var host = UIHeadlessHost.Create();
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
        using var host = UIHeadlessHost.Create();
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
