using System.Buffers;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Reassembles a terminal's ESC-introduced VT <b>response</b> (a cursor-position report, DA1/DA2,
/// an OSC colour reply, …) from the per-character <c>KEY_EVENT</c> records conhost / Windows Terminal
/// deliver it as, on the native-console input path.
/// </summary>
/// <remarks>
/// <para>
/// On the native-console families (<see cref="TerminalFamily.WindowsTerminal"/> /
/// <see cref="TerminalFamily.WindowsConsoleHost"/>), <see cref="WindowsConsoleInputByteSource"/> reads
/// input as <c>INPUT_RECORD</c>s and wraps every key in a Win32 Input Mode envelope
/// (<c>CSI Vk;Sc;Uc;Kd;Cs;Rc _</c>) for lossless keyboard fidelity. But a terminal's reply to a query
/// like DSR-CPR (<c>ESC[6n</c> → <c>ESC[row;colR</c>) is injected into the input queue as a run of
/// <b>vk=0</b> records — one per character — so enveloping each destroys the sequence: <c>[ 3 ; 1 R</c>
/// surface as literal <see cref="Input.Events.KeyEvent"/> text and the CPR the inline host is waiting
/// on never arrives (the origin never resolves and the raw <c>[3;1R</c> leaks into the app).
/// </para>
/// <para>
/// This reassembler is the surgical remedy: while a vk=0 ASCII run forms an <b>ESC-introduced</b> VT
/// sequence, its bytes are emitted <i>raw</i> so the downstream <see cref="Input.Parsing.VtSequenceClassifier"/>
/// frames the real <c>ESC[…R</c> and the interpreter routes it to
/// <see cref="Input.Events.DeviceResponseKind.CursorPositionReport"/>. A vk=0 record that is <b>not</b> part of an
/// ESC-introduced sequence (a genuine standalone character) is left to the envelope path untouched — real
/// keystrokes carry a non-zero virtual-key code and never reach this reassembler at all, so the lossless
/// keyboard path is unaffected. Only the terminal's query replies are diverted.
/// </para>
/// <para>
/// It knows just enough VT framing to find each sequence's terminator (it does not interpret the
/// payload): CSI ends at a final byte <c>0x40–0x7E</c>; SS3 is a single following byte; string sequences
/// (OSC / DCS / SOS / PM / APC) end at BEL or the ST <c>ESC \</c>; a bare <c>ESC x</c> ends after one byte.
/// The framing lives here rather than in the classifier because it must run <i>before</i> the enveloping
/// decision — the classifier only ever sees what this reassembler chose to pass through raw.
/// </para>
/// </remarks>
internal sealed class Win32VtResponseReassembler
{
    private enum State
    {
        /// <summary>Not inside a sequence — only an ESC starts one.</summary>
        Idle,
        /// <summary>Just consumed ESC; the next byte selects the sequence kind.</summary>
        AfterEsc,
        /// <summary>Inside a CSI (<c>ESC [ …</c>); ends at a final byte 0x40–0x7E.</summary>
        Csi,
        /// <summary>Inside SS3 (<c>ESC O</c>); exactly one more byte.</summary>
        Ss3,
        /// <summary>Inside a string sequence (OSC / DCS / SOS / PM / APC); ends at BEL or ST.</summary>
        String,
        /// <summary>Saw ESC inside a string sequence; a following <c>\</c> is the ST terminator.</summary>
        StringEsc,
    }

    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;
    private const byte Backslash = 0x5C;

    private State _state;

    /// <summary>True when a VT response is mid-reassembly (bytes are being diverted raw).</summary>
    public bool InSequence => _state != State.Idle;

    /// <summary>
    /// Offer one ASCII byte carried by a vk=0 key-down record. Returns <see langword="true"/> when the byte
    /// was consumed as part of an ESC-introduced VT response and written raw to <paramref name="output"/>;
    /// returns <see langword="false"/> when the byte does not begin (and is not inside) such a sequence, in
    /// which case the caller emits it through its normal path (the Win32 Input Mode envelope).
    /// </summary>
    public bool TryConsume(byte b, IBufferWriter<byte> output)
    {
        switch (_state)
        {
            case State.Idle:
                if (b != Esc)
                    return false; // a genuine standalone character — not a response; let it envelope
                Emit(b, output);
                _state = State.AfterEsc;
                return true;

            case State.AfterEsc:
                Emit(b, output);
                _state = b switch
                {
                    0x5B => State.Csi,                                        // '['  CSI
                    0x4F => State.Ss3,                                        // 'O'  SS3
                    0x5D or 0x50 or 0x58 or 0x5E or 0x5F => State.String,     // ] P X ^ _  OSC/DCS/SOS/PM/APC
                    _ => State.Idle,                                          // bare ESC x — complete
                };
                return true;

            case State.Csi:
                Emit(b, output);
                if (b is >= 0x40 and <= 0x7E)
                    _state = State.Idle; // CSI final byte
                return true;

            case State.Ss3:
                Emit(b, output);
                _state = State.Idle; // SS3 is ESC O then one byte
                return true;

            case State.String:
                Emit(b, output);
                if (b == Bel)
                    _state = State.Idle;
                else if (b == Esc)
                    _state = State.StringEsc;
                return true;

            case State.StringEsc:
                Emit(b, output);
                _state = b == Backslash ? State.Idle : State.String; // ST = ESC '\'
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Abandon any partial sequence. Called when a non-vk0-ASCII event intervenes (a genuine key, a mouse
    /// or focus event) — a terminal injects a reply atomically, so an interruption means the run was not a
    /// response after all; dropping the partial state avoids a stuck latch swallowing later input.
    /// </summary>
    public void Reset() => _state = State.Idle;

    private static void Emit(byte b, IBufferWriter<byte> output)
    {
        var dst = output.GetSpan(1);
        dst[0] = b;
        output.Advance(1);
    }
}
