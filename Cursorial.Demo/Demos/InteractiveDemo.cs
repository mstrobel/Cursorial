using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;

// ReSharper disable CheckNamespace

// Base for render-loop demos. Owns the whole interactive harness ONCE: alt-screen entry/exit, the
// background input pump, the optional CURSORIAL_TRACE_OUTPUT capture, the dirty/animated render
// loop, and teardown (renderer close + cooked-mode restore before the exit message). Subclasses
// build their assets once in Initialize() (held as fields), paint into Buffer in RenderFrame(), and
// optionally react to input in OnEvent() / OnResize().
internal abstract class InteractiveDemo : IDemo
{
    public abstract string Name { get; }
    public virtual IReadOnlyList<string> Aliases => [];
    public abstract string Description { get; }

    protected virtual string? IntroMessage => null;          // printed (cooked mode) before alt-screen
    protected virtual TimeSpan FrameInterval => TimeSpan.FromMilliseconds(50);
    protected virtual bool Animated => false;                 // true: render every tick; false: only when dirty

    // Prepared resources, set before Initialize():
    protected TerminalSession Session { get; private set; } = null!;
    protected CellBuffer Buffer { get; private set; } = null!;
    protected FrameRenderer Renderer { get; private set; } = null!;
    protected CellStyle Style { get; private set; }
    protected TerminalPalette Palette { get; private set; } = null!;
    protected TerminalCapabilities Capabilities { get; private set; } = null!;
    protected System.IO.Pipelines.PipeWriter Writer { get; private set; } = null!;
    protected string Argument { get; private set; } = "";

    // Hooks:
    protected virtual void Initialize() { }                  // build instance assets ONCE (brushes/pens/scenes/images as FIELDS)
    protected abstract void RenderFrame(long frame);         // paint into Buffer
    protected virtual bool OnEvent(InputEvent evt) => false;  // return true if state changed (request a redraw)
    protected virtual void OnResize(int columns, int rows) => Buffer.Resize(columns, rows);

    // Request a redraw from OUTSIDE the input-event flow — e.g., a Task continuation when async work
    // (a palette query, a network fetch) completes off the last input event. Thread-safe: sets a flag
    // the render loop consumes on its next tick (≤ FrameInterval later). Without this, a non-animated
    // demo only repaints on input/resize, so an async result that lands after the final input event
    // sits unpainted until the next keypress. Safe to call from any thread (continuation/timer/pump).
    private int _invalidated;
    protected void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

    public async Task RunAsync(string argument)
    {
        // ReSharper disable MethodHasAsyncOverloadWithCancellation
        // ReSharper disable MethodSupportsCancellation
        // ReSharper disable MethodHasAsyncOverload
        
        Argument = argument;
        if (IntroMessage is { } intro) Console.WriteLine(intro);

        var (session, buffer, renderer, style, palette, capabilities) = await DemoSupport.PrepareDemo();
        Session = session;
        Buffer = buffer;
        Renderer = renderer;
        Style = style;
        Palette = palette;
        Capabilities = capabilities;
        Writer = session.Output.Writer;

        await using var ds = session;
        using var dp = palette;

        var writer = Writer;

        // Enter alt screen and reset SGR. We leave the cursor decision to the cell buffer
        // (CursorVisible = false renders DECRST 25 on the first frame).
        ScreenWriter.WriteEnterAlternateScreen(writer);
        SgrEncoder.WriteReset(writer);
        await writer.FlushAsync();

        Initialize();

        // Background pump for input events — main loop polls the queue between renders.
        var events = new ConcurrentQueue<InputEvent>();
        using var stopCts = new CancellationTokenSource();

        var inputPump =
            Task.Run(async () =>
                     {
                         try
                         {
                             // ReSharper disable AccessToDisposedClosure
                             await foreach (var evt in session.Input.ReadAllAsync(stopCts.Token))
                             {
                                 events.Enqueue(evt);
                             }
                             // ReSharper restore AccessToDisposedClosure
                         }
                         catch (OperationCanceledException) {}
                     });

        // Optional diagnostic: when CURSORIAL_TRACE_OUTPUT is set, mirror every frame's emitted
        // bytes to a file. Each frame is preceded by a short ASCII marker, so the resulting
        // capture is grep-able / easy to split by frame. Use when you suspect the renderer is
        // emitting bad bytes (cursor shifts, wrap surprises, SGR drift, …) and want ground truth
        // for the wire form before guessing.
        FileStream? traceStream = null;
        int traceFrame = 0;
        if (Environment.GetEnvironmentVariable("CURSORIAL_TRACE_OUTPUT") is { Length: > 0 } tracePath)
        {
            try { traceStream = File.Create(tracePath); }
            catch { /* best-effort — if we can't open the trace, run without it */ }
        }

        try
        {
            long frame = 0;
            bool dirty = true;
            var scratch = new ArrayBufferWriter<byte>(65536);

            while (!stopCts.IsCancellationRequested)
            {
                // Drain pending events.
                while (events.TryDequeue(out var evt))
                {
                    switch (evt)
                    {
                        case ResizeEvent { Columns: > 0, Rows: > 0 } r:
                            OnResize(r.Columns, r.Rows);
                            dirty = true;
                            break;

                        case KeyEvent k when DemoSupport.IsExit(k):
                            stopCts.Cancel();
                            break;

                        default:
                            dirty |= OnEvent(evt);
                            break;
                    }
                }

                if (stopCts.IsCancellationRequested) break;

                // Consume any external redraw request (a Task continuation calling Invalidate, etc.)
                // atomically, so a request that arrives mid-render still triggers the next frame.
                bool invalidated = Interlocked.Exchange(ref _invalidated, 0) == 1;

                if (Animated || dirty || invalidated)
                {
                    RenderFrame(frame);
                    scratch.ResetWrittenCount();
                    Renderer.Render(Buffer, scratch);

                    await writer.WriteAsync(scratch.WrittenMemory);
                    await writer.FlushAsync();

                    if (traceStream is not null)
                    {
                        var marker = Encoding.ASCII.GetBytes($"\n===== FRAME {traceFrame++:D4} ({scratch.WrittenCount} bytes) =====\n");
                        traceStream.Write(marker);
                        traceStream.Write(scratch.WrittenSpan);
                        traceStream.Flush();
                    }

                    frame++;
                    dirty = false;
                }

                try { await Task.Delay(FrameInterval, stopCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            stopCts.Cancel();
            traceStream?.Dispose();

            try { await inputPump.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }

            try { Renderer.Close(writer); } catch { /* best-effort */ }

            CursorWriter.WriteShow(writer);
            SgrEncoder.WriteReset(writer);
            ScreenWriter.WriteLeaveAlternateScreen(writer);

            try { await writer.FlushAsync(); } catch { /* best-effort */ }
        }

        // Tear down BEFORE printing the exit message. The session is still in raw mode here (the
        // `await using` would otherwise dispose it only at method end, after this WriteLine), and in
        // POSIX raw mode Console.WriteLine's '\n' is a bare line-feed with no carriage return — so
        // the message lands mid-line and the next REPL prompt is mispositioned. Dispose the palette
        // first (it writes OSC palette-reset sequences to the still-open sink), then the session
        // (negotiator restore + tcflush + cooked-mode restore). Both are idempotent, so the
        // `using` / `await using` declarations safely no-op at method end.

        // ReSharper disable DisposeOnUsingVariable
        dp.Dispose();
        await ds.DisposeAsync();
        // ReSharper restore DisposeOnUsingVariable

        Console.WriteLine($"{Name} demo exited.");

        // ReSharper enable MethodHasAsyncOverload
        // ReSharper enable MethodSupportsCancellation
        // ReSharper enable MethodHasAsyncOverloadWithCancellation
    }
}
