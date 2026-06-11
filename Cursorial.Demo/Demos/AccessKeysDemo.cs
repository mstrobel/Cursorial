using System.Text;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Terminal;

// Probe 3 (ui-layer-design §14 P0): the access-key terminal probe. Validates the requirement-6
// gate truth table on live terminals BEFORE S3 exists. Opens a session, prints the gate inputs
// once — the negotiated keyboard / protocol capabilities, the RAW Kitty flags read from the
// VtInputDevice's VtInputMode (walked via IInputDeviceDecorator.Inner), and terminal identity —
// then pins the gate verdict:
//
//     (Keyboard.DistinguishesKeyUpDown && Keyboard.ReportsRepeats) || Protocol.Win32InputMode
//         => Alt-toggle mode, otherwise permanently-visible mode.
//
// After the header it streams events live, highlighting LeftAlt / RightAlt key events (the Alt
// "bracket"), the inferred Alt-held window — set on Alt Down, cleared on Alt Up OR terminal
// focus-out, with the reason shown — FocusEvents, and Alt-modified chords (the legacy ESC-prefix
// path). A status line tracks the current Alt-held state. q / Esc exits.
internal sealed class AccessKeysDemo : IDemo
{
    public string Name => "accesskeys";
    public IReadOnlyList<string> Aliases => ["ak"];
    public string Description =>
        "Access-key gate probe: print gate inputs + verdict, then stream Alt/focus events live (q/Esc exits).";

    // SGR fragments for highlighting; raw strings because the demo writes bytes itself in raw mode.
    private const string Reset   = "\x1b[0m";
    private const string Bold    = "\x1b[1m";
    private const string Dim     = "\x1b[2m";
    private const string Red     = "\x1b[31m";
    private const string Green   = "\x1b[32m";
    private const string Yellow  = "\x1b[33m";
    private const string Magenta = "\x1b[35m";
    private const string Cyan    = "\x1b[36m";
    private const string Inverse = "\x1b[7m";

    // Clear the in-place status line (the cursor parks on it between events).
    private const string ClearStatusLine = "\r\x1b[2K";

    public async Task RunAsync(string argument)
    {
        int eventCount = 0, altKeyEvents = 0, chordCount = 0, focusCount = 0;
        bool leftHeld = false, rightHeld = false;

        TerminalCapabilities caps;
        KittyKeyboardFlags? rawKittyFlags;
        bool gateVerdict;

        await using (var session = await TerminalSession.OpenAsync())
        {
            caps = session.Capabilities;

            // The doc/input maps' sanctioned route to the raw negotiated flags: walk the decorator
            // chain down to the VtInputDevice and read its shared VtInputMode.
            rawKittyFlags = FindVtInputDevice(session.Input)?.Mode.KittyKeyboard;

            var keyboard = caps.Input.Keyboard;
            var protocol = caps.Input.Protocol;

            // The pinned requirement-6 gate (ui-layer-design §7.7/§7.8, punch 21).
            gateVerdict = (keyboard.DistinguishesKeyUpDown && keyboard.ReportsRepeats) || protocol.Win32InputMode;

            using var stopCts = new CancellationTokenSource();

            try
            {
                await WriteAsync(session, FormatHeader(caps, rawKittyFlags, gateVerdict), stopCts.Token);
                await WriteAsync(session, StatusLine(), stopCts.Token);

                await foreach (var inputEvent in session.Input.ReadAllAsync(stopCts.Token))
                {
                    eventCount++;

                    var sb = new StringBuilder(ClearStatusLine);
                    bool wasHeld = leftHeld || rightHeld;
                    string? transition = null;

                    switch (inputEvent)
                    {
                        // The Alt bracket itself. Matched before the chord arm because on Kitty-class
                        // terminals the LeftAlt/RightAlt events carry Modifiers = Alt themselves.
                        case KeyEvent { Key: Key.LeftAlt or Key.RightAlt } alt:
                        {
                            altKeyEvents++;
                            bool down = alt.Kind != KeyEventKind.Up;

                            if (alt.Key == Key.LeftAlt) leftHeld = down;
                            else rightHeld = down;

                            sb.Append(Yellow).Append(Bold)
                              .Append($"[{eventCount,4}] ALT    {alt.Key} {(down ? "Dn" : "Up")}");
                            if (alt.IsRepeat)
                            {
                                sb.Append(" (repeat");
                                if (alt.RepeatCount > 1) sb.Append('×').Append(alt.RepeatCount);
                                sb.Append(')');
                            }
                            if (alt.Synthesized) sb.Append(" SYNTHESIZED");
                            sb.Append(Reset);

                            bool nowHeld = leftHeld || rightHeld;
                            if (nowHeld && !wasHeld)
                                transition = $"{Green}      ── Alt-held window OPEN ({alt.Key} Down){Reset}";
                            else if (!nowHeld && wasHeld)
                                transition = $"{Red}      ── Alt-held window CLOSED (reason: {alt.Key} Up){Reset}";
                            break;
                        }

                        case FocusEvent focus:
                        {
                            focusCount++;
                            sb.Append(Cyan).Append(Bold)
                              .Append($"[{eventCount,4}] FOCUS  {(focus.HasFocus ? "in" : "out")}");
                            if (focus.Synthesized) sb.Append(" SYNTHESIZED");
                            sb.Append(Reset);

                            if (!focus.HasFocus)
                            {
                                // Terminal focus-out clears the inferred window unconditionally —
                                // Alt+Tab swallows the Alt Up, so we'd otherwise latch "held" forever.
                                leftHeld = rightHeld = false;
                                if (wasHeld)
                                    transition = $"{Red}      ── Alt-held window CLOSED (reason: terminal focus lost — Alt+Tab swallows the Up){Reset}";
                            }
                            break;
                        }

                        // An Alt-modified chord on another key — on legacy terminals this is the
                        // ESC-prefix path and the ONLY Alt evidence we ever see.
                        case KeyEvent chord when (chord.Modifiers & KeyModifiers.Alt) != 0:
                        {
                            chordCount++;
                            sb.Append(Magenta).Append(Bold)
                              .Append($"[{eventCount,4}] CHORD  Alt+{DescribeKey(chord)} {(chord.Kind == KeyEventKind.Up ? "Up" : "Dn")}");
                            if (chord.IsRepeat) sb.Append(" (repeat)");
                            if (chord.Modifiers != KeyModifiers.Alt) sb.Append($" Mod={chord.Modifiers}");
                            if (chord.Synthesized) sb.Append(" SYNTHESIZED");
                            sb.Append(Reset);
                            sb.Append(wasHeld
                                ? $"{Dim} (bracket observed — Alt window already open){Reset}"
                                : $"{Dim} (NO bracket observed — legacy ESC-prefix / chord-flash path){Reset}");
                            break;
                        }

                        default:
                            sb.Append(Dim)
                              .Append($"[{eventCount,4}] ").Append(DemoSupport.FormatEvent(inputEvent))
                              .Append(Reset);
                            break;
                    }

                    sb.Append("\r\n");
                    if (transition is not null) sb.Append(transition).Append("\r\n");
                    sb.Append(StatusLine());

                    await WriteAsync(session, sb.ToString(), stopCts.Token);

                    if (IsExitSignal(inputEvent))
                    {
                        await WriteAsync(session, ClearStatusLine, stopCts.Token);
                        stopCts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on stop */ }
        }

        // Cooked mode again — print a copy-friendly summary for the comparison table.
        Console.WriteLine();
        Console.WriteLine($"Stopped after {eventCount} event(s): " +
                          $"{altKeyEvents} Alt key event(s), {chordCount} Alt chord(s), {focusCount} focus event(s).");
        Console.WriteLine($"Terminal: {caps.Terminal.Family} / {caps.Terminal.Name ?? "(unknown)"} / {caps.Terminal.Version ?? "(unknown)"}");
        Console.WriteLine($"Gate inputs: UpDown={caps.Input.Keyboard.DistinguishesKeyUpDown} " +
                          $"Repeats={caps.Input.Keyboard.ReportsRepeats} " +
                          $"DetailedMods={caps.Input.Keyboard.DetailedModifiers} " +
                          $"Kitty={caps.Input.Protocol.KittyKeyboardProtocol} " +
                          $"Win32={caps.Input.Protocol.Win32InputMode} " +
                          $"RawKittyFlags={(rawKittyFlags is { } f ? f.ToString() : "(unavailable)")}");
        Console.WriteLine($"Gate verdict: {(gateVerdict ? "Alt-toggle mode" : "permanently-visible mode")}");

        string StatusLine()
        {
            bool held = leftHeld || rightHeld;
            return $"{Inverse} Alt held: {(held ? "YES" : "no ")} " +
                   $"[L:{(leftHeld ? '■' : '·')} R:{(rightHeld ? '■' : '·')}]  " +
                   $"alt-keys:{altKeyEvents}  chords:{chordCount}  focus:{focusCount}  events:{eventCount}  " +
                   $"— q/Esc exits {Reset}";
        }
    }

    private static string FormatHeader(TerminalCapabilities caps, KittyKeyboardFlags? rawFlags, bool gateVerdict)
    {
        var keyboard = caps.Input.Keyboard;
        var protocol = caps.Input.Protocol;

        var sb = new StringBuilder();

        void Line(string text = "") => sb.Append(text).Append("\r\n");
        void Row(string label, object? value) =>
            Line($"  {label,-34}{value?.ToString() ?? "(unknown)"}");

        Line($"{Bold}Access-key terminal probe{Reset} (ui-layer-design §14 P0, probe 3)");
        Line("Purpose: validates doc §7.7's gate table — run on kitty, WezTerm, Windows Terminal,");
        Line("         xterm, tmux, Alacritty and compare.");
        Line();

        Line($"{Bold}Terminal{Reset}");
        Row("Family / Name / Version", $"{caps.Terminal.Family} / {caps.Terminal.Name ?? "(unknown)"} / {caps.Terminal.Version ?? "(unknown)"}");
        Row("TERM / TERM_PROGRAM", $"{caps.Terminal.RawTermEnv ?? "(unset)"} / {caps.Terminal.RawTermProgramEnv ?? "(unset)"}");
        Row("Inside multiplexer", caps.Terminal.InsideMultiplexer);
        Line();

        Line($"{Bold}Gate inputs{Reset}");
        Row("Keyboard.DistinguishesKeyUpDown", keyboard.DistinguishesKeyUpDown);
        Row("Keyboard.ReportsRepeats", keyboard.ReportsRepeats);
        Row("Keyboard.DetailedModifiers", keyboard.DetailedModifiers);
        Row("Protocol.KittyKeyboardProtocol", protocol.KittyKeyboardProtocol);
        Row("Protocol.Win32InputMode", protocol.Win32InputMode);

        if (rawFlags is { } flags)
        {
            Row("Raw negotiated Kitty flags", flags == KittyKeyboardFlags.None ? "None" : flags);
            Row("  has ReportEventTypes", flags.HasFlag(KittyKeyboardFlags.ReportEventTypes));
            Row("  has ReportAllKeysAsEscapeCodes",
                $"{flags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes)}  " +
                $"{Dim}(standalone Alt Dn/Up needs this on Kitty-protocol terminals){Reset}");
        }
        else
        {
            Row("Raw negotiated Kitty flags", "(unavailable — no VtInputDevice in the decorator chain)");
        }
        Line();

        Line($"{Bold}GATE VERDICT{Reset}  (DistinguishesKeyUpDown && ReportsRepeats) || Win32InputMode = {Bold}{gateVerdict}{Reset}");
        Line(gateVerdict
            ? $"  => {Green}{Bold}Alt-toggle mode{Reset} — underscore cues toggle while Alt is held."
            : $"  => {Yellow}{Bold}permanently-visible mode{Reset} — underscore cues are always shown.");
        Line();

        Line("Hold and release each Alt key, try Alt+letter chords, Alt+Tab away and back (focus");
        Line($"events), and watch the inferred Alt-held window. {Bold}q{Reset} or {Bold}Esc{Reset} exits.");
        Line();

        return sb.ToString();
    }

    /// <summary>
    /// Walks an input-device decorator chain down to the underlying <see cref="VtInputDevice"/>,
    /// or null when the chain doesn't bottom out in one (e.g. a BYO non-VT device).
    /// </summary>
    private static VtInputDevice? FindVtInputDevice(IInputDevice device)
    {
        while (true)
        {
            switch (device)
            {
                case VtInputDevice vt: return vt;
                case IInputDeviceDecorator decorator: device = decorator.Inner; break;
                default: return null;
            }
        }
    }

    private static string DescribeKey(KeyEvent k) =>
        k is { Key: Key.Character, Text.Length: > 0 }
            ? $"\"{DemoSupport.Escape(new string(k.Text.Span))}\""
            : k.Key.ToString();

    // q / Esc (chordless, Down) or Ctrl+C exits. Shift is tolerated on 'q'; Alt-modified Esc /
    // Alt+Q are chords under observation, not exit requests.
    private static bool IsExitSignal(InputEvent inputEvent) =>
        DemoSupport.IsStopSignal(inputEvent) ||
        inputEvent is KeyEvent { Key: Key.Escape, Modifiers: KeyModifiers.None } and not KeyEvent { Kind: KeyEventKind.Up } ||
        (inputEvent is KeyEvent { Key: Key.Character, Text.Length: > 0 } k &&
         k.Kind != KeyEventKind.Up &&
         (k.Modifiers & ~KeyModifiers.Shift) == KeyModifiers.None &&
         (k.Text.Span[0] == 'q' || k.Text.Span[0] == 'Q'));

    private static async Task WriteAsync(TerminalSession session, string text, CancellationToken ct)
    {
        await session.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await session.Output.Writer.FlushAsync(ct);
    }
}
