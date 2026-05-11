using System.Buffers;
using System.Text;
using GlowTerm.Core.Input;
using GlowTerm.Core.Input.Parsing;
using GlowTerm.Core.Terminal;
using GlowTerm.Demo;

// Simple REPL for exercising GlowTerm against a real terminal.
//
// Two-phase model: the prompt itself runs in cooked mode (Console.ReadLine for command entry).
// Each command that needs raw input opens a TerminalSession (which puts the terminal into raw
// mode), does its work, and disposes the session — restoring cooked mode before the next prompt.

PrintBanner();

while (true)
{
    Console.Write("glowterm> ");
    string? line = Console.ReadLine();
    if (line is null) break; // EOF (e.g., piped input)

    string command = line.Trim().ToLowerInvariant();
    if (command is "quit" or "exit" or "q") break;

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
            case "probe":
                await ProbeRawAsync();
                break;
            case "directprobe":
                await DirectProbeAsync();
                break;
            case "libcprobe":
                await LibcProbeAsync();
                break;
            case "minraw":
                MinRawTest();
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
    Console.WriteLine("GlowTerm demo — interactive runner");
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
    Console.WriteLine("  raw              Dump raw bytes from stdin verbatim (no parsing) for diagnostics");
    Console.WriteLine("                   (Press Ctrl+C to stop)");
    Console.WriteLine("  probe            Send XTVERSION + DA1 via StdioTransports / PipeWriter+PipeReader");
    Console.WriteLine("                   and dump raw response bytes for 1 second. Confirms the negotiator's");
    Console.WriteLine("                   read/write path independent of the parser.");
    Console.WriteLine("  directprobe      Same as 'probe' but bypasses StdioTransports and PipeWriter/Reader,");
    Console.WriteLine("                   using stty + Console.OpenStandardInput/Output directly.");
    Console.WriteLine("  libcprobe        Same as probe but bypasses System.Console entirely — writes / reads");
    Console.WriteLine("                   fd 1 / fd 0 via libc P/Invoke. If THIS sees responses but probe");
    Console.WriteLine("                   /directprobe don't, the System.Console API is interfering with raw mode.");
    Console.WriteLine("  minraw           Set raw mode, read ONE byte from fd 0 via libc, print and restore.");
    Console.WriteLine("                   Should return after a single keypress (no Enter required).");
    Console.WriteLine("  quit, exit       Exit the demo");
}

static async Task NegotiateAsync()
{
    // Capture the capabilities inside the session, then dispose the session BEFORE writing
    // results. Writing during a session puts our diagnostic output through a terminal in raw
    // mode (OPOST off) where bare \n doesn't get a CR — output overlaps and looks broken.
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

                // We're in raw mode (OPOST off) — write \r\n explicitly so each line wraps
                // back to column 0 instead of stair-stepping right.
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
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
    } // session disposed — terminal back to cooked mode.

    Console.WriteLine();
    Console.WriteLine($"Stopped after {eventCount} event(s).");
}

static async Task DumpRawAsync()
{
    Console.WriteLine("Dumping raw stdin bytes. Press Ctrl+C to stop.");
    Console.WriteLine();

    string sttyDiag = "(not captured)";

    await using (var transports = GlowTerm.Core.Terminal.Stdio.StdioTransports.Open())
    {
        if (!OperatingSystem.IsWindows())
        {
            sttyDiag = CaptureSttyFlags();
        }

        using var stopCts = new CancellationTokenSource();
        var reader = transports.Source.Reader;

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(stopCts.Token);
                var buffer = result.Buffer;

                // Copy out of the pipe buffer first — its segments are Span<byte> and can't
                // survive across the awaits below.
                var bytes = System.Buffers.BuffersExtensions.ToArray(buffer);
                reader.AdvanceTo(buffer.End);

                foreach (byte b in bytes)
                {
                    var msg = $"  byte 0x{b:X2}{(b is >= 0x20 and < 0x7F ? $" '{(char)b}'" : "")}\r\n";
                    await transports.Sink.Writer.WriteAsync(Encoding.UTF8.GetBytes(msg));

                    if (b == 0x03) // Ctrl+C
                    {
                        stopCts.Cancel();
                    }
                }
                await transports.Sink.Writer.FlushAsync();

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine();
        Console.WriteLine("--- termios state observed during raw read ---");
        Console.WriteLine($"  {sttyDiag}");
        Console.WriteLine("(Each flag should appear with a leading '-' for raw mode to be in effect.)");
        Console.WriteLine("-----------------------------------------------");
    }

    Console.WriteLine();
    Console.WriteLine("Raw dump stopped.");
}

static string CaptureSttyFlags()
{
    try
    {
        // Capture stdout only — same pattern as the reference Unix.Terminal.Ansi library.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "stty",
            Arguments = "-a",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return "(could not run stty -a)";

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // stty -a output is verbose; filter to the flags that matter for raw-mode diagnosis.
        // We're looking for: -icanon -echo -isig -opost -ixon. If any of these appear without
        // the leading '-', raw mode is NOT in effect.
        string[] interestingFlags = ["icanon", "echo", "isig", "opost", "ixon", "icrnl"];
        var sb = new StringBuilder();
        var tokens = output.Split([' ', '\t', '\n', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            string bare = token.TrimStart('-');
            if (Array.IndexOf(interestingFlags, bare) >= 0)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(token);
            }
        }
        return sb.Length > 0 ? sb.ToString() : "(no relevant flags found in stty -a output)";
    }
    catch (Exception ex)
    {
        return $"(stty -a failed: {ex.Message})";
    }
}

static void MinRawTest()
{
    Console.WriteLine("Min-raw test: apply stty raw -echo, then libc read() ONE byte from fd 0.");
    Console.WriteLine("If raw mode is in effect, this returns the moment you press a key.");
    Console.WriteLine("If you have to press Enter, raw mode is NOT in effect.");
    Console.WriteLine();
    Console.Write("Press a key: ");

    string? saved = null;
    try
    {
        saved = RunStty("-g", captureOutput: true)?.Trim();
        RunStty("raw -echo", captureOutput: false);

        var sttyDiag = CaptureSttyFlags();

        Span<byte> buffer = stackalloc byte[1];
        int n = LibcInterop.ReadFd(0, buffer);

        Console.Write($"\r\n");
        Console.WriteLine($"libc read returned {n} byte(s)");
        if (n > 0)
        {
            Console.WriteLine($"  byte 0x{buffer[0]:X2}{(buffer[0] is >= 0x20 and < 0x7F ? $" '{(char)buffer[0]}'" : "")}");
        }
        Console.WriteLine($"Termios flags during read: {sttyDiag}");
    }
    finally
    {
        if (saved is not null)
        {
            try { RunStty(saved, captureOutput: false); } catch { }
        }
    }
}

static async Task LibcProbeAsync()
{
    Console.WriteLine("Lib-direct probe: bypass System.Console entirely — libc write() probes + libc read() responses.");
    Console.WriteLine("Reading raw response bytes for 1 second...");
    Console.WriteLine();

    var collected = new List<byte>();
    string sttyDiag = "(not captured)";
    string? saved = null;

    try
    {
        saved = RunStty("-g", captureOutput: true)?.Trim();
        RunStty("raw -echo", captureOutput: false);
        sttyDiag = CaptureSttyFlags();

        // Write probes via libc write() on fd 1 — never touches System.Console.
        Span<byte> xtversion = stackalloc byte[] { 0x1B, (byte)'[', (byte)'>', (byte)'q' };
        Span<byte> da1 = stackalloc byte[] { 0x1B, (byte)'[', (byte)'c' };
        LibcInterop.WriteFd(1, xtversion);
        LibcInterop.WriteFd(1, da1);

        // Read responses via libc read() with poll() for the 1-second timeout — also bypasses
        // System.Console.
        var buffer = new byte[1024];
        var endTime = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < endTime)
        {
            int remainingMs = (int)Math.Max(0, (endTime - DateTime.UtcNow).TotalMilliseconds);
            if (remainingMs == 0) break;

            if (!LibcInterop.PollFdHasInput(0, Math.Min(remainingMs, 200)))
            {
                continue;
            }

            int n = LibcInterop.ReadFd(0, buffer);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) collected.Add(buffer[i]);
        }
    }
    finally
    {
        if (saved is not null)
        {
            try { RunStty(saved, captureOutput: false); } catch { }
        }
    }

    PrintProbeResult(collected, sttyDiag);
}

static async Task ProbeRawAsync()
{
    Console.WriteLine("Probing through StdioTransports + PipeWriter/Reader: writing XTVERSION (CSI > q) + DA1 (CSI c).");
    Console.WriteLine("Reading raw response bytes for 1 second...");
    Console.WriteLine();

    var collected = new List<byte>();
    string sttyDiag = "(not captured)";

    await using (var transports = GlowTerm.Core.Terminal.Stdio.StdioTransports.Open())
    {
        if (!OperatingSystem.IsWindows())
        {
            sttyDiag = CaptureSttyFlags();
        }

        // Write the same probes the negotiator sends.
        await transports.Sink.Writer.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'>', (byte)'q' });
        await transports.Sink.Writer.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'c' });
        await transports.Sink.Writer.FlushAsync();

        // Collect bytes for up to 1 second using Task.WhenAny so the timeout actually works
        // even when the underlying read syscall doesn't honor cancellation.
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
    } // transports disposed — terminal back to cooked mode for the result print below.

    PrintProbeResult(collected, sttyDiag);
}

static async Task DirectProbeAsync()
{
    Console.WriteLine("Direct probe: bypass StdioTransports entirely.");
    Console.WriteLine("Uses stty raw -echo and Console.OpenStandardOutput().Write() / Console.OpenStandardInput().ReadAsync()");
    Console.WriteLine("with no PipeWriter/PipeReader in between. If THIS sees a response but 'probe' doesn't,");
    Console.WriteLine("the issue is in our PipeWriter/PipeReader wrappers. If neither sees a response,");
    Console.WriteLine("the issue is at the transport level.");
    Console.WriteLine();

    var collected = new List<byte>();
    string sttyDiag = "(not captured)";
    string? savedSttyState = null;

    try
    {
        // 1. Save current stty state.
        savedSttyState = RunStty("-g", captureOutput: true)?.Trim();

        // 2. Apply raw mode.
        RunStty("raw -echo", captureOutput: false);

        // 3. Capture state to confirm raw mode took effect.
        sttyDiag = CaptureSttyFlags();

        // 4. Write probes directly to stdout (no PipeWriter).
        var stdout = Console.OpenStandardOutput();
        stdout.Write([0x1B, (byte)'[', (byte)'>', (byte)'q']);
        stdout.Write([0x1B, (byte)'[', (byte)'c']);
        stdout.Flush();

        // 5. Read responses directly from stdin (no PipeReader). Wrap the synchronous read in
        //    a Task so we can timeout via Task.WhenAny — same trick as elsewhere because the
        //    underlying read syscall doesn't honor cancellation mid-read.
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[1024];

        var endTime = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < endTime)
        {
            var readTask = Task.Run(() => stdin.Read(buffer, 0, buffer.Length));
            var remaining = endTime - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var timeoutTask = Task.Delay(remaining);
            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (completed != readTask) break;

            int n = await readTask;
            if (n <= 0) break;
            for (int i = 0; i < n; i++) collected.Add(buffer[i]);
        }
    }
    finally
    {
        // Restore stty.
        if (savedSttyState is not null)
        {
            try { RunStty(savedSttyState, captureOutput: false); } catch { }
        }
    }

    PrintProbeResult(collected, sttyDiag);
}

static string? RunStty(string arguments, bool captureOutput)
{
    // IMPORTANT: when APPLYING termios changes (captureOutput == false), redirect nothing.
    // Redirecting any stream — even just stderr — prevents stty's changes from taking effect
    // even though stty itself exits with code 0. See the lesson in PosixStdioTransports.
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "stty",
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = captureOutput,
        RedirectStandardError = false,
        RedirectStandardInput = false,
        CreateNoWindow = true,
    };
    using var process = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to start stty.");

    string? output = captureOutput ? process.StandardOutput.ReadToEnd() : null;
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"stty {arguments} exited with code {process.ExitCode}.");
    return output;
}

static void PrintProbeResult(List<byte> collected, string sttyDiag)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine();
        Console.WriteLine($"Termios flags during probe: {sttyDiag}");
        Console.WriteLine("(Each flag should appear with a leading '-' for raw mode to be in effect.)");
    }

    Console.WriteLine();
    if (collected.Count == 0)
    {
        Console.WriteLine("(no response received within 1 second)");
    }
    else
    {
        Console.WriteLine($"Received {collected.Count} byte(s):");
        Console.Write("  hex:   ");
        foreach (byte b in collected) Console.Write($"{b:X2} ");
        Console.WriteLine();
        Console.Write("  ascii: ");
        foreach (byte b in collected)
            Console.Write(b is >= 0x20 and < 0x7F ? (char)b : '·');
        Console.WriteLine();
    }
}

static bool IsStopSignal(InputEvent inputEvent) =>
    // In raw mode, Ctrl+C arrives as a KeyEvent with Key.Character + Modifiers.Control + Text="c".
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
    var sb = new StringBuilder("Key         ");
    sb.Append(k.Key);
    if (k.Modifiers != KeyModifiers.None)
    {
        sb.Append(' ').Append(k.Modifiers);
    }
    sb.Append(' ').Append(k.Kind);
    if (k.IsRepeat) sb.Append(" (repeat");
    if (k.IsRepeat && k.RepeatCount > 1) sb.Append('×').Append(k.RepeatCount);
    if (k.IsRepeat) sb.Append(')');
    if (k.Text.Length > 0)
    {
        sb.Append(" text=\"").Append(Escape(new string(k.Text.Span))).Append('"');
    }
    if (k.RawCode is { } code)
    {
        sb.Append(" raw=0x").Append(code.ToString("X4"));
    }
    return sb.ToString();
}

static string FormatMouseEvent(MouseEvent m)
{
    var sb = new StringBuilder("Mouse       ");
    sb.Append(m.Kind);
    if (m.Button != MouseButton.None)
    {
        sb.Append(' ').Append(m.Button);
    }
    sb.Append(" @(").Append(m.Position.Column).Append(',').Append(m.Position.Row).Append(')');
    if (m.ButtonsHeld != MouseButtons.None)
    {
        sb.Append(" held=").Append(m.ButtonsHeld);
    }
    if (m.Modifiers != KeyModifiers.None)
    {
        sb.Append(' ').Append(m.Modifiers);
    }
    if (m.Kind == MouseEventKind.Wheel)
    {
        sb.Append(" wheel=(").Append(m.WheelDeltaX).Append(',').Append(m.WheelDeltaY).Append(')');
    }
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
                if (c < 0x20 || c == 0x7F)
                    sb.Append($"\\x{(int)c:X2}");
                else
                    sb.Append(c);
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
