// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
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
    private static UIHeadlessHost CreateInline(int? maxHeight = null,
                                               InlineExitBehavior exitBehavior = InlineExitBehavior.Clear)
        => UIHeadlessHost.Create(new UIHeadlessHostOptions
                                 {
                                     InitialSize = new Size(40, 12),
                                     CaptureFrameBytes = true,
                                     ConfigureBuilder = b => b.UseInline(maxHeight, exitBehavior)
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
        Assert.Contains("\x1b[12;1H\n\n", frame);     // make room: CUP bottom + 2 × LF
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
        // sweep anything staler below it, and leave the frame's rows untouched.
        var teardown = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        Assert.Contains("\x1b[7;1H\n\x1b[0J", teardown);
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

    [Fact]
    public void ProductionInline_QueriesCursor_NeverTakesTheScreen()
    {
        // The full production path (dedicated UI thread, real clock) — the one place the ENTRY
        // bytes are observable (the headless harness pre-drains them): a DSR-CPR query and no
        // screen takeover of any kind, then the first paint at the reported origin, then the
        // Clear-exit teardown.
        var terminal = new SyntheticTerminalHost(HeadlessCapabilities.KittyTruecolor, new Size(40, 12));
        var app = UIApplication.CreateBuilder()
            .WithTerminalHost(terminal, disposeWithApp: true)
            .UseInline()
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
}
