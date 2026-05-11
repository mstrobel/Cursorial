using System.Buffers;
using System.Text;
using Cursorial.Core.Input;
using Cursorial.Core.Input.Parsing;
using Cursorial.Core.Terminal;

// REPL for exercising Cursorial against a real terminal.
//
// Two-phase model: the prompt itself runs in cooked mode (Console.ReadLine for command entry).
// Each command that needs raw input opens a TerminalSession (which puts the terminal into raw
// mode), does its work, and disposes the session — restoring cooked mode before the next prompt.

PrintBanner();

while (true)
{
    Console.Write("glowterm> ");
    string? line = Console.ReadLine();
    if (line is null) break;

    string command = line.Trim().ToLowerInvariant();
    if (command is "quit" or "exit" or "q") break;
    if (command.Length == 0) continue;

    try
    {
        switch (command)
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
            case "raw":
                await DumpRawAsync();
                break;
            case "trace":
                await TraceAsync();
                break;
            case "probe":
                await ProbeAsync();
                break;
            default:
                Console.WriteLine($"Unknown command: '{command}'. Type 'help' for the list.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

Console.WriteLine("Bye.");
return 0;

static void PrintBanner()
{
    Console.WriteLine("Cursorial demo — interactive runner");
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
    Console.WriteLine("  raw              Dump raw bytes from stdin verbatim — no parsing.");
    Console.WriteLine("                   Useful for seeing exactly what the terminal sends.");
    Console.WriteLine("                   (Press Ctrl+C to stop)");
    Console.WriteLine("  trace            Like 'read', but each input chunk is logged as raw bytes");
    Console.WriteLine("                   followed by the decoded events. Lets you cross-reference");
    Console.WriteLine("                   wire format against parser output for protocol debugging.");
    Console.WriteLine("                   (Press Ctrl+C to stop)");
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
    await using (var session = await TerminalSession.OpenAsync())
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

static async Task DumpRawAsync()
{
    Console.WriteLine("Dumping raw stdin bytes. Press Ctrl+C to stop.");
    Console.WriteLine();

    await using (var transports = Cursorial.Core.Terminal.Stdio.StdioTransports.Open())
    {
        using var stopCts = new CancellationTokenSource();
        var reader = transports.Source.Reader;

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(stopCts.Token);
                var bytes = BuffersExtensions.ToArray(result.Buffer);
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

    await using var transports = Cursorial.Core.Terminal.Stdio.StdioTransports.Open();
    var mode = new VtInputMode();
    var negotiator = new VtTerminalNegotiator(transports.Source, transports.Sink, mode);

    try
    {
        // For tracing we want every key event to arrive as an escape sequence carrying full
        // modifier state — including for plain text keys. Otherwise Kitty's text-shortcut
        // optimization elides the modifier annotation on presses of printable keys, leaving
        // an asymmetric press-vs-release picture in the trace.
        var traceOptions = new NegotiationOptions
        {
            KittyKeyboardFlags = KittyKeyboardFlags.DisambiguateEscapeCodes
                                 | KittyKeyboardFlags.ReportEventTypes
                                 | KittyKeyboardFlags.ReportAlternateKeys
                                 | KittyKeyboardFlags.ReportAssociatedText
                                 | KittyKeyboardFlags.ReportAllKeysAsEscapeCodes,
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
                    var bytes = BuffersExtensions.ToArray(buffer);
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

    Console.WriteLine();
    Console.WriteLine("Trace stopped.");
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

static async Task ProbeAsync()
{
    Console.WriteLine("Probing: writing XTVERSION (CSI > q) + DA1 (CSI c).");
    Console.WriteLine("Reading raw response bytes for 1 second...");
    Console.WriteLine();

    var collected = new List<byte>();

    await using (var transports = Cursorial.Core.Terminal.Stdio.StdioTransports.Open())
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
            collected.AddRange(BuffersExtensions.ToArray(result.Buffer));
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
    inputEvent is KeyEvent { Key: Key.Character, Modifiers: KeyModifiers.Control } k
        && k.Text.Length > 0
        && (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C');

static string FormatEvent(InputEvent inputEvent) => inputEvent switch
{
    KeyEvent k => FormatKeyEvent(k),
    MouseEvent m => FormatMouseEvent(m),
    FocusEvent f => $"Focus       {(f.HasFocus ? "in" : "out")}",
    PasteEvent p => $"Paste       \"{Escape(new string(p.Text.Span))}\" ({p.Text.Length} char{(p.Text.Length == 1 ? "" : "s")})",
    ResizeEvent r => $"Resize      {r.Columns}x{r.Rows} cells"
                     + (r.PixelWidth is { } pw && r.PixelHeight is { } ph ? $" ({pw}x{ph} px)" : ""),
    DeviceResponseEvent d => $"DeviceResp  {d.Kind} \"{Encoding.ASCII.GetString(d.Payload.Span)}\"",
    UnknownEvent u => $"Unknown     {u.RawBytes.Length} bytes: {BytesToHex(u.RawBytes.Span)}",
    _ => $"<unhandled event type {inputEvent.GetType().Name}>",
};

static string FormatKeyEvent(KeyEvent k)
{
    var sb = new StringBuilder($"Keyboard    { (k.Kind == KeyEventKind.Up ? "Up" : "Dn")} {k.Key}");
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

    Header("Output — Graphics");
    Row("Sixel",                 caps.Output.Graphics.Sixel);
    Row("Kitty graphics",        caps.Output.Graphics.KittyGraphics);
    Row("iTerm2 inline images",  caps.Output.Graphics.Iterm2InlineImages);

    Header("Output — Cursor / Window");
    Row("Cursor shape control",  caps.Output.Cursor.ShapeControl);
    Row("Cursor visibility",     caps.Output.Cursor.VisibilityControl);
    Row("Cursor blink control",  caps.Output.Cursor.BlinkControl);
    Row("Cursor color control",  caps.Output.Cursor.ColorControl);
    Row("Title set",             caps.Output.Window.TitleSet);
    Row("Pixel size query",      caps.Output.Window.SizeQueryInPixels);
    Row("Alt screen buffer",     caps.Output.Window.AlternateScreenBuffer);

    Header("Output — Protocol opt-ins enabled");
    Row("SGR mouse",             caps.Output.Protocol.SgrMouseEnable);
    Row("Any-event mouse",       caps.Output.Protocol.AnyEventMouseEnable);
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
