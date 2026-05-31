using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Text;
using Cursorial.Terminal;
using Cursorial.Terminal.Stdio;

// REPL for exercising Cursorial against a real terminal.
//
// Two-phase model: the prompt itself runs in cooked mode (Console.ReadLine for command entry).
// Each command that needs raw input opens a TerminalSession (which puts the terminal into raw
// mode), does its work, and disposes the session — restoring cooked mode before the next prompt.

PrintBanner();

// Trace.Listeners.Add(new TextWriterTraceListener(File.Open("cursorial.log", FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
//                                                 "Cursorial"));
// Trace.AutoFlush = true;
// Trace.WriteLine($"Starting at {DateTime.Now:yyyy-MM-dd HH:mm:ss}...");

while (true)
{
    Console.Write("cursorial> ");
    string? line = Console.ReadLine();
    if (line is null) break;

    string trimmed = line.Trim();
    if (trimmed.Length == 0) continue;

    // Split the verb (lowercased for dispatch) from its argument (original case preserved —
    // file paths and similar args need it). A trailing-args-friendly split keeps the existing
    // arg-less commands working unchanged.
    int spaceIdx = trimmed.IndexOf(' ');
    string verb = (spaceIdx < 0 ? trimmed : trimmed[..spaceIdx]).ToLowerInvariant();
    string argument = spaceIdx < 0 ? "" : trimmed[(spaceIdx + 1)..].Trim();

    if (verb is "quit" or "exit" or "q") break;

    try
    {
        switch (verb)
        {
            case "help" or "?":
                PrintHelp();
                break;
            case "negotiate" or "caps":
                await NegotiateAsync();
                break;
            case "read" or "report" or "events":
                await ReadEventsAsync();
                break;
            case "read-synth" or "events-synth":
                await ReadEventsWithSynthesisAsync();
                break;
            case "raw":
                await DumpRawAsync();
                break;
            case "trace":
                await TraceAsync();
                break;
            case "sizing" or "text-sizing":
                await DemoTextSizingAsync();
                break;
            case "render" or "showcase":
                await DemoRenderAsync();
                break;
            case "image":
                await DemoImageAsync(argument);
                break;
            case "format" or "richtext":
                await DemoFormatAsync();
                break;
            case "palette":
                await DemoPaletteAsync();
                break;
            case "probe":
                await ProbeAsync();
                break;
            default:
                Console.WriteLine($"Unknown command: '{verb}'. Type 'help' for the list.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        Console.WriteLine($"{ex.StackTrace}");
    }
}

Console.WriteLine("Bye.");
return 0;

static void PrintBanner()
{
    Console.WriteLine("Cursorial demo — interactive runner");
    #if DEBUG
    Console.WriteLine($"Runtime: {RuntimeInformation.RuntimeIdentifier}; OS: {RuntimeInformation.OSDescription}; " +
                      $"Framework: {RuntimeInformation.FrameworkDescription}");
    #endif
    Console.WriteLine("Type 'help' for commands, 'quit' to exit.");
    Console.WriteLine();
}

static void PrintHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  help, ?          Show this help");
    Console.WriteLine("  negotiate, caps  Open a session, dump negotiated TerminalCapabilities, restore");
    Console.WriteLine("  read, report     Open a session and stream input events to stdout");
    Console.WriteLine("                   (Press Ctrl+C inside read mode to return to the prompt)");
    Console.WriteLine("  read-synth       Like 'read', but wraps the input device in a");
    Console.WriteLine("                   KeyReleaseSynthesizer — terminals that don't natively");
    Console.WriteLine("                   report key-up events get synthesized releases after a");
    Console.WriteLine("                   short idle window. Synthesized events appear with");
    Console.WriteLine("                   '(synth)' next to the kind so you can tell them apart.");
    Console.WriteLine("                   (Press Ctrl+C to stop)");
    Console.WriteLine("  raw              Dump raw bytes from stdin verbatim — no parsing.");
    Console.WriteLine("                   Useful for seeing exactly what the terminal sends.");
    Console.WriteLine("                   (Press Ctrl+C to stop)");
    Console.WriteLine("  trace            Like 'read', but each input chunk is logged as raw bytes");
    Console.WriteLine("                   followed by the decoded events. Lets you cross-reference");
    Console.WriteLine("                   wire format against parser output for protocol debugging.");
    Console.WriteLine("                   (Press Ctrl+C to stop)");
    Console.WriteLine("  sizing           Print samples of Kitty's text-sizing protocol (OSC 66).");
    Console.WriteLine("                   On supporting terminals you'll see scaled/wider glyphs;");
    Console.WriteLine("                   non-supporting terminals render the sample at normal size.");
    Console.WriteLine("                   (Press Enter to return)");
    Console.WriteLine("  render           Cursorial.Rendering showcase — opens the alternate screen,");
    Console.WriteLine("                   draws a panel of colors, wide glyphs, attributes, and an");
    Console.WriteLine("                   alpha-blended overlay, with a clock ticking in the corner.");
    Console.WriteLine("                   (Press q or Ctrl+C to exit)");
    Console.WriteLine("  image <path>     Load and render the file at <path> via the negotiated graphics");
    Console.WriteLine("                   protocol (Kitty graphics → iTerm2 inline → cell placeholder).");
    Console.WriteLine("                   Supports PNG / JPEG / GIF; format inferred from extension.");
    Console.WriteLine("                   (Press q or Ctrl+C to exit)");
    Console.WriteLine("  format, richtext Showcase the rich-text formatter — wrap modes, alignment");
    Console.WriteLine("                   (including justify), soft hyphens, multi-block layout, BBcode-style" +
                      "                   markup languge.");
    Console.WriteLine("                   (Press q or Ctrl+C to exit)");
    Console.WriteLine("  palette          Query the terminal's 256-color palette via OSC 4 and display");
    Console.WriteLine("                   each entry as a colored block with its hex code overlaid.");
    Console.WriteLine("                   (Press q or Ctrl+C to exit)");
    Console.WriteLine("  probe            Send XTVERSION + DA1 and dump the raw response bytes for 1 second.");
    Console.WriteLine("                   Confirms whether the terminal responds to standard probes.");
    Console.WriteLine("  quit, exit       Exit the demo");
}

static async Task NegotiateAsync()
{
    // Capture capabilities inside the session, dispose before printing — otherwise we'd
    // write multi-line output through a raw-mode terminal (OPOST off) where bare \n doesn't
    // get a CR.
    TerminalCapabilities caps;

    var sessionOptions = new TerminalSessionOptions { Negotiation = new NegotiationOptions { ProbeTimeout = TimeSpan.FromSeconds(3) } };
    
    await using (var session = await TerminalSession.OpenAsync(sessionOptions))
    {
        caps = session.Capabilities;
    }
 
    Console.Write(FormatCapabilities(caps));
}

static async Task ReadEventsAsync()
{
    Console.WriteLine("Reading input events. Press Ctrl+C to return to the prompt.");
    Console.WriteLine();

    int eventCount = 0;
    await using (var session = await TerminalSession.OpenAsync())
    {
        using var stopCts = new CancellationTokenSource();

        try
        {
            var message = $"Your input device is {session.Input}.\n\n";

            await session.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes(message), stopCts.Token);
            await session.Output.Writer.FlushAsync(stopCts.Token);

            await foreach (var inputEvent in session.Input.ReadAllAsync(stopCts.Token))
            {
                eventCount++;

                // Raw mode (OPOST off) — write \r\n explicitly so each line wraps back to
                // column 0 instead of stair-stepping right.
                await session.Output.Writer.WriteAsync(
                    Encoding.UTF8.GetBytes($"  [{eventCount,4}] {FormatEvent(inputEvent)}\r\n"));
                await session.Output.Writer.FlushAsync();

                if (IsStopSignal(inputEvent))
                {
                    stopCts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    Console.WriteLine();
    Console.WriteLine($"Stopped after {eventCount} event(s).");
}

static async Task ReadEventsWithSynthesisAsync()
{
    Console.WriteLine("Reading input events with key-up synthesis. Press Ctrl+C to return.");
    Console.WriteLine(
        $"Synthesized releases appear after the up timeout ({KeyReleaseSynthesizer.DefaultUpTimeout.TotalMilliseconds:0} ms); " +
        $"subsequent Downs within the repeat timeout ({KeyReleaseSynthesizer.DefaultRepeatTimeout.TotalMilliseconds:0} ms) " +
        $"are marked IsRepeat.");
    Console.WriteLine();

    int eventCount = 0;
    await using (var session = await TerminalSession.OpenAsync())
    {
        // KeyReleaseSynthesizer is a decorator — it takes the session's IAsyncInputDevice and
        // exposes a wrapped one. Per the decorator contract, disposing the synthesizer also
        // disposes the inner. We dispose it explicitly so the synthesizer's pump task stops
        // when this command returns; the outer `await using` on the session handles transport
        // restoration.
        await using var synth = new KeyReleaseSynthesizer(session.Input);
        using var stopCts = new CancellationTokenSource();

        try
        {
            await foreach (var inputEvent in synth.ReadAllAsync(stopCts.Token))
            {
                eventCount++;

                var label = inputEvent.Synthesized ? " (synth)" : "";
                await session.Output.Writer.WriteAsync(
                    Encoding.UTF8.GetBytes($"  [{eventCount,4}]{label} {FormatEvent(inputEvent)}\r\n"));
                await session.Output.Writer.FlushAsync();

                if (IsStopSignal(inputEvent))
                {
                    stopCts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    Console.WriteLine();
    Console.WriteLine($"Stopped after {eventCount} event(s).");
}

static async Task DumpRawAsync()
{
    Console.WriteLine("Dumping raw stdin bytes. Press Ctrl+C to stop.");
    Console.WriteLine();

    await using (var transports = StdioTransports.Open())
    {
        using var stopCts = new CancellationTokenSource();
        var reader = transports.Source.Reader;

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(stopCts.Token);
                var bytes = result.Buffer.ToArray();
                reader.AdvanceTo(result.Buffer.End);

                foreach (byte b in bytes)
                {
                    var msg = $"  byte 0x{b:X2}{(b is >= 0x20 and < 0x7F ? $" '{(char)b}'" : "")}\r\n";
                    await transports.Sink.Writer.WriteAsync(Encoding.UTF8.GetBytes(msg));

                    if (b == 0x03) stopCts.Cancel(); // Ctrl+C
                }
                await transports.Sink.Writer.FlushAsync();

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    Console.WriteLine();
    Console.WriteLine("Raw dump stopped.");
}

static async Task TraceAsync()
{
    Console.WriteLine("Tracing raw bytes + decoded events. Press Ctrl+C to stop.");
    Console.WriteLine();

    await using var transports = StdioTransports.Open();
    var mode = new VtInputMode();
    var negotiator = new VtTerminalNegotiator(transports.Source, transports.Sink, mode);

    try
    {
        // For tracing we want every key event to arrive as an escape sequence carrying full
        // modifier state — including for plain text keys. Otherwise, Kitty's text-shortcut
        // optimization elides the modifier annotation on presses of printable keys, leaving
        // an asymmetric press-vs.-release picture in the trace.
        var traceOptions = new NegotiationOptions
        {
            KittyKeyboardFlags = KittyKeyboardFlags.DisambiguateEscapeCodes
                                 | KittyKeyboardFlags.ReportEventTypes
                                 | KittyKeyboardFlags.ReportAlternateKeys
                                 | KittyKeyboardFlags.ReportAssociatedText
                                 | KittyKeyboardFlags.ReportAllKeysAsEscapeCodes
        };
        await negotiator.NegotiateAsync(traceOptions);

        var classifier = new VtSequenceClassifier();
        var events = new List<InputEvent>();
        var interpreter = new VtInputInterpreter(mode, new TraceEventSink(events));

        using var stopCts = new CancellationTokenSource();
        var reader = transports.Source.Reader;
        var writer = transports.Sink.Writer;
        var ambiguityTimeout = TimeSpan.FromMilliseconds(50);

        Task<System.IO.Pipelines.ReadResult>? pendingRead = null;
        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                pendingRead ??= reader.ReadAsync(stopCts.Token).AsTask();
                var completed = await Task.WhenAny(pendingRead, Task.Delay(ambiguityTimeout, stopCts.Token));

                if (completed != pendingRead)
                {
                    // Idle window — flush any pending bare-ESC so an Escape keypress doesn't
                    // sit invisibly inside the classifier.
                    classifier.Flush(interpreter);
                    await DrainEventsAsync(events, writer, stopCts);
                    await writer.FlushAsync();
                    continue;
                }

                var result = await pendingRead;
                pendingRead = null;

                var buffer = result.Buffer;
                if (buffer.Length > 0)
                {
                    var bytes = buffer.ToArray();
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(
                        $"RX  {BytesToHex(bytes)}  |{BytesToPrintable(bytes)}|\r\n"));

                    foreach (var segment in buffer)
                        classifier.Process(segment.Span, interpreter);
                }
                reader.AdvanceTo(buffer.End);

                await DrainEventsAsync(events, writer, stopCts);
                await writer.FlushAsync();

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }
    finally
    {
        await negotiator.DisposeAsync();
    }
}

static async Task DrainEventsAsync(
    List<InputEvent> events,
    System.IO.Pipelines.PipeWriter writer,
    CancellationTokenSource stopCts)
{
    foreach (var evt in events)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes($"    ↳ {FormatEvent(evt)}\r\n"));
        if (IsStopSignal(evt)) stopCts.Cancel();
    }
    events.Clear();
}

static string BytesToPrintable(ReadOnlySpan<byte> bytes)
{
    var sb = new StringBuilder(bytes.Length);
    foreach (byte b in bytes) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '·');
    return sb.ToString();
}

static async Task DemoTextSizingAsync()
{
    Console.WriteLine("Text-sizing demo. Press Enter to return; Ctrl+C also works.");
    Console.WriteLine();

    var (session, _, _, _, palette, capabilities) = await PrepareDemo();
    
    await using var ds = session;
    using var dp = palette;

    var writer = session.Output.Writer;
    var caps = capabilities.Output.TextSizing;
    
    if (caps is { Width: false, Scale: false })
    {
        await WriteLineAsync(writer,
            "  Terminal does not advertise text-sizing support. Sending the sequences anyway —");
        await WriteLineAsync(writer,
            "  non-supporting terminals render OSC 66 payloads at normal size (the metadata is ignored).");
        await WriteLineAsync(writer, "");
    }
    else
    {
        var support = (caps.Width, caps.Scale) switch
                      {
                          (true, true)  => "Width + Scale",
                          (true, false) => "Width only",
                          (false, true) => "Scale only",
                          _             => "none"
                      };
        
        await WriteLineAsync(writer, $"  Negotiated text-sizing support: {support}.");
        await WriteLineAsync(writer, "");
    }

    await WriteLineAsync(writer, "  Reference (no OSC 66): Hello, world!");
    await WriteLineAsync(writer, "");

    await WriteLineAsync(writer, "  s=2 (double-sized):");
    await WriteSizedAsync(writer, "s=2", "Hello, world!");
    await WriteLineAsync(writer, "");
    await WriteLineAsync(writer, "");

    await WriteLineAsync(writer, "  n=1:d=2 (half-sized):");
    await WriteSizedAsync(writer, "n=1:d=2", "Hello, world!");
    await WriteLineAsync(writer, "");
    await WriteLineAsync(writer, "");

    await WriteLineAsync(writer, "  w=2 (forced two-cell width on emoji):");
    await WriteSizedAsync(writer, "w=2", "🐈");
    await WriteSizedAsync(writer, "w=2", "🌶");
    await WriteSizedAsync(writer, "w=2", "🚀");
    await WriteLineAsync(writer, "");
    await WriteLineAsync(writer, "");

    await WriteLineAsync(writer, "  s=2:h=2 (double-sized, horizontally centered in the 2-cell block):");
    await WriteSizedAsync(writer, "s=2:h=2", "Cursorial");
    await WriteLineAsync(writer, "");
    await WriteLineAsync(writer, "");

    await WriteLineAsync(writer, "  (press Enter to return)");
    await writer.FlushAsync();

    using var stopCts = new CancellationTokenSource();
    try
    {
        await foreach (var evt in session.Input.ReadAllAsync(stopCts.Token))
        {
            if (evt is KeyEvent { Key: Key.Enter }) break;
            if (IsStopSignal(evt)) break;
        }
    }
    catch (OperationCanceledException) { }
}

static async Task WriteSizedAsync(
    System.IO.Pipelines.PipeWriter writer,
    string metadata,
    string text)
{
    // ESC ] 66 ; <metadata> ; <text> ST.  Uses the centralized prefix/terminator constants,
    // so the demo doubles as an exercise for the Cursorial.Output byte-string surface.
    var metadataBytes = Encoding.UTF8.GetBytes(metadata);
    var textBytes = Encoding.UTF8.GetBytes(text);
    var prefix = VtOutputSequences.KittyTextSizing.Prefix;
    var terminator = VtOutputSequences.KittyTextSizing.StringTerminator;

    int total = prefix.Length + metadataBytes.Length + 1 + textBytes.Length + terminator.Length;
    var dest = writer.GetSpan(total);
    int i = 0;
    prefix.CopyTo(dest[i..]); i += prefix.Length;
    metadataBytes.CopyTo(dest[i..]); i += metadataBytes.Length;
    dest[i++] = (byte)';';
    textBytes.CopyTo(dest[i..]); i += textBytes.Length;
    terminator.CopyTo(dest[i..]); i += terminator.Length;
    writer.Advance(total);
    await writer.FlushAsync();
    
    // ReSharper restore RedundantAssignment
}

static async Task WriteLineAsync(System.IO.Pipelines.PipeWriter writer, string text)
{
    // Raw mode (OPOST off) — write \r\n explicitly.
    await writer.WriteAsync(Encoding.UTF8.GetBytes(text + "\r\n"));
}

static async Task<(TerminalSession session,
                   CellBuffer buffer,
                   FrameRenderer renderer,
                   Style style,
                   TerminalPalette palette,
                   TerminalCapabilities capabilities)> PrepareDemo()
{
    var cts = new CancellationTokenSource();

    cts.CancelAfter(TimeSpan.FromSeconds(5));

    var session = await TerminalSession.OpenAsync();
    
    // Initial size from Console (TIOCGWINSZ-equivalent; safe for read-only queries even though
    // we don't open the Console streams). The SIGWINCH-driven ResizeEvent will correct us once
    // it fires, but starting with the right dimensions avoids a redraw on the first resize.
    var size = await session.QueryTerminalSizeAsync();

    var capabilities = session.Capabilities;
    var palette = new TerminalPalette(session.Output, capabilities.Output);

    var fg = Color.FromHex("#c0caf5");
    var bg = Color.FromHex("#0d0f18");

    Color[] colors =
    [
        Color.FromHex("#15161e"),
        Color.FromHex("#f7768e"),
        Color.FromHex("#9ece6a"),
        Color.FromHex("#e0af68"),
        Color.FromHex("#7aa2f7"),
        Color.FromHex("#bb9af7"),
        Color.FromHex("#7dcfff"),
        Color.FromHex("#a9b1d6"),
        Color.FromHex("#414868"),
        Color.FromHex("#ff899d"),
        Color.FromHex("#9fe044"),
        Color.FromHex("#faba4a"),
        Color.FromHex("#8db0ff"),
        Color.FromHex("#c7a9ff"),
        Color.FromHex("#a4daff"),
        Color.FromHex("#c0caf5"),
        Color.FromHex("#0d0f18"),
        Color.FromHex("#ff9e64"),
        Color.FromHex("#db4b4b")
    ];

    for (var i = 0; i < colors.Length; i++)
        palette.Set(i, colors[i]);

    palette.SetBackground(bg);
    palette.SetForeground(fg);
    palette.SetCursor(fg);

    if (palette.IsSupported)
    {
        capabilities = capabilities with
                       {
                           Output = capabilities.Output with
                                    {
                                        // Report the (now overridden) rgb colors to enable alpha blending
                                        // in cases where the user pulls from these.
                                        Color = capabilities.Output.Color with
                                                {
                                                    DefaultForeground = fg,
                                                    DefaultBackground = bg,
                                                    DefaultCursorColor = fg, 
                                                }
                                    }
                       };
    }

    var style = Style.Default with { Foreground = fg, Background = bg }; // Use rgb colors to enable alpha blending.

    // Falling back to Console.WindowWidth/Height as a last resort, but on non-console stdout
    // (MSYS2 / Cygwin / MobaXterm bash) those throw IOException("The handle is invalid").
    // Default to 80x24 in that case so the demo at least starts; a SIGWINCH-equivalent resize
    // will correct the dimensions once one fires.
    int cols, rows;
    if (size is { } s)
    {
        cols = s.Columns;
        rows = s.Rows;
    }
    else
    {
        try { cols = Console.WindowWidth;  } catch { cols = 80; }
        try { rows = Console.WindowHeight; } catch { rows = 24; }
    }

    var buffer = new CellBuffer(cols, rows, capabilities);

    // Hand the renderer the negotiated capabilities so it can quantize cells before emission
    // (RGB → palette where truecolor isn't available, extended underline → Single where the
    // extended forms aren't supported, drop unsupported attributes, …). Without this, terminals
    // like Apple Terminal that report Ansi256 receive raw truecolor SGR and render
    // unpredictably.
    var renderer = new FrameRenderer(capabilities.Output);
    
    return (session, buffer, renderer, style, palette, capabilities);
}

static async Task DemoRenderAsync()
{
    Console.WriteLine("Render demo. Opening alt screen — press q or Ctrl+C to exit.");

    var (session, buffer, renderer, style, palette, capabilities) = await PrepareDemo();

    await using var ds = session;
    using var dp = palette;

    var writer = session.Output.Writer;

    // Enter alt screen and reset SGR. We leave the cursor decision to the cell buffer
    // (CursorVisible = false renders DECRST 25 on the first frame).
    ScreenWriter.WriteEnterAlternateScreen(writer);
    SgrEncoder.WriteReset(writer);
    await writer.FlushAsync();

    // Background pump for input events — main loop polls the queue between renders.
    var events = new System.Collections.Concurrent.ConcurrentQueue<InputEvent>();
    using var stopCts = new CancellationTokenSource();
    var inputPump = Task.Run(async () =>
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
        catch (OperationCanceledException) { }
    });

    // Optional diagnostic: when CURSORIAL_TRACE_OUTPUT is set, mirror every frame's emitted
    // bytes to a file. Each frame is preceded by a short ASCII marker so the resulting
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
        while (!stopCts.IsCancellationRequested)
        {
            // Drain pending events.
            while (events.TryDequeue(out var evt))
            {
                switch (evt)
                {
                    case ResizeEvent { Columns: > 0, Rows: > 0 } r:
                        buffer.Resize(r.Columns, r.Rows);
                        // Resize discards content; the renderer will detect dimension change
                        // and full-redraw on the next render.
                        break;

                    case KeyEvent k when IsExit(k):
                        stopCts.Cancel();
                        break;
                }
            }

            if (stopCts.IsCancellationRequested) break;

            PaintRenderShowcase(buffer, style, capabilities.Output);

            var scratch = new ArrayBufferWriter<byte>();
            renderer.Render(buffer, scratch);
            await writer.WriteAsync(scratch.WrittenMemory);
            await writer.FlushAsync();

            if (traceStream is not null)
            {
                var marker = System.Text.Encoding.ASCII.GetBytes($"\n===== FRAME {traceFrame++:D4} ({scratch.WrittenCount} bytes) =====\n");
                traceStream.Write(marker);
                traceStream.Write(scratch.WrittenSpan);
                traceStream.Flush();
            }

            try { await Task.Delay(333, stopCts.Token); } // ~30fps
            catch (OperationCanceledException) { break; }
        }
    }
    finally
    {
        stopCts.Cancel();
        traceStream?.Dispose();

        try { await inputPump.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }

        try { renderer.Close(writer); } catch { /* best-effort */ }

        CursorWriter.WriteShow(writer);
        SgrEncoder.WriteReset(writer);
        ScreenWriter.WriteLeaveAlternateScreen(writer);

        try { await writer.FlushAsync(); } catch { /* best-effort */ }
    }

    Console.WriteLine("Render demo exited.");

    static bool IsExit(KeyEvent k)
    {
        if (k.Kind != KeyEventKind.Down) return false;
        if (k.Key == Key.Escape) return true;

        if (k is { Key: Key.Character, Text.Length: > 0 } && (k.Text.Span[0] == 'q' || k.Text.Span[0] == 'Q'))
            return true;

        // Ctrl+C as a Kitty/Win32 character key.
        if (k.Key == Key.Character &&
            k.Modifiers.HasFlag(KeyModifiers.Control) &&
            k.Text.Length > 0 &&
            (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C'))
        {
            return true;
        }

        return false;
    }
}

static async Task DemoFormatAsync()
{
    Console.WriteLine("Formatting demo. Opening alt screen — press q or Ctrl+C to exit; ↑/↓ + PgUp/PgDn + Home/End to scroll.");
    
    var (session, screen, renderer, style, palette, capabilities) = await PrepareDemo();
    
    await using var ds = session;
    using var dp = palette;

    var writer = session.Output.Writer;

    ScreenWriter.WriteEnterAlternateScreen(writer);
    SgrEncoder.WriteReset(writer);
    await writer.FlushAsync();

    var doc = BuildFormattingShowcase(style);
    var formatter = new TextFormatter { Trim = TextTrimming.WordEllipsis };

    // Off-screen buffer the whole document paints into. Per-frame we blit a sliding window of
    // its cells into `screen`. Re-built on resize.
    CellBuffer? offscreen;
    int docRows;
    int scrollOffset = 0;
    int viewportRows;

    void Reformat()
    {
        var margins = new Margins(2, 1);
        int viewWidth = Math.Max(20, Math.Min(100, screen.Columns) - margins.Horizontal);

        viewportRows = Math.Max(4, screen.Rows - margins.Vertical);

        // Format with no row cap; render into an off-screen buffer sized to the full document.
        var ft = formatter.Format(doc, viewWidth, maxRows: null, capabilities.Output);

        docRows = Math.Max(1, ft.Size.Rows);
        offscreen = new CellBuffer(viewWidth, docRows, capabilities);
        offscreen.Clear(style);

        ft.Paint(offscreen,
                 offscreen.Bounds.LayoutContent(Anchor.Top, ft.Size), 
                 capabilities.Output);

        ClampScroll();
    }

    void ClampScroll()
    {
        int maxScroll = Math.Max(0, docRows - viewportRows);
        if (scrollOffset < 0) scrollOffset = 0;
        if (scrollOffset > maxScroll) scrollOffset = maxScroll;
    }

    Reformat();

    var events = new System.Collections.Concurrent.ConcurrentQueue<InputEvent>();
    using var stopCts = new CancellationTokenSource();
    var inputPump = Task.Run(async () =>
    {
        try
        {
            // ReSharper disable AccessToDisposedClosure
            await foreach (var evt in session.Input.ReadAllAsync(stopCts.Token))
                events.Enqueue(evt);
            // ReSharper restore AccessToDisposedClosure
        }
        catch (OperationCanceledException) { }
    });

    // Optional diagnostic: CURSORIAL_TRACE_OUTPUT=<path> mirrors each rendered frame's emitted
    // bytes to a file, preceded by a marker, for protocol-level debugging.
    FileStream? traceStream = null;
    int traceFrame = 0;
    if (Environment.GetEnvironmentVariable("CURSORIAL_TRACE_OUTPUT") is { Length: > 0 } tracePath)
    {
        try { traceStream = File.Create(tracePath); }
        catch { /* best-effort */ }
    }

    try
    {
        bool needsRepaint = true;

        while (!stopCts.IsCancellationRequested)
        {
            while (events.TryDequeue(out var evt))
            {
                switch (evt)
                {
                    case ResizeEvent { Columns: > 0, Rows: > 0 } r:
                        screen.Resize(r.Columns, r.Rows);
                        Reformat();
                        needsRepaint = true;
                        break;
                    case KeyEvent k when IsExit(k):
                        stopCts.Cancel();
                        break;
                    case KeyEvent { Kind: KeyEventKind.Down } k:
                        if (HandleScrollKey(k, viewportRows, docRows, ref scrollOffset))
                        {
                            ClampScroll();
                            needsRepaint = true;
                        }
                        break;
                    case MouseEvent { Kind: MouseEventKind.Wheel } m:
                        // Positive WheelDeltaY conventionally means scroll up.
                        scrollOffset -= m.WheelDeltaY / 120;
                        ClampScroll();
                        needsRepaint = true;
                        break;
                }
            }

            if (stopCts.IsCancellationRequested) break;

            if (needsRepaint)
            {
                PaintScrolledShowcase(screen, offscreen!, scrollOffset, viewportRows, docRows, style);

                var scratch = new ArrayBufferWriter<byte>();
                renderer.Render(screen, scratch);
                await writer.WriteAsync(scratch.WrittenMemory);
                await writer.FlushAsync();

                if (traceStream is not null)
                {
                    var marker = System.Text.Encoding.ASCII.GetBytes(
                        $"\n===== FRAME {traceFrame++:D4} scroll={scrollOffset} cols={screen.Columns} rows={screen.Rows} ({scratch.WrittenCount} bytes) =====\n");
                    traceStream.Write(marker);
                    traceStream.Write(scratch.WrittenSpan);
                    traceStream.Flush();
                }

                needsRepaint = false;
            }

            try { await Task.Delay(50, stopCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    finally
    {
        stopCts.Cancel();
        traceStream?.Dispose();

        try { await inputPump.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }
        try { renderer.Close(writer); } catch { /* best-effort */ }

        CursorWriter.WriteShow(writer);
        SgrEncoder.WriteReset(writer);
        ScreenWriter.WriteLeaveAlternateScreen(writer);

        try { await writer.FlushAsync(); } catch { /* best-effort */ }
    }

    Console.WriteLine("Format demo exited.");

    static bool IsExit(KeyEvent k)
    {
        if (k.Kind != KeyEventKind.Down) return false;
        if (k.Key == Key.Escape) return true;
        if (k is { Key: Key.Character, Text.Length: > 0 } && (k.Text.Span[0] == 'q' || k.Text.Span[0] == 'Q')) return true;
        if (k.Key == Key.Character && k.Modifiers.HasFlag(KeyModifiers.Control) &&
            k.Text.Length > 0 && (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C')) return true;
        return false;
    }

    static bool HandleScrollKey(KeyEvent k, int viewportRows, int docRows, ref int scrollOffset)
    {
        switch (k.Key)
        {
            case Key.UpArrow:   scrollOffset -= 1; return true;
            case Key.DownArrow: scrollOffset += 1; return true;
            case Key.PageUp:    scrollOffset -= viewportRows - 1; return true;
            case Key.PageDown:  scrollOffset += viewportRows - 1; return true;
            case Key.Home:      scrollOffset = 0; return true;
            case Key.End:       scrollOffset = docRows; return true;
            default:            return false;
        }
    }
}

// Blit the <paramref name="offscreen"/> document's rows [<paramref name="scrollOffset"/>,
// scrollOffset + viewportRows) into the visible <paramref name="screen"/>, plus a scroll
// indicator. Fragments whose anchor falls inside the visible window are re-attached at
// translated coordinates so inline graphics (Kitty icons, Sixel) scroll with the cells.
static void PaintScrolledShowcase(
    CellBufferView screen, CellBufferView offscreen, int scrollOffset, int viewportRows, int docRows, in Style style)
{
    screen.CursorVisible = false;
    screen.Clear(style);

    int innerCols = Math.Min(offscreen.Columns, screen.Columns);
    int innerRows = Math.Min(viewportRows, screen.Rows);

    var rect = screen.Bounds.LayoutContent(Anchor.Top, offscreen.Dimensions);
    var screenView = screen.View(rect);
    
    // Cells.
    for (int dy = 0; dy < innerRows; dy++)
    {
        int srcRow = scrollOffset + dy;
        if (srcRow >= offscreen.Rows) break;
        for (int dx = 0; dx < innerCols; dx++)
        {
            var cell = offscreen[dx, srcRow];
            // Reuse the raw indexer rather than Set(); we already have the destination grapheme +
            // style and don't want grapheme-width recomputation to second-guess the off-screen.
            screenView[dx, dy] = cell;
        }
    }

    // Fragments anchored within the visible window.
    foreach (var (anchor, entry) in offscreen.Fragments)
    {
        int relRow = anchor.Row - scrollOffset;
        if (relRow < 0 || relRow >= innerRows) continue;
        if (anchor.Column < 0 || anchor.Column >= innerCols) continue;
        screen.AddFragment(rect.Column + anchor.Column, rect.Row + relRow, entry.Fragment, entry.AnchorStyle);
    }

    // Scroll indicator in the top-right gutter.
    if (docRows > viewportRows)
    {
        int percent = (int) (100.0 * scrollOffset / Math.Max(1, docRows - viewportRows));

        string indicator = $" ▲▼ {percent,3}% ";

        int x = Math.Max(0, screen.Columns - 1 - indicator.Length);
        int y = Math.Min(rect.RowEnd, screen.Rows - 1 - (screen.Rows - viewportRows));

        if (x > rect.ColumnEnd + 2)
            x = rect.ColumnEnd + 2;

        var indicatorStyle = Style.Default
                                  .WithForeground(style.Background)
                                  .WithBackground(style.Foreground.WithAlpha(191));

        for (int i = 0; i < indicator.Length && x + i < screen.Columns; i++)
            screen.Set(x + i, y, indicator[i] == ' ' ? "" : indicator[i].ToString(), indicatorStyle);
    }
}

static RichText BuildFormattingShowcase(in Style defaultStyle = default)
{
    // A composite document mixing BBcode markup (for the prose-heavy sections) and the builder
    // (for FIGlet + HR blocks that don't have terse markup equivalents). Builds once and is
    // reused across resizes by re-formatting against the new column budget.
    var builder = new RichTextBuilder(defaultStyle);

    // Inline content registry: makes embedded icons / badges reachable from markup via
    // [content=name/]. The PNG paths use embedded resources; Icon falls back to its glyph
    // when the negotiated terminal can't render images, so the markup is portable.
    var assembly = Assembly.GetExecutingAssembly();

    var contentRegistry = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase)
                          {
                              ["settings"] = Icon.FromEmbedded(assembly, "Icons/settings.png", "⚙️ ",
                                                               renderSize: new Size(2, 0)),
                              ["download"] = Icon.FromEmbedded(assembly, "Icons/download.png", "⬇️ ",
                                                               renderSize: new Size(2, 0)),
                              ["calendar"] = Icon.FromEmbedded(assembly, "Icons/calendar.png", "📆 ",
                                                               renderSize: new Size(2, 0)),
                              ["power"] = Icon.FromEmbedded(assembly, "Icons/power.png", "⚡️",
                                                            renderSize: new Size(2, 0))
                          };

    var markupOptions = new TextMarkupOptions { Content = contentRegistry, DefaultStyle = defaultStyle };

    // Title.
    builder.Figlet("Rich Text",
                   FigletFonts.Standard,
                   defaultStyle.WithForeground(Color.FromHex("#f92572"))
                               .WithAttributes(TextAttributes.Bold),
                   alignment: TextAlignment.Center);

    builder.HorizontalRule(HorizontalRule.Double);

    // BBcode-driven intro.
    TextMarkup.Parse(
        "Cursorial's [b]TextFormatter[/b] lays out [fg=brightcyan]styled rich text[/fg] " +
        "into cell-grid lines. Hand it a [link=https://en.wikipedia.org/wiki/BBCode]BBcode[/link]-flavored " +
        "markup string or build it programmatically; the result is the same: an [i]immutable[/i] " +
        "[fg=brightgreen]FormattedText[/fg] you can paint once or many times.",
        builder,
        markupOptions);
    builder.EndParagraph();

    builder.HorizontalRule(margin: new Margins(0, 1));

    TextMarkup.Parse(
        "[b][fg=brightyellow]Wrap modes.[/fg][/b]  [u]WordWrap[/u] breaks at whitespace and word " +
        "boundaries; long words split mid-character. [u]WordWrapOverflow[/u] lets them overflow " +
        "past the right edge. [u]CharacterWrap[/u] is CJK-friendly and breaks at any grapheme. " +
        "[u]NoWrap[/u] keeps everything on a single line, relying on the active trim mode to clip.",
        builder,
        markupOptions);
    builder.EndParagraph();

    builder.HorizontalRule(margin: new Margins(0, 1));

    // Justify demonstration — paragraph with align=justify.
    TextMarkup.Parse(
        "[p align=justify][b][fg=brightyellow]Justify alignment.[/fg][/b]  Slack cells are " +
        "distributed across inter-word gaps so every line except the last fills the column budget " +
        "exactly. The last line of a paragraph stays in its natural alignment — it would look " +
        "stretched if it were justified to a half-empty row. Cell-aware width accounting means " +
        "this also works for mixed-script text without surprises.[/p]",
        builder,
        markupOptions);

    builder.HorizontalRule(margin: new Margins(0, 1));

    TextMarkup.Parse(
        "[b][fg=brightyellow]Soft hyphens.[/fg][/b]  Insert U+00AD where a long word can split. " +
        "The hyphen is invisible until the formatter wraps at it, in which case a literal " +
        "[fg=brightred]-[/fg] is appended to the previous line. Example: " +
        "interna­tionali­zation, hyper­exten­sibility, " +
        "contra­distinc­tion. Narrow the terminal to watch them activate.",
        builder,
        markupOptions);
    builder.EndParagraph();

    builder.HorizontalRule(margin: new Margins(0, 1));

    TextMarkup.Parse(
        "[b][fg=brightyellow]Grapheme maps.[/fg][/b]  Per-run substitution preserves cell-stream " +
        "semantics. [font=fullwidth]Fullwidth[/font] · [font=doublestruck]DoubleStruck[/font] · " +
        "[font=smallcaps]small caps[/font] · [font=superscript]012345[/font] · [font=subscript]012345[/font].",
        builder,
        markupOptions);
    builder.EndParagraph();

    builder.HorizontalRule(margin: new Margins(0, 1));

    TextMarkup.Parse(
        "[b][fg=brightyellow]Inline styles.[/fg][/b]  " +
        "[b]bold[/b] · [i]italic[/i] · [u]underline[/u] · [s]strike[/s] · " +
        "[fg=red]red[/fg] · [fg=#ffa500]hex orange[/fg] · [fg=42]palette 42[/fg] · " +
        "[bg=blue][fg=brightwhite] on blue [/fg][/bg] · " +
        "[link=https://github.com/anthropics/claude-code]Ctrl+click hyperlink[/link]",
        builder,
        markupOptions);
    builder.EndParagraph();

    builder.HorizontalRule(margin: new Margins(0, 1));

    TextMarkup.Parse(
        "[b][fg=brightyellow]Inline content.[/fg][/b]  Register an [i]IContent[/i] under a name " +
        "and reference it from markup with [fg=brightcyan]\\[content=name/\\][/fg]. The formatter " +
        "places it atomically in the paragraph flow at its measured width: " +
        "[content=settings/] settings · " +
        "[content=download/] download · " +
        "[content=calendar/] calendar · " +
        "[content=power/] power.",
        builder,
        markupOptions);
    builder.EndParagraph();

    builder.HorizontalRule(margin: new Margins(0, 1));

    TextMarkup.Parse(
        "Press [b][fg=brightcyan]q[/fg][/b] or [b][fg=brightcyan]Ctrl+C[/fg][/b] to exit.",
        builder,
        markupOptions);

    return builder.Build();
}

static async Task DemoPaletteAsync()
{
    Console.WriteLine("Palette demo. Opening alt screen — press q or Ctrl+C to exit.");

    var (session, buffer, renderer, style, palette, _) = await PrepareDemo();

    await using var ds = session;
    using var dp = palette;

    var writer = session.Output.Writer;

    ScreenWriter.WriteEnterAlternateScreen(writer);
    SgrEncoder.WriteReset(writer);
    await writer.FlushAsync();

    var events = new System.Collections.Concurrent.ConcurrentQueue<InputEvent>();
    using var stopCts = new CancellationTokenSource();

    // Input pump — tees events to the palette (for query-response resolution) and to the local
    // event queue (for resize + exit handling). Single owner of the input stream.
    var inputPump = Task.Run(async () =>
    {
        try
        {
            // ReSharper disable AccessToDisposedClosure
            await foreach (var evt in session.Input.ReadAllAsync(stopCts.Token))
            {
                palette.OnInputEvent(evt);
                events.Enqueue(evt);
            }
            // ReSharper restore AccessToDisposedClosure
        }
        catch (OperationCanceledException) { }
    });

    IColorPalette? colors = null;
    string? status = null;

    if (!palette.IsSupported)
    {
        status = "Terminal doesn't advertise OSC 4 palette-query support.";
    }
    else
    {
        // Generous timeout — 256 round-trips at a few ms each on a fast terminal, much slower over SSH.
        colors = await palette.QueryExtendedPaletteAsync(TimeSpan.FromSeconds(3), stopCts.Token);
        if (colors is null || colors.Count == 0)
        {
            status = """
                     No palette response within the timeout.
                     Press [b][fg=brightcyan]q[/fg][/b] or [b][fg=brightcyan]Esc[/fg][/b] to exit.
                     """;
        }
    }

    try
    {
        bool needsRepaint = true;

        while (!stopCts.IsCancellationRequested)
        {
            while (events.TryDequeue(out var evt))
            {
                switch (evt)
                {
                    case ResizeEvent { Columns: > 0, Rows: > 0 } r:
                        buffer.Resize(r.Columns, r.Rows);
                        needsRepaint = true;
                        break;
                    case KeyEvent k when IsExit(k):
                        stopCts.Cancel();
                        break;
                }
            }

            if (stopCts.IsCancellationRequested) break;

            if (needsRepaint)
            {
                PaintPaletteShowcase(buffer, colors, style, status);

                var scratch = new ArrayBufferWriter<byte>();
                renderer.Render(buffer, scratch);
                await writer.WriteAsync(scratch.WrittenMemory);
                await writer.FlushAsync();

                needsRepaint = false;
            }

            try { await Task.Delay(50, stopCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    finally
    {
        stopCts.Cancel();

        try { await inputPump.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }
        try { renderer.Close(writer); } catch { /* best-effort */ }

        CursorWriter.WriteShow(writer);
        SgrEncoder.WriteReset(writer);
        ScreenWriter.WriteLeaveAlternateScreen(writer);

        try { await writer.FlushAsync(); } catch { /* best-effort */ }
    }

    Console.WriteLine("Palette demo exited.");

    static bool IsExit(KeyEvent k)
    {
        if (k.Kind != KeyEventKind.Down) return false;
        if (k.Key == Key.Escape) return true;
        if (k is { Key: Key.Character, Text.Length: > 0 } && (k.Text.Span[0] == 'q' || k.Text.Span[0] == 'Q')) return true;
        if (k.Key == Key.Character && k.Modifiers.HasFlag(KeyModifiers.Control) &&
            k.Text.Length > 0 && (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C')) return true;
        return false;
    }
}

static void PaintPaletteShowcase(CellBufferView buffer, IColorPalette? palette, in Style style, string? statusMessage)
{
    buffer.CursorVisible = false;
    buffer.Clear();

    if (palette is null || palette.Count == 0)
    {
        var msg = statusMessage ?? "[fg=#dcdcff]No palette data.[/]";
        var rtb = new RichTextBuilder(style).Paragraph(alignment: TextAlignment.Center).Run(msg).EndParagraph().Build();
        var msgFormatted = new TextFormatter().Format(rtb, buffer.Columns, buffer.Rows);

        msgFormatted.Paint(buffer, buffer.Bounds, OutputCapabilities.None);
        
        return;
    }

    // Header.
    string header = $"OSC 4 palette — {palette.Count} entries (press q to exit)";
    var headerStyle = style.WithForeground(Color.FromRgb(20, 20, 30))
                           .WithBackground(Color.FromRgb(180, 220, 255))
                           .WithAttributes(TextAttributes.Bold);
    PaintTextRow(buffer, 0, 0, header.PadRight(buffer.Columns), headerStyle);

    // Grid layout. 16 columns; each cell wide enough to display "#RRGGBB" (7 chars) with one
    // cell of horizontal breathing room. Adapts to narrow terminals by shrinking the cell width.
    const int desiredGridCols = 16;
    const int desiredCellWidth = 7 + /* side margins */ 2;

    int cellWidth = Math.Max(desiredCellWidth, (buffer.Columns - 2) / desiredGridCols);
    int cellHeight = 2;

    int colsAvailable = Math.Min(16, buffer.Columns / cellWidth);
    
    int gridLeft = Math.Max(0, (buffer.Columns - cellWidth * colsAvailable) / 2);
    int gridTop = 2;

    int rowsAvailable = Math.Max(0, buffer.Rows - gridTop - 1);
    int gridRows = (palette.Count + colsAvailable - 1) / colsAvailable;

    if (gridRows * cellHeight > rowsAvailable)
    {
        cellHeight = 1;
        var maxGridRows = Math.Max(1, rowsAvailable);
        gridRows = Math.Min((palette.Count + colsAvailable - 1) / colsAvailable, maxGridRows);
    }

    int idx = 0;

    for (int gy = 0; gy < gridRows; gy++)
    {
        for (int gx = 0; gx < colsAvailable; gx++)
        {
            if (idx >= palette.Count) break;

            var color = palette[idx];
            // Only paint hex when we have a real RGB color (Color.Default sentinel means the
            // entry didn't respond — show as a dimly-marked tile so missing entries are obvious).
            bool hasColor = color.Kind == ColorKind.Rgb;
            var bg = hasColor ? color : Color.FromRgb(30, 30, 30);
            var fg = hasColor ? PickReadableForeground(color) : Color.FromRgb(120, 120, 120);
            var cellStyle = style.WithForeground(fg).WithBackground(bg);

            int cellX = gridLeft + gx * cellWidth;
            int cellY = gridTop + gy * cellHeight;

            // Fill the cell with the color.
            for (int dy = 0; dy < cellHeight && cellY + dy < buffer.Rows; dy++)
            {
                for (int dx = 0; dx < cellWidth && cellX + dx < buffer.Columns; dx++)
                    buffer.Set(cellX + dx, cellY + dy, " ", cellStyle);
            }

            // Overlay: hex code on the first row of the cell, index on the second.
            if (cellY < buffer.Rows)
            {
                string hex = hasColor ? $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}" : "—";
                int hx = cellX + Math.Max(0, (cellWidth - hex.Length) / 2);
                for (int i = 0; i < hex.Length && hx + i < buffer.Columns; i++)
                    buffer.Set(hx + i, cellY, hex[i].ToString(), cellStyle);
            }

            if (cellHeight >= 2 && cellY + 1 < buffer.Rows)
            {
                string label = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
                int lx = cellX + Math.Max(0, (cellWidth - label.Length) / 2);
                for (int i = 0; i < label.Length && lx + i < buffer.Columns; i++)
                    buffer.Set(lx + i, cellY + 1, label[i].ToString(), cellStyle);
            }

            idx++;
        }
    }
}

static Color PickReadableForeground(in Color bg)
{
    // Relative luminance (Rec. 601) is plenty for picking light-vs.-dark text on a solid block.
    int luminance = (bg.Red * 299 + bg.Green * 587 + bg.Blue * 114) / 1000;
    return luminance > 140
        ? Color.FromRgb(0, 0, 0)
        : Color.FromRgb(255, 255, 255);
}

static void PaintTextRow(CellBufferView buffer, int column, int row, string text, in Style style)
{
    if (row < 0 || row >= buffer.Rows) return;
    int x = column;
    foreach (char c in text)
    {
        if (x >= buffer.Columns) break;
        buffer.Set(x++, row, c.ToString(), style);
    }
}

static async Task DemoImageAsync(string argument)
{
    if (string.IsNullOrWhiteSpace(argument))
    {
        Console.WriteLine("Usage: image <path-to-png-jpeg-or-gif>");
        return;
    }

    byte[]? bytes = null;

    string path = argument.Trim('"', '\'');
    if (path.StartsWith("~/", StringComparison.Ordinal))
    {
        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
    }
    else if (path.StartsWith("embedded:") &&
             Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
             ResourceLoader.Default.TryLoadBytes(uri) is {} embeddedBytes)
    {
        bytes = embeddedBytes;
    }
    
    if (bytes is null && !File.Exists(path))
    {
        Console.WriteLine($"File not found: {path}");
        return;
    }

    if (bytes is null)
    {
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read {path}: {ex.Message}");
            return;
        }
    }

    var format = Path.GetExtension(path).ToLowerInvariant() switch
                 {
                     ".png"            => ImageFormat.Png,
                     ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                     ".gif"            => ImageFormat.Gif,
                     _                 => ImageFormat.Png // best-guess; Kitty will refuse non-PNG, iTerm2 accepts most
                 };

    Console.WriteLine(
        $"Image demo. Loading {path} ({bytes.Length} bytes, {format}). Press q or Ctrl+C to exit.");

    var (session, buffer, renderer, defaultStyle, palette, capabilities) = await PrepareDemo();

    await using var ds = session;
    using var dp = palette;

    var writer = session.Output.Writer;

    ScreenWriter.WriteEnterAlternateScreen(writer);
    SgrEncoder.WriteReset(writer);
    await writer.FlushAsync();

    var events = new System.Collections.Concurrent.ConcurrentQueue<InputEvent>();
    using var stopCts = new CancellationTokenSource();
    var inputPump = Task.Run(async () =>
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
        catch (OperationCanceledException) { }
    });

    try
    {
        // Render once on entry, then only repaint when something material changes (resize).
        // Images are expensive to re-emit (base64-encoded protocol payloads can be hundreds of
        // KB) — re-rendering at 30 fps for static content would be wasteful and visible as
        // flicker on slower terminals.
        bool needsRepaint = true;

        while (!stopCts.IsCancellationRequested)
        {
            while (events.TryDequeue(out var evt))
            {
                switch (evt)
                {
                    case ResizeEvent { Columns: > 0, Rows: > 0 } r:
                        buffer.Resize(r.Columns, r.Rows);
                        needsRepaint = true;
                        break;

                    case KeyEvent k when IsExit(k):
                        stopCts.Cancel();
                        break;
                }
            }

            if (stopCts.IsCancellationRequested) break;

            if (needsRepaint)
            {
                PaintImageShowcase(buffer, path, bytes, format, capabilities.Output, defaultStyle);

                var scratch = new ArrayBufferWriter<byte>();
                renderer.Render(buffer, scratch);
                await writer.WriteAsync(scratch.WrittenMemory);
                await writer.FlushAsync();

                needsRepaint = false;
            }

            try { await Task.Delay(50, stopCts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    finally
    {
        stopCts.Cancel();

        try { await inputPump.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }

        CursorWriter.WriteShow(writer);
        SgrEncoder.WriteReset(writer);
        ScreenWriter.WriteLeaveAlternateScreen(writer);

        try { await writer.FlushAsync(); } catch { /* best-effort */ }
    }

    Console.WriteLine("Image demo exited.");

    static bool IsExit(KeyEvent k)
    {
        if (k.Kind != KeyEventKind.Down) return false;
        if (k.Key == Key.Escape) return true;
        if (k is { Key: Key.Character, Text.Length: > 0 } && (k.Text.Span[0] == 'q' || k.Text.Span[0] == 'Q'))
            return true;
        if (k.Key == Key.Character &&
            k.Modifiers.HasFlag(KeyModifiers.Control) &&
            k.Text.Length > 0 &&
            (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C'))
        {
            return true;
        }
        return false;
    }
}

static void PaintImageShowcase(
    CellBufferView buf,
    string path,
    byte[] bytes,
    ImageFormat format,
    OutputCapabilities outputCaps,
    in Style defaultStyle)
{
    buf.CursorVisible = false;
    buf.Clear();
    buf.Fill(Cell.Blank with { Style = defaultStyle });

    int cols = buf.Columns;
    int rows = buf.Rows;

    // Reserve top rows for the header (path + protocol info) and bottom rows for the footer.
    // The image occupies the rectangle between them, centered horizontally.
    const int headerRows = 3;
    const int footerRows = 2;

    int availableRows = Math.Max(1, rows - headerRows - footerRows);
    int availableCols = Math.Max(1, cols - 2);

    int imageW = Math.Max(10, Math.Min(60, availableCols));
    int imageH = Math.Max(4, Math.Min(availableRows, 20));

    int anchorRow = headerRows;
    int anchorCol = Math.Max(1, (cols - imageW) / 2);

    // Header line 1: full path (truncated from the left if too long, so the file name stays visible).
    string header = $"image: {path}";
    if (header.Length > cols - 2) header = "..." + header[^(cols - 5)..];

    PaintLine(buf, 
              1,
              0,
              header,
              defaultStyle.WithForeground(Color.FromRgb(220, 220, 255))
                          .WithAttributes(TextAttributes.Bold));

    // Header line 2: chosen protocol + cell footprint.
    string protocol = ChooseProtocolLabel(outputCaps, format);
    string sub = $"  {bytes.Length:N0} bytes, {format} → {imageW}×{imageH} cells via {protocol}";

    PaintLine(buf, 1, 1, sub, defaultStyle.WithForeground(Color.FromRgb(160, 160, 200)));

    // Image (capability-aware — Image content picks Kitty / iTerm2 / placeholder at paint time).
    var data = new ImageData(bytes, format, new Size(imageW, imageH));

    var placeholderStyle = defaultStyle.WithBackground(Color.FromRgb
                                                           (40, 40, 70))
                                       .WithForeground(Color.FromRgb(200, 200, 220));

    var content = new Image(data, placeholderStyle);

    content.Paint(buf, anchorCol, anchorRow, defaultStyle, outputCaps);

    // Footer.
    int footerRow = Math.Min(anchorRow + imageH + 1, rows - 1);

    PaintLine(buf, 1, footerRow,
              "Press q or Ctrl+C to return.",
              defaultStyle.WithForeground(Color.FromRgb(180, 180, 220)));
}

static string ChooseProtocolLabel(OutputCapabilities caps, ImageFormat format)
{
    if (caps.Graphics.KittyGraphics && format == ImageFormat.Png)
        return "Kitty graphics protocol";
    if (caps.Graphics.ITerm2InlineImages)
        return "iTerm2 inline images";
    if (caps.Graphics.Sixel)
        return "Sixel graphics protocol";
    return "cell-grid placeholder (no graphics support)";
}

// Paint the render-demo content into <paramref name="buf"/>. Uses every piece of the rendering
// surface we've built: SGR styles via <c>buffer.Set</c>, wide glyphs that auto-pair into
// wide-left+continuation, an alpha-blended overlay with a pushed blending mode, and a clock
// that changes once per second — the clock is how you tell the diff renderer is doing per-cell
// deltas instead of repainting the whole screen each frame.
static void PaintRenderShowcase(CellBufferView buf, in Style style, OutputCapabilities outputCaps)
{
    // The sized title flows through a ScaledText content (Phase 3) — on terminals that honor
    // OSC 66 it attaches a SizedTextFragment; on the rest it falls back to a bundled FIGlet
    // face. The cell buffer + FrameRenderer take care of the rest (capability gating,
    // DECSC/DECRC bracketing, diff rendering).
    buf.CursorVisible = false;
    buf.Clear(style);

    int cols = buf.Columns;
    int rows = buf.Rows;

    // ---- Title bar ----
    var titleStyle = new Style(
        Foreground: Colors.Black,
        Background: Colors.LightBlue,
        Attributes: TextAttributes.Bold,
        UnderlineStyle: default,
        UnderlineColor: default);

    PaintLine(buf, 0, 0, "  Cursorial render demo — press q or Ctrl+C to exit  ".PadRight(cols), titleStyle);

    // ---- 16-color ANSI palette ----
    int row = 5;
    if (row < rows) PaintLine(buf, 1, row, "ANSI 16-color palette:", style);
    if (row + 1 < rows)
    {
        for (int i = 0; i < 16 && (1 + i * 3 + 2) < cols; i++)
        {
            var bg = Color.FromPalette((byte)i);
            var swatch = new Style(
                Foreground: Color.Default,
                Background: bg,
                Attributes: default,
                UnderlineStyle: default,
                UnderlineColor: default);
            int x = 1 + i * 3;
            buf.Set(x,     row + 1, " ", swatch);
            buf.Set(x + 1, row + 1, " ", swatch);
        }
    }

    // ---- Truecolor gradient ----
    row += 2;
    if (row < rows) PaintLine(buf, 1, row, "24-bit truecolor gradient:", style);
    if (row + 1 < rows)
    {
        int width = Math.Min(cols - 2, 60);
        for (int i = 0; i < width; i++)
        {
            // Hue-like sweep across red/green/blue.
            byte r = (byte)(255 - (i * 255 / width));
            byte g = (byte)(i * 255 / width);
            byte b = (byte)(128 + (i * 64 / width) % 128);
            var swatch = new Style(
                Foreground: Color.Default,
                Background: Color.FromRgb(r, g, b),
                Attributes: default,
                UnderlineStyle: default,
                UnderlineColor: default);
            buf.Set(1 + i, row + 1, " ", swatch);
        }
    }

    // ---- Wide glyphs ----
    row += 3;
    if (row < rows) PaintLine(buf, 1, row, "Wide glyphs (emoji + CJK, each occupies 2 cells):", style);

    if (row + 1 < rows)
    {
        int x = 1;
        foreach (var g in new[] { "🚀", "🌍", "🎨", "🐈", "中", "日", "本", "文" })
        {
            if (x + 2 >= cols) break;
            buf.Set(x, row + 1, g, style);
            x += 3; // 2 cells for the glyph + 1 space
        }
    }

    // ---- Attribute showcase ----
    row += 2;
    if (row < rows) PaintLine(buf, 1, row, "Text attributes:", style);
    if (row + 1 < rows)
    {
        int x = 1;
        x += PaintWord(buf, x, row + 1,
            "Bold ", style.WithAttributes(TextAttributes.Bold));
        x += PaintWord(buf, x, row + 1,
            "Italic ", style.WithAttributes(TextAttributes.Italic));
        x += PaintWord(buf, x, row + 1,
            "Underline ", style.WithAttributes(TextAttributes.Underline));
        x += PaintWord(buf, x, row + 1,
            "Curly ", style
                      .WithAttributes(TextAttributes.Underline)
                      .WithUnderlineStyle(UnderlineStyle.Curly)
                      .WithUnderlineColor(Color.FromRgb(255, 80, 80)));
        x += PaintWord(buf, x, row + 1,
            "Strike ", style.WithAttributes(TextAttributes.Strikethrough));
        PaintWord(buf, x, row + 1,
            "Inverse", style.WithAttributes(TextAttributes.Inverse));
    }

    // ---- Alpha-blended overlay ----
    row += 2;
    if (row < rows) PaintLine(buf, 1, row, "Alpha-blended overlay (Multiply mode, α=128):", style);
    if (row + 1 < rows && row + 4 < rows)
    {
        // Backdrop: solid color stripes.
        var stripes = new[]
                      {
                          Color.FromRgb(220, 60, 60),
                          Color.FromRgb(60, 220, 60),
                          Color.FromRgb(60, 60, 220),
                          Color.FromRgb(220, 220, 60)
                      };

        int barWidth = Math.Min(cols - 2, 60);

        for (int dy = 0; dy < 3; dy++)
        {
            if (row + 1 + dy >= rows) break;

            for (int x = 0; x < barWidth; x++)
            {
                var bg = stripes[(x * stripes.Length) / barWidth];

                buf.Set(1 + x, row + 1 + dy,
                        " ", new(Color.Default, bg, default, default, default));
            }
        }

        // Translucent overlay in Multiply mode. The mid-gray + Multiply darkens each stripe
        // toward its own color * gray, and the α=128 means we mix 50/50 with the original.
        buf.PushBlendingMode(BlendingModes.Multiply);
        try
        {
            int overlayStart = Math.Min(barWidth - 20, 10);
            int overlayWidth = Math.Min(20, barWidth - overlayStart - 1);
            for (int dy = 0; dy < 3; dy++)
            {
                if (row + 1 + dy >= rows) break;
                for (int dx = 0; dx < overlayWidth; dx++)
                {
                    buf.Set(1 + overlayStart + dx, row + 1 + dy,
                            " ", new Style(
                                Color.Default,
                                Color.FromRgba(128, 128, 128, 128),
                                default, default, default));
                }
            }
        }
        finally
        {
            buf.PopBlendingMode();
        }
    }

    row += 5;
    
    var tf = new TextFormatter { Trim = TextTrimming.CharacterEllipsis };
    var ft = tf.Format(RenderShowcaseFragments.GithubLink, cols, capabilities: outputCaps);
    
    ft.Paint(buf, buf.Bounds.Translate(1, row).WithSize(ft.Size), outputCaps);

    // ---- Sized Text below Title Bar ----
    // ScaledText is the capability-aware entry point: when the terminal honors OSC 66, it
    // attaches a SizedTextFragment (Kitty / Ghostty / etc.); otherwise it falls back to a
    // bundled FIGlet face. The styled title here uses italic + curly underline, so the OSC 66
    // path picks up the SGR backdrop visibly when supported.
    var sizedTitleStyle = style
        .WithForeground(Color.FromRgb(192, 202, 245))
        .WithBackground(Color.Transparent)
        .WithAttributes(TextAttributes.Italic | TextAttributes.Underline)
        .WithUnderlineStyle(UnderlineStyle.Curly)
        .WithUnderlineColor(Color.FromPalette(5));

    var desiredSize = RenderShowcaseFragments.Title.Measure(buf.Size, outputCaps);

    // Reuse a single ScaledText instance across frames — Phase 6.8's fragment diff uses
    // reference equality on the underlying IBufferFragment, so a stable instance lets the
    // renderer skip re-emission when the title and sizing haven't changed.
    RenderShowcaseFragments.Title.Paint(buf, new Rect(1, 2, desiredSize), style: sizedTitleStyle, capabilities: outputCaps);

    var iconMargin = new Margins(2, 1);

    desiredSize = RenderShowcaseFragments.Icon.Measure(buf.Size, outputCaps);

    RenderShowcaseFragments.Icon.Paint(buf,
                                       buf.Bounds.LayoutContent(Anchor.BottomRight, desiredSize, iconMargin),
                                       in style,
                                       outputCaps);

    const int iconY = 5;
    const int iconCount = 4;
    const int iconColumns = 2;

    var iconStyle = style.WithBackground(Color.FromRgb(40, 52, 87));

    string[] icons = ["settings.png", "download.png", "calendar.png", "power.png"];
    string[] iconFallbacks = ["⚙️", "⬇️", "📆", "⚡️"];

    for (int i = 0; i < iconCount; i++)
    {
        var d = icons.Length - i;
        var x = buf.Columns - ((d + 1) * (iconColumns + 1) + 1) - 8;
        // Resolve each PNG to an embedded resource under Cursorial.Rendering.Icons.<name>.
        // When the resource isn't shipped (or can't be loaded), Icon falls back to the glyph.
        var icon = Icon.FromEmbedded(
            assembly: Assembly.GetExecutingAssembly(),
            resourceName: $"Icons/{icons[i]}",
            fallbackGlyph: iconFallbacks[i],
            fallbackStyle: iconStyle,
            renderSize: new Size(iconColumns, Math.Max(iconColumns / 2, 1)));
        buf.Set(x, iconY, " ", iconStyle);
        buf.Set(x + 1, iconY, " ", iconStyle);
        icon.Measure(icon.RenderSize, outputCaps);
        icon.Paint(buf, column: x, row: iconY, style: iconStyle, capabilities: outputCaps);
        buf.Set(x, iconY + 2, icon.FallbackGlyph, iconStyle);

        if (i == 0)
        {
            var labelStyle = (style with { Foreground = style.Foreground.WithAlpha(0xC0) }).BlendOver(style);

            PaintWord(buf, x, iconY - 3, "PNG Icons w/", labelStyle);
            PaintWord(buf, x, iconY - 2, "Emoji Fallback", labelStyle);
        }
    }

    // ---- Clock in top-right corner ----
    // The right margin pad (` ` after the clock plus a single empty cell at the rightmost
    // column) is a deliberate workaround for ConEmu / Cmder, whose DECAWM-off behavior isn't
    // honored across frames — writing the last clock digit at the rightmost column lets a
    // residual deferred-wrap state trip later ticks and the digit wraps to the next line.
    // Backing off one cell keeps the digit on-screen on every terminal we test.
    string clock = DateTime.Now.ToString("HH:mm:ss");
    if (clock.Length + 2 < cols)
    {
        var clockStyle = style
            .WithForeground(Colors.TrueWhite)
            .WithBackground(Colors.Extended1)
            .WithAttributes(TextAttributes.Bold);
        int x = cols - clock.Length - 2;
        PaintLine(buf, x, 0, " " + clock + " ", clockStyle);
    }
}

static void PaintLine(CellBufferView buf, int col, int row, string text, Style style)
{
    if (row < 0 || row >= buf.Rows) return;
    int x = col;
    var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
    while (enumerator.MoveNext())
    {
        if (x >= buf.Columns) break;
        var cluster = (string)enumerator.Current;
        int width = buf.Set(x, row, cluster, style);
        x += width;
    }
}

static int PaintWord(CellBufferView buf, int col, int row, string text, Style style)
{
    int startCol = col;
    int x = col;
    var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
    while (enumerator.MoveNext())
    {
        if (row >= buf.Rows || x >= buf.Columns) break;
        var cluster = (string)enumerator.Current;
        int width = buf.Set(x, row, cluster, style);
        x += width;
    }
    return x - startCol;
}

static async Task ProbeAsync()
{
    Console.WriteLine("Probing: writing XTVERSION (CSI > q) + DA1 (CSI c).");
    Console.WriteLine("Reading raw response bytes for 1 second...");
    Console.WriteLine();

    var collected = new List<byte>();

    await using (var transports = StdioTransports.Open())
    {
        await transports.Sink.Writer.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'>', (byte)'q' });
        await transports.Sink.Writer.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'c' });
        await transports.Sink.Writer.FlushAsync();

        // Task.WhenAny timeout — the underlying read syscall doesn't honor cancellation
        // mid-call, so we rely on the timeout task to break the wait.
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(1));
        var reader = transports.Source.Reader;

        while (true)
        {
            var readTask = reader.ReadAsync().AsTask();
            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (completed != readTask) break;

            var result = await readTask;
            collected.AddRange(result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
    }

    if (collected.Count == 0)
    {
        Console.WriteLine("(no response received within 1 second)");
        return;
    }

    Console.WriteLine($"Received {collected.Count} byte(s):");
    Console.Write("  hex:   ");
    foreach (byte b in collected) Console.Write($"{b:X2} ");
    Console.WriteLine();
    Console.Write("  ascii: ");
    foreach (byte b in collected) Console.Write(b is >= 0x20 and < 0x7F ? (char)b : '·');
    Console.WriteLine();
}

static bool IsStopSignal(InputEvent inputEvent) =>
    inputEvent is KeyEvent { Key: Key.Character, Modifiers: KeyModifiers.Control, Text.Length: > 0 } k &&
    (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C');

static string FormatEvent(InputEvent inputEvent) =>
    inputEvent switch
    {
        KeyEvent k   => FormatKeyEvent(k),
        MouseEvent m => FormatMouseEvent(m),
        FocusEvent f => $"Focus       {(f.HasFocus ? "in" : "out")}",
        PasteEvent p => $"Paste       \"{Escape(new string(p.Text.Span))}\" ({p.Text.Length} char{(p.Text.Length == 1 ? "" : "s")})",
        ResizeEvent r => $"Resize      {r.Columns}x{r.Rows} cells"
                         + (r is { PixelWidth: {} pw, PixelHeight: {} ph } ? $" ({pw}x{ph} px)" : ""),
        DeviceResponseEvent d => $"DeviceResp  {d.Kind} \"{Encoding.ASCII.GetString(d.Payload.Span)}\"",
        UnknownEvent u        => $"Unknown     {u.RawBytes.Length} bytes: {BytesToHex(u.RawBytes.Span)}",
        _                     => $"<unhandled event type {inputEvent.GetType().Name}>"
    };

static string FormatKeyEvent(KeyEvent k)
{
    var sb = new StringBuilder($"Keyboard    { (k.Kind == KeyEventKind.Up ? "Up" : "Dn")} {k.Key,-14}");
    if (k.IsRepeat)
    {
        sb.Append(" (repeat");
        if (k.RepeatCount > 1) sb.Append('×').Append(k.RepeatCount);
        sb.Append(')');
    }
    if (k.Text.Length > 0) sb.Append(" Text=\"").Append(Escape(new string(k.Text.Span))).Append('"');
    // if (k.Modifiers != KeyModifiers.None)
        sb.Append($" Mod={k.Modifiers}");
    if (k.RawCode is { } code) sb.Append(" Raw=0x").Append(code.ToString("X4"));
    return sb.ToString();
}

static string FormatMouseEvent(MouseEvent m)
{
    var sb = new StringBuilder("Mouse       ");
    sb.Append(m.Kind);
    if (m.Button != MouseButton.None) sb.Append(' ').Append(m.Button);
    sb.Append(" @(").Append(m.Position.Column).Append(',').Append(m.Position.Row).Append(')');
    if (m.ButtonsHeld != MouseButtons.None) sb.Append(" held=").Append(m.ButtonsHeld);
    if (m.Modifiers != KeyModifiers.None) sb.Append(' ').Append(m.Modifiers);
    if (m.Kind == MouseEventKind.Wheel) sb.Append(" wheel=(").Append(m.WheelDeltaX).Append(',').Append(m.WheelDeltaY).Append(')');
    return sb.ToString();
}

static string Escape(string text)
{
    var sb = new StringBuilder(text.Length + 8);
    foreach (char c in text)
    {
        switch (c)
        {
            case '\\': sb.Append("\\\\"); break;
            case '\"': sb.Append("\\\""); break;
            case '\r': sb.Append("\\r"); break;
            case '\n': sb.Append("\\n"); break;
            case '\t': sb.Append("\\t"); break;
            case '\x1B': sb.Append("\\e"); break;
            default:
                if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:X2}");
                else sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}

static string BytesToHex(ReadOnlySpan<byte> bytes)
{
    var sb = new StringBuilder(bytes.Length * 3);
    foreach (byte b in bytes)
    {
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(b.ToString("X2"));
    }
    return sb.ToString();
}

static string FormatCapabilities(TerminalCapabilities caps)
{
    var sb = new StringBuilder();

    void Header(string title)
    {
        sb.AppendLine();
        sb.AppendLine(title);
        sb.AppendLine(new string('─', title.Length));
    }

    void Row(string label, object? value)
    {
        sb.Append("  ").Append(label.PadRight(28)).AppendLine(value?.ToString() ?? "(null)");
    }

    Header("Terminal");
    Row("Family",                caps.Terminal.Family);
    Row("Name",                  caps.Terminal.Name ?? "(unknown)");
    Row("Version",               caps.Terminal.Version ?? "(unknown)");
    Row("TERM env",              caps.Terminal.RawTermEnv ?? "(unset)");
    Row("TERM_PROGRAM env",      caps.Terminal.RawTermProgramEnv ?? "(unset)");
    Row("Inside multiplexer",    caps.Terminal.InsideMultiplexer);

    Header("Input — Mouse");
    Row("Button press / release", $"{caps.Input.Mouse.ButtonPress} / {caps.Input.Mouse.ButtonRelease}");
    Row("Drag",                  caps.Input.Mouse.Drag);
    Row("Motion (any-event)",    caps.Input.Mouse.Motion);
    Row("Wheel",                 caps.Input.Mouse.Wheel);
    Row("Pixel coordinates",     caps.Input.Mouse.PixelCoordinates);
    Row("Extended buttons",      caps.Input.Mouse.ExtendedButtonCount);

    Header("Input — Keyboard");
    Row("Distinguishes up/down", caps.Input.Keyboard.DistinguishesKeyUpDown);
    Row("Reports repeats",       caps.Input.Keyboard.ReportsRepeats);
    Row("Detailed modifiers",    caps.Input.Keyboard.DetailedModifiers);
    Row("Text input",            caps.Input.Keyboard.TextInput);

    Header("Input — Protocol");
    Row("Bracketed paste",       caps.Input.Protocol.BracketedPaste);
    Row("Focus events",          caps.Input.Protocol.FocusEvents);
    Row("Kitty keyboard",        caps.Input.Protocol.KittyKeyboardProtocol);
    Row("Win32 input mode",      caps.Input.Protocol.Win32InputMode);

    Header("Output — Color");
    Row("Depth",                 caps.Output.Color.Depth);
    Row("Truecolor verified",    caps.Output.Color.TruecolorVerified);
    Row("Default-color reset",   caps.Output.Color.DefaultColorReset);
    Row("OSC palette set",       caps.Output.Color.OscPaletteSet);

    Header("Output — Styling");
    Row("Italic",                caps.Output.Styling.Italic);
    Row("Underline",             caps.Output.Styling.Underline);
    Row("Extended underline",    caps.Output.Styling.ExtendedUnderline);
    Row("Colored underline",     caps.Output.Styling.ColoredUnderline);
    Row("Strikethrough",         caps.Output.Styling.Strikethrough);
    Row("Hyperlinks (OSC 8)",    caps.Output.Styling.Hyperlinks);

    Header("Output — Text Sizing (Kitty OSC 66)");
    Row("Width (w=)",            caps.Output.TextSizing.Width);
    Row("Scale (s=, n=/d=)",     caps.Output.TextSizing.Scale);
    Row("Reliable Wide Glyphs",  caps.Output.TextSizing.WideGlyphs);

    Header("Output — Graphics");
    Row("Sixel",                 caps.Output.Graphics.Sixel);
    Row("Kitty graphics",        caps.Output.Graphics.KittyGraphics);
    Row("iTerm2 inline images",  caps.Output.Graphics.ITerm2InlineImages);

    Header("Output — Cursor / Window");
    Row("Cursor shape control",  caps.Output.Cursor.ShapeControl);
    Row("Cursor visibility",     caps.Output.Cursor.VisibilityControl);
    Row("Cursor blink control",  caps.Output.Cursor.BlinkControl);
    Row("Cursor color control",  caps.Output.Cursor.ColorControl);
    Row("Cell pixel width",      caps.Output.Window.CellPixelWidth);
    Row("Cell pixel height",     caps.Output.Window.CellPixelHeight);
    Row("Title set",             caps.Output.Window.TitleSet);
    Row("Pixel size query",      caps.Output.Window.SizeQueryInPixels);
    Row("Alt screen buffer",     caps.Output.Window.AlternateScreenBuffer);
    Row("Native cursor shape",   caps.Output.Protocol.MouseCursorShape);

    Header("Output — Protocol opt-ins enabled");
    Row("SGR mouse reporting",   caps.Output.Protocol.SgrMouseEnable);
    Row("Mouse buttons",         caps.Output.Protocol.MouseButtonsEnable);
    Row("Mouse drag"     ,       caps.Output.Protocol.MouseDragEnable);
    Row("Mouse motion",          caps.Output.Protocol.MouseMotionEnable);
    Row("Focus reporting",       caps.Output.Protocol.FocusReportingEnable);
    Row("Bracketed paste",       caps.Output.Protocol.BracketedPasteEnable);
    Row("Kitty keyboard push",   caps.Output.Protocol.KittyKeyboardPush);
    Row("Win32 input mode",      caps.Output.Protocol.Win32InputModeEnable);
    Row("Synchronized output",   caps.Output.Protocol.SynchronizedOutput);

    return sb.ToString();
}

file sealed class TraceEventSink(List<InputEvent> events) : IInputEventSink
{
    public void OnInputEvent(InputEvent inputEvent) => events.Add(inputEvent);
}

/// <summary>
/// Cache of stable fragment instances used by the render showcase. Hoisting these out of the
/// per-frame paint loop lets the FrameRenderer's fragment diff (Phase 6.8) skip re-emission
/// when nothing meaningful has changed — the reference-equality contract means the same
/// instance across frames is enough to take the skip path.
/// </summary>
file static class RenderShowcaseFragments
{
    public static readonly Icon Icon = Icon.FromEmbedded(
        Assembly.GetExecutingAssembly(), 
        "Icons/cursorial_icon.png",
        "\\[[b][fg=cyan]C[/fg] [fg=white][u]>[/u][/fg][/b]\\]",
        renderSize: new Size(6, 0));

    public static readonly ScaledText Title = new(
        "Cursorial Rendering Demo",
        new TextSizing(Scale: 2),
        fallbackFont: DecoratedFont.QuarterBlockUnderline);

    public static readonly RichText GithubLink =
        TextMarkup.Parse("[i]Cursorial[/i] is hosted at [link=https://github.com/mstrobel/cursorial][fg=blue]GitHub[/fg][/link].");
}
