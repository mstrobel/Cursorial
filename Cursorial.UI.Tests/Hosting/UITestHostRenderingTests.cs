// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The P1 exit criterion (design doc §14): "UITestHost renders a static panel tree headlessly with
/// cell/byte assertions green" — plus the caret write, the parser-inclusive <c>SendBytes</c> path
/// on the fake clock, and the idle protocol.
/// </summary>
public sealed class UITestHostRenderingTests
{
    [Fact]
    public void StaticPanelTree_RendersHeadlessly_CellAndByteAssertions()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { CaptureFrameBytes = true });
        var panel = new StackPanel();
        panel.Children.Add(new Probe(6, 1) { FillGlyph = "A", HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new Probe(6, 1) { FillGlyph = "B", HorizontalAlignment = HorizontalAlignment.Left });
        host.ShowRoot(panel);

        host.RunFrame();

        // Cell assertions: two stacked rows, glyphs where the probes painted.
        Assert.StartsWith("AAAAAA", host.GetRowText(0));
        Assert.StartsWith("BBBBBB", host.GetRowText(1));
        Assert.Equal("A", host.GetCell(0, 0).Grapheme);
        Assert.Equal("B", host.GetCell(5, 1).Grapheme);
        // A cell no probe painted carries the SURFACE's blank, which is not necessarily CellStyle.Default
        // (what Cell.IsBlank tests): on a truecolor host the buffer's blank is the terminal's reported default
        // fg/bg promoted to RGB by CellBuffer.DeriveDefaultStyle, and the frame compositor resets the cells it
        // rewrites to that derived blank rather than flattening them back to Color.Default.
        var unpainted = host.GetCell(6, 0);
        Assert.Equal(CellKind.Single, unpainted.Kind);
        Assert.True(string.IsNullOrEmpty(unpainted.Grapheme));
        Assert.Equal(host.FrameBuffer.DefaultStyle, unpainted.Style);

        // Byte assertions: the first frame emitted a non-empty delta containing the painted runs.
        var wire = System.Text.Encoding.UTF8.GetString(host.LastFrameBytes.Span);
        Assert.Contains("AAAAAA", wire);
        Assert.Contains("BBBBBB", wire);

        // A second frame with no changes emits nothing — the retained-buffer diff contract.
        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length);
    }

    [Fact]
    public void RunUntilIdle_ReachesIdle_AndReportsWork()
    {
        using var host = UIHeadlessHost.Create();
        host.ShowRoot(new Probe(4, 1));
        Assert.False(host.Application.IsIdle); // layout + render pending

        Assert.True(host.RunUntilIdle());
        Assert.True(host.Application.IsIdle);

        host.Application.RootElement!.InvalidateMeasure();
        Assert.False(host.Application.IsIdle);
        Assert.True(host.RunUntilIdle());
    }

    [Fact]
    public void RunFrames_EarlyExitsOnShutdown()
    {
        using var host = UIHeadlessHost.Create();
        host.Application.Shutdown();
        Assert.Equal(0, host.RunFrames(5));
    }

    [Fact]
    public void Caret_PublishedState_ReachesBufferCursor()
    {
        using var host = UIHeadlessHost.Create();
        var panel = new StackPanel();
        var editor = new Probe(10, 1);
        panel.Children.Add(new Probe(10, 1));
        panel.Children.Add(editor);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());
        Assert.False(host.FrameBuffer.CursorVisible); // hidden until someone publishes

        // Publish alone arms the Phase-6 render gate (no manual RequestRender): a pure caret
        // move — arrow key, no content change — must not leave the terminal cursor stale.
        host.Application.CaretService.Publish(editor, column: 3, row: 0, CursorShape.SteadyBar);
        Assert.False(host.Application.IsIdle);
        host.RunFrame();

        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(3, host.FrameBuffer.CursorColumn);
        Assert.Equal(1, host.FrameBuffer.CursorRow); // editor sits on the stacked second row
        Assert.Equal(CursorShape.SteadyBar, host.FrameBuffer.CursorShape);
        Assert.True(host.RunUntilIdle());

        // The equality gate: re-publishing the identical winning caret changes nothing and does
        // not re-arm the loop.
        host.Application.CaretService.Publish(editor, column: 3, row: 0, CursorShape.SteadyBar);
        Assert.True(host.Application.IsIdle);

        host.Application.CaretService.Clear(editor);
        Assert.False(host.Application.IsIdle); // Clear arms the gate too
        host.RunFrame();
        Assert.False(host.FrameBuffer.CursorVisible);
    }

    [Fact]
    public void SendBytes_ParserPath_DeliversKeyEvents()
    {
        using var host = UIHeadlessHost.Create();
        var dispatched = new List<InputEvent>();
        host.Application.InputDispatchTarget = new CollectingTarget(dispatched);

        host.SendBytes("hi"u8);
        host.DrainParsedInputAsync().GetAwaiter().GetResult();
        host.RunFrame();

        Assert.Equal(2, dispatched.Count);
        Assert.Equal("h", ((KeyEvent)dispatched[0]).Text.ToString());
        Assert.Equal("i", ((KeyEvent)dispatched[1]).Text.ToString());
    }

    [Fact]
    public void SendBytes_LoneEsc_CommitsOnlyAfterFakeClockCrossesAmbiguityWindow()
    {
        using var host = UIHeadlessHost.Create();
        var dispatched = new List<InputEvent>();
        host.Application.InputDispatchTarget = new CollectingTarget(dispatched);

        host.SendBytes([0x1b]);
        host.DrainParsedInputAsync().GetAwaiter().GetResult();
        host.RunFrame();
        Assert.Empty(dispatched); // held pending — never commits on the wall clock

        host.AdvanceTime(TimeSpan.FromMilliseconds(100)); // crosses the 50 ms ambiguity window
        host.DrainParsedInputAsync().GetAwaiter().GetResult();
        host.RunFrame();

        var key = Assert.IsType<KeyEvent>(Assert.Single(dispatched));
        Assert.Equal(Key.Escape, key.Key);
    }

    [Fact]
    public void SendInput_DefaultTimestamp_StampedFromFakeClock()
    {
        using var host = UIHeadlessHost.Create();
        var dispatched = new List<InputEvent>();
        host.Application.InputDispatchTarget = new CollectingTarget(dispatched);
        host.Time.Advance(TimeSpan.FromSeconds(5));

        host.SendInput(new KeyEvent
        {
            Key = Key.Enter,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Timestamp = default
        });
        host.RunFrame();

        Assert.Equal(host.Time.GetUtcNow(), Assert.Single(dispatched).Timestamp);
    }

    [Fact]
    public void RootElement_Swap_DetachesOldAndRendersNew()
    {
        using var host = UIHeadlessHost.Create();
        var first = new Probe(4, 1) { FillGlyph = "1" };
        host.ShowRoot(first);
        Assert.True(host.RunUntilIdle());
        Assert.StartsWith("1111", host.GetRowText(0));

        var second = new Probe(4, 1) { FillGlyph = "2" };
        host.ShowRoot(second);
        Assert.True(host.RunUntilIdle());

        Assert.False(first.IsAttachedToTree);
        Assert.True(second.IsAttachedToTree);
        Assert.StartsWith("2222", host.GetRowText(0));
    }

    [Fact]
    public void SupportsAltKeyTracking_FollowsUndecoratedSnapshot()
    {
        using var kitty = UIHeadlessHost.Create();
        Assert.True(kitty.Application.SupportsAltKeyTracking); // Kitty: up/down + repeats

        using var legacy = UIHeadlessHost.Create(new UIHeadlessHostOptions { Capabilities = HeadlessCapabilities.Ansi16Legacy });
        Assert.False(legacy.Application.SupportsAltKeyTracking);
    }

    [Fact]
    public void EffectiveInputCapabilities_ReflectClickDecoration()
    {
        using var host = UIHeadlessHost.Create();
        // The default pipeline applies click-count synthesis (S3 contract defaults).
        Assert.True(host.Application.EffectiveInputCapabilities.Mouse.SynthesizesClickCounts);
        Assert.False(host.Application.EffectiveInputCapabilities.Mouse.SynthesizesClicks);
        // The undecorated snapshot stays pristine.
        Assert.False(host.Application.Capabilities.Input.Mouse.SynthesizesClickCounts);
    }

    [Fact]
    public void TwoParallelHosts_OnDifferentThreads_NeverCrossWire()
    {
        using var host = UIHeadlessHost.Create();
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "A" });
        Assert.True(host.RunUntilIdle());

        var otherThreadOk = WorkerThread.Run(() =>
        {
            using var other = UIHeadlessHost.Create();
            other.ShowRoot(new Probe(4, 1) { FillGlyph = "B" });
            return other.RunUntilIdle() && other.GetRowText(0).StartsWith("BBBB", StringComparison.Ordinal);
        });

        Assert.True(otherThreadOk);
        Assert.StartsWith("AAAA", host.GetRowText(0)); // ours untouched; Current is thread-local
        Assert.Same(host.Application, UIApplication.Current);
    }

    [Fact]
    public void CleanFrame_AllocatesNothing()
    {
        using var host = UIHeadlessHost.Create();
        var panel = new StackPanel();
        panel.Children.Add(new Probe(6, 1) { FillGlyph = "A" });
        panel.Children.Add(new Probe(6, 1) { FillGlyph = "B" });
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());

        for (var warm = 0; warm < 32; warm++)
            host.RunFrame(); // settle tiering / lazy caches

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            host.RunFrame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated); // the steady-state frame path is allocation-free (ground rule)
    }

    private sealed class CollectingTarget(List<InputEvent> dispatched) : IInputDispatchTarget
    {
        public InputDispatchResult Dispatch(InputEvent inputEvent)
        {
            dispatched.Add(inputEvent);
            return InputDispatchResult.DispatchedUnhandled;
        }

        public void UpdateHover()
        {
        }

        public void OnCapabilitiesChanged(Terminal.TerminalCapabilities capabilities)
        {
        }
    }
}
