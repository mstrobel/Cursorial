using Cursorial.Output;
using Cursorial.Terminal;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The Kitty glyph-height caret band (proposal-glyph-runs §4): on a terminal with the negotiated
/// multiple-cursors capability, a visible caret publishing <c>Rows &gt; 1</c> emits ONE
/// rectangle-form escape putting a beam extra cursor on every band row above the anchor
/// (<c>CSI &gt; 2;4:top:col:bottom:col SP q</c>, 1-based) — the hardware cursor renders the
/// bottom row itself and is never doubled. Extra cursors are screen-fixed, so a moved / shrunk /
/// hidden band first clears the standing extras (<c>CSI &gt; 0;4 SP q</c>); a 1-row caret or a
/// non-supporting terminal emits no extra-cursor bytes at all.
/// </summary>
public sealed class CaretBandEmissionTests
{
    private const string Clear = "\x1b[>0;4 q";

    private static string FrameText(UIHeadlessHost host)
        => System.Text.Encoding.ASCII.GetString(host.LastFrameBytes.ToArray());

    private static UIHeadlessHost CreateHost(TerminalCapabilities? capabilities = null)
    {
        var options = capabilities is { } caps
            ? new UIHeadlessHostOptions { CaptureFrameBytes = true, Capabilities = caps }
            : new UIHeadlessHostOptions { CaptureFrameBytes = true };

        var host = UIHeadlessHost.Create(options);
        host.ShowRoot(new Probe(20, 10) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());
        return host;
    }

    [Fact]
    public void VisibleCaret_RowsThree_EmitsBeamBandAboveTheAnchor()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        // Window (1, 2), band height 3: the extras beam rows 0..1 of column 1 — on the wire
        // (1-based, top:left:bottom:right) that is 1:2:2:2. Row 2 stays the hardware cursor's.
        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        var bytes = FrameText(host);
        Assert.Contains("\x1b[>2;4:1:2:2:2 q", bytes);
        Assert.DoesNotContain(Clear, bytes); // nothing stood before — no clear on first emission

        // The anchor contract: the hardware cursor sits on the band's BOTTOM row.
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(1, host.FrameBuffer.CursorColumn);
        Assert.Equal(2, host.FrameBuffer.CursorRow);
    }

    [Fact]
    public void CaretMove_ClearsTheStandingExtras_ThenReEmitsTheBand()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        // Extra cursors are screen-fixed — the move must clear before re-emitting, in that order.
        host.Application.CaretService.Publish(root, column: 2, row: 3, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        var bytes = FrameText(host);
        var clearIndex = bytes.IndexOf(Clear, StringComparison.Ordinal);
        var bandIndex = bytes.IndexOf("\x1b[>2;4:2:3:3:3 q", StringComparison.Ordinal);
        Assert.True(clearIndex >= 0, "the standing extras must be cleared on a band move");
        Assert.True(bandIndex >= 0, "the moved band must re-emit");
        Assert.True(clearIndex < bandIndex, "the clear must precede the re-emitted band");
    }

    [Fact]
    public void RowsShrinkToOne_ClearsTheBand_AndEmitsNoNewOne()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar);
        host.Application.RequestRender();
        host.RunFrame();

        var bytes = FrameText(host);
        Assert.Contains(Clear, bytes);
        Assert.DoesNotContain("\x1b[>2;4", bytes);
    }

    [Fact]
    public void CaretHide_ClearsTheBand()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        host.Application.CaretService.Clear(root);
        host.Application.RequestRender();
        host.RunFrame();

        var bytes = FrameText(host);
        Assert.Contains(Clear, bytes);
        Assert.DoesNotContain("\x1b[>2;4", bytes);
        Assert.False(host.FrameBuffer.CursorVisible);
    }

    [Fact]
    public void UnchangedBand_ReEmitsNothing()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        // A forced frame over a clean tree with an unmoved band emits zero bytes — the extras
        // are retained terminal state, not per-frame traffic.
        host.Application.RequestRender();
        host.RunFrame();

        Assert.Equal(0, host.LastFrameBytes.Length);
    }

    [Fact]
    public void SingleRowCaret_EmitsNoExtraCursorBytes()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar);
        host.Application.RequestRender();
        host.RunFrame();

        // Byte-identical to today: the hardware cursor moves, no `CSI >` sequence of any form.
        Assert.DoesNotContain("\x1b[>", FrameText(host));
        Assert.True(host.FrameBuffer.CursorVisible);
    }

    [Fact]
    public void NonSupportingTerminal_TallCaret_EmitsNoExtraCursorBytes()
    {
        using var host = CreateHost(HeadlessCapabilities.NoMultipleCursors);
        var root = host.Application.RootElement!;

        host.Application.CaretService.Publish(root, column: 1, row: 2, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        // The capability gate: without negotiated multiple-cursors support the Rows request is
        // ignored on the wire — single bottom-row hardware caret, zero extra-cursor bytes.
        Assert.DoesNotContain("\x1b[>", FrameText(host));
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(2, host.FrameBuffer.CursorRow);
    }

    [Fact]
    public void CaretOnTheTopRow_LeavesNoRoomAboveTheAnchor_EmitsNoBand()
    {
        using var host = CreateHost();
        var root = host.Application.RootElement!;

        // Row 0 with Rows 3: every band row above the anchor is off-screen — nothing to beam.
        host.Application.CaretService.Publish(root, column: 1, row: 0, CursorShape.BlinkingBar, rows: 3);
        host.Application.RequestRender();
        host.RunFrame();

        Assert.DoesNotContain("\x1b[>", FrameText(host));
        Assert.Equal(0, host.FrameBuffer.CursorRow);
    }
}
