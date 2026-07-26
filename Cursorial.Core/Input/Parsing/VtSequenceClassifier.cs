using System.Diagnostics;

namespace Cursorial.Input.Parsing;

/// <summary>
/// Stateful byte-level classifier for VT/ANSI input sequences. Consumes bytes (which may
/// arrive in arbitrary chunks across <see cref="Process"/> calls) and dispatches framed
/// sequences to an <see cref="IVtSequenceTokenSink"/>. The classifier is purely a framing
/// layer — it does not interpret the meaning of any sequence.
/// </summary>
/// <remarks>
/// <para>
/// Implements a small subset of the Williams VT500 state machine: ground, escape,
/// escape-intermediate, CSI (entry / param / intermediate / ignore), OSC string, and DCS
/// (entry / param / intermediate / passthrough / ignore). APC, SOS, PM, and the 8-bit C1
/// control set are intentionally out of scope — they do not occur in modern terminal input.
/// </para>
/// <para>
/// <b>ESC ambiguity.</b> A bare ESC byte is held pending in the <c>Escape</c> state — it could
/// be an Escape keypress or the introducer of CSI/SS3/Alt+key. The classifier does not own
/// the timeout; the device above is expected to call <see cref="Flush"/> after its
/// platform-appropriate quiet period (xterm convention is 50 ms). Flush commits any pending
/// bare-ESC as an <see cref="IVtSequenceTokenSink.OnEscDispatch"/> with empty intermediates
/// and final <c>0</c>. <c>ESC ESC</c> is the second-order case — Alt+Esc, or an Escape press
/// followed by another ESC-introduced sequence — so it parks in <c>EscapeEscape</c> and resolves
/// the same way: a following byte means the first ESC stood alone, silence means Alt+Esc.
/// </para>
/// <para>
/// <b>Alt+key (meta-sends-escape).</b> Terminals in the xterm family spell <c>Alt+&lt;key&gt;</c>
/// as ESC followed by whatever the key sends on its own, and the classifier frames all three
/// shapes it can take: <c>ESC &lt;0x30-0x7E&gt;</c> (letters, digits, most symbols) and
/// <c>ESC &lt;0x20-0x2F&gt;</c> (Alt+Space, Alt+/, Alt+-, … recovered from the parked
/// <c>EscapeIntermediate</c> state) both dispatch through
/// <see cref="IVtSequenceTokenSink.OnEscDispatch"/> with empty intermediates, while
/// <c>ESC &lt;C0|DEL&gt;</c> (Alt+Enter, Alt+Tab, Alt+Backspace, Alt+Esc, Alt+Ctrl+letter)
/// dispatches through <see cref="IVtSequenceTokenSink.OnAltExecute"/>. Not covered:
/// <c>ESC &lt;multi-byte UTF-8&gt;</c> (Alt+é) and <c>ESC ESC &lt;sequence&gt;</c> (Alt+Up on
/// terminals that prefix the whole cursor-key sequence) — both need state the framing layer
/// doesn't carry.
/// </para>
/// <para>
/// <b>Buffers.</b> CSI/DCS parameter and intermediate bytes plus OSC bodies are accumulated
/// in fixed-size buffers. When a buffer overflows the sequence enters an "ignore" state and
/// is discarded on completion. Defaults are sized for realistic inputs (256 byte parameters,
/// 16 intermediates, 4096 byte OSC bodies); large DCS bodies are streamed via
/// <see cref="IVtSequenceTokenSink.OnDcsPut"/> rather than buffered.
/// </para>
/// <para>
/// <b>API stability.</b> Types in the <c>Cursorial.Input.Parsing</c> namespace are public
/// so advanced consumers can compose alternative pipelines (recording / replay, fuzz harnesses,
/// custom input devices). They are NOT covered by Cursorial's outer-API stability guarantees —
/// expect the classifier, interpreter, and sink interfaces to evolve as new protocols land.
/// Consumers wanting a stable surface should build against <c>VtInputDevice</c> /
/// <c>TerminalSession</c> instead.
/// </para>
/// <para>
/// <b>X10 mouse framing.</b> The X10 mouse protocol (<c>ESC [ M cb cx cy</c>) breaks the rule
/// that the classifier is purely byte-pattern driven: the three bytes after <c>M</c> are not
/// otherwise distinguishable from printable text, so the classifier must know whether to expect
/// them. <see cref="X10MouseFramingEnabled"/> is the narrow exception — when set, an unadorned
/// <c>CSI M</c> (no private prefix, no parameters, no intermediates) consumes three additional
/// bytes and dispatches them via <see cref="IVtSequenceTokenSink.OnX10MouseDispatch"/>; when
/// unset the classifier remains fully stateless w.r.t. protocol mode.
/// </para>
/// </remarks>
public sealed class VtSequenceClassifier
{
    private const int MaxParameterBytes = 256;
    private const int MaxIntermediateBytes = 16;
    private const int MaxOscBodyBytes = 4096;

    private readonly byte[] _parameterBuffer = new byte[MaxParameterBytes];
    private readonly byte[] _intermediateBuffer = new byte[MaxIntermediateBytes];
    private readonly byte[] _oscBuffer = new byte[MaxOscBodyBytes];

    private State _state = State.Ground;
    private int _parameterLength;
    private int _intermediateLength;
    private int _oscLength;
    private byte _privatePrefix;
    private byte _x10MouseCb;
    private byte _x10MouseCx;

    /// <summary>
    /// When true, an unadorned <c>CSI M</c> (no private prefix, no parameters, no intermediates)
    /// is treated as the introducer for an X10 mouse report and the next three bytes are
    /// consumed and delivered via <see cref="IVtSequenceTokenSink.OnX10MouseDispatch"/>. The
    /// negotiator (or transport setup) is expected to keep this flag in sync with
    /// <see cref="VtInputMode.MouseEncoding"/>; the classifier does not read mode state itself.
    /// </summary>
    /// <remarks>
    /// <see cref="VtInputDevice"/> mirrors <see cref="VtInputMode.MouseEncoding"/> ==
    /// <see cref="MouseEncoding.X10"/> onto this flag on each pump iteration, so callers who
    /// only manipulate the mode bag get correct framing automatically. Tests and standalone
    /// consumers may also set this directly. The negotiator does not push X10 today
    /// (<see cref="MouseEncoding.X10"/> is reachable only by direct application opt-in).
    /// </remarks>
    public bool X10MouseFramingEnabled { get; set; }

    /// <summary>
    /// When true, legacy 8-bit meta input is decoded: a ground-state byte with the high bit set
    /// (<c>0xA0–0xFF</c>) is treated as <c>Alt+&lt;key&gt;</c> — exactly the equivalent of
    /// <c>ESC (b &amp; 0x7F)</c> in the modern meta-sends-escape mode — and never fed to the printable/UTF-8
    /// path. This mode is <b>mutually exclusive with UTF-8 input</b> (a high byte is otherwise a UTF-8
    /// lead/continuation), so it is OFF by default and only an explicit opt-in should enable it on a terminal
    /// known to be in 8-bit (non-UTF-8) mode. Like <see cref="X10MouseFramingEnabled"/>, this is a mode the
    /// classifier cannot infer from bytes alone; <see cref="VtInputDevice"/> mirrors
    /// <see cref="VtInputMode.EightBitMetaInput"/> onto it each pump iteration.
    /// </summary>
    /// <remarks>
    /// Known limitation: the bare-introducer recovery (Alt+[ / Alt+] / Alt+P / Alt+O) only engages here for
    /// the single-key-then-idle path. A sub-50&#xa0;ms <em>back-to-back</em> 8-bit-meta introducer burst
    /// (e.g. <c>0xDD 0xDB</c> = Alt+] then Alt+[) is not recovered: the second high byte is not <c>ESC</c>, so
    /// the parked state's ESC-interrupt leg never fires and the byte is consumed as sequence body. The 7-bit
    /// (<c>ESC&#xa0;]</c>) form has no such gap. This is acceptable because 8-bit meta is an off-by-default
    /// legacy mode the negotiator never auto-enables.
    /// </remarks>
    public bool EightBitMetaEnabled { get; set; }

    /// <summary>
    /// Feed a chunk of bytes through the classifier. Tokens are dispatched to
    /// <paramref name="sink"/> synchronously as they are framed.
    /// </summary>
    public void Process(ReadOnlySpan<byte> bytes, IVtSequenceTokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        // Coalesce printable runs in Ground state to a single OnPrint call.
        int printRunStart = -1;

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];

            if (_state == State.Ground)
            {
                // In 8-bit-meta mode a high byte is an Alt keypress, not printable/UTF-8 — exclude it from the
                // print run so it falls through to StepGround. (When the mode is off, high bytes are UTF-8.)
                if (IsPrintable(b) && !(EightBitMetaEnabled && b >= VtInputSequences.EightBitMeta))
                {
                    if (printRunStart < 0) printRunStart = i;
                    continue;
                }

                FlushPrintRun(bytes, ref printRunStart, i, sink);
            }

            StepByte(b, sink);
        }

        FlushPrintRun(bytes, ref printRunStart, bytes.Length, sink);
    }

    /// <summary>
    /// <see langword="true"/> while the state machine holds a partially-received sequence — a
    /// lone ESC awaiting disambiguation, an unterminated CSI/OSC/DCS, a partial X10 mouse
    /// report. This is exactly the condition under which <see cref="Flush"/> has work to do;
    /// when <see langword="false"/> (ground state) <see cref="Flush"/> is a no-op, so a device
    /// can skip arming its idle-timeout timer entirely instead of waking on every quiet window.
    /// </summary>
    public bool HasPendingSequence => _state != State.Ground;

    /// <summary>
    /// Commit any pending state that is waiting on more input — most importantly, a lone ESC
    /// held in <see cref="State.Escape"/>. Call from the device after the bare-ESC ambiguity
    /// timeout elapses (xterm convention: 50 ms with no further input).
    /// </summary>
    public void Flush(IVtSequenceTokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        switch (_state)
        {
            case State.Escape:
                sink.OnEscDispatch(intermediates: ReadOnlySpan<byte>.Empty, final: 0);
                ResetToGround();
                break;

            case State.EscapeEscape:
                // `ESC ESC` with nothing after it: the idle window is the signal that the pair was
                // one keypress — Alt+Esc under meta-sends-escape — and not an Escape press that
                // introduced a following sequence (that case leaves this state at StepEscapeEscape,
                // before any flush can run). Two *separately typed* Escapes can't land here either:
                // the first commits on its own idle timeout long before the second arrives.
                sink.OnAltExecute(VtInputSequences.Escape);
                ResetToGround();
                break;

            case State.EscapeIntermediate:
                // `ESC <0x20-0x2F>` parked on the idle timeout — Alt+<symbol>. See
                // RecoverAltSymbolOrDrop for why a multi-intermediate buffer is dropped instead.
                RecoverAltSymbolOrDrop(sink);
                ResetToGround();
                break;

            // A lone Alt+<introducer> typed under meta-sends-escape — ESC [ (Alt+[), ESC ] (Alt+]),
            // ESC P (Alt+P), ESC O (Alt+O) — parks the machine in the matching sequence state with an empty
            // body, waiting for a continuation a single keypress never sends. The idle timeout is the signal
            // that the wait is over: recover it as the Alt+key it was. This is the symmetric completion of
            // the bare-ESC commit above and the StepCsiParam ESC-abort — without it the keystroke is swallowed
            // and (for OSC) every subsequent byte is eaten as OSC body until a stray terminator unsticks the
            // parser. OnEscDispatch(empty, introducer) is exactly the shape VtInputInterpreter maps to a clean
            // Alt+<key> (even 'O' — an empty-intermediate 'O' final bypasses its SS3 decode). The empty-body
            // guard is the safety net: a partially-arrived REAL sequence (private prefix / parameters / OSC
            // body present) is a fragmented device response — dropped, never re-read as a keypress.
            case State.CsiEntry:
            case State.CsiParam:
                RecoverBareIntroducerOrDrop((byte) '[', sink);
                break;

            case State.DcsEntry:
            case State.DcsParam:
                RecoverBareIntroducerOrDrop((byte) 'P', sink);
                break;

            case State.OscString:
                if (_oscLength == 0)
                    sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) ']'); // bare ESC ] = Alt+]
                ResetToGround(); // a non-empty body was a truncated real OSC — drop it
                break;

            case State.OscEsc:
                // ESC ] … ESC parked awaiting ST: a real OSC that began and was cut off — drop, never Alt.
                ResetToGround();
                break;

            case State.Ss3:
                sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) 'O'); // bare ESC O = Alt+O
                ResetToGround();
                break;

            case State.X10MouseCb:
            case State.X10MouseCx:
            case State.X10MouseCy:
                // A partial X10 report stuck on the idle window. The few bytes we've collected
                // are not useful on their own, but leaving the state in place would cause the
                // next legitimate bytes to be misinterpreted as the rest of this report. Drop
                // the partial and return to Ground.
                ResetToGround();
                break;

            default:
                // Backstop against stranding. Any other mid-sequence state reached when 50 ms of silence says
                // input has stopped — a truncated CSI/DCS intermediate or ignore, or (the case this closes) a
                // DCS that hooked but never received its ST and sits in passthrough — is an abandoned sequence.
                // Drop it and return to Ground rather than leaving the parser parked, silently swallowing every
                // later keystroke (the exact failure class this whole change addresses). Ground falls here too —
                // a harmless no-op. A genuine device response arrives as a sub-50 ms burst, so this never
                // truncates one.
                ResetToGround();
                break;
        }
    }

    // Recover a bare ESC-introducer state parked on the idle timeout as Alt+<introducer> — but only when
    // nothing followed the introducer; a private prefix / parameters / intermediates mean real sequence
    // bytes arrived (a fragmented device response), so the partial is dropped rather than mis-keyed. (A
    // CsiParam/DcsParam state is only reachable once a body byte arrived, so its guard always drops; it is
    // included for symmetry with the StepCsiParam abort gate.)
    private void RecoverBareIntroducerOrDrop(byte introducer, IVtSequenceTokenSink sink)
    {
        if (_privatePrefix == 0 && _parameterLength == 0 && _intermediateLength == 0)
            sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, introducer);

        ResetToGround();
    }

    /// <summary>
    /// Reset the classifier to <see cref="State.Ground"/>, discarding any in-flight sequence.
    /// Call when reconfiguring the terminal or recovering from a parse error in the consumer.
    /// </summary>
    public void Reset()
    {
        ResetToGround();
    }

    private static bool IsPrintable(byte b) => b >= 0x20 && b != 0x7F;

    private static void FlushPrintRun(
        ReadOnlySpan<byte> bytes,
        ref int printRunStart,
        int endExclusive,
        IVtSequenceTokenSink sink)
    {
        if (printRunStart < 0) return;
        sink.OnPrint(bytes[printRunStart..endExclusive]);
        printRunStart = -1;
    }

    private void StepByte(byte b, IVtSequenceTokenSink sink)
    {
        switch (_state)
        {
            case State.Ground:
                StepGround(b, sink);
                break;

            case State.Escape:
                StepEscape(b, sink);
                break;

            case State.EscapeEscape:
                StepEscapeEscape(b, sink);
                break;

            case State.EscapeIntermediate:
                StepEscapeIntermediate(b, sink);
                break;

            case State.Ss3:
                StepSs3(b, sink);
                break;

            case State.CsiEntry:
                StepCsiEntry(b, sink);
                break;

            case State.CsiParam:
                StepCsiParam(b, sink);
                break;

            case State.CsiIntermediate:
                StepCsiIntermediate(b, sink);
                break;

            case State.CsiIgnore:
                StepCsiIgnore(b);
                break;

            case State.OscString:
                StepOscString(b, sink);
                break;

            case State.OscEsc:
                StepOscEsc(b, sink);
                break;

            case State.DcsEntry:
                StepDcsEntry(b, sink);
                break;

            case State.DcsParam:
                StepDcsParam(b, sink);
                break;

            case State.DcsIntermediate:
                StepDcsIntermediate(b, sink);
                break;

            case State.DcsPassthrough:
                StepDcsPassthrough(b, sink);
                break;

            case State.DcsPassthroughEsc:
                StepDcsPassthroughEsc(b, sink);
                break;

            case State.DcsIgnore:
                StepDcsIgnore(b);
                break;

            case State.DcsIgnoreEsc:
                StepDcsIgnoreEsc(b);
                break;

            case State.X10MouseCb:
                _x10MouseCb = b;
                _state = State.X10MouseCx;
                break;

            case State.X10MouseCx:
                _x10MouseCx = b;
                _state = State.X10MouseCy;
                break;

            case State.X10MouseCy:
                sink.OnX10MouseDispatch(_x10MouseCb, _x10MouseCx, b);
                ResetToGround();
                break;
        }
    }

    // ---- Ground ----

    private void StepGround(byte b, IVtSequenceTokenSink sink)
    {
        if (b == VtInputSequences.Escape)
        {
            ResetSequenceBuffers();
            _state = State.Escape;
            return;
        }

        // Legacy 8-bit meta input (opt-in; see EightBitMetaEnabled): Alt+<key> arrives as (key | 0x80).
        // Decode it as the equivalent ESC + (b & 0x7F), reusing the meta-sends-escape path verbatim so the two
        // forms are indistinguishable downstream. Gated at >= 0xA0 so the stripped byte is a printable/DEL key,
        // never a C0 control; 0x80–0x9F (8-bit C1 controls) fall through to OnExecute below.
        if (EightBitMetaEnabled && b >= 0xA0)
        {
            ResetSequenceBuffers();
            _state = State.Escape;
            StepEscape((byte) (b & 0x7F), sink);
            return;
        }

        // C0 control or DEL.
        sink.OnExecute(b);
    }

    // ---- Escape ----

    private void StepEscape(byte b, IVtSequenceTokenSink sink)
    {
        switch (b)
        {
            case (byte) '[':
                _state = State.CsiEntry;
                return;

            case (byte) ']':
                _oscLength = 0;
                _state = State.OscString;
                return;

            case (byte) 'P':
                _state = State.DcsEntry;
                return;

            case (byte) 'O':
                // Single Shift 3: one more byte will follow as the SS3 final.
                _state = State.Ss3;
                return;

            case VtInputSequences.Escape:
                // Two ESCs back-to-back are ambiguous and CANNOT be resolved from bytes alone:
                //
                //   ESC ESC          → Alt+Esc under meta-sends-escape (the whole keypress)
                //   ESC ESC [ A      → Escape, then Alt+Up on the terminals (macOS Terminal.app
                //                      with option-as-meta, rxvt) that spell Alt+<key> as ESC +
                //                      the key's ordinary sequence
                //
                // So park instead of deciding: State.EscapeEscape holds the pair pending. If the
                // burst ends there, Flush commits Alt+Esc; if a third byte arrives, the first ESC
                // was a standalone Escape press and StepEscapeEscape replays the byte through the
                // Escape state — byte-for-byte the behavior this state machine had before Alt+Esc
                // decoding existed. Bug this closes: the old immediate commit made Alt+Esc
                // undeliverable (it always decoded as two Escape keypresses, unwinding two focus
                // scopes), even though the Kitty (CSI 27;3u) and Win32 wires both report it.
                _state = State.EscapeEscape;
                return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            // Intermediate.
            AppendIntermediate(b);
            _state = State.EscapeIntermediate;
            return;
        }

        if (b is >= 0x30 and <= 0x7E)
        {
            // Final byte of an ESC sequence with no intermediates (e.g., ESC 7 = DECSC).
            sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, b);
            ResetToGround();
            return;
        }

        // ───────────────────── ESC + C0/DEL = Alt+<control key> ─────────────────────
        // Under meta-sends-escape the terminal prefixes ESC to whatever the key would otherwise
        // send, and for the control keys that "whatever" is a C0 byte: Alt+Enter is ESC CR,
        // Alt+Tab is ESC HT, Alt+Backspace is ESC BS or ESC DEL, Alt+Ctrl+<letter> is ESC + the
        // Ctrl code. Route them to OnAltExecute and return to Ground.
        //
        // BUG this fixes: these bytes used to go to OnExecute *while staying in Escape*, so
        // Alt+Enter decoded as a plain Enter (wrong action) plus a phantom Escape at the 50 ms
        // ambiguity flush (which additionally unwound a focus scope). ESC is excluded — the
        // switch above parks it — so `b < 0x20` here is a genuine control byte. Bytes >= 0x80
        // are deliberately left on the old path: they are UTF-8 continuation bytes of an
        // Alt+<non-ASCII> keypress (ESC C3 A9 = Alt+é), which needs a UTF-8 assembler the
        // classifier doesn't have.
        if (b < 0x20 || b == VtInputSequences.Delete)
        {
            sink.OnAltExecute(b);
            ResetToGround();
            return;
        }

        // 8-bit C1 / UTF-8 continuation inside Escape — execute and stay.
        sink.OnExecute(b);
    }

    private void StepEscapeIntermediate(byte b, IVtSequenceTokenSink sink)
    {
        if (b is >= 0x20 and <= 0x2F)
        {
            AppendIntermediate(b);
            return;
        }

        if (b is >= 0x30 and <= 0x7E)
        {
            sink.OnEscDispatch(IntermediateSpan, b);
            ResetToGround();
            return;
        }

        if (b == VtInputSequences.Escape)
        {
            // ESC aborts the in-flight ESC-intermediate sequence. A lone pending intermediate was
            // Alt+<symbol> typed under meta-sends-escape (e.g. Alt+/ then a second Alt key, fast
            // enough to beat the idle timer) — surface it, exactly as the Flush() leg does, so the
            // keystroke is reported whether it arrives alone-then-idle or back-to-back. Symmetric
            // with the StepCsiParam / StepOscString / StepSs3 ESC-abort recoveries.
            RecoverAltSymbolOrDrop(sink);
            _state = State.Escape;
            return;
        }

        sink.OnExecute(b);
    }

    // A third byte inside an `ESC ESC` burst: the pair was a standalone Escape keypress followed by
    // a sequence that itself opens with ESC (Alt+<key> as ESC + the key's own sequence, or simply a
    // fast Escape-then-anything). Commit the first ESC and replay the byte through Escape — the
    // pre-Alt+Esc behavior, preserved so `ESC ESC [ A`, `ESC ESC O P`, and `ESC ESC g` keep decoding
    // as they always did. Only the burst-ends-here case (Flush) becomes Alt+Esc.
    private void StepEscapeEscape(byte b, IVtSequenceTokenSink sink)
    {
        sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, 0);
        ResetSequenceBuffers();
        _state = State.Escape;
        StepEscape(b, sink);
    }

    // Recover a parked ESC-intermediate state as Alt+<symbol>, or drop it. Exactly one buffered
    // intermediate means the wire carried `ESC <0x20-0x2F>` and nothing else — Alt+Space, Alt+/,
    // Alt+-, Alt+. and friends, which meta-sends-escape spells with a byte in the ESC-intermediate
    // range. OnEscDispatch(empty, symbol) is the shape VtInputInterpreter already maps to a clean
    // Alt+<character>, so the symbol keys agree with the letter/digit keys that take the
    // 0x30-0x7E final path. Two or more intermediates is a genuine (now-truncated) multi-byte ESC
    // sequence — `ESC ( B`, `ESC SP F` — and is dropped, never re-read as a keypress. Callers own
    // the state transition; this only clears the buffers.
    //
    // BUG this fixes: these keystrokes used to be swallowed outright — StepEscape parked them in
    // EscapeIntermediate and Flush's default arm discarded the buffer, making Alt+/ unusable.
    private void RecoverAltSymbolOrDrop(IVtSequenceTokenSink sink)
    {
        if (_intermediateLength == 1)
            sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, _intermediateBuffer[0]);

        ResetSequenceBuffers();
    }

    // ---- SS3 (single shift 3) ----

    private void StepSs3(byte b, IVtSequenceTokenSink sink)
    {
        if (b is >= 0x20 and <= 0x7E)
        {
            // Dispatch as ESC dispatch with intermediate 'O' so the interpreter recognizes SS3.
            Span<byte> intermediates = stackalloc byte[1];
            intermediates[0] = (byte) 'O';
            sink.OnEscDispatch(intermediates, b);
            ResetToGround();
            return;
        }

        if (b == VtInputSequences.Escape)
        {
            // A new ESC interrupts the SS3 — the partial ESC O was Alt+O (mirroring StepCsiParam's Alt+[
            // recovery), so surface it and re-enter Escape state for the interrupting sequence. Identical to
            // the Flush() SS3 leg, so ESC O on idle and ESC O ESC <key> agree.
            sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) 'O');
            ResetSequenceBuffers();
            _state = State.Escape;
            return;
        }

        // C0 control inside SS3 — execute and stay (rare).
        if (b < 0x20) sink.OnExecute(b);
    }

    // ---- CSI ----

    private void StepCsiEntry(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (byte) '?' or (byte) '>' or (byte) '<' or (byte) '=')
        {
            _privatePrefix = b;
            _state = State.CsiParam;
            return;
        }

        StepCsiParam(b, sink);
    }

    private void StepCsiParam(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (>= (byte) '0' and <= (byte) '9') or (byte) ';' or (byte) ':')
        {
            if (!AppendParameter(b))
                _state = State.CsiIgnore;
            else
                _state = State.CsiParam;

            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            AppendIntermediate(b);
            _state = State.CsiIntermediate;
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            DispatchCsi(b, sink);
            return;
        }

        // ESC aborts the in-flight CSI (VT500: ESC re-enters Escape from anywhere). A *bare* `ESC [` — nothing
        // accumulated after the introducer — was actually Alt+[ under meta-sends-escape (e.g. the user pressed
        // Alt+[ then another Alt key), so surface it; a CSI that already carried a private prefix / parameters /
        // intermediates was a real (now-aborted) sequence and is discarded. Either way the buffers are reset so
        // the aborted sequence cannot leak its parameters into the next one (e.g. a following plain Up-arrow).
        if (b == VtInputSequences.Escape)
        {
            if (_privatePrefix == 0 && _parameterLength == 0 && _intermediateLength == 0)
                sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) '[');

            ResetSequenceBuffers();
            _state = State.Escape;
            return;
        }

        // C0 control mid-sequence — execute but stay.
        if (b < 0x20) sink.OnExecute(b);
    }

    private void StepCsiIntermediate(byte b, IVtSequenceTokenSink sink)
    {
        if (b is >= 0x20 and <= 0x2F)
        {
            if (!AppendIntermediate(b))
                _state = State.CsiIgnore;

            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            DispatchCsi(b, sink);
            return;
        }

        if (b < 0x20) sink.OnExecute(b);
    }

    private void StepCsiIgnore(byte b)
    {
        if (b is >= 0x40 and <= 0x7E)
            ResetToGround();
    }

    private void DispatchCsi(byte final, IVtSequenceTokenSink sink)
    {
        // X10 mouse exception — an unadorned `CSI M` introduces three raw bytes when framing
        // is enabled. See class remarks for why this lives in the classifier.
        if (final == (byte) 'M' &&
            X10MouseFramingEnabled &&
            _privatePrefix == 0 &&
            _parameterLength == 0 &&
            _intermediateLength == 0)
        {
            _state = State.X10MouseCb;
            return;
        }

        sink.OnCsiDispatch(_privatePrefix, ParameterSpan, IntermediateSpan, final);
        ResetToGround();
    }

    // ---- OSC ----

    private void StepOscString(byte b, IVtSequenceTokenSink sink)
    {
        if (b == VtInputSequences.Bel)
        {
            // A bare `ESC ]` terminated by BEL was Alt+] then Ctrl+G (BEL = 0x07), not an empty OSC — no
            // terminal emits a body-less OSC. Surface the Alt+] (matching the ESC-interrupt and idle-Flush
            // empty-body legs, so all three OSC exits agree) and let the BEL through as its own control. A
            // non-empty body is a genuine OSC and dispatches normally.
            if (_oscLength == 0)
            {
                sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) ']');
                ResetToGround();
                sink.OnExecute(b); // the BEL is Ctrl+G in its own right
                return;
            }

            sink.OnOscDispatch(OscBodySpan);
            ResetToGround();
            return;
        }

        if (b == VtInputSequences.Escape)
        {
            // A bare `ESC ]` (no OSC body yet) interrupted by another ESC was Alt+] typed under
            // meta-sends-escape — e.g. Alt+] then a second Alt key, pressed fast enough to beat the idle
            // timer. Surface it and re-enter Escape for the interrupting sequence: the symmetric twin of
            // StepCsiParam's Alt+[ abort and the Flush() OscString recovery, so Alt+] is reported whether it
            // arrives alone-then-idle or back-to-back with another key. A real OSC always carries a command
            // byte right after `]`, so an empty body here is never a genuine sequence; only with a body
            // present is this ESC the lead of an ST (`ESC \`) terminator.
            if (_oscLength == 0)
            {
                sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) ']');
                ResetSequenceBuffers();
                _state = State.Escape;
                return;
            }

            _state = State.OscEsc;
            return;
        }

        AppendOsc(b);
    }

    private void StepOscEsc(byte b, IVtSequenceTokenSink sink)
    {
        if (b == (byte) '\\')
        {
            // ESC \ = ST: terminate OSC.
            sink.OnOscDispatch(OscBodySpan);
            ResetToGround();
            return;
        }

        // Not ST after ESC inside OSC — abort the OSC and re-process from Escape state.
        ResetSequenceBuffers();
        _state = State.Escape;
        StepEscape(b, sink);
    }

    // ---- DCS ----

    private void StepDcsEntry(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (byte) '?' or (byte) '>' or (byte) '<' or (byte) '=')
        {
            _privatePrefix = b;
            _state = State.DcsParam;
            return;
        }

        StepDcsParam(b, sink);
    }

    private void StepDcsParam(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (>= (byte) '0' and <= (byte) '9') or (byte) ';' or (byte) ':')
        {
            if (!AppendParameter(b))
                _state = State.DcsIgnore;
            else
                _state = State.DcsParam;

            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            AppendIntermediate(b);
            _state = State.DcsIntermediate;
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            HookDcs(b, sink);
            return;
        }

        // ESC aborts the in-flight DCS (VT500: ESC re-enters Escape from anywhere). A *bare* `ESC P` —
        // nothing accumulated after the introducer — was Alt+P under meta-sends-escape, so surface it; a DCS
        // that already carried a prefix / parameters / intermediates is a real (now-truncated) sequence and is
        // discarded. Either way re-enter Escape so the interrupting sequence parses cleanly. A real DCS never
        // carries an ESC in its parameter phase — its only ESC is the ST terminator, reached from
        // DcsPassthrough — so this branch is unambiguous. Symmetric with StepCsiParam / StepOscString.
        if (b == VtInputSequences.Escape)
        {
            if (_privatePrefix == 0 && _parameterLength == 0 && _intermediateLength == 0)
                sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, (byte) 'P');

            ResetSequenceBuffers();
            _state = State.Escape;
            return;
        }

        // Anything else aborts the sequence.
        _state = State.DcsIgnore;
    }

    private void StepDcsIntermediate(byte b, IVtSequenceTokenSink sink)
    {
        if (b is >= 0x20 and <= 0x2F)
        {
            if (!AppendIntermediate(b))
                _state = State.DcsIgnore;

            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            HookDcs(b, sink);
            return;
        }

        _state = State.DcsIgnore;
    }

    private void HookDcs(byte final, IVtSequenceTokenSink sink)
    {
        sink.OnDcsHook(_privatePrefix, ParameterSpan, IntermediateSpan, final);
        _state = State.DcsPassthrough;
    }

    private void StepDcsPassthrough(byte b, IVtSequenceTokenSink sink)
    {
        if (b == VtInputSequences.Escape)
        {
            _state = State.DcsPassthroughEsc;
            return;
        }

        // Forward this byte. Single-byte forwarding is a perf compromise versus coalescing —
        // worth profiling later if DCS bodies become hot.
        Span<byte> single = stackalloc byte[1];
        single[0] = b;
        sink.OnDcsPut(single);
    }

    private void StepDcsPassthroughEsc(byte b, IVtSequenceTokenSink sink)
    {
        if (b == (byte) '\\')
        {
            sink.OnDcsUnhook();
            ResetToGround();
            return;
        }

        // ESC inside body that wasn't ST — treat ESC as part of body and re-step the byte.
        Span<byte> esc = stackalloc byte[1];
        esc[0] = VtInputSequences.Escape;
        sink.OnDcsPut(esc);
        _state = State.DcsPassthrough;
        StepDcsPassthrough(b, sink);
    }

    private void StepDcsIgnore(byte b)
    {
        if (b == VtInputSequences.Escape)
            _state = State.DcsIgnoreEsc;
    }

    private void StepDcsIgnoreEsc(byte b)
    {
        if (b == (byte) '\\')
            ResetToGround();
        else
            _state = State.DcsIgnore;
    }

    // ---- Buffer management ----

    private bool AppendParameter(byte b)
    {
        if (_parameterLength >= _parameterBuffer.Length) return false;
        _parameterBuffer[_parameterLength++] = b;
        return true;
    }

    private bool AppendIntermediate(byte b)
    {
        if (_intermediateLength >= _intermediateBuffer.Length) return false;
        _intermediateBuffer[_intermediateLength++] = b;
        return true;
    }

    private void AppendOsc(byte b)
    {
        if (_oscLength >= _oscBuffer.Length)
        {
            // Drop overflow bytes silently; the OSC will still dispatch with the prefix at
            // termination. This is preferable to discarding the entire OSC since the leading
            // bytes (e.g., OSC parameter prefix) are usually what the consumer needs.
            return;
        }

        _oscBuffer[_oscLength++] = b;
    }

    private ReadOnlySpan<byte> ParameterSpan => _parameterBuffer.AsSpan(0, _parameterLength);
    private ReadOnlySpan<byte> IntermediateSpan => _intermediateBuffer.AsSpan(0, _intermediateLength);
    private ReadOnlySpan<byte> OscBodySpan => _oscBuffer.AsSpan(0, _oscLength);

    private void ResetSequenceBuffers()
    {
        _parameterLength = 0;
        _intermediateLength = 0;
        _oscLength = 0;
        _privatePrefix = 0;
    }

    private void ResetToGround()
    {
        ResetSequenceBuffers();
        _state = State.Ground;
    }

    /// <summary>Internal classifier states. Exposed for white-box testing only.</summary>
    internal enum State
    {
        Ground,
        Escape,
        EscapeEscape,
        EscapeIntermediate,
        Ss3,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        OscEsc,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsPassthroughEsc,
        DcsIgnore,
        DcsIgnoreEsc,
        X10MouseCb,
        X10MouseCx,
        X10MouseCy,
    }

    /// <summary>Visible to tests so they can assert state-machine progression.</summary>
    internal State CurrentState => _state;

    [Conditional("DEBUG")]
    internal void DebugAssertGround() => Debug.Assert(_state == State.Ground);
}