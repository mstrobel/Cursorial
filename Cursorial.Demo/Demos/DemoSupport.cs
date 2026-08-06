using System.Text;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;

// ReSharper disable RedundantAssignment
// ReSharper disable CheckNamespace

// Shared helpers for the demos. Moved out of Program.cs so both the still-static demos there and
// the migrated IDemo classes can call them. Pure utility — no per-demo state.
internal static class DemoSupport
{
    public static async Task<(TerminalSession session,
                              CellBuffer buffer,
                              FrameRenderer renderer,
                              CellStyle style,
                              TerminalPalette palette,
                              TerminalCapabilities capabilities)> PrepareDemo()
    {
        var cts = new CancellationTokenSource();

        cts.CancelAfter(TimeSpan.FromSeconds(5));

        // ReSharper disable once MethodSupportsCancellation
        var session = await TerminalSession.OpenAsync();

        // Initial size from Console (TIOCGWINSZ-equivalent; safe for read-only queries even though
        // we don't open the Console streams). The SIGWINCH-driven ResizeEvent will correct us once
        // it fires, but starting with the right dimensions avoids a redraw on the first resize.
        // ReSharper disable once MethodSupportsCancellation
        var size = await session.QueryTerminalSizeAsync();

        var capabilities = session.Capabilities;
        var palette = new TerminalPalette(session.Output, capabilities.Output);

        Color fg;
        Color bg;

        var themeBase = ThemeVariant.FromCapabilities(capabilities);

        Color[] colors;

        if (themeBase.IsDark)
        {
            fg = Color.FromHex("#c0caf5");
            bg = Color.FromHex("#0d0f18");

            colors =
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
        }
        else
        {
            fg = Color.FromHex("#343b58");
            bg = Color.FromHex("#e6e7ec");

            // Light (Tokyo-Night-Day-inspired): the dark palette's named hues are too pale to read on a light
            // background, so darken/saturate them (the gallery's light tokens for red/green/amber/blue/purple/
            // cyan), keep 0 dark / 15 dark-ink, and a light background entry.
            colors =
            [
                Color.FromHex("#16161e"), // 0  black
                Color.FromHex("#8c4351"), // 1  red
                Color.FromHex("#485e30"), // 2  green
                Color.FromHex("#8f5e15"), // 3  yellow
                Color.FromHex("#34548a"), // 4  blue
                Color.FromHex("#5a3e8e"), // 5  magenta
                Color.FromHex("#0f4b6e"), // 6  cyan
                Color.FromHex("#565a6e"), // 7  white (mid grey on light)
                Color.FromHex("#9699a3"), // 8  bright black
                Color.FromHex("#a14860"), // 9  bright red
                Color.FromHex("#587539"), // 10 bright green
                Color.FromHex("#a06a1a"), // 11 bright yellow
                Color.FromHex("#3a5f9e"), // 12 bright blue
                Color.FromHex("#6a4a9e"), // 13 bright magenta
                Color.FromHex("#1a6088"), // 14 bright cyan
                Color.FromHex("#343b58"), // 15 bright white (dark ink)
                Color.FromHex("#e6e7ec"), // 16 background
                Color.FromHex("#b5621f"), // 17 orange
                Color.FromHex("#c64343")  // 18 red
            ];
        }

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

        var style = CellStyle.Default with { Foreground = fg, Background = bg }; // Use rgb colors to enable alpha blending.

        // Falling back to Console.WindowWidth/Height as a last resort, but on non-console stdout
        // (MSYS2 / Cygwin / MobaXterm bash) those throw IOException("The handle is invalid").
        // Default to 80x24 in that case, so the demo at least starts; a SIGWINCH-equivalent resize
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

        var buffer = new CellBuffer(cols, rows, capabilities) { CursorVisible = false };

        // Hand the renderer the negotiated capabilities so it can quantize cells before emission
        // (RGB → palette where truecolor isn't available, extended underline → Single where the
        // extended forms aren't supported, drop unsupported attributes, …). Without this, terminals
        // like Apple Terminal that report Ansi256 receive raw truecolor SGR and render
        // unpredictably.
        var renderer = new FrameRenderer(capabilities.Output);

        return (session, buffer, renderer, style, palette, capabilities);
    }

    // The q / Q / Esc / Ctrl+C exit gesture, shared by the interactive demos.
    public static bool IsExit(KeyEvent k)
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

    public static bool IsStopSignal(InputEvent inputEvent) =>
        inputEvent is KeyEvent { Key: Key.Character, Modifiers: KeyModifiers.Control, Text.Length: > 0 } k &&
        (k.Text.Span[0] == 'c' || k.Text.Span[0] == 'C');

    public static async Task DrainEventsAsync(
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

    public static async Task WriteSizedAsync(
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

    public static async Task WriteLineAsync(System.IO.Pipelines.PipeWriter writer, string text)
    {
        // Raw mode (OPOST off) — write \r\n explicitly.
        await writer.WriteAsync(Encoding.UTF8.GetBytes(text + "\r\n"));
    }

    public static void PaintLine(CellBufferView buf, int col, int row, string text, CellStyle style)
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

    public static int PaintWord(CellBufferView buf, int col, int row, string text, CellStyle style)
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

    public static void PaintTextRow(CellBufferView buffer, int column, int row, string text, in CellStyle style)
    {
        if (row < 0 || row >= buffer.Rows) return;
        int x = column;
        foreach (char c in text)
        {
            if (x >= buffer.Columns) break;
            buffer.Set(x++, row, c.ToString(), style);
        }
    }

    public static string FormatEvent(InputEvent inputEvent) =>
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

    public static string FormatKeyEvent(KeyEvent k)
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

    public static string FormatMouseEvent(MouseEvent m)
    {
        var sb = new StringBuilder("Mouse       ");
        sb.Append(m.Kind);
        if (m.Button != MouseButton.None) sb.Append(' ').Append(m.Button);
        sb.Append(" @(").Append(m.Position.Column).Append(',').Append(m.Position.Row).Append(')');
        if (m.ButtonsHeld != MouseButtons.None) sb.Append(" held=").Append(m.ButtonsHeld);
        if (m.Modifiers != KeyModifiers.None) sb.Append(' ').Append(m.Modifiers);
        if (m.Kind == MouseEventKind.Wheel) sb.Append(" wheel=(").Append(m.WheelDeltaX).Append(',').Append(m.WheelDeltaY).Append(')');
        if (m.Kind is MouseEventKind.Click || (m.Kind is MouseEventKind.ButtonDown or MouseEventKind.ButtonUp && m.ClickCount > 1))
            sb.Append(" clicks=").Append(m.ClickCount);
        return sb.ToString();
    }

    public static string Escape(string text)
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

    public static string BytesToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (byte b in bytes)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
