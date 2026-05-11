using System.Buffers;
using System.Text;

namespace GlowTerm.Core.Input.Parsing;

/// <summary>
/// Consumes <see cref="IVtSequenceTokenSink"/> callbacks from <see cref="VtSequenceClassifier"/>
/// and emits <see cref="InputEvent"/>s to an <see cref="IInputEventSink"/>. Holds the mutable
/// session state — paste accumulator, UTF-8 continuation buffer, and a reference to the
/// <see cref="VtInputMode"/> the negotiator updates as opt-ins are pushed and popped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoder coverage:</b> printable UTF-8 runs (one <see cref="KeyEvent"/> per
/// <see cref="System.Text.Rune"/>), C0 control characters (Tab, Enter, Backspace, NUL→Ctrl+Space,
/// Ctrl+letter for 0x01–0x1A), DEL→Backspace, bare-ESC committed by classifier flush,
/// focus events (<c>CSI I</c> / <c>CSI O</c>), bracketed-paste accumulation
/// (<c>CSI 200~</c> … <c>CSI 201~</c>), CSI cursor keys (<c>A B C D H F</c>) and special
/// keys (Insert, Delete, Page Up/Down, Home, End), function keys F1–F20 via the
/// <c>CSI n ~</c> form, F1–F4 + cursor + Home / End via SS3 (<c>ESC O …</c>), BackTab
/// (<c>CSI Z</c> → Shift+Tab), and xterm modifier-bearing variants (<c>CSI 1 ; mod letter</c>
/// and <c>CSI n ; mod ~</c>) decoding Shift / Alt / Ctrl / Super.
/// </para>
/// <para>
/// <b>Not yet decoded</b> (silently dropped, will be added in subsequent passes): SGR / X10
/// mouse, modifyOtherKeys character-key reporting, Kitty keyboard protocol (with
/// disambiguated up/down events and alternate keys), ESC charset designators, OSC color
/// responses, DCS XTVERSION responses, Win32 input mode.
/// </para>
/// <para>
/// <b>Threading.</b> The interpreter is single-threaded with respect to its sink and mode —
/// the same thread that drives <see cref="VtSequenceClassifier.Process"/> calls the sink
/// callbacks here, and the consumer is expected to drive both from a single byte-pump.
/// </para>
/// </remarks>
public sealed class VtInputInterpreter : IVtSequenceTokenSink
{
    private readonly IInputEventSink _eventSink;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;

    // Paste accumulator. Empty when not in paste mode.
    private readonly StringBuilder _pasteBuffer = new();
    private bool _inPaste;

    // UTF-8 continuation buffer for printable runs split across Process boundaries.
    // Sized to the maximum UTF-8 sequence length (4 bytes); never holds a complete rune.
    private readonly byte[] _utf8Continuation = new byte[4];
    private int _utf8ContinuationLength;

    public VtInputInterpreter(VtInputMode mode, IInputEventSink eventSink, TimeProvider? timeProvider = null)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The mode bag the interpreter consults; shared with the negotiator.</summary>
    public VtInputMode Mode => _mode;

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---- IVtSequenceTokenSink ----

    public void OnPrint(ReadOnlySpan<byte> bytes)
    {
        if (_inPaste)
        {
            AppendPasteText(bytes);
            return;
        }

        DecodePrintable(bytes);
    }

    public void OnExecute(byte controlChar)
    {
        if (_inPaste)
        {
            // Whitespace controls are preserved verbatim in the paste payload; everything
            // else is dropped. Keeping CR + LF distinct lets consumers detect line endings.
            switch (controlChar)
            {
                case VtInputSequences.Tab: _pasteBuffer.Append('\t'); break;
                case VtInputSequences.LineFeed: _pasteBuffer.Append('\n'); break;
                case VtInputSequences.CarriageReturn: _pasteBuffer.Append('\r'); break;
            }
            return;
        }

        EmitControlEvent(controlChar);
    }

    public void OnEscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        // Bare ESC committed by classifier flush.
        if (intermediates.IsEmpty && final == 0)
        {
            EmitNamedKey(Key.Escape);
            return;
        }

        // SS3 — application-mode arrow / F1-F4 / Home / End: ESC O <final>.
        if (intermediates.Length == 1 && intermediates[0] == (byte)'O')
        {
            DecodeSs3(final);
            return;
        }

        // Other ESC sequences (charset designators, etc.) — not yet decoded.
    }

    public void OnCsiDispatch(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final)
    {
        // Only decode the no-prefix / no-intermediate subset in v1.
        if (privatePrefix != 0 || !intermediates.IsEmpty)
        {
            return;
        }

        Span<int> parameterBuffer = stackalloc int[8];
        int parameterCount = ParseParameters(parameters, parameterBuffer);
        ReadOnlySpan<int> p = parameterBuffer[..parameterCount];

        if (p.IsEmpty)
        {
            DecodeCsiNoParams(final);
            return;
        }

        if (p.Length == 1 && final == (byte)'~')
        {
            DecodeCsiTildeOneParam(p[0]);
            return;
        }

        // Modifier-bearing arrows / Home / End: CSI 1 ; <mod> <A|B|C|D|H|F>.
        if (p.Length == 2 && p[0] == 1)
        {
            Key arrowKey = ArrowOrHomeEndKey(final);
            if (arrowKey != Key.None)
            {
                EmitNamedKey(arrowKey, ParseModifiersParam(p[1]));
                return;
            }
        }

        // Modifier-bearing function / special keys: CSI <n> ; <mod> ~.
        if (p.Length == 2 && final == (byte)'~')
        {
            if (TryFunctionOrSpecialKey(p[0], out Key funcKey))
            {
                EmitNamedKey(funcKey, ParseModifiersParam(p[1]));
            }
            return;
        }

        // Other CSI sequences (mouse, modifyOtherKeys, Kitty keyboard) — not yet decoded.
    }

    private void DecodeCsiNoParams(byte final)
    {
        switch (final)
        {
            case VtInputSequences.FocusInFinal:
                _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = true });
                return;
            case VtInputSequences.FocusOutFinal:
                _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = false });
                return;
            case (byte)'Z':
                // BackTab — Shift+Tab.
                EmitNamedKey(Key.Tab, KeyModifiers.Shift);
                return;
        }

        Key key = ArrowOrHomeEndKey(final);
        if (key != Key.None) EmitNamedKey(key);
    }

    private void DecodeCsiTildeOneParam(int parameter)
    {
        switch (parameter)
        {
            case VtInputSequences.BracketedPasteStartParam:
                EnterPaste();
                return;
            case VtInputSequences.BracketedPasteEndParam:
                ExitPaste();
                return;
        }

        if (TryFunctionOrSpecialKey(parameter, out Key key))
        {
            EmitNamedKey(key);
        }
    }

    private void DecodeSs3(byte final)
    {
        Key key = final switch
        {
            (byte)'A' => Key.UpArrow,
            (byte)'B' => Key.DownArrow,
            (byte)'C' => Key.RightArrow,
            (byte)'D' => Key.LeftArrow,
            (byte)'H' => Key.Home,
            (byte)'F' => Key.End,
            (byte)'P' => Key.F1,
            (byte)'Q' => Key.F2,
            (byte)'R' => Key.F3,
            (byte)'S' => Key.F4,
            _ => Key.None,
        };

        if (key != Key.None) EmitNamedKey(key);
    }

    private static Key ArrowOrHomeEndKey(byte final) => final switch
    {
        (byte)'A' => Key.UpArrow,
        (byte)'B' => Key.DownArrow,
        (byte)'C' => Key.RightArrow,
        (byte)'D' => Key.LeftArrow,
        (byte)'H' => Key.Home,
        (byte)'F' => Key.End,
        _ => Key.None,
    };

    private static bool TryFunctionOrSpecialKey(int parameter, out Key key)
    {
        // CSI n ~ encoding for special and function keys (xterm + vt220 / vt320 conventions).
        key = parameter switch
        {
            // Special navigation keys (alternate codes per terminal).
            1 or 7 => Key.Home,
            4 or 8 => Key.End,
            2 => Key.Insert,
            3 => Key.Delete,
            5 => Key.PageUp,
            6 => Key.PageDown,

            // Function keys F1–F12 (xterm).
            11 => Key.F1,
            12 => Key.F2,
            13 => Key.F3,
            14 => Key.F4,
            15 => Key.F5,
            17 => Key.F6,
            18 => Key.F7,
            19 => Key.F8,
            20 => Key.F9,
            21 => Key.F10,
            23 => Key.F11,
            24 => Key.F12,

            // Extended function keys F13–F20 (vt220 / vt320).
            25 => Key.F13,
            26 => Key.F14,
            28 => Key.F15,
            29 => Key.F16,
            31 => Key.F17,
            32 => Key.F18,
            33 => Key.F19,
            34 => Key.F20,

            _ => Key.None,
        };
        return key != Key.None;
    }

    private static KeyModifiers ParseModifiersParam(int parameter)
    {
        // xterm encoding: parameter = 1 + bitfield (Shift=1, Alt=2, Ctrl=4, Meta=8).
        if (parameter < 1) return KeyModifiers.None;
        int bits = parameter - 1;

        KeyModifiers modifiers = KeyModifiers.None;
        if ((bits & 0b0001) != 0) modifiers |= KeyModifiers.Shift;
        if ((bits & 0b0010) != 0) modifiers |= KeyModifiers.Alt;
        if ((bits & 0b0100) != 0) modifiers |= KeyModifiers.Control;
        if ((bits & 0b1000) != 0) modifiers |= KeyModifiers.Super;
        return modifiers;
    }

    public void OnOscDispatch(ReadOnlySpan<byte> body)
    {
        // OSC bodies (color queries, hyperlink anchors, etc.) — not yet decoded.
    }

    public void OnDcsHook(byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        // DCS responses (XTVERSION, terminfo) — not yet decoded.
    }

    public void OnDcsPut(ReadOnlySpan<byte> bytes)
    {
        // No DCS decoder active.
    }

    public void OnDcsUnhook()
    {
        // No DCS decoder active.
    }

    // ---- Print / UTF-8 decode ----

    private void DecodePrintable(ReadOnlySpan<byte> bytes)
    {
        var ts = Now;

        // Combine any pending continuation bytes with this chunk and decode runes.
        Span<byte> combined = stackalloc byte[_utf8ContinuationLength + bytes.Length];
        _utf8Continuation.AsSpan(0, _utf8ContinuationLength).CopyTo(combined);
        bytes.CopyTo(combined[_utf8ContinuationLength..]);
        _utf8ContinuationLength = 0;

        ReadOnlySpan<byte> remaining = combined;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                // Save trailing bytes for next call.
                remaining.CopyTo(_utf8Continuation);
                _utf8ContinuationLength = remaining.Length;
                return;
            }

            // OperationStatus.Done or InvalidData: emit (substitution rune for invalid)
            // and advance.
            EmitPrintableRune(rune, ts);
            remaining = remaining[consumed..];
        }
    }

    private void EmitPrintableRune(Rune rune, DateTimeOffset timestamp)
    {
        Span<char> charBuf = stackalloc char[2];
        int written = rune.EncodeToUtf16(charBuf);
        var text = new char[written];
        charBuf[..written].CopyTo(text);

        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = timestamp,
            Key = Key.Character,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = text,
        });
    }

    // ---- Control character → KeyEvent ----

    private void EmitControlEvent(byte controlChar)
    {
        var ts = Now;
        KeyEvent? evt = controlChar switch
        {
            VtInputSequences.Tab => Named(Key.Tab),
            VtInputSequences.CarriageReturn or VtInputSequences.LineFeed => Named(Key.Enter),
            VtInputSequences.Backspace or VtInputSequences.Delete => Named(Key.Backspace),
            VtInputSequences.Nul => CtrlSpace(),
            >= 0x01 and <= 0x1A => CtrlLetter(controlChar),
            _ => null, // Other low-range controls (0x1C-0x1F, BEL, etc.) — ignored in v1.
        };

        if (evt is not null) _eventSink.OnInputEvent(evt);

        KeyEvent Named(Key key) => new()
        {
            Timestamp = ts,
            Key = key,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
        };

        KeyEvent CtrlSpace() => new()
        {
            Timestamp = ts,
            Key = Key.Space,
            Modifiers = KeyModifiers.Control,
            Kind = KeyEventKind.Down,
        };

        KeyEvent CtrlLetter(byte b)
        {
            // 0x01-0x1A → 'a'-'z' (Ctrl strips bits 5-6).
            char letter = (char)(b + 0x60);
            return new KeyEvent
            {
                Timestamp = ts,
                Key = Key.Character,
                Modifiers = KeyModifiers.Control,
                Kind = KeyEventKind.Down,
                Text = new[] { letter },
            };
        }
    }

    private void EmitNamedKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = key,
            Modifiers = modifiers,
            Kind = KeyEventKind.Down,
        });
    }

    // ---- Bracketed paste ----

    private void EnterPaste()
    {
        _inPaste = true;
        _pasteBuffer.Clear();
    }

    private void ExitPaste()
    {
        if (!_inPaste) return;

        var text = _pasteBuffer.ToString();
        _pasteBuffer.Clear();
        _inPaste = false;

        _eventSink.OnInputEvent(new PasteEvent
        {
            Timestamp = Now,
            Text = text.AsMemory(),
        });
    }

    private void AppendPasteText(ReadOnlySpan<byte> bytes)
    {
        // Worst case: every byte is a 1-byte ASCII char.
        Span<char> chars = stackalloc char[bytes.Length];
        int written = Encoding.UTF8.GetChars(bytes, chars);
        _pasteBuffer.Append(chars[..written]);
    }

    // ---- Parameter parsing helpers ----

    /// <summary>
    /// Parse a CSI parameter byte run into integer values, splitting on <c>;</c>. For v1, the
    /// sub-parameter separator <c>:</c> is treated identically to <c>;</c> — protocols that
    /// use sub-parameters meaningfully (Kitty keyboard, SGR colon-form colors) will get a
    /// dedicated parser when those decoders land.
    /// </summary>
    /// <returns>The number of parameters written to <paramref name="output"/>.</returns>
    private static int ParseParameters(ReadOnlySpan<byte> raw, Span<int> output)
    {
        if (raw.IsEmpty || output.IsEmpty) return 0;

        int count = 0;
        int current = 0;
        bool started = false;

        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                current = current * 10 + (b - (byte)'0');
                started = true;
            }
            else if (b is (byte)';' or (byte)':')
            {
                if (count < output.Length)
                {
                    output[count++] = started ? current : 0;
                }
                current = 0;
                started = false;
            }
            // Other bytes are ignored — the classifier already filtered to valid CSI.
        }

        // Final parameter (CSI sequences end the param run before the final byte).
        if (count < output.Length)
        {
            output[count++] = started ? current : 0;
        }

        return count;
    }
}
