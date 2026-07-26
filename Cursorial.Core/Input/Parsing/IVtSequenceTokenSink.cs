using Cursorial.Input.Events;

namespace Cursorial.Input.Parsing;

/// <summary>
/// Receives classified tokens emitted by <see cref="VtSequenceClassifier"/>. The classifier
/// is purely a framing layer; this sink interprets framed sequences into higher-level events.
/// </summary>
/// <remarks>
/// <para>
/// All <see cref="ReadOnlySpan{T}"/> arguments are valid only for the duration of the call.
/// Implementations that need to retain the data MUST copy it (per the buffer-lifetime contract
/// on <see cref="InputEvent"/>).
/// </para>
/// <para>
/// Method names follow Williams VT500 conventions where applicable so readers familiar with
/// reference implementations recognize the dispatch points.
/// </para>
/// </remarks>
public interface IVtSequenceTokenSink
{
    /// <summary>
    /// A run of printable bytes (UTF-8). The interpreter is responsible for UTF-8 decoding.
    /// Successive printable bytes within a single <see cref="VtSequenceClassifier.Process"/>
    /// call are coalesced into one call.
    /// </summary>
    void OnPrint(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// A C0 control character (0x00–0x1F) or DEL (0x7F) outside of any sequence body.
    /// Includes Tab, CR, LF, BS, and Ctrl+letter codes; ESC is consumed as a sequence
    /// introducer and never appears here.
    /// </summary>
    void OnExecute(byte controlChar);

    /// <summary>
    /// A C0 control character (0x00–0x1F) or DEL (0x7F) that arrived <em>directly after</em> an
    /// <c>ESC</c> introducer — the meta-sends-escape wire form of <c>Alt+&lt;control key&gt;</c>.
    /// This is the control-key twin of the <c>ESC &lt;printable&gt;</c> form that reaches
    /// <see cref="OnEscDispatch"/> as an empty-intermediate dispatch: <c>ESC CR</c> is Alt+Enter,
    /// <c>ESC HT</c> is Alt+Tab, <c>ESC BS</c> / <c>ESC DEL</c> are Alt+Backspace, and
    /// <c>ESC ESC</c> (committed by <see cref="VtSequenceClassifier.Flush"/>, since the pair is
    /// ambiguous until the burst ends) is Alt+Esc.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate callback rather than a bare-final <see cref="OnEscDispatch"/> because
    /// <c>final == 0</c> is already spoken for as the bare-ESC commit — <c>ESC NUL</c>
    /// (Alt+Ctrl+Space) would otherwise be indistinguishable from a lone Escape keypress.
    /// </para>
    /// <para>
    /// Default-implemented as a forward to <see cref="OnExecute"/> so sinks written before
    /// Alt+&lt;control key&gt; decoding existed keep compiling and keep seeing the un-modified
    /// key (the behavior they had when the classifier executed these bytes in place) instead of
    /// silently dropping the keystroke.
    /// </para>
    /// </remarks>
    void OnAltExecute(byte controlChar) => OnExecute(controlChar);

    /// <summary>
    /// An <c>ESC</c> sequence with an optional run of intermediates and a final byte —
    /// e.g. <c>ESC ( B</c> (intermediates=<c>(</c>, final=<c>B</c>) for G0 charset, or
    /// <c>ESC O P</c> (intermediates=<c>O</c>, final=<c>P</c>) for SS3 F1.
    /// </summary>
    /// <remarks>
    /// A bare ESC committed by <see cref="VtSequenceClassifier.Flush"/> arrives here with
    /// empty <paramref name="intermediates"/> and final == 0.
    /// </remarks>
    void OnEscDispatch(ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>
    /// A CSI sequence: <c>ESC [ [private] [params] [intermediates] final</c>. Parameter bytes
    /// are passed raw (digits, semicolons, colons) so the interpreter can apply
    /// protocol-specific rules (sub-parameter handling, default values, etc.).
    /// </summary>
    /// <param name="privatePrefix">
    /// 0 if no private-marker byte, otherwise one of <c>?</c>, <c>&gt;</c>, <c>&lt;</c>, or <c>=</c>.
    /// </param>
    /// <param name="parameters">Raw parameter bytes between the prefix and intermediates (may be empty).</param>
    /// <param name="intermediates">Intermediate bytes (0x20–0x2F), commonly empty.</param>
    /// <param name="final">The final byte (0x40–0x7E) that ended the sequence.</param>
    void OnCsiDispatch(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final);

    /// <summary>
    /// A complete OSC sequence: <c>ESC ] body (ST | BEL)</c>. The string terminator is
    /// consumed by the classifier and not included in <paramref name="body"/>.
    /// </summary>
    void OnOscDispatch(ReadOnlySpan<byte> body);

    /// <summary>
    /// Start of a DCS sequence: <c>ESC P [private] [params] [intermediates] final</c>. Body
    /// bytes follow via <see cref="OnDcsPut"/>. The sequence ends with <see cref="OnDcsUnhook"/>.
    /// </summary>
    void OnDcsHook(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final);

    /// <summary>A chunk of bytes within a DCS body. Multiple calls may occur per DCS sequence.</summary>
    void OnDcsPut(ReadOnlySpan<byte> bytes);

    /// <summary>End of a DCS body — the string terminator (ST) was observed.</summary>
    void OnDcsUnhook();

    /// <summary>
    /// A complete X10 mouse report: <c>ESC [ M cb cx cy</c>. The three follow bytes are dispatched
    /// raw — they are each <c>value + 0x20</c> per the X10 encoding, and the interpreter is
    /// responsible for unmasking. The classifier only emits this token when its
    /// <see cref="VtSequenceClassifier.X10MouseFramingEnabled"/> flag is set; otherwise the
    /// <c>M</c> is dispatched as an ordinary CSI final and the follow bytes flow into
    /// <see cref="OnPrint"/>.
    /// </summary>
    /// <remarks>
    /// Default-implemented as a no-op so external sinks that pre-date X10 framing keep
    /// compiling without an action being silently dropped at runtime — the classifier will
    /// only call this when X10 framing is explicitly enabled, which a sink ignorant of the
    /// callback never does.
    /// </remarks>
    void OnX10MouseDispatch(byte cb, byte cx, byte cy) {}
}