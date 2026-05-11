using System.Diagnostics;

namespace Cursorial.Core.Input.Parsing;

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
/// and final <c>0</c>.
/// </para>
/// <para>
/// <b>Buffers.</b> CSI/DCS parameter and intermediate bytes plus OSC bodies are accumulated
/// in fixed-size buffers. When a buffer overflows the sequence enters an "ignore" state and
/// is discarded on completion. Defaults are sized for realistic inputs (256 byte parameters,
/// 16 intermediates, 4096 byte OSC bodies); large DCS bodies are streamed via
/// <see cref="IVtSequenceTokenSink.OnDcsPut"/> rather than buffered.
/// </para>
/// <para>
/// <b>API stability.</b> Types in the <c>Cursorial.Core.Input.Parsing</c> namespace are public
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
    /// TODO: No production path currently sets this. <c>VtTerminalNegotiator</c> only ever
    /// pushes DECSET 1006 (SGR mouse), and <c>VtInputDevice</c> constructs its classifier
    /// privately, so a caller assigning <c>VtInputMode.MouseEncoding = MouseEncoding.X10</c>
    /// today has no way to reach the classifier flag. The decoder is exercised end-to-end by
    /// tests that set this flag directly. Wire when a real X10 use case appears (legacy
    /// terminal support or an explicit fallback opt-in on <c>NegotiationOptions</c>).
    /// </remarks>
    public bool X10MouseFramingEnabled { get; set; }

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
                if (IsPrintable(b))
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
            case State.Ss3:
                FlushSs3AsRecovery(sink);
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
        }
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
        }
        else
        {
            // C0 control or DEL.
            sink.OnExecute(b);
        }
    }

    // ---- Escape ----

    private void StepEscape(byte b, IVtSequenceTokenSink sink)
    {
        switch (b)
        {
            case (byte)'[':
                _state = State.CsiEntry;
                return;
            case (byte)']':
                _oscLength = 0;
                _state = State.OscString;
                return;
            case (byte)'P':
                _state = State.DcsEntry;
                return;
            case (byte)'O':
                // Single Shift 3: one more byte will follow as the SS3 final.
                _state = State.Ss3;
                return;
            case VtInputSequences.Escape:
                // Two ESCs in a row — commit the first as a bare ESC and start a new sequence.
                sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, 0);
                ResetSequenceBuffers();
                // Stay in Escape for the second ESC.
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
            // Final byte of an ESC sequence with no intermediates (e.g. ESC 7 = DECSC).
            sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, b);
            ResetToGround();
            return;
        }

        // C0 control inside Escape — execute and stay.
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

        sink.OnExecute(b);
    }

    // ---- SS3 (single shift 3) ----

    private void StepSs3(byte b, IVtSequenceTokenSink sink)
    {
        if (b is >= 0x20 and <= 0x7E)
        {
            // Dispatch as ESC dispatch with intermediate 'O' so the interpreter recognizes SS3.
            Span<byte> intermediates = stackalloc byte[1];
            intermediates[0] = (byte)'O';
            sink.OnEscDispatch(intermediates, b);
            ResetToGround();
            return;
        }

        if (b == VtInputSequences.Escape)
        {
            // A new ESC interrupts the SS3 — recover the partial as bare-ESC + 'O' printable
            // and re-enter Escape state for the new sequence.
            FlushSs3AsRecovery(sink);
            ResetSequenceBuffers();
            _state = State.Escape;
            return;
        }

        // C0 control inside SS3 — execute and stay (rare).
        if (b < 0x20) sink.OnExecute(b);
    }

    private static void FlushSs3AsRecovery(IVtSequenceTokenSink sink)
    {
        // Emit the held ESC as a bare-ESC dispatch and the trigger 'O' as a print so the
        // user's two keystrokes are preserved when an SS3 sequence never completes.
        sink.OnEscDispatch(ReadOnlySpan<byte>.Empty, 0);
        Span<byte> oByte = stackalloc byte[1];
        oByte[0] = (byte)'O';
        sink.OnPrint(oByte);
    }

    // ---- CSI ----

    private void StepCsiEntry(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (byte)'?' or (byte)'>' or (byte)'<' or (byte)'=')
        {
            _privatePrefix = b;
            _state = State.CsiParam;
            return;
        }

        StepCsiParam(b, sink);
    }

    private void StepCsiParam(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (>= (byte)'0' and <= (byte)'9') or (byte)';' or (byte)':')
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
        if (final == (byte)'M'
            && X10MouseFramingEnabled
            && _privatePrefix == 0
            && _parameterLength == 0
            && _intermediateLength == 0)
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
            sink.OnOscDispatch(OscBodySpan);
            ResetToGround();
            return;
        }

        if (b == VtInputSequences.Escape)
        {
            _state = State.OscEsc;
            return;
        }

        AppendOsc(b);
    }

    private void StepOscEsc(byte b, IVtSequenceTokenSink sink)
    {
        if (b == (byte)'\\')
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
        if (b is (byte)'?' or (byte)'>' or (byte)'<' or (byte)'=')
        {
            _privatePrefix = b;
            _state = State.DcsParam;
            return;
        }

        StepDcsParam(b, sink);
    }

    private void StepDcsParam(byte b, IVtSequenceTokenSink sink)
    {
        if (b is (>= (byte)'0' and <= (byte)'9') or (byte)';' or (byte)':')
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
        if (b == (byte)'\\')
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
        if (b == (byte)'\\')
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
            // bytes (e.g. OSC parameter prefix) are usually what the consumer needs.
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
