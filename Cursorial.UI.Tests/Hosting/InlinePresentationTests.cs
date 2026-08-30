// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using InputProbe = Cursorial.Tests.UI.InputMatrix.Probe;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// Inline applications (<see cref="UIApplicationBuilder.UseInline"/>): DSR-CPR origin discovery
/// (render gated until the reply or its timeout fallback), content-fitted region height (grow,
/// shrink-wipe, growth-past-bottom scroll), screen→region mouse translation, the resize
/// re-anchor, and the Clear / Retain exit behaviors.
/// </summary>
public sealed class InlinePresentationTests
{
    // The LEGACY absolute-origin path (deprecated, removed before v1.0). Relative moves are the default
    // now, so these tests opt into absolute explicitly to keep exercising that path while it exists.
    private static UIHeadlessHost CreateInline(int? maxHeight = null,
                                               InlineExitBehavior exitBehavior = InlineExitBehavior.Clear)
        => UIHeadlessHost.Create(new UIHeadlessHostOptions
                                 {
                                     InitialSize = new Size(40, 12),
                                     CaptureFrameBytes = true,
                                     ConfigureBuilder = b => b.UseInline(maxHeight, exitBehavior, relativeMoves: false)
                                 });

    /// <summary>Delivers the terminal's CPR reply (1-based wire coordinates) through the real parser.</summary>
    private static void ReplyCursorPosition(UIHeadlessHost host, int row, int column = 1)
    {
        host.SendBytes(Encoding.ASCII.GetBytes($"\x1b[{row};{column}R"));
        host.DrainParsedInputAsync().GetAwaiter().GetResult();
    }

    private static string Frame(UIHeadlessHost host) => Encoding.UTF8.GetString(host.LastFrameBytes.Span);

    [Fact]
    public void Startup_HoldsRendering_UntilCursorReport_ThenPaintsAtReportedOrigin()
    {
        using var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });

        // Layout runs and the region fits to content while the origin is still unknown — but
        // nothing goes on the wire.
        host.RunFrame();
        Assert.Equal(3, host.FrameBuffer.Rows);
        Assert.Equal(40, host.FrameBuffer.Columns);
        Assert.Empty(host.LastFrameBytes.ToArray());

        // The shell left the cursor at row 5, column 1 → the region tops at terminal row 5.
        ReplyCursorPosition(host, row: 5);
        host.RunFrame();

        var frame = Frame(host);
        Assert.Contains("\x1b[5;1H\x1b[0J", frame);  // region-scoped full-redraw erase at the origin
        Assert.Contains("\x1b[6;1H", frame);          // buffer row 1 → terminal row 6
        Assert.Contains("\x1b[7;1H", frame);          // buffer row 2 → terminal row 7
        Assert.Contains("XXXXXXXXXX", frame);
        Assert.DoesNotContain("\x1b[2J", frame);      // the screen is never cleared
        Assert.DoesNotContain("\x1b[?1049h", frame);  // the alt screen is never entered
    }

    [Fact]
    public void UseInline_DefaultsToRelativeMoves()
    {
        // Relative-move rendering is the STANDARD inline behavior now — the flag defaults true, so a caller
        // that does not specify gets a floating region. (The legacy absolute-origin path stays opt-in via
        // relativeMoves: false — CreateInline above — deprecated pending removal before v1.0.)
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(40, 12),
                                                   CaptureFrameBytes = true,
                                                   ConfigureBuilder = b => b.UseInline() // no relativeMoves arg → the default
                                               });
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        host.RunFrame();

        var frame = Frame(host);
        Assert.Contains("XXXXXXXXXX", frame);         // it rendered...
        Assert.DoesNotContain("\x1b[7;1H", frame);    // ...but NOT via the absolute per-row CUP (buffer row 2 → term row 7)
                                                      // the legacy path emits — so the default is the relative floating region.
    }

    [Fact]
    public void Startup_MidLinePrompt_RegionStartsOnNextLine()
    {
        using var host = CreateInline();
        host.ShowRoot(new Probe(10, 2) { FillGlyph = "X" });
        host.RunFrame();

        // The shell left the cursor mid-line (a prompt without a trailing newline, column 9):
        // the region must not paint over it — it starts on the NEXT line.
        ReplyCursorPosition(host, row: 5, column: 9);
        host.RunFrame();

        Assert.Contains("\x1b[6;1H\x1b[0J", Frame(host));
    }

    [Fact]
    public void Region_GrowsWithContent()
    {
        using var host = CreateInline();
        var probe = new Probe(10, 2) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 2);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(2, host.FrameBuffer.Rows);

        probe.Natural = new Size(10, 4);
        probe.InvalidateMeasure();
        host.RunFrame();

        Assert.Equal(4, host.FrameBuffer.Rows);
        var frame = Frame(host);
        Assert.Contains("\x1b[2;1H\x1b[0J", frame);   // full region repaint at the (unmoved) origin
        Assert.Contains("\x1b[5;1H", frame);          // the new bottom row (buffer row 3 → terminal row 5)
    }

    [Fact]
    public void Region_Shrinks_WipesVacatedRows()
    {
        using var host = CreateInline();
        var probe = new Probe(10, 4) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 3);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(4, host.FrameBuffer.Rows);

        probe.Natural = new Size(10, 2);
        probe.InvalidateMeasure();
        host.RunFrame();

        Assert.Equal(2, host.FrameBuffer.Rows);

        // The full-redraw erase lands at the region top and sweeps to end-of-screen — the two
        // vacated rows go with it, and the origin does not move (regions shrink at the bottom).
        Assert.Contains("\x1b[3;1H\x1b[0J", Frame(host));
    }

    [Fact]
    public void Growth_PastTerminalBottom_ScrollsWithLineFeeds()
    {
        using var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();

        // The shell prompt sat on the LAST row (12): a 3-row region needs 2 more rows than the
        // screen has below the origin — the frame opens by minting them with literal line feeds
        // from the bottom row (shell history scrolls into the scrollback), then paints at the
        // origin that scrolled up with everything else.
        ReplyCursorPosition(host, row: 12);
        host.RunFrame();

        var frame = Frame(host);
        Assert.Contains("\x1b[r\x1b[12;1H\n\n", frame); // make room: DECSTBM reset (so LF scrolls at the true bottom margin — kitty) + CUP bottom + 2 × LF
        Assert.Contains("\x1b[10;1H\x1b[0J", frame);  // then the full repaint at the moved origin (row 9)
        Assert.DoesNotContain("\x1b[2S", frame);      // no SU — those discard the shell history
    }

    [Fact]
    public void Startup_NoCursorReport_FallsBackToBottomAnchor()
    {
        using var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        Assert.Empty(host.LastFrameBytes.ToArray());

        // The terminal never answers DSR. Past the timeout the region reserves blind: from the
        // bottom row, `height` line feeds guarantee the rows above the cursor are ours.
        host.Time.Advance(TimeSpan.FromSeconds(1.5));
        host.RunFrame();

        var frame = Frame(host);
        Assert.Contains("\x1b[12;1H\n\n\n", frame);
        Assert.Contains("\x1b[10;1H\x1b[0J", frame);
        Assert.Contains("XXXXXXXXXX", frame);
    }

    [Fact]
    public void MaxHeight_CapsTheRegion()
    {
        using var host = CreateInline(maxHeight: 2);
        host.ShowRoot(new Probe(10, 6) { FillGlyph = "X" });
        host.RunFrame();

        Assert.Equal(2, host.FrameBuffer.Rows);
    }

    [Fact]
    public void Mouse_TranslatesIntoRegionSpace_AndIgnoresTheShellArea()
    {
        using var host = CreateInline();
        var log = new List<string>();
        var probe = new InputProbe("p", log, 40, 3);
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 5); // origin = terminal row 4 (0-based)
        Assert.True(host.RunUntilIdle());

        // Screen row 6 = region row 2 — inside the 3-row surface only AFTER translation.
        host.SendClick(column: 2, row: 6);
        Assert.True(host.RunUntilIdle());
        Assert.Contains("p.OnMouseDown", log);

        // Screen row 1 is the shell's estate above the region: not delivered.
        log.Clear();
        host.SendClick(column: 2, row: 1);
        Assert.True(host.RunUntilIdle());
        Assert.DoesNotContain("p.OnMouseDown", log);
    }

    [Fact]
    public void Resize_ReanchorsWithFreshCursorQuery()
    {
        using var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        // The terminal resized: the main buffer rewrapped, so the origin is stale. The frame
        // that applies the resize re-queries DSR-CPR and holds emission until the reply.
        host.SendResize(40, 10);
        host.RunFrame();
        Assert.Contains("\x1b[6n", Frame(host));
        Assert.DoesNotContain("\x1b[0J", Frame(host));

        // The hardware cursor rode the region through the rewrap; its reply re-derives the top.
        ReplyCursorPosition(host, row: 3);
        host.RunFrame();
        Assert.Contains("\x1b[3;1H\x1b[0J", Frame(host));
    }

    [Fact]
    public void Resize_SameSize_DoesNotReanchor()
    {
        using var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        // A resize to the SAME dimensions is a no-op — nothing rewrapped. A spurious same-size
        // SIGWINCH (kitty emits one at startup) must NOT re-query DSR: the re-anchor's reply, landing
        // mid-growth, clamps the region origin to `rows - height` and pre-empts the make-room growth
        // scroll — the region then overpaints shell history instead of scrolling it into scrollback.
        host.SendResize(40, 12); // identical to the initial 40×12
        host.RunFrame();
        Assert.DoesNotContain("\x1b[6n", Frame(host));
    }

    [Fact]
    public void ExitClear_RewindsToOrigin_AndErasesTheRegion()
    {
        var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        host.Dispose();

        var teardown = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        Assert.Contains("\x1b[?25h", teardown);           // cursor shown again
        Assert.Contains("\x1b[5;1H\x1b[0J", teardown);    // rewind to the region top, erase below
        Assert.DoesNotContain("\x1b[2J", teardown);       // never the whole screen
        Assert.DoesNotContain("\x1b[?1049", teardown);    // the alt screen was never involved
    }

    [Fact]
    public void ExitRetain_ParksBelowTheLastFrame()
    {
        var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        // The runtime seam: an application decides AT EXIT TIME to keep the frame standing.
        host.Application.InlineExitBehavior = InlineExitBehavior.Retain;
        host.Dispose();

        // Region rows 4..6 (0-based) → park on the line below the bottom row (terminal row 7),
        // sweep anything staler below it, and leave the frame's rows untouched. The CHA (`[1G`) after
        // the LF is the exit invariant made explicit: the session is still raw (no ONLCR), so the LF
        // alone keeps its column — whoever writes next must start flush-left by CONTRACT, not luck.
        var teardown = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        Assert.Contains("\x1b[7;1H\n\x1b[1G\x1b[0J", teardown);
        Assert.DoesNotContain("\x1b[5;1H\x1b[0J", teardown);
        Assert.DoesNotContain("\x1b[2J", teardown);
    }

    [Fact]
    public void ExitBeforeOriginResolves_LeavesTheShellLineUntouched()
    {
        var host = CreateInline();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame(); // origin still pending — nothing was ever painted

        host.Dispose();

        var teardown = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        Assert.DoesNotContain("\x1b[0J", teardown);
        Assert.DoesNotContain("\x1b[2J", teardown);
    }

    [Fact]
    public void UseInline_RejectsNonPositiveMaxHeight()
    {
        var builder = UIApplication.CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseInline(maxHeight: 0));
    }

    // ── Relative-move inline (UseInline(relativeMoves: true)) — the floating-region path ──────────

    private static UIHeadlessHost CreateInlineRelative()
        => UIHeadlessHost.Create(new UIHeadlessHostOptions
                                 {
                                     InitialSize = new Size(40, 12),
                                     CaptureFrameBytes = true,
                                     ConfigureBuilder = b => b.UseInline(relativeMoves: true)
                                 });

    [Fact]
    public void RelativeMoves_PaintWithRelativeClimb_NotAbsoluteOrigin()
    {
        using var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5); // region tops at terminal row 5
        host.RunFrame();

        var frame = Frame(host);
        // Relative mode climbs from the parked region bottom (CUU) instead of the absolute origin CUP
        // the absolute path would emit (CSI 5;1H / 6;1H / 7;1H).
        Assert.Matches("\\[[0-9]+A", frame);   // a CUU climb is present
        Assert.DoesNotContain("\x1b[5;1H", frame);
        Assert.DoesNotContain("\x1b[6;1H", frame);
        Assert.DoesNotContain("\x1b[7;1H", frame);
        Assert.Contains("XXXXXXXXXX", frame);
        Assert.DoesNotContain("\x1b[2J", frame);
    }

    [Fact]
    public void RelativeMoves_SecondFrame_NeverReaddressesTheStaleAbsoluteOrigin()
    {
        // The reported bug in absolute mode: after the region moves (an unobserved clear), the diff
        // keeps repainting at the ORIGINAL absolute rows — the region "snaps back". Relative mode
        // emits only deltas, so a later frame never re-addresses the stale origin rows.
        using var host = CreateInlineRelative();
        var probe = new Probe(10, 3) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        host.RunFrame();

        probe.FillGlyph = "Y";
        probe.InvalidateVisual();
        host.RunFrame();

        var frame = Frame(host);
        Assert.Contains("Y", frame);                 // the change reached the region
        Assert.DoesNotContain("\x1b[5;1H", frame);   // no absolute origin re-address
        Assert.DoesNotContain("\x1b[6;1H", frame);
        Assert.DoesNotContain("\x1b[7;1H", frame);
    }

    [Fact]
    public void RelativeMoves_ExitClear_ClimbsToRegionTop_NotAbsoluteOrigin()
    {
        var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        host.Dispose();

        var teardown = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        // The frame ended at the caret (row 0 — no focused input), so Clear climbs CUU(caretRow=0) — a
        // no-op — then CHA + ED0 erases the region and below. Never the absolute origin CUP the absolute
        // path emits (CSI 5;1H). Survives an unobserved clear as rendering does.
        Assert.Contains("\x1b[1G\x1b[0J", teardown);
        Assert.DoesNotContain("\x1b[5;1H", teardown);
        Assert.DoesNotContain("\x1b[2J", teardown);
    }

    [Fact]
    public void RelativeMoves_ExitRetain_DropsOneLineBelow_NotAbsoluteOrigin()
    {
        var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        host.Application.InlineExitBehavior = InlineExitBehavior.Retain;
        host.Dispose();

        var teardown = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        // Already parked at the region bottom-left — one LF drops below the last frame, then CHA + erase.
        Assert.Contains("\n\x1b[1G\x1b[0J", teardown);
        Assert.DoesNotContain("\x1b[7;1H", teardown);
        Assert.DoesNotContain("\x1b[2J", teardown);
    }

    // ── Phase 3: the DSR-CPR poll desync heal (relative-inline only) ──────────────────────────────

    // Explicit RunFrame stepping (not RunUntilIdle, which advances fake time far enough to fire a poll
    // on its own) so the interval crossing is controlled precisely.

    [Fact]
    public void RelativeMoves_PollsCursorPosition_OnInterval()
    {
        var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 8); // startup reply
        host.RunFrame();                    // process it → DSR proven, the poll clock starts

        // Past the poll interval, an idle frame emits a background DSR-CPR poll.
        host.Time.Advance(TimeSpan.FromSeconds(2)); // advance the clock only — no catch-up frames
        host.RunFrame();
        Assert.Contains("\x1b[6n", Frame(host));
    }

    [Fact]
    public void RelativeMoves_Poll_ReanchorsAndRepaints_WhenTheRegionMoved()
    {
        var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 8); // startup → origin 7; region [7..9], parked bottom at row 9
        host.RunFrame();

        host.Time.Advance(TimeSpan.FromSeconds(2)); // advance the clock only — no catch-up frames
        host.RunFrame();
        Assert.Contains("\x1b[6n", Frame(host)); // the poll query went out

        // An unobserved clear moved the region up: the parked bottom now reports wire row 5 (0-based 4)
        // → origin 4 − (height−1) = 2, a CHANGE from 7. The poll re-anchors and forces a repaint.
        ReplyCursorPosition(host, row: 5);
        host.RunFrame();
        Assert.Contains("XXXXXXXXXX", Frame(host)); // the region was repainted at the new anchor
    }

    [Fact]
    public void AbsoluteInline_NeverPolls()
    {
        var host = CreateInline(); // relativeMoves: false (the default)
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 8);
        host.RunFrame();

        host.Time.Advance(TimeSpan.FromSeconds(5));
        host.RunFrame();
        Assert.DoesNotContain("\x1b[6n", Frame(host));
    }

    // ── Phase 4: growth/scroll re-validation under the relative model ─────────────────────────────

    [Fact]
    public void RelativeMoves_GrowthPastBottom_ScrollsThenRepaintsRelatively()
    {
        var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 3) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: 12); // shell prompt on the last row → a 3-row region needs 2 more
        host.RunFrame();

        var frame = Frame(host);
        // The host make-room stays absolute — CUP to the physical bottom (always valid) + line feeds; only
        // the RENDER goes relative. So it scrolls with LFs and climbs relatively, and never emits the
        // absolute repaint the absolute path would (CSI 10;1H at the moved origin 9).
        Assert.Contains("\x1b[r\x1b[12;1H\n\n", frame); // make room: DECSTBM reset (kitty scrolls only at the bottom margin) + CUP bottom + 2 × LF
        Assert.DoesNotContain("\x1b[10;1H", frame);  // NOT the absolute repaint at the moved origin
        Assert.Matches("\\[[0-9]+A", frame);          // the render climbs relatively from the scrolled bottom
        Assert.Contains("XXXXXXXXXX", frame);
    }

    // Screen-grid verification (VtScreen actually EXECUTES the emitted moves) — byte-substring
    // assertions can't verify where relative (position-dependent) moves LAND.

    [Fact]
    public void VtScreen_ExecutesRelativeMovesAndScroll()
    {
        var screen = new VtScreen(6, 3);
        screen.Feed(Encoding.UTF8.GetBytes("\x1b[3;1HXY"));  // CUP row 3 (0-based 2), col 1 → "XY" on row 2
        screen.Feed(Encoding.UTF8.GetBytes("\x1b[2A"));       // CUU 2 → row 0
        screen.Feed(Encoding.UTF8.GetBytes("\x1b[1GAB"));     // CHA col 1 (0-based 0) → "AB" on row 0
        Assert.Equal("AB", screen.LineTrimmed(0));
        Assert.Equal("XY", screen.LineTrimmed(2));

        screen.Feed(Encoding.UTF8.GetBytes("\x1b[3;1H\n"));   // to the bottom row, then LF → whole screen scrolls up
        Assert.Equal("XY", screen.LineTrimmed(1));            // row 2 → row 1
        Assert.Equal("", screen.LineTrimmed(2));              // new blank bottom row
    }

    [Fact]
    public void RelativeMoves_GrowthAtBottom_LandsTheRegionOnScreen()
    {
        var screen = new VtScreen(40, 12);
        for (int r = 0; r < 12; r++) { screen.SetCursor(r, 0); screen.Print($"history-{r:00}"); }
        screen.SetCursor(11, 0); // the shell prompt sits on the LAST row

        using var host = CreateInlineRelative();
        var probe = new Probe(10, 1) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: screen.CursorRow + 1); // reply with the prompt row (12)
        host.RunFrame();
        screen.Feed(host.LastFrameBytes.Span); // first paint — a 1-row region on the bottom line

        // The dropdown grows to 5 rows: no room below the bottom-row prompt, so the region must scroll
        // the shell history up. Feed the frame and assert what actually LANDS on screen.
        probe.Natural = new Size(10, 5);
        probe.InvalidateMeasure();
        host.RunFrame();
        screen.Feed(host.LastFrameBytes.Span);

        // The 5-row region occupies the bottom five rows (7..11), full width — NOT clipped to the last
        // line. The Probe stretches to the region width, so each row is all 'X'.
        for (int r = 7; r <= 11; r++)
            Assert.Equal(new string('X', screen.Cols), screen.Line(r));
        // The row just above the region is scrolled shell history, not region content.
        Assert.DoesNotContain('X', screen.Line(6));
        Assert.Contains("history-", screen.LineTrimmed(0));
    }

    [Fact] // a mid-line prompt (CPR column > 1) starts the region on the NEXT line while the cursor is still on
    // the prompt line — one row ABOVE the region. The relative renderer must know, or its first paint erases
    // and overwrites the prompt line (the absolute path re-addresses with CUP and never had the problem).
    public void RelativeMoves_Startup_MidLinePrompt_PaintsBelowThePromptLine()
    {
        var screen = new VtScreen(40, 12);
        for (int r = 0; r < 12; r++) { screen.SetCursor(r, 0); screen.Print($"history-{r:00}"); }
        screen.SetCursor(5, 0); screen.Print("$ prompt: "); // over history-05; the cursor sits mid-line at (5, 10)

        using var host = CreateInlineRelative();
        host.ShowRoot(new Probe(10, 2) { FillGlyph = "X" });
        host.RunFrame();
        ReplyCursorPosition(host, row: screen.CursorRow + 1, column: screen.CursorCol + 1);
        host.RunFrame();
        var frame = Frame(host);
        screen.Feed(host.LastFrameBytes.Span);

        Assert.Contains("\x1b[1B\x1b[1G\x1b[0J", frame);          // the first paint steps DOWN onto the region's line
        Assert.Equal("$ prompt:", screen.LineTrimmed(5));           // the prompt line is untouched
        Assert.Equal(new string('X', screen.Cols), screen.Line(6)); // region rows 0..1 on the two lines below it
        Assert.Equal(new string('X', screen.Cols), screen.Line(7));
        Assert.Contains("history-", screen.LineTrimmed(4));
    }

    [Fact] // the renegotiation rebuild is the same seam: a fresh buffer AND a fresh relative renderer while the
    // physical cursor still sits on the old caret cell — the repaint must climb from there, not from (0,0).
    public async Task RelativeMoves_Renegotiation_RepaintClimbsFromTheCaretRow()
    {
        var screen = new VtScreen(40, 12);
        for (int r = 0; r < 12; r++) { screen.SetCursor(r, 0); screen.Print($"history-{r:00}"); }
        screen.SetCursor(5, 0); // the shell prompt sits on row 5

        using var host = CreateInlineRelative();
        var box = new TextBox { Width = 10, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(new TextBlock { Text = "Prompt" });
        root.Children.Add(box); // the caret rides region row 1
        host.ShowRoot(root);
        host.RunFrame();
        box.Focus();
        ReplyCursorPosition(host, row: 6);
        for (int i = 0; i < 3; i++) { host.RunFrame(); screen.Feed(host.LastFrameBytes.Span); }

        Assert.Equal("Prompt", screen.LineTrimmed(5));
        Assert.Equal(6, screen.CursorRow); // the frame ended on the caret — region row 1

        host.Terminal.ScriptRenegotiatedCapabilities(HeadlessCapabilities.KittyTruecolor);
        await host.Application.RenegotiateAsync();
        for (int i = 0; i < 3; i++) { host.RunFrame(); screen.Feed(host.LastFrameBytes.Span); }

        Assert.Equal("Prompt", screen.LineTrimmed(5));    // the rebuilt renderer's full repaint landed on the origin row...
        Assert.DoesNotContain("Prompt", screen.Line(6));  // ...not a caret-row lower
        Assert.Equal(6, screen.CursorRow);
    }

    [Fact]
    public void RelativeMoves_SpuriousSameSizeResize_StillScrollsAtGrowth()
    {
        // The kitty regression: a spurious same-size SIGWINCH between startup and the region growing
        // past the bottom used to trigger a re-anchor whose reply clamped the origin to `rows - height`,
        // pre-empting the make-room scroll — the region overpainted shell history in place instead of
        // scrolling it into scrollback (Ctrl+L then "healed" by overpainting more). With the no-op
        // resize skipped, the origin stays at the bottom and make-room scrolls as it should.
        var screen = new VtScreen(40, 12);
        for (int r = 0; r < 12; r++) { screen.SetCursor(r, 0); screen.Print($"history-{r:00}"); }
        screen.SetCursor(11, 0); // the shell prompt sits on the LAST row

        using var host = CreateInlineRelative();
        var probe = new Probe(10, 1) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: screen.CursorRow + 1); // reply with the prompt row (12)
        host.RunFrame();
        screen.Feed(host.LastFrameBytes.Span); // first paint — a 1-row region on the bottom line

        // The spurious same-size resize arrives (kitty emits one at startup). Must be a no-op.
        host.SendResize(40, 12); // identical to the initial 40×12
        host.RunFrame();
        screen.Feed(host.LastFrameBytes.Span);

        // The dropdown grows to 5 rows: the region must scroll the history up and land on the bottom
        // five rows (7..11), full width — NOT clipped, NOT overpainting in place.
        probe.Natural = new Size(10, 5);
        probe.InvalidateMeasure();
        host.RunFrame();
        screen.Feed(host.LastFrameBytes.Span);

        for (int r = 7; r <= 11; r++)
            Assert.Equal(new string('X', screen.Cols), screen.Line(r));
        Assert.DoesNotContain('X', screen.Line(6));
        // The distinguishing check: the top history rows SCROLLED off into scrollback. Under the bug
        // (origin clamped, no scroll) history-00 would still sit on row 0, overpainted below.
        Assert.NotEqual("history-00", screen.LineTrimmed(0));
    }

    [Fact]
    public void RelativeMoves_ReanchorMidGrowth_ScrollsInsteadOfClamping()
    {
        // The DISEASE, isolated from the spurious-resize trigger: a re-anchor that observes the region
        // needing to grow past the bottom must leave make-room to SCROLL — it must NOT clamp the origin
        // to `rows - height`, which pins the region on-screen and guarantees `scroll <= 0` (overpaint).
        // Driven by a GENUINE (width-only) resize so the no-op skip does not apply and the row math is
        // unchanged; the re-anchor reply lands after the region has grown.
        using var host = CreateInlineRelative();
        var probe = new Probe(10, 1) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 12); // prompt on the bottom row (12) of the 12-row screen → origin 11
        host.RunFrame();

        // A genuine width-only resize re-anchors (DSR re-query, emission holds); the dropdown grows to
        // 5 rows WHILE the re-anchor is outstanding (bufRows becomes 5).
        host.SendResize(42, 12);
        probe.Natural = new Size(10, 5);
        probe.InvalidateMeasure();
        host.RunFrame();

        // The re-anchor reply: the cursor still rode the 1-row region at the bottom row (12).
        ReplyCursorPosition(host, row: 12);
        host.RunFrame();

        // Origin stays 11 (clamped only on-screen), so make-room scrolls `11 + 5 - 12 = 4`: CUP bottom
        // + line feeds. Under the bug the origin clamps to `12 - 5 = 7`, scroll = 0, and no make-room
        // line feeds go out — the region would overpaint instead.
        Assert.Contains("\x1b[12;1H\n", Frame(host));
    }

    [Fact]
    public void ProductionInline_QueriesCursor_NeverTakesTheScreen()
    {
        // The full production path (dedicated UI thread, real clock) — the one place the ENTRY
        // bytes are observable (the headless harness pre-drains them): a DSR-CPR query and no
        // screen takeover of any kind, then the first paint at the reported origin, then the
        // Clear-exit teardown. Pinned to the legacy absolute path (relativeMoves: false) — its
        // assertions are absolute per-row CUP; relative is the default now (deprecated until v1.0).
        var terminal = new SyntheticTerminalHost(HeadlessCapabilities.KittyTruecolor, new Size(40, 12));
        var app = UIApplication.CreateBuilder()
            .WithTerminalHost(terminal, disposeWithApp: true)
            .UseInline(relativeMoves: false)
            .Build();

        var run = app.RunAsync(() => new Probe(10, 2) { FillGlyph = "Q" });

        var output = new StringBuilder();

        void DrainUntil(string marker)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (!output.ToString().Contains(marker, StringComparison.Ordinal) && Environment.TickCount64 < deadline)
            {
                output.Append(Encoding.UTF8.GetString(terminal.DrainOutput()));
                Thread.Sleep(5);
            }
        }

        DrainUntil("\x1b[6n");
        Assert.Contains("\x1b[6n", output.ToString());
        Assert.DoesNotContain("\x1b[?1049h", output.ToString());
        Assert.DoesNotContain("\x1b[2J", output.ToString());

        // The shell's cursor was at row 4, column 1 → the region roots there.
        terminal.WriteInputBytes(Encoding.ASCII.GetBytes("\x1b[4;1R"));
        DrainUntil("\x1b[4;1H\x1b[0J");
        Assert.Contains("\x1b[4;1H\x1b[0J", output.ToString());
        Assert.DoesNotContain("\x1b[2J", output.ToString());

        app.Shutdown(0);
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)), "the loop did not exit");

        var teardown = Encoding.UTF8.GetString(terminal.FinalOutput.Span);
        Assert.Contains("\x1b[4;1H\x1b[0J", teardown); // the Clear exit rewinds to the origin
        Assert.DoesNotContain("\x1b[?1049", teardown);

        // Clear this thread's thread-local Current (teardown cleared the loop thread's only).
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ───────────────── InlineWithSwitching (design doc §3.1, FW-7) ─────────────────

    private static UIHeadlessHost CreateSwitching(int? maxHeight = null)
        => UIHeadlessHost.Create(new UIHeadlessHostOptions
                                 {
                                     InitialSize = new Size(40, 12),
                                     CaptureFrameBytes = true,
                                     ConfigureBuilder = b => b.UseInlineWithSwitching(maxHeight),
                                 });

    [Fact]
    public void Switching_StartsInline_WithTheInlineStamp()
    {
        using var host = CreateSwitching();
        var probe = new Probe(10, 3) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(ApplicationModel.InlineWithSwitching, host.Application.ApplicationModel);
        Assert.True(host.Application.IsPresentingInline);
        Assert.Contains(PresentationClasses.Inline, probe.Classes);
        Assert.DoesNotContain(PresentationClasses.FullScreen, probe.Classes);
    }

    [Fact]
    public void Switching_WindowOpens_Escalates_AndLastCloseReturnsInline()
    {
        using var host = CreateSwitching();
        var probe = new Probe(10, 3) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());
        Assert.True(host.Application.IsPresentingInline);

        var window = host.NewWindow(content: new Probe(8, 2) { FillGlyph = "W" }, left: 4, top: 2);
        window.Show(host.Application.WindowManager!);
        host.RunFrame();

        Assert.False(host.Application.IsPresentingInline); // window ⇒ escalate (§3.1)
        Assert.Contains(PresentationClasses.FullScreen, probe.Classes); // the pair flipped through the restamp fan-out...
        Assert.DoesNotContain(PresentationClasses.Inline, probe.Classes);
        var escalation = Encoding.ASCII.GetString(host.LastFrameBytes.ToArray());
        Assert.Contains("\x1b[?1049h", escalation); // ...and the transition frame entered the alt buffer

        window.Close();
        host.RunFrame(); // the transition frame consumes the pending 1→0 switch and pops the scope...
        ReplyCursorPosition(host, row: 5); // ...then the return path re-derives the origin via a fresh CPR round
        Assert.True(host.RunUntilIdle());

        Assert.True(host.Application.IsPresentingInline);
        Assert.Contains(PresentationClasses.Inline, probe.Classes);
        Assert.DoesNotContain(PresentationClasses.FullScreen, probe.Classes);
    }

    [Fact] // the return leg builds a FRESH relative renderer, and DECRST 1049 restores the cursor to the caret
    // cell the last inline frame parked it on — the renderer must delta from THERE, not from (0,0), or the
    // region repaints caret-row rows too low (the curio filter symptom: prompt on row 0, query box on row 1).
    public void Switching_ReturnToInline_RelativeRepaintClimbsFromTheRestoredCaretRow()
    {
        var screen = new VtScreen(40, 12);
        for (int r = 0; r < 12; r++) { screen.SetCursor(r, 0); screen.Print($"history-{r:00}"); }
        screen.SetCursor(5, 0); // the shell prompt sits on row 5

        using var host = CreateSwitching();
        var box = new TextBox { Width = 10, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(new TextBlock { Text = "Prompt" });
        root.Children.Add(box); // the caret rides region row 1
        host.ShowRoot(root);
        host.RunFrame();
        box.Focus();
        ReplyCursorPosition(host, row: 6);
        for (int i = 0; i < 3; i++) { host.RunFrame(); screen.Feed(host.LastFrameBytes.Span); }

        Assert.Equal("Prompt", screen.LineTrimmed(5));
        Assert.Equal(6, screen.CursorRow); // the frame ended on the caret — region row 1
        var (savedRow, savedCol) = (screen.CursorRow, screen.CursorCol);

        var window = host.NewWindow(content: new Probe(8, 2) { FillGlyph = "W" }, left: 4, top: 2);
        window.Show(host.Application.WindowManager!);
        host.RunFrame(); // the escalation paints the ALT buffer — not the main raster, so not fed
        Assert.False(host.Application.IsPresentingInline);

        window.Close();
        host.RunFrame(); // the return frame pops 1049 and re-queries DSR...
        screen.SetCursor(savedRow, savedCol); // ...and DECRST 1049 restored the push-time cursor (VtScreen has no alt buffer)
        ReplyCursorPosition(host, row: savedRow + 1, column: savedCol + 1);
        host.RunFrame();
        var frame = Frame(host);
        screen.Feed(host.LastFrameBytes.Span);

        Assert.Contains("\x1b[1A\x1b[1G\x1b[0J", frame); // the full repaint CLIMBS from the caret row to the region top
        Assert.Equal("Prompt", screen.LineTrimmed(5));    // region row 0 is back on the origin row...
        Assert.DoesNotContain("Prompt", screen.Line(6));  // ...not pushed down a row
        Assert.Equal(6, screen.CursorRow);                // and the frame ends on the caret again
    }

    [Fact]
    public void Switching_PopupNeverEscalates()
    {
        using var host = CreateSwitching();
        var box = new TextBox { Width = 12, Height = 1 };
        var popup = new CompletionPopup { Target = box, Provider = new DelegateCompletionProvider(q => new CompletionContext(0, q.Text.Length, q.Text, [new CompletionItem("alpha"), new CompletionItem("beta")])) };
        var root = new Grid();
        root.Children.Add(box);
        root.Children.Add(popup);
        host.ShowRoot(root);
        host.RunFrame();
        ReplyCursorPosition(host, row: 3);
        Assert.True(host.RunUntilIdle());
        box.Focus();
        host.SendText("a");
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen); // a popup is up...
        Assert.True(host.Application.IsPresentingInline); // ...and the presentation did not move (§3.1: popup ⇒ inline)
    }

    [Fact] // the presentation axis lives in the capability MASK: Style.RequiresCapabilities gates on the
    // same single derivation as the app-inline/app-fullscreen classes, and flips with the switch.
    public void Switching_RequiresCapabilitiesGate_FollowsThePresentationAxis()
    {
        using var host = CreateSwitching();
        var probe = new Probe(10, 3) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        var inlineOnly = new Style(Selectors.Is<Probe>()) { RequiresCapabilities = StyleCapabilities.Inline };
        inlineOnly.Setters.Add(new Setter(UIElement.MinWidthProperty, 7));
        host.Application.Styles.Add(inlineOnly);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(7, probe.MinWidth); // presenting inline — the Inline-requiring rule applies

        var window = host.NewWindow(content: new Probe(8, 2) { FillGlyph = "W" }, left: 4, top: 2);
        window.Show(host.Application.WindowManager!);
        Assert.True(host.RunUntilIdle());
        Assert.NotEqual(7, probe.MinWidth); // escalated — the gate detached the rule, same tick as the stamp

        window.Close();
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(7, probe.MinWidth); // back inline — reattached
    }

    [Fact]
    public void PlainInline_WindowOpen_DoesNotEscalate()
    {
        using var host = CreateInline();
        var probe = new Probe(10, 3) { FillGlyph = "X" };
        host.ShowRoot(probe);
        host.RunFrame();
        ReplyCursorPosition(host, row: 5);
        Assert.True(host.RunUntilIdle());

        var window = host.NewWindow(content: new Probe(8, 2) { FillGlyph = "W" }, left: 4, top: 2);
        window.Show(host.Application.WindowManager!);
        Assert.True(host.RunUntilIdle());

        Assert.True(host.Application.IsPresentingInline); // Inline never switches — InlineWithSwitching is the opt-in
        Assert.Equal(ApplicationModel.Inline, host.Application.ApplicationModel);
    }
}
