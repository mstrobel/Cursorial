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
/// <b>v1 decoder coverage:</b> printable UTF-8 runs (one <see cref="KeyEvent"/> per
/// <see cref="System.Text.Rune"/>), C0 control characters (Tab, Enter, Backspace, NUL→Ctrl+Space,
/// Ctrl+letter for 0x01–0x1A), DEL→Backspace, bare-ESC committed by classifier flush,
/// focus events (<c>CSI I</c> / <c>CSI O</c>), and bracketed-paste accumulation
/// (<c>CSI 200~</c> … <c>CSI 201~</c>).
/// </para>
/// <para>
/// <b>Not yet decoded</b> (silently dropped, will be added in subsequent passes):
/// CSI cursor / function keys, modifier-bearing key encodings (modifyOtherKeys, Kitty
/// keyboard), SGR / X10 mouse, ESC charset designators, OSC color responses, DCS
/// XTVERSION responses, Win32 input mode.
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

        // Other ESC sequences (charset designators, single-shifts, etc.) — not yet decoded.
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

        // Focus events: empty params, final 'I' / 'O'.
        if (parameters.IsEmpty)
        {
            switch (final)
            {
                case VtInputSequences.FocusInFinal:
                    _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = true });
                    return;
                case VtInputSequences.FocusOutFinal:
                    _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = false });
                    return;
            }
        }

        // Bracketed paste start / end: CSI 200 ~ / CSI 201 ~.
        if (final == (byte)'~' && TryParseFirstParam(parameters, out int param))
        {
            switch (param)
            {
                case VtInputSequences.BracketedPasteStartParam:
                    EnterPaste();
                    return;
                case VtInputSequences.BracketedPasteEndParam:
                    ExitPaste();
                    return;
            }
        }

        // Other CSI sequences (cursor keys, F-keys, mouse, etc.) — not yet decoded.
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

    private void EmitNamedKey(Key key)
    {
        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = key,
            Modifiers = KeyModifiers.None,
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
    /// Parse the first parameter (digits up to the first <c>;</c> or <c>:</c>) from a CSI
    /// parameter byte run. Returns false on empty or non-digit input.
    /// </summary>
    private static bool TryParseFirstParam(ReadOnlySpan<byte> parameters, out int value)
    {
        value = 0;
        if (parameters.IsEmpty) return false;

        bool any = false;
        foreach (byte b in parameters)
        {
            if (b is (byte)';' or (byte)':') break;
            if (b is < (byte)'0' or > (byte)'9') return false;

            value = checked(value * 10 + (b - (byte)'0'));
            any = true;
        }

        return any;
    }
}
