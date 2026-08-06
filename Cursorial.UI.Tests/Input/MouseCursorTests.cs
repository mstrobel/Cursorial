using System.Buffers;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Terminal;
using Cursorial.Tests.UI.InputMatrix;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Input;

/// <summary>
/// Element-level mouse cursors (design doc §7.6, the P2.5 batch): <see cref="UIElement.Cursor"/>
/// resolution riding the hover/capture machinery in S3, equality-gated OSC 22 emission through
/// S6's <c>QueueControlSequence</c>, the <c>OutputProtocolCapabilities.MouseCursorShape</c>
/// capability gate, and the teardown / renegotiation resets — asserted on the wire bytes under
/// the <see cref="HeadlessCapabilities"/> presets.
/// </summary>
public class MouseCursorTests
{
    // ───────────────────────────── wire-byte helpers ─────────────────────────────

    private static byte[] SetSequence(MouseCursorShape shape)
    {
        var writer = new ArrayBufferWriter<byte>();
        MouseCursorWriter.WriteSet(writer, shape);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] ResetSequence()
    {
        // "Back to default" is an explicit set of the named "default" shape, never the
        // empty-payload WriteReset — kitty honors empty-as-reset, Ghostty ignores it (§7.6).
        var writer = new ArrayBufferWriter<byte>();
        MouseCursorWriter.WriteSet(writer, MouseCursorShape.Default);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>The OSC 22 envelope opener — counting it counts every pointer-shape sequence of any kind.</summary>
    private static ReadOnlySpan<byte> Osc22Prefix => "\x1b]22;"u8;

    private static int Count(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        while (true)
        {
            var index = haystack.IndexOf(needle);
            if (index < 0)
                return count;

            count++;
            haystack = haystack[(index + needle.Length)..];
        }
    }

    // ───────────────────────────── the fixture ─────────────────────────────

    /// <summary>
    /// The §5/§7 matrix tree with byte capture: <c>Root</c> (Canvas-shaped, 80×24) with
    /// <c>Left</c> at (10,5,10×4), <c>Right</c> at (30,5,10×4), and <c>Leaf</c> a child of
    /// <c>Left</c> at terminal (12,6,4×2). Shown and settled; cursor values are set per test.
    /// </summary>
    private sealed class CursorHost : IDisposable
    {
        private CursorHost(UIHeadlessHost host, CanvasProbe root, CanvasProbe left, Probe right, Probe leaf)
        {
            Host = host;
            Root = root;
            Left = left;
            Right = right;
            Leaf = leaf;
        }

        public UIHeadlessHost Host { get; }

        public CanvasProbe Root { get; }

        public CanvasProbe Left { get; }

        public Probe Right { get; }

        public Probe Leaf { get; }

        public InputDispatcher Dispatcher => Host.Application.InputDispatcher;

        public static CursorHost Create(TerminalCapabilities? capabilities = null)
        {
            var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
            {
                Capabilities = capabilities ?? HeadlessCapabilities.KittyTruecolor,
                CaptureFrameBytes = true
            });

            var log = new List<string>();
            var root = new CanvasProbe("Root", log, 80, 24);
            var left = new CanvasProbe("Left", log, 10, 4);
            var right = new Probe("Right", log, 10, 4);
            var leaf = new Probe("Leaf", log, 4, 2);
            root.Place(left, 10, 5);
            root.Place(right, 30, 5);
            left.Place(leaf, 2, 1); // terminal (12, 6)

            host.ShowRoot(root);
            host.RunFrame(); // settle layout + first render — HitTest is valid after it
            return new CursorHost(host, root, left, right, leaf);
        }

        /// <summary>Sends one mouse move and runs one frame; returns the frame's emitted bytes.</summary>
        public byte[] MoveAndPump(int column, int row)
        {
            Host.SendMouseMove(column, row);
            Host.RunFrame();
            return Host.LastFrameBytes.ToArray();
        }

        /// <summary>Runs one frame; returns its emitted bytes.</summary>
        public byte[] Pump()
        {
            Host.RunFrame();
            return Host.LastFrameBytes.ToArray();
        }

        public void Dispose() => Host.Dispose();
    }

    // ───────────────────────────── emission: enter / leave / equality gate ─────────────────────────────

    [Fact]
    public void HoverEnter_EmitsExactlyOneSetSequence()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;

        var bytes = fixture.MoveAndPump(13, 7); // over Leaf (null) → walks up to Left

        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Pointer)));
        Assert.Equal(1, Count(bytes, Osc22Prefix));
        Assert.Equal(MouseCursorShape.Pointer, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public void MoveWithinSameElement_NoReEmission()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        var bytes = fixture.MoveAndPump(14, 7); // same leaf, one cell over

        Assert.Equal(0, Count(bytes, Osc22Prefix));
    }

    [Fact]
    public void StationaryFrames_NoReEmission_PerFrameUpdateHoverIsEqualityGated()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        for (var frame = 0; frame < 3; frame++)
        {
            fixture.Host.Application.RequestRender(); // force a rendered frame → UpdateHover re-resolves
            Assert.Equal(0, Count(fixture.Pump(), Osc22Prefix));
        }
    }

    [Fact]
    public void HoverLeave_EmitsExactlyOneReset()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        var bytes = fixture.MoveAndPump(0, 0); // root only — no Cursor anywhere on the chain

        Assert.Equal(1, Count(bytes, ResetSequence()));
        Assert.Equal(1, Count(bytes, Osc22Prefix));
        Assert.Null(fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public void SameEffectiveShape_AcrossDifferentElements_NoReEmission()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.Right.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7); // over Left's subtree

        var bytes = fixture.MoveAndPump(31, 6); // chain changes to Right; effective shape doesn't

        Assert.Equal(0, Count(bytes, Osc22Prefix));
        Assert.Equal(MouseCursorShape.Pointer, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public void CursorPropertyChange_ReResolvesOnNextRenderedFrame()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        // [no invalidation] — the property change alone schedules nothing; the next rendered
        // frame's UpdateHover re-resolution picks it up (doc §7.6).
        fixture.Left.Cursor = MouseCursorShape.Text;
        fixture.Host.Application.RequestRender();
        var bytes = fixture.Pump();

        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Text)));
        Assert.Equal(1, Count(bytes, Osc22Prefix));
    }

    [Fact]
    public void TerminalFocusOut_ClearsToTerminalDefault()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        fixture.Host.SendInput(new Cursorial.Input.Events.FocusEvent
        {
            HasFocus = false,
            Timestamp = fixture.Host.Time.GetUtcNow()
        });
        var bytes = fixture.Pump();

        Assert.Equal(1, Count(bytes, ResetSequence()));
        Assert.Null(fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    // ───────────────────────────── resolution precedence ─────────────────────────────

    [Fact]
    public void LeafCursor_WinsOverAncestor()
    {
        using var fixture = CursorHost.Create();
        fixture.Leaf.Cursor = MouseCursorShape.Text;
        fixture.Left.Cursor = MouseCursorShape.Pointer;

        var bytes = fixture.MoveAndPump(13, 7); // over Leaf

        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Text)));
        Assert.Equal(MouseCursorShape.Text, fixture.Dispatcher.EffectiveCursorShapeInternal);

        bytes = fixture.MoveAndPump(10, 5); // inside Left, outside Leaf

        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Pointer)));
        Assert.Equal(MouseCursorShape.Pointer, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public void NullCursor_WalksUpTheHoverChain()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Grab; // Leaf stays null

        fixture.MoveAndPump(13, 7); // over Leaf

        Assert.Equal(MouseCursorShape.Grab, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public void CaptureTarget_WinsOverHoverChain_AndReleaseRestoresIt()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.Right.Cursor = MouseCursorShape.Text;
        fixture.MoveAndPump(13, 7); // hovering Left's subtree → Pointer

        Assert.True(fixture.Dispatcher.CaptureMouse(fixture.Right));
        var bytes = fixture.Pump();

        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Text)));
        Assert.Equal(1, Count(bytes, Osc22Prefix));
        Assert.Equal(MouseCursorShape.Text, fixture.Dispatcher.EffectiveCursorShapeInternal);

        fixture.Dispatcher.ReleaseMouseCapture(fixture.Right);
        bytes = fixture.Pump();

        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Pointer))); // back to the hover chain's resolution
        Assert.Equal(1, Count(bytes, Osc22Prefix));
        Assert.Equal(MouseCursorShape.Pointer, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public void CaptureTarget_NullCursor_WalksItsAncestorChain()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Move; // Leaf stays null

        Assert.True(fixture.Dispatcher.CaptureMouse(fixture.Leaf)); // no prior mouse event needed
        fixture.Host.RunFrame();

        Assert.Equal(MouseCursorShape.Move, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    // ───────────────────────────── the capability gate ─────────────────────────────

    [Fact]
    public void Unsupported_ZeroBytes_AndNoTracking()
    {
        using var fixture = CursorHost.Create(HeadlessCapabilities.NoMouseCursorShape);
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.Right.Cursor = MouseCursorShape.Text;

        var bytes = fixture.MoveAndPump(13, 7); // hover works (motion is on) — emission must not

        Assert.Equal(0, Count(bytes, Osc22Prefix));
        Assert.True(fixture.Leaf.IsPointerOver); // the hover machinery itself is untouched
        Assert.Null(fixture.Dispatcher.EffectiveCursorShapeInternal);

        Assert.True(fixture.Dispatcher.CaptureMouse(fixture.Right));
        bytes = fixture.Pump();

        Assert.Equal(0, Count(bytes, Osc22Prefix));
        Assert.Null(fixture.Dispatcher.EffectiveCursorShapeInternal); // no tracking cost when unsupported
    }

    [Fact]
    public void Teardown_EmitsReset_WhenSupported()
    {
        var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        fixture.Dispose();

        Assert.Equal(1, Count(fixture.Host.TeardownBytes.Span, ResetSequence()));
    }

    [Fact]
    public void Teardown_EmitsNothing_WhenUnsupported()
    {
        var fixture = CursorHost.Create(HeadlessCapabilities.NoMouseCursorShape);
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7);

        fixture.Dispose();

        Assert.Equal(0, Count(fixture.Host.TeardownBytes.Span, Osc22Prefix));
    }

    // ───────────────────────────── renegotiation gate flips ─────────────────────────────

    [Fact]
    public async Task Renegotiate_GateLoss_ResetsOnce_ThenStaysSilent()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.Right.Cursor = MouseCursorShape.Text;
        fixture.MoveAndPump(13, 7); // a shape is active on the wire

        fixture.Host.Terminal.ScriptRenegotiatedCapabilities(HeadlessCapabilities.NoMouseCursorShape);
        await fixture.Host.Application.RenegotiateAsync();

        // Exactly 1 OSC 22 sequence in this frame, and it is the reset: the renegotiate path wrote
        // it directly because the OLD gate was on; the dispatcher's capability fan-out then ran its
        // re-resolution, but the NEW gate is off, so it emitted nothing. The next frame's drain
        // flushes the reset alongside the rebuild's redraw.
        var bytes = fixture.Pump();
        Assert.Equal(1, Count(bytes, ResetSequence()));
        Assert.Equal(1, Count(bytes, Osc22Prefix));

        bytes = fixture.MoveAndPump(31, 6); // over Right (Cursor = Text) — silently inert now
        Assert.Equal(0, Count(bytes, Osc22Prefix));
        Assert.Null(fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact]
    public async Task Renegotiate_GateGain_ReEmitsTheActiveShape_WithoutNewMotion()
    {
        using var fixture = CursorHost.Create(HeadlessCapabilities.NoMouseCursorShape);
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.MoveAndPump(13, 7); // chain is live; nothing emitted (gate off)

        fixture.Host.Terminal.ScriptRenegotiatedCapabilities(HeadlessCapabilities.KittyTruecolor);
        await fixture.Host.Application.RenegotiateAsync();
        var bytes = fixture.Pump();

        // No reset (the old gate was off — nothing to re-baseline); the dispatcher's capability
        // fan-out re-resolved against the surviving chain and re-emitted exactly one set.
        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Pointer)));
        Assert.Equal(1, Count(bytes, Osc22Prefix));
        Assert.Equal(MouseCursorShape.Pointer, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    // ───────────────────────────── the property itself ─────────────────────────────

    [Fact]
    public void CursorProperty_DefaultsToNull_AndCarriesNoEffects()
    {
        using var fixture = CursorHost.Create();

        Assert.Null(fixture.Leaf.Cursor);

        fixture.Host.RunUntilIdle();
        fixture.Leaf.Cursor = MouseCursorShape.Crosshair; // no invalidation: the tree stays idle

        Assert.True(fixture.Host.Application.IsIdle);
        Assert.Equal(MouseCursorShape.Crosshair, fixture.Leaf.Cursor);
    }

    // ───────────────────────────── ForceCursor + the QueryCursor event (P10, §7.6) ─────────────────────────────

    [Fact] // ForceCursor on an ancestor overrides a nearer descendant's cursor (WPF parity).
    public void ForceCursor_OnAncestor_OverridesDescendant()
    {
        using var fixture = CursorHost.Create();
        fixture.Leaf.Cursor = MouseCursorShape.Text;
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.Left.ForceCursor = true;

        var bytes = fixture.MoveAndPump(13, 7); // over Leaf — but Left forces its Pointer over Leaf's Text

        Assert.Equal(MouseCursorShape.Pointer, fixture.Dispatcher.EffectiveCursorShapeInternal);
        Assert.Equal(1, Count(bytes, SetSequence(MouseCursorShape.Pointer)));
    }

    [Fact] // ForceCursor with no Cursor set is inert — the descendant still wins.
    public void ForceCursor_WithoutCursor_IsInert()
    {
        using var fixture = CursorHost.Create();
        fixture.Leaf.Cursor = MouseCursorShape.Text;
        fixture.Left.ForceCursor = true; // Left.Cursor is null → nothing to force

        fixture.MoveAndPump(13, 7);

        Assert.Equal(MouseCursorShape.Text, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact] // A QueryCursor handler that claims the cursor (Handled) overrides the resolved shape.
    public void QueryCursorHandler_OverridesTheResolvedCursor()
    {
        using var fixture = CursorHost.Create();
        fixture.Left.Cursor = MouseCursorShape.Pointer;
        fixture.Leaf.AddHandler(UIElement.QueryCursorEvent, static (_, e) =>
        {
            e.Cursor = MouseCursorShape.Crosshair;
            e.Handled = true; // claim it so the ancestor's class stage doesn't override
        });

        fixture.MoveAndPump(13, 7); // over Leaf

        Assert.Equal(MouseCursorShape.Crosshair, fixture.Dispatcher.EffectiveCursorShapeInternal);
    }

    [Fact] // QueryCursor bubbles leaf→root.
    public void QueryCursor_Bubbles_LeafToRoot()
    {
        using var fixture = CursorHost.Create();
        var order = new List<string>();
        fixture.Leaf.AddHandler(UIElement.QueryCursorEvent, (_, _) => order.Add("leaf"));
        fixture.Left.AddHandler(UIElement.QueryCursorEvent, (_, _) => order.Add("left"));
        fixture.Root.AddHandler(UIElement.QueryCursorEvent, (_, _) => order.Add("root"));

        fixture.MoveAndPump(13, 7);

        Assert.Equal(new[] { "leaf", "left", "root" }, order);
    }
}
