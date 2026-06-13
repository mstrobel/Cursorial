using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Terminal;

/// <summary>
/// VT/ANSI implementation of <see cref="ITerminalNegotiator"/>. Drives the probe-and-respond
/// handshake using the same classifier and interpreter the input device uses long-term, then
/// (in subsequent passes) emits opt-in sequences for the protocols the application requested.
/// </summary>
/// <remarks>
/// <para>
/// <b>Probe pattern.</b> Identification queries are batched and terminated by a Primary
/// Device Attributes (DA1) request that acts as a synchronization sentinel. Every modern
/// terminal responds to DA1; whatever responses arrive before DA1's are valid replies to the
/// preceding probes, and any that don't arrive are unsupported. This is the standard pattern
/// used by libtickit, Kitty itself, and the reference helix terminal layer.
/// </para>
/// <para>
/// <b>This iteration covers identification only.</b> Active opt-in negotiation (mouse, focus,
/// bracketed paste, Kitty keyboard, Win32 input mode, synchronized output) and the
/// truecolor-verification round-trip will land in subsequent passes. <see cref="RestoreAsync"/>
/// is therefore currently a no-op.
/// </para>
/// </remarks>
public sealed class VtTerminalNegotiator : ITerminalNegotiator
{
    private enum ModeBit
    {
        MouseButtons,
        MouseButtonTracking,
        MouseMotionTracking,
        ExtendedMouseTracking,
        FocusEvents,
        BracketedPaste,
        SynchronizedOutput,
    }

    private readonly IInputByteSource _source;
    private readonly IOutputByteSink _sink;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;
    private readonly IEnvironmentReader _environment;

    // Lifecycle gates use Interlocked to stay safe when callers drive the negotiator outside
    // a TerminalSession (which already serializes against itself). 0 = open, 1 = transitioned.
    private int _negotiated;
    private int _disposed;
    private int _restored;
    private AppliedOptIns _applied;

    // Whether to emit the Kitty multi-cursor-clear sequence (CSI > 0 ; 4 SP q) at restore. Set
    // from the identified family during negotiation — only Kitty-family terminals understand it.
    // Other terminals (notably Apple Terminal) mis-parse the `> … SP q` form and print the
    // literal 'q', so it must NOT be emitted unconditionally.
    private bool _emitKittyExtraCursorClear;

    // Probe-phase pending read, persisted across DrainResponsesUntilSentinelAsync calls so a
    // second drain phase (DECRQM verification, future truecolor / default-color probes) can
    // reuse a still-in-flight read instead of starting a concurrent one on the same
    // PipeReader (which throws "Concurrent reads or writes are not supported.").
    private Task<ReadResult>? _pendingProbeRead;

    // Shared probe pipeline: a single classifier + interpreter + collector serves every probe
    // phase. The classifier holds a partial-sequence state that must persist across phases (a
    // sequence split across two reads should reassemble cleanly). The collector
    // accumulates every response we've seen so far — each phase waits for an additional
    // sentinel (DA1) tick rather than starting from zero. This is also what makes the
    // in-memory test fixture work: when responses are pre-enqueued, the first read returns
    // everything in one buffer; with shared state, the verification phase finds its DECRQM
    // responses already in the collector instead of waiting on an empty pipe.
    private VtSequenceClassifier? _probeClassifier;
    private VtInputInterpreter? _probeInterpreter;
    private ResponseCollector? _probeCollector;

    public VtTerminalNegotiator(
        IInputByteSource source,
        IOutputByteSink sink,
        VtInputMode? mode = null,
        TimeProvider? timeProvider = null,
        IEnvironmentReader? environmentReader = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _mode = mode ?? new VtInputMode();
        _time = timeProvider ?? TimeProvider.System;
        _environment = environmentReader ?? EnvironmentReader.Instance;
    }

    /// <summary>
    /// The mutable mode bag that the negotiator updates as opt-ins are pushed (and that the
    /// downstream input interpreter reads). Shared with the caller so the same instance can
    /// be passed to a future <c>VtInputDevice</c>.
    /// </summary>
    public VtInputMode Mode => _mode;

    public async Task<TerminalCapabilities> NegotiateAsync(
        NegotiationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _negotiated, 1) != 0)
        {
            throw new InvalidOperationException(
                "NegotiateAsync was already called on this instance. Negotiator instances are single-shot; " +
                "create a new VtTerminalNegotiator for a fresh negotiation.");
        }

        try
        {
            var responses = await ProbeIdentificationAsync(options, cancellationToken).ConfigureAwait(false);
            var identification = ResolveIdentification(responses, _environment);

            // The Kitty multi-cursor-clear restore sequence is only understood by Kitty-family
            // terminals (Kitty + Ghostty, which implements the same protocols). Everything else
            // mis-parses it; gate emission so we never send it elsewhere.
            _emitKittyExtraCursorClear = identification.Family is TerminalFamily.Kitty or TerminalFamily.Ghostty;

            if (options.OptIns == OptInPolicy.Allowed)
            {
                await ApplyOptInsAsync(options, identification, cancellationToken).ConfigureAwait(false);

                // After applying opt-ins, query DECRQM for each DEC private mode we just enabled.
                // A status=0 (unrecognized) or 2 (reset) response means the terminal silently
                // didn't honor our DECSET — we clear the corresponding bit so the realized
                // capabilities reflect what the terminal *actually* supports, not what we asked
                // for. Modes the terminal doesn't acknowledge with a DECRQM response at all leave
                // their bit alone (absent evidence is not evidence of absence).
                await VerifyAppliedOptInsAsync(options, identification, cancellationToken).ConfigureAwait(false);

                ApplyToInputMode(_applied);
            }

            // Probe color capabilities — empirical truecolor verification via OSC 4 set+query
            // round-trip, plus default fg / bg / cursor color queries (OSC 10 / 11 / 12). Skipped
            // when opt-ins are turned off, since the same connection-quality assumption (the
            // terminal is alive and willing to respond) underlies both phases.
            if (options.OptIns == OptInPolicy.Allowed)
            {
                await ProbeColorsAsync(options, cancellationToken).ConfigureAwait(false);
            }
            
            // _negotiated is now durably 1 even if anything above threw — restore still runs.
            var inputCapabilities = ResolveInputCapabilities(identification, _applied);
            var outputCapabilities = ResolveOutputCapabilities(identification, _applied);

            return new TerminalCapabilities(
                Terminal: identification,
                Input: inputCapabilities,
                Output: outputCapabilities);
        }
        finally
        {
            // Release any in-flight probe read so the PipeReader's state is clean before the
            // downstream VtInputDevice takes over. Without this, a probe phase that times out
            // before bytes arrive leaves the reader's awaitable "active," and the next
            // ReadAsync on the same pipe throws "Concurrent reads or writes are not supported."
            await ReleasePendingProbeReadAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// If a probe-phase <see cref="PipeReader.ReadAsync(CancellationToken)"/> is still in flight
    /// (because the phase's timeout fired before bytes arrived), cancel it and complete the
    /// resulting <see cref="ReadResult"/> by calling <see cref="PipeReader.AdvanceTo(SequencePosition, SequencePosition)"/>
    /// with <c>consumed = Start</c> and <c>examined = End</c> so any bytes that did arrive remain
    /// available for the next reader (typically the input device's pump).
    /// </summary>
    private async ValueTask ReleasePendingProbeReadAsync()
    {
        if (_pendingProbeRead is null) return;

        try
        {
            _source.Reader.CancelPendingRead();
            var result = await _pendingProbeRead.ConfigureAwait(false);
            // consumed = Start, examined = End: don't drop bytes that arrived between
            // CancelPendingRead and the cancel-result being consumed — they're handed off to
            // the next reader intact.
            _source.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
        catch
        {
            // Best-effort cleanup: if the read faulted, the downstream consumer will surface
            // the same failure on its own next read attempt.
        }
        finally
        {
            _pendingProbeRead = null;
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent and best-effort: a second call after a successful restore is a no-op,
        // and transport failures are swallowed so disposal stays reliable.
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;

        if (Volatile.Read(ref _negotiated) == 0) return;

        try
        {
            // LIFO order — opt-ins came on as Mouse, Focus, Paste, Kitty, Win32, Sync;
            // turn them off in the reverse order.
            
            // @formatter:off
            // Synchronized output is not session-level — it's wrapped per-frame by FrameRenderer,
            // so there's nothing to disable here. Recorded support is cleared in
            // ResetAppliedToReflectRestore so the capability reads consistently after restore.
            if (_applied.Win32InputMode)        QueueWrite(VtInputSequences.OptInSequences.DisableWin32InputMode);
            if (_applied.KittyKeyboard)         QueueWrite(VtInputSequences.OptInSequences.PopKittyKeyboard);
            if (_applied.BracketedPaste)        QueueWrite(VtInputSequences.OptInSequences.DisableBracketedPaste);
            if (_applied.FocusEvents)           QueueWrite(VtInputSequences.OptInSequences.DisableFocusEvents);
            if (_applied.SgrPixelsMouse)        QueueWrite(VtInputSequences.OptInSequences.DisableSgrPixelsMouse);
            if (_applied.MouseMotionTracking)   QueueWrite(VtInputSequences.OptInSequences.DisableMotionMouse);
            if (_applied.MouseButtonTracking)   QueueWrite(VtInputSequences.OptInSequences.DisableButtonMotionMouse);
            if (_applied.ExtendedMouseTracking) QueueWrite(VtInputSequences.OptInSequences.DisableSgrMouse);
            if (_applied.MouseButtons)          QueueWrite(VtInputSequences.OptInSequences.DisableMouseButtons);
            // @formatter:on

            // Unconditional belt-and-braces: clear any Kitty multi-cursor extras the terminal
            // is holding onto. The spec says alt-screen-toggle clears these implicitly, but a
            // timing-dependent Kitty bug leaves ghost cursors after quit-during-resize; this
            // sequence is silently ignored by non-supporting terminals. Emit independent of
            // _applied because the protocol isn't gated on an enable we'd have tracked. Family-
            // gated, though: non-Kitty terminals (Apple Terminal) mis-parse `CSI > 0 ; 4 SP q`
            // and print the literal 'q'.
            if (_emitKittyExtraCursorClear)
                QueueWrite(VtInputSequences.OptInSequences.ClearKittyExtraCursors);

            await _sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested == false)
        {
            // Best-effort — terminal may have already gone away.
        }
        finally
        {
            ResetAppliedToReflectRestore();
        }
    }

    public ReadOnlyMemory<byte> BuildRestoreSequence()
    {
        // Idempotent: once we hand bytes out for synchronous emission, RestoreAsync becomes a
        // no-op so the same 'disable' sequences aren't written twice (once via the sync path here,
        // once via the async sink during DisposeAsync). Matches RestoreAsync's _restored gate.
        if (Interlocked.Exchange(ref _restored, 1) != 0) return ReadOnlyMemory<byte>.Empty;
        if (Volatile.Read(ref _negotiated) == 0) return ReadOnlyMemory<byte>.Empty;

        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        // Mirror RestoreAsync's LIFO order. 'ClearKittyExtraCursors' is family-gated to Kitty —
        // non-Kitty terminals mis-parse it (see RestoreAsync / _emitKittyExtraCursorClear).
        if (_applied.Win32InputMode)        AppendBytes(writer, VtInputSequences.OptInSequences.DisableWin32InputMode);
        if (_applied.KittyKeyboard)         AppendBytes(writer, VtInputSequences.OptInSequences.PopKittyKeyboard);
        if (_applied.BracketedPaste)        AppendBytes(writer, VtInputSequences.OptInSequences.DisableBracketedPaste);
        if (_applied.FocusEvents)           AppendBytes(writer, VtInputSequences.OptInSequences.DisableFocusEvents);
        if (_applied.SgrPixelsMouse)        AppendBytes(writer, VtInputSequences.OptInSequences.DisableSgrPixelsMouse);
        if (_applied.MouseMotionTracking)   AppendBytes(writer, VtInputSequences.OptInSequences.DisableMotionMouse);
        if (_applied.MouseButtonTracking)   AppendBytes(writer, VtInputSequences.OptInSequences.DisableButtonMotionMouse);
        if (_applied.ExtendedMouseTracking) AppendBytes(writer, VtInputSequences.OptInSequences.DisableSgrMouse);
        if (_applied.MouseButtons)          AppendBytes(writer, VtInputSequences.OptInSequences.DisableMouseButtons);
        if (_emitKittyExtraCursorClear)     AppendBytes(writer, VtInputSequences.OptInSequences.ClearKittyExtraCursors);

        ResetAppliedToReflectRestore();
        return writer.WrittenMemory;

        static void AppendBytes(System.Buffers.ArrayBufferWriter<byte> w, ReadOnlySpan<byte> bytes)
        {
            var dest = w.GetSpan(bytes.Length);
            bytes.CopyTo(dest);
            w.Advance(bytes.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            await RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Restore is best-effort. Failures here are swallowed, so disposal is reliable
            // even when the underlying transport has already gone away.
        }
    }

    // ---- Opt-in application ----

    private async ValueTask ApplyOptInsAsync(
        NegotiationOptions options,
        TerminalIdentification identification,
        CancellationToken cancellationToken)
    {
        // Decide what to apply (family-gated). The byte emission is factored into EmitOptInEnables
        // so the re-apply path (ReapplyScreenLocalOptInsAsync) produces byte-identical sequences from
        // the same AppliedOptIns — the "three identical producers" discipline keeps apply ≡ re-apply.
        var applied = DecideOptIns(options, identification);

        EmitOptInEnables(in applied);

        if (!applied.IsEmpty)
        {
            await _sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _applied = applied;
    }

    /// <summary>
    /// Resolves the opt-in set to apply from <paramref name="options"/> and the identified family
    /// (family gating, mutual-exclusion of the two mouse modes). Pure — emits no bytes.
    /// </summary>
    private static AppliedOptIns DecideOptIns(NegotiationOptions options, TerminalIdentification identification)
    {
        var applied = default(AppliedOptIns);

        // Mouse: SGR encoding (1006) + button-event tracking (1002) is the standard combo for press /
        // release / drag on every modern terminal; any-event tracking (1003) is additive. SGR-Pixels
        // (1016) swaps cell coords for pixel coords — family-gated because non-supporting terminals
        // don't always ignore the DECSET cleanly. The two branches are mutually exclusive.
        if (options.EnableMouseTracking | options.EnableExtendedMouseTracking)
        {
            applied.ExtendedMouseTracking = options.EnableExtendedMouseTracking;
            applied.MouseButtonTracking = true;
            applied.MouseMotionTracking = options.EnableMouseTracking;
            applied.SgrPixelsMouse = options.EnableSgrPixelsMouse &&
                                     applied.ExtendedMouseTracking &&
                                     TerminalSupportsSgrPixelsMouse(identification.Family);
        }
        else if (options.EnableMouseButtons | options.EnableMouseButtonTracking)
        {
            applied.MouseButtons = true;
            applied.MouseButtonTracking = options.EnableMouseButtonTracking;
        }

        applied.FocusEvents = options.EnableFocusEvents;
        applied.BracketedPaste = options.EnableBracketedPaste;

        // Kitty keyboard: gate on family because pushing on a non-Kitty terminal is silently ignored,
        // which would cause us to wrongly report the protocol as enabled.
        if (options.EnableKittyKeyboard &&
            TerminalSupportsKittyKeyboard(identification.Family) &&
            options.KittyKeyboardFlags != KittyKeyboardFlags.None)
        {
            applied.KittyKeyboard = true;
            applied.KittyFlags = options.KittyKeyboardFlags;
        }

        // Win32 input mode: only meaningful on the Windows console host or Windows Terminal.
        applied.Win32InputMode = options.EnableWin32InputMode &&
                                 TerminalSupportsWin32InputMode(identification.Family);

        // Synchronized output (DECSET 2026): record realized support so per-frame consumers
        // (FrameRenderer) can wrap each redraw — but DO NOT issue DECSET 2026 at session level (it
        // begins a sync block that buffers output until DECRST 2026, which would stall interactive
        // output for the whole session on terminals that honor it strictly). Hence no byte in
        // EmitOptInEnables; it is recorded for the capability snapshot only.
        applied.SynchronizedOutput = options.EnableSynchronizedOutput &&
                                     TerminalSupportsSynchronizedOutput(identification.Family);

        return applied;
    }

    /// <summary>
    /// Writes the opt-in <b>enable</b> sequences for <paramref name="applied"/> to the output sink, in
    /// the canonical order. The single producer shared by the initial apply and the screen-local
    /// re-apply paths, so both emit byte-identical bytes. Synchronized output emits nothing (it is
    /// wrapped per-frame, not at session level). Does not flush.
    /// </summary>
    private void EmitOptInEnables(in AppliedOptIns applied)
    {
        // Order matches the original interleaved apply path. The two mouse modes are mutually
        // exclusive (ExtendedMouseTracking xor MouseButtons), so this single ordered run reproduces
        // both the extended branch [SgrMouse, ButtonMotion, Motion, SgrPixels] and the basic branch
        // [MouseButtons, ButtonMotion] byte-for-byte.
        // @formatter:off
        if (applied.ExtendedMouseTracking) QueueWrite(VtInputSequences.OptInSequences.EnableSgrMouse);
        if (applied.MouseButtons)          QueueWrite(VtInputSequences.OptInSequences.EnableMouseButtons);
        if (applied.MouseButtonTracking)   QueueWrite(VtInputSequences.OptInSequences.EnableButtonMotionMouse);
        if (applied.MouseMotionTracking)   QueueWrite(VtInputSequences.OptInSequences.EnableMotionMouse);
        if (applied.SgrPixelsMouse)        QueueWrite(VtInputSequences.OptInSequences.EnableSgrPixelsMouse);
        if (applied.FocusEvents)           QueueWrite(VtInputSequences.OptInSequences.EnableFocusEvents);
        if (applied.BracketedPaste)        QueueWrite(VtInputSequences.OptInSequences.EnableBracketedPaste);
        if (applied.KittyKeyboard)         QueueKittyPush(applied.KittyFlags);
        if (applied.Win32InputMode)        QueueWrite(VtInputSequences.OptInSequences.EnableWin32InputMode);
        // @formatter:on
    }

    /// <inheritdoc cref="ITerminalNegotiator.ReapplyScreenLocalOptInsAsync"/>
    public async ValueTask ReapplyScreenLocalOptInsAsync(CancellationToken cancellationToken = default)
    {
        // Re-emit the opt-in enable sequences that NegotiateAsync already applied (and
        // VerifyAppliedOptInsAsync confirmed the terminal honored), WITHOUT touching the restore
        // accounting — _applied is unchanged, so the negotiator still tracks exactly one push and will
        // emit exactly one matching pop at restore.
        //
        // Motivating case (the reason this exists): the Kitty keyboard flag stack is per-screen-buffer.
        // Negotiation pushes the flags on whichever screen was active at open (the main screen); a
        // consumer that then switches to the alternate screen lands on a fresh, empty Kitty stack and
        // silently loses key-up/repeat reporting. Re-emitting here, after the screen switch, lands the
        // push on the now-active screen. Mouse / focus / paste / Win32 DECSETs are screen-INDEPENDENT
        // global modes, so re-issuing them is an idempotent no-op.
        //
        // Restore stays symmetric: the one tracked pop fires on whatever screen is active at restore
        // time, and the extra screen-local push evaporates when that screen buffer is left (DECRST 1049
        // discards its Kitty stack) — so no orphan push survives teardown on any spec-conforming terminal.
        //
        // NOT idempotent for the Kitty push: each call appends another entry to the active screen's Kitty
        // stack (the global mouse/focus/paste/Win32 DECSETs ARE idempotent). The wiring calls this exactly
        // once per alt-screen entry (UIApplication.InitializeFromHost, gated on _enteredAltScreen), and the
        // matching screen-leave DECRST 1049 reclaims every entry — so the single-call contract is what
        // keeps it leak-free. A caller that re-enters screens itself must call once per entry.
        //
        // Concurrency: like the startup NegotiateAsync `_applied = applied` write, this reads `_applied`
        // OUTSIDE the lock-free signal path (EmergencyRestoreAndDispose → BuildRestoreSequence, which sets
        // _restored then zeroes `_applied`). The _restored check below is racy with that, and a SIGINT that
        // lands mid-emit can interleave async enables here against the direct-syscall disables there. This
        // is the same bounded, best-effort-during-process-death tradeoff the negotiator already documents
        // (see the lock-free note at the top of this file); the snapshot below keeps the emit self-consistent.
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _negotiated) == 0) return;
        if (Volatile.Read(ref _restored) != 0) return;

        var applied = _applied; // snapshot: emit a stable set rather than re-reading the mutable field
        if (applied.IsEmpty) return;

        EmitOptInEnables(in applied);
        await _sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void QueueKittyPush(KittyKeyboardFlags flags)
    {
        // CSI > <flags> u — flags as decimal ASCII. Bounded small string; allocate once.
        var ascii = ((uint) flags).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = VtInputSequences.OptInSequences.KittyPushPrefix;

        int total = prefix.Length + ascii.Length + 1;
        var span = _sink.Writer.GetSpan(total);
        prefix.CopyTo(span);
        Encoding.ASCII.GetBytes(ascii, span[prefix.Length..]);
        span[prefix.Length + ascii.Length] = VtInputSequences.OptInSequences.KittyPushSuffix;
        _sink.Writer.Advance(total);
    }

    private void QueueWrite(ReadOnlySpan<byte> bytes)
    {
        var span = _sink.Writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _sink.Writer.Advance(bytes.Length);
    }

    private void ApplyToInputMode(in AppliedOptIns applied)
    {
        // SGR-Pixels is a strict extension of SGR — when both apply, the pixels encoding wins
        // and the interpreter routes pixel coords into CellPosition.PixelX/Y.
        if (applied.SgrPixelsMouse) _mode.MouseEncoding = MouseEncoding.SgrPixels;
        else if (applied.ExtendedMouseTracking) _mode.MouseEncoding = MouseEncoding.Sgr;
        if (applied.FocusEvents) _mode.FocusReportingEnabled = true;
        if (applied.BracketedPaste) _mode.BracketedPasteEnabled = true;
        if (applied.KittyKeyboard) _mode.KittyKeyboard = applied.KittyFlags;
        if (applied.Win32InputMode) _mode.Win32InputModeEnabled = true;
    }

    private void ResetAppliedToReflectRestore()
    {
        // Reflect the restored state on the shared mode bag. The interpreter reads these,
        // so leaving them set after restore would mis-report capabilities.
        if (_applied.SgrPixelsMouse || _applied.ExtendedMouseTracking) _mode.MouseEncoding = MouseEncoding.None;
        if (_applied.FocusEvents) _mode.FocusReportingEnabled = false;
        if (_applied.BracketedPaste) _mode.BracketedPasteEnabled = false;
        if (_applied.KittyKeyboard) _mode.KittyKeyboard = KittyKeyboardFlags.None;
        if (_applied.Win32InputMode) _mode.Win32InputModeEnabled = false;
        _applied = default;
    }

    private static bool TerminalSupportsKittyKeyboard(TerminalFamily family) =>
        family is TerminalFamily.Kitty or
                  TerminalFamily.Ghostty or
                  TerminalFamily.WezTerm or
                  TerminalFamily.Konsole or
                  TerminalFamily.Foot or
                  TerminalFamily.ITerm2; // Partial support since iTerm2 3.5.

    private static bool TerminalSupportsWin32InputMode(TerminalFamily family) =>
        family is TerminalFamily.WindowsTerminal or
                  TerminalFamily.WindowsConsoleHost;

    /// <summary>
    /// DECRQM (<c>CSI ? &lt;mode&gt; $ p</c>) request-mode support, used to verify that opt-in
    /// DEC private modes actually took effect. Allow-listed to the xterm-derived families known
    /// to implement it. Notably EXCLUDES <see cref="TerminalFamily.AppleTerminal"/>, which
    /// mis-parses the <c>$ p</c> form and prints the literal final byte, plus
    /// <see cref="TerminalFamily.GenericVt"/> / <see cref="TerminalFamily.Unknown"/> where the
    /// behavior is unproven — sending DECRQM there risks the same stray-output leak for no gain,
    /// since verification is only a refinement (its absence falls back to trusting the opt-in's
    /// own family gating).
    /// </summary>
    private static bool TerminalSupportsDecRqm(TerminalFamily family) =>
        family is TerminalFamily.Xterm or
                  TerminalFamily.Kitty or
                  TerminalFamily.Ghostty or
                  TerminalFamily.Rio or
                  TerminalFamily.WezTerm or
                  TerminalFamily.Foot or
                  TerminalFamily.ITerm2 or
                  TerminalFamily.Alacritty or
                  TerminalFamily.Konsole or
                  TerminalFamily.GnomeTerminal or
                  TerminalFamily.WindowsTerminal or
                  TerminalFamily.WindowsConsoleHost or
                  TerminalFamily.GenericWsl or
                  TerminalFamily.Tmux or
                  TerminalFamily.GnuScreen;

    private static bool TerminalSupportsSynchronizedOutput(TerminalFamily family) =>
        family is TerminalFamily.Kitty or
                  TerminalFamily.Ghostty or
                  TerminalFamily.ITerm2 or
                  TerminalFamily.WezTerm or
                  TerminalFamily.Alacritty or
                  TerminalFamily.WindowsTerminal or
                  TerminalFamily.GenericWsl or
                  TerminalFamily.Konsole or
                  TerminalFamily.Foot;

    /// <summary>
    /// Kitty's OSC 22 pointer-shape protocol. Not negotiated — terminals don't advertise it; we
    /// gate on family identification. Per <see href="https://sw.kovidgoyal.net/kitty/pointer-shapes/"/>
    /// the protocol is honored by Kitty, Ghostty, and Foot. (Contour and Wayst also implement
    /// it but aren't in our <see cref="TerminalFamily"/> enum yet.)
    /// </summary>
    private static bool TerminalSupportsMouseCursorShape(TerminalFamily family) =>
        family is TerminalFamily.Kitty or
                  TerminalFamily.Ghostty or
                  TerminalFamily.Foot;

    /// <summary>
    /// DECSET 1016 (SGR-Pixels mouse) — terminals that report mouse coords in pixels rather
    /// than cells. Gated on family because non-supporting terminals often don't ignore the
    /// DECSET cleanly: some leave the previous mouse mode disabled instead. Honored by Kitty,
    /// Ghostty, WezTerm, Foot, iTerm2, and modern xterm (358+).
    /// </summary>
    private static bool TerminalSupportsSgrPixelsMouse(TerminalFamily family) =>
        family is TerminalFamily.Kitty or
                  TerminalFamily.Ghostty or
                  TerminalFamily.Rio or
                  TerminalFamily.WezTerm or
                  TerminalFamily.Foot or
                  TerminalFamily.ITerm2 or
                  TerminalFamily.Xterm;

    /// <summary>
    /// OSC 52 clipboard write. Most modern terminals honor this; some gate it behind a user
    /// prompt or an allow-list. We claim support for the families known to implement it; older
    /// terminals or those that strip OSCs we don't recognize silently fall through.
    /// </summary>
    private static bool TerminalSupportsClipboardWrite(TerminalFamily family) =>
        family is TerminalFamily.Kitty or
                  TerminalFamily.Ghostty or
                  TerminalFamily.Rio or
                  TerminalFamily.WezTerm or
                  TerminalFamily.Foot or
                  TerminalFamily.ITerm2 or
                  TerminalFamily.Alacritty or
                  TerminalFamily.Xterm or
                  TerminalFamily.Konsole or
                  TerminalFamily.WindowsTerminal or
                  TerminalFamily.GenericWsl or
                  TerminalFamily.Tmux;

    // ---- Probe orchestration ----

    private async Task<ProbeResponses> ProbeIdentificationAsync(
        NegotiationOptions options,
        CancellationToken cancellationToken)
    {
        // Send XTVERSION, CSI 16 t (cell size in pixels), CSI 18 t (text area size in
        // characters), then DA1 (the sentinel). We wait for DA1's response; whatever
        // arrived before it is taken as the response set.
        await WriteAsync(XtVersionRequest, cancellationToken).ConfigureAwait(false);
        await WriteAsync(CellSizeRequest, cancellationToken).ConfigureAwait(false);
        await WriteAsync(TextAreaSizeRequest, cancellationToken).ConfigureAwait(false);
        await WriteAsync(Da1Request, cancellationToken).ConfigureAwait(false);

        // Initialize the shared probe pipeline. This collector accumulates every response
        // across every probe phase the negotiator runs.
        _probeCollector ??= new ResponseCollector();
        _probeClassifier ??= new VtSequenceClassifier();
        _probeInterpreter ??= new VtInputInterpreter(_mode, _probeCollector, _time);

        await DrainResponsesUntilSentinelAsync(_probeClassifier,
                                               _probeInterpreter,
                                               _probeCollector,
                                               sentinelTarget: _probeCollector.SentinelCount + 1,
                                               options.ProbeTimeout,
                                               cancellationToken)
            .ConfigureAwait(false);

        var collector = _probeCollector;
        var cellSize = collector.FindFirst(DeviceResponseKind.CellSizeInPixels);

        if (cellSize is not null && TryParseCellSize(cellSize.Payload.Span, out int h, out int w))
        {
            _mode.CellPixelHeight = h;
            _mode.CellPixelWidth = w;
        }

        var textArea = collector.FindFirst(DeviceResponseKind.TextAreaSizeInChars);

        // Payload shape is identical to CellSize ("8;rows;cols" vs. "6;h;w"); TryParseCellSize
        // peels the leading sentinel and yields the two numeric tail parameters in payload
        // order, which here means (rows, cols).
        if (textArea is not null && TryParseCellSize(textArea.Payload.Span, out int rows, out int cols))
        {
            _mode.TextAreaRows = rows;
            _mode.TextAreaColumns = cols;
        }

        return new ProbeResponses(
            XtVersion: collector.FindFirst(DeviceResponseKind.XtVersion),
            PrimaryDeviceAttributes: collector.FindFirst(DeviceResponseKind.PrimaryDeviceAttributes));
    }

    /// <summary>
    /// Verify that the DEC private modes we just enabled actually took effect, by batching
    /// DECRQM queries (<c>CSI ? &lt;mode&gt; $ p</c>) for each applied bit plus a DA1 sentinel.
    /// Responses arrive tagged by mode number (<c>CSI ? &lt;mode&gt; ; &lt;status&gt; $ y</c>),
    /// so they can be matched in parallel as they stream in.
    /// </summary>
    /// <remarks>
    /// Status interpretation per the VT510 reference:
    /// <list type="bullet">
    /// <item><description>0 — unrecognized; the terminal doesn't implement this mode. Clear the bit.</description></item>
    /// <item><description>1 — set; the mode is currently on. Confirmed.</description></item>
    /// <item><description>2 — reset; the mode is off (our DECSET didn't stick). Clear the bit.</description></item>
    /// <item><description>3 — permanently set; mode is always on. Treat as confirmed.</description></item>
    /// <item><description>4 — permanently reset; mode can never be on. Clear the bit.</description></item>
    /// </list>
    /// If no DECRQM response arrives for a given mode by the time DA1 returns, we leave the
    /// bit as-is — silence is treated as "unknowable" rather than "definitely unsupported",
    /// because some legacy terminals respond to DA1 but not to DECRQM.
    /// </remarks>
    private async Task VerifyAppliedOptInsAsync(
        NegotiationOptions options, TerminalIdentification identification, CancellationToken cancellationToken)
    {
        // Family gate: only probe DECRQM on terminals known to implement it. DECRQM
        // (CSI ? <mode> $ p) is an xterm-family feature; terminals that don't implement it should
        // ignore the sequence, but some — notably Apple Terminal — mis-parse the `$ p` form and
        // PRINT the final 'p' to the screen (one stray 'p' per query, surfacing as a "ppppp"
        // smear after the session restores). Verification is only a refinement — it downgrades
        // capabilities the terminal silently didn't honor — so skipping it on unknown/non-DECRQM
        // families just falls back to trusting the opt-in's own family gating, which is safe.
        if (!TerminalSupportsDecRqm(identification.Family)) return;

        // Build the (mode, applied-bit) pairs we want to verify. Kitty keyboard and Win32 input
        // mode don't have DECRQM responses (Kitty uses its own keyboard-stack query, Win32
        // input mode is an MS extension with no documented verification path) — skip those.
        // Mouse tracking lives in two bits: MouseTracking covers both SGR (1006) and button
        // event (1002); we verify 1006 since that's the one consumers depend on directly.
        var probes = new List<(int Mode, ModeBit Bit)>(6);

        if (_applied.MouseButtons) probes.Add((1000, ModeBit.MouseButtons));
        if (_applied.ExtendedMouseTracking) probes.Add((1006, ModeBit.ExtendedMouseTracking));
        if (_applied.MouseButtonTracking) probes.Add((1002, ModeBit.MouseButtonTracking));
        if (_applied.MouseMotionTracking) probes.Add((1003, ModeBit.MouseMotionTracking));
        if (_applied.FocusEvents) probes.Add((1004, ModeBit.FocusEvents));
        if (_applied.BracketedPaste) probes.Add((2004, ModeBit.BracketedPaste));
        if (_applied.SynchronizedOutput) probes.Add((2026, ModeBit.SynchronizedOutput));

        if (probes.Count == 0) return;

        // Emit queries + DA1 sentinel.
        foreach (var probe in probes)
            await WriteDecRqmAsync(probe.Mode, cancellationToken).ConfigureAwait(false);

        await WriteAsync(Da1Request, cancellationToken).ConfigureAwait(false);

        // Share the probe pipeline with the identification phase. The collector already holds
        // identification responses; drain until *another* sentinel arrives — that's the DA1
        // that terminates this phase. If responses were pre-enqueued (in tests or by a chatty
        // terminal), they're already in the collector and the drain returns immediately.
        var collector = _probeCollector!;
        var classifier = _probeClassifier!;
        var interpreter = _probeInterpreter!;

        await DrainResponsesUntilSentinelAsync(classifier,
                                               interpreter,
                                               collector,
                                               sentinelTarget: collector.SentinelCount + 1,
                                               options.ProbeTimeout,
                                               cancellationToken)
            .ConfigureAwait(false);

        // For each probe, find the matching DECRQM response and update _applied. Either of
        // the mouse-tracking probes (1006 SGR or 1002 button-event) reporting unsupported is
        // enough to drop the whole MouseTracking bit, since SGR without a button-event yields
        // no useful press events and button-event without SGR can't decode coordinates.
        foreach (var probe in probes)
        {
            if (!TryFindDecRqmStatus(collector, probe.Mode, out int status)) continue;
            bool confirmed = status is 1 or 3;
            if (!confirmed) ClearAppliedBit(probe.Bit);
        }
    }

    private async ValueTask WriteDecRqmAsync(int mode, CancellationToken cancellationToken)
    {
        // CSI ? <mode> $ p — 5 fixed bytes plus the ASCII mode digits.
        Span<byte> bytes = stackalloc byte[16];
        int written = 0;
        bytes[written++] = 0x1B;
        bytes[written++] = (byte) '[';
        bytes[written++] = (byte) '?';

        // Decimal-format the mode in place.
        Span<byte> digits = stackalloc byte[6];
        int digitCount = 0;
        int v = mode;

        do
        {
            digits[digitCount++] = (byte) ('0' + v % 10);
            v /= 10;
        }
        while (v > 0);

        for (int i = digitCount - 1; i >= 0; i--) bytes[written++] = digits[i];

        bytes[written++] = (byte) '$';
        bytes[written++] = (byte) 'p';

        // Cannot pass stackalloc spans to async APIs; rent a heap byte[] for this one write.
        var payload = bytes[..written].ToArray();
        await WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryFindDecRqmStatus(ResponseCollector collector, int mode, out int status)
    {
        status = 0;

        foreach (var response in collector.Responses)
        {
            if (response.Kind != DeviceResponseKind.DecRqmPrivate) continue;

            // Payload is "<mode>;<status>" as ASCII bytes.
            var payload = response.Payload.Span;
            int sep = payload.IndexOf((byte) ';');
            if (sep < 0) continue;

            if (!TryParseAsciiInt(payload[..sep], out int respMode)) continue;
            if (respMode != mode) continue;

            if (!TryParseAsciiInt(payload[(sep + 1)..], out status)) continue;
            return true;
        }

        return false;
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.IsEmpty) return false;

        foreach (byte b in bytes)
        {
            if (b < (byte) '0' || b > (byte) '9') return false;
            value = value * 10 + (b - (byte) '0');
        }

        return true;
    }

    private void ClearAppliedBit(ModeBit bit)
    {
        // @formatter:off
        switch (bit)
        {
            case ModeBit.MouseButtons:          _applied.MouseButtons          = false; break;
            case ModeBit.MouseButtonTracking:   _applied.MouseButtonTracking   = false; break;
            case ModeBit.MouseMotionTracking:   _applied.MouseMotionTracking   = false; break;
            case ModeBit.ExtendedMouseTracking: _applied.ExtendedMouseTracking = false; break;
            case ModeBit.FocusEvents:           _applied.FocusEvents           = false; break;
            case ModeBit.BracketedPaste:        _applied.BracketedPaste        = false; break;
            case ModeBit.SynchronizedOutput:    _applied.SynchronizedOutput    = false; break;
        }
        // @formatter:on
    }

    // ---- Color probes -----------------------------------------------------------------

    // Stash for color-probe results, read by ResolveColorCapabilities after probes complete.
    private bool _truecolorVerified;
    private Color? _defaultForeground;
    private Color? _defaultBackground;
    private Color? _defaultCursorColor;

    // A fixed three-byte probe color used for truecolor verification. The triplet is arbitrary
    // but distinctive — it was picked so it doesn't land near any common default-palette value
    // (avoiding false positives if a terminal's palette[255] happens to be close to our probe).
    private const byte TruecolorProbeRed = 0xAB;
    private const byte TruecolorProbeGreen = 0xCD;
    private const byte TruecolorProbeBlue = 0xEF;

    /// <summary>
    /// Probe color capabilities — set palette slot 255 to a distinctive triplet, query it back
    /// for empirical truecolor verification, and query OSC 10 / 11 / 12 for the terminal's
    /// default foreground / background / cursor colors. All queries plus the DA1 sentinel are
    /// batched into a single round-trip; responses route to a single shared collector.
    /// </summary>
    private async Task ProbeColorsAsync(NegotiationOptions options, CancellationToken cancellationToken)
    {
        // ReSharper disable CommentTypo

        // Write the truecolor SET — palette slot 255 to (AB, CD, EF). Use 2-digit hex so the
        // terminal's 16-bit-internal representation (rgb:abab/cdcd/efef on xterm-class
        // terminals) round-trips cleanly when we parse the top byte of each channel.
        await WriteOsc4SetAsync(255, TruecolorProbeRed, TruecolorProbeGreen, TruecolorProbeBlue,
                                cancellationToken)
            .ConfigureAwait(false);

        // ReSharper restore CommentTypo
        
        // Query palette slot 255 to see what the terminal stored.
        await WriteOsc4QueryAsync(255, cancellationToken).ConfigureAwait(false);

        // Default-color queries — OSC 10 / 11 / 12 each accept a single "?" parameter to ask
        // for the current value.
        await WriteAsync(Osc10Query, cancellationToken).ConfigureAwait(false);
        await WriteAsync(Osc11Query, cancellationToken).ConfigureAwait(false);
        await WriteAsync(Osc12Query, cancellationToken).ConfigureAwait(false);

        // DA1 sentinel.
        await WriteAsync(Da1Request, cancellationToken).ConfigureAwait(false);

        var collector = _probeCollector!;
        var classifier = _probeClassifier!;
        var interpreter = _probeInterpreter!;

        await DrainResponsesUntilSentinelAsync(classifier,
                                               interpreter,
                                               collector,
                                               sentinelTarget: collector.SentinelCount + 1,
                                               options.ProbeTimeout,
                                               cancellationToken)
            .ConfigureAwait(false);

        // Extract truecolor verification — find the PaletteColor response for slot 255 (or
        // accept any PaletteColor response if it's the only one, since the index is in the
        // payload).
        foreach (var response in collector.Responses)
        {
            if (response.Kind != DeviceResponseKind.PaletteColor) continue;

            if (!TryParsePalettePayload(response.Payload.Span, out int index, out byte r, out byte g, out byte b))
                continue;

            if (index != 255) continue;

            // Byte-exact comparison: a truecolor terminal stores the value losslessly, so the
            // top byte of each channel in its response matches what we sent. A terminal that
            // quantizes to 256 colors (or to a different palette representation) returns a
            // different value.
            _truecolorVerified = r == TruecolorProbeRed &&
                                 g == TruecolorProbeGreen &&
                                 b == TruecolorProbeBlue;

            break;
        }

        // Default colors — first matching response per kind. Most terminals send one of each;
        // if multiple responses arrive, we take the first as the canonical value.
        _defaultForeground = FindFirstColor(collector, DeviceResponseKind.ForegroundColor);
        _defaultBackground = FindFirstColor(collector, DeviceResponseKind.BackgroundColor);
        _defaultCursorColor = FindFirstColor(collector, DeviceResponseKind.CursorColor);
    }

    private static Color? FindFirstColor(ResponseCollector collector, DeviceResponseKind kind)
    {
        foreach (var response in collector.Responses)
        {
            if (response.Kind != kind) continue;

            if (TryParseRgbColor(response.Payload.Span, out byte r, out byte g, out byte b))
                return Color.FromRgb(r, g, b);
        }

        return null;
    }

    // ReSharper disable once CommentTypo
    /// <summary>Parse "<c>&lt;index&gt;;rgb:RRRR/GGGG/BBBB</c>" — an OSC 4 palette-color payload.</summary>
    private static bool TryParsePalettePayload(ReadOnlySpan<byte> payload, out int index,
                                               out byte r, out byte g, out byte b)
    {
        index = 0;
        r = g = b = 0;

        int separator = payload.IndexOf((byte) ';');
        if (separator < 0) return false;

        if (!TryParseAsciiInt(payload[..separator], out index)) return false;
        return TryParseRgbColor(payload[(separator + 1)..], out r, out g, out b);
    }

    /// <summary>Parse "<c>rgb:R*/G*/B*</c>" with 1–4 hex digits per channel, reducing to 8-bit.</summary>
    private static bool TryParseRgbColor(ReadOnlySpan<byte> payload, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;

        // Must begin with the "rgb:" prefix.
        ReadOnlySpan<byte> prefix = "rgb:"u8;
        if (payload.Length < prefix.Length) return false;
        if (!payload[..prefix.Length].SequenceEqual(prefix)) return false;

        var rest = payload[prefix.Length..];
        int slash1 = rest.IndexOf((byte) '/');
        if (slash1 < 0) return false;
        var redSpan = rest[..slash1];
        var afterRed = rest[(slash1 + 1)..];

        int slash2 = afterRed.IndexOf((byte) '/');
        if (slash2 < 0) return false;
        var greenSpan = afterRed[..slash2];
        var blueSpan = afterRed[(slash2 + 1)..];

        return TryParseHexChannel(redSpan, out r) &&
               TryParseHexChannel(greenSpan, out g) &&
               TryParseHexChannel(blueSpan, out b);
    }

    /// <summary>
    /// Convert a 1–4 hex-digit X11 color-channel string to 8-bit. One-digit values duplicate
    /// (F → FF); two-or-more-digit values take the leading two hex digits.
    /// </summary>
    private static bool TryParseHexChannel(ReadOnlySpan<byte> hex, out byte value)
    {
        value = 0;
        if (hex.IsEmpty || hex.Length > 4) return false;

        if (hex.Length == 1)
        {
            if (!TryParseHexDigit(hex[0], out int d)) return false;
            value = (byte) ((d << 4) | d);
            return true;
        }

        if (!TryParseHexDigit(hex[0], out int hi)) return false;
        if (!TryParseHexDigit(hex[1], out int lo)) return false;
        value = (byte) ((hi << 4) | lo);
        return true;
    }

    private static bool TryParseHexDigit(byte b, out int value)
    {
        if (b is >= (byte) '0' and <= (byte) '9')
        {
            value = b - (byte) '0';
            return true;
        }

        if (b is >= (byte) 'a' and <= (byte) 'f')
        {
            value = b - (byte) 'a' + 10;
            return true;
        }

        if (b is >= (byte) 'A' and <= (byte) 'F')
        {
            value = b - (byte) 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    private async ValueTask WriteOsc4SetAsync(int index, byte r, byte g, byte b, CancellationToken ct)
    {
        // "ESC ] 4 ; <index> ; rgb:RR/GG/BB ESC \"
        var payload = Encoding.ASCII.GetBytes($"\x1b]4;{index};rgb:{r:x2}/{g:x2}/{b:x2}\x1b\\");
        await WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private async ValueTask WriteOsc4QueryAsync(int index, CancellationToken ct)
    {
        var payload = Encoding.ASCII.GetBytes($"\x1b]4;{index};?\x1b\\");
        await WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> Osc10Query { get; } = "\x1b]10;?\x1b\\"u8.ToArray();
    private static ReadOnlyMemory<byte> Osc11Query { get; } = "\x1b]11;?\x1b\\"u8.ToArray();
    private static ReadOnlyMemory<byte> Osc12Query { get; } = "\x1b]12;?\x1b\\"u8.ToArray();

    /// <summary>
    /// Parse a <c>CSI 16 t</c> response payload of the form "6;height;width". The leading "6;"
    /// (the reply code) is consumed by the classifier into the 'parameters' span; what arrives
    /// here is the raw parameter run, so we reparse rather than treat it as just 'height;width'.
    /// </summary>
    private static bool TryParseCellSize(ReadOnlySpan<byte> payload, out int height, out int width)
    {
        height = 0;
        width = 0;

        int firstSep = payload.IndexOf((byte) ';');
        if (firstSep < 0) return false;
        var afterFirst = payload[(firstSep + 1)..];

        int secondSep = afterFirst.IndexOf((byte) ';');
        if (secondSep < 0) return false;

        var heightSpan = afterFirst[..secondSep];
        var widthSpan = afterFirst[(secondSep + 1)..];

        if (!int.TryParse(Encoding.ASCII.GetString(heightSpan), out height)) return false;
        if (!int.TryParse(Encoding.ASCII.GetString(widthSpan), out width)) return false;

        return height > 0 && width > 0;
    }

    private async Task DrainResponsesUntilSentinelAsync(
        VtSequenceClassifier classifier,
        VtInputInterpreter interpreter,
        ResponseCollector collector,
        int sentinelTarget,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        // Fast path — sentinel already observed (e.g., a previous phase processed pre-buffered
        // bytes that included this phase's response set).
        if (collector.SentinelCount >= sentinelTarget) return;

        // Total-elapsed timeout for the probe phase. We use Task.WhenAny rather than the
        // CancellationToken passed to ReadAsync because Stream.ReadAsync over POSIX stdin
        // (`__ConsoleStream` on a thread-pool worker performing a synchronous `read(2)`)
        // doesn't honor cancellation mid-syscall — the token fires but the read doesn't
        // wake up. Task.WhenAny lets us return regardless. Any bytes the abandoned read
        // eventually produces land in the StreamPipeReader's buffer for the input device
        // pump to consume.
        var timeoutTask = Task.Delay(probeTimeout, _time, cancellationToken);

        try
        {
            while (collector.SentinelCount < sentinelTarget &&
                   !cancellationToken.IsCancellationRequested &&
                   !timeoutTask.IsCompleted)
            {
                // Reuse a pending read from a previous drain phase if one is still in flight.
                // Starting a fresh ReadAsync while the old one is alive throws "Concurrent
                // reads or writes are not supported." on the underlying PipeReader.
                _pendingProbeRead ??= _source.Reader.ReadAsync(cancellationToken).AsTask();

                var completed = await Task.WhenAny(_pendingProbeRead, timeoutTask).ConfigureAwait(false);

                if (completed != _pendingProbeRead)
                {
                    // Timeout (or outer cancel) fired before bytes arrived.
                    break;
                }

                ReadResult result;

                try
                {
                    result = await _pendingProbeRead.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    _pendingProbeRead = null;
                }

                var buffer = result.Buffer;

                foreach (var segment in buffer)
                    classifier.Process(segment.Span, interpreter);

                _source.Reader.AdvanceTo(buffer.End);

                if (result.IsCompleted) break;
            }
        }
        finally
        {
            // Ensure any pending lone-ESC is flushed (otherwise it'd corrupt the next consumer).
            classifier.Flush(interpreter);
        }
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _sink.Writer.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Identification resolution ----

    public static TerminalIdentification ResolveIdentification(IEnvironmentReader? environment = null, TerminalFamily? familyOverride = null)
    {
        environment ??= IEnvironmentReader.Default;
        return ResolveIdentification(null, environment);
    }

    private static TerminalIdentification ResolveIdentification(ProbeResponses? responses,
                                                                IEnvironmentReader? environment = null,
                                                                TerminalFamily? familyOverride = null)
    {
        environment ??= IEnvironmentReader.Default;
        
        string? rawTerm = environment.GetVariable("TERM");
        string? rawTermProgram = environment.GetVariable("TERM_PROGRAM");
        string? termVersion = environment.GetVariable("TERM_PROGRAM_VERSION");
        bool insideMultiplexer = DetectMultiplexer(rawTerm, environment);

        string? xtVersionPayload = responses?.XtVersion is {} x ? AsciiPayloadOrNull(x.Payload) : null;
        var (familyFromXt, nameFromXt, versionFromXt) = ParseXtVersionPayload(xtVersionPayload);

        var family = familyOverride ?? familyFromXt;

        if (family == TerminalFamily.Unknown)
            family = ClassifyFromEnvironment(rawTerm, rawTermProgram, environment);

        // DA1 response carries DEC private feature codes. Parameter 4 is "Sixel graphics
        // support." Parsing it lets us detect Sixel on terminals the family list misses
        // (xterm-with-sixel, modern Kitty, modern Konsole, …) without maintaining a
        // perpetually-out-of-date family allow-list.
        bool advertisesSixel = responses?.PrimaryDeviceAttributes is { } da1 &&
                               Da1Advertises(da1.Payload.Span, parameter: 4);

        return new TerminalIdentification(
            Family: family,
            Name: nameFromXt ?? rawTermProgram,
            Version: versionFromXt ?? termVersion,
            RawTermEnv: rawTerm,
            RawTermProgramEnv: rawTermProgram,
            InsideMultiplexer: insideMultiplexer,
            AdvertisesSixel: advertisesSixel);
    }

    /// <summary>
    /// Scan a DA1 payload — semicolon-separated decimal parameters like <c>"64;1;4;9;22"</c> —
    /// for an exact match of <paramref name="parameter"/>. The DEC spec defines each parameter
    /// as a numbered feature; parameter 4 is Sixel, 22 is ANSI color, etc. Exact-match
    /// semantics are required because "4" and "44" are different features (44 is PCTerm).
    /// </summary>
    private static bool Da1Advertises(ReadOnlySpan<byte> payload, int parameter)
    {
        int start = 0;
        for (int i = 0; i <= payload.Length; i++)
        {
            if (i == payload.Length || payload[i] == (byte) ';')
            {
                var token = payload[start..i];
                if (TryParseAsciiInt(token, out int value) && value == parameter)
                    return true;
                start = i + 1;
            }
        }
        return false;
    }

    private static bool DetectMultiplexer(string? rawTerm, IEnvironmentReader? environment = null)
    {
        environment ??= IEnvironmentReader.Default;
        
        if (environment.GetVariable("TMUX") is { Length: > 0 }) return true;
        if (environment.GetVariable("STY") is { Length: > 0 }) return true; // GNU Screen

        if (rawTerm is null) return false;

        return MatchIdentifier(rawTerm, "screen") ||
               MatchIdentifier(rawTerm, "tmux");
    }

    private static (TerminalFamily family, string? name, string? version) ParseXtVersionPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return (TerminalFamily.Unknown, null, null);

        // Form A: "name(version)" — e.g., "kitty(0.46.2)". Treat the parenthesized payload as a
        // version only when it looks like one (contains a '.'). Some terminals park a bare build
        // identifier in this slot — GNOME Terminal reports <c>VTE(7600)</c>, which is not a
        // semantic version — and we want to leave the whole identifier as the name so the
        // existing family classifier can still match it as a literal.
        int parenOpen = payload.IndexOf('(');
        int parenClose = payload.LastIndexOf(')');
        if (parenOpen > 0 && parenClose > parenOpen + 1)
        {
            var inside = payload.AsSpan(parenOpen + 1, parenClose - parenOpen - 1);
            if (inside.Contains('.'))
            {
                var parenName = payload[..parenOpen].Trim();
                var parenVersion = inside.ToString();
                return (ClassifyByName(parenName), parenName, parenVersion);
            }
        }

        // Form B: "name version" — the historical xterm shape used by iTerm2, WezTerm, kitty
        // (pre-0.46), tmux. Split on the first run of whitespace; the first chunk is the name,
        // the rest is the version.
        int sepIndex = payload.IndexOfAny([' ', '\t']);
        string name = sepIndex < 0 ? payload : payload[..sepIndex];
        string? version = sepIndex < 0 ? null : payload[(sepIndex + 1)..].TrimStart();

        var family = ClassifyByName(name);
        return (family, name, version);
    }

    private static TerminalFamily ClassifyByName(string name)
    {
        // Match case-insensitively on substrings — terminals self-report with varied casing.
        if (MatchIdentifier(name, "kitty")) return TerminalFamily.Kitty;
        if (MatchIdentifier(name, "ghostty")) return TerminalFamily.Ghostty;
        if (MatchIdentifier(name, "Rio")) return TerminalFamily.Rio;
        if (MatchIdentifier(name, "iTerm[2]?")) return TerminalFamily.ITerm2;
        if (MatchIdentifier(name, "WezTerm")) return TerminalFamily.WezTerm;
        if (MatchIdentifier(name, "Alacritty")) return TerminalFamily.Alacritty;
        if (MatchIdentifier(name, "Tabby")) return TerminalFamily.Tabby;
        if (MatchIdentifier(name, "foot")) return TerminalFamily.Foot;
        if (MatchIdentifier(name, "Konsole")) return TerminalFamily.Konsole;
        if (MatchIdentifier(name, "VTE\\(7600\\)")) return TerminalFamily.GnomeTerminal;
        if (MatchIdentifier(name, "Terminus.*")) return TerminalFamily.Terminus;
        if (MatchIdentifier(name, "xterm")) return TerminalFamily.Xterm;
        
        if (MatchIdentifier(name, "Apple_Terminal")) return TerminalFamily.AppleTerminal;

        if (MatchIdentifier(name, "WindowsTerminal") ||
            MatchIdentifier(name, "Windows Terminal"))
        {
            return TerminalFamily.WindowsTerminal;
        }

        return TerminalFamily.Unknown;
    }

    private static bool MatchIdentifier(string input, string identifier)
    {
        return Regex.IsMatch(input, @$"\b{identifier}", RegexOptions.IgnoreCase);
    }

    private static TerminalFamily ClassifyFromEnvironment(string? rawTerm, string? rawTermProgram, IEnvironmentReader? environment = null)
    {
        environment ??= IEnvironmentReader.Default;

        if (rawTermProgram is { Length: > 0 } && ClassifyByName(rawTermProgram) is var family and not TerminalFamily.Unknown)
            return family;

        // First chance rawTerm match (ignore common values like xterm, vt100, etc.)
        if (rawTerm is { Length: > 0 })
        {
            // GNU screen tends t
            if (MatchIdentifier(rawTerm, "screen\\[.].*")) return TerminalFamily.GnuScreen;

            if (MatchIdentifier(rawTerm, "kitty")) return TerminalFamily.Kitty;
            if (MatchIdentifier(rawTerm, "ghostty")) return TerminalFamily.Ghostty;
            if (MatchIdentifier(rawTerm, "rio")) return TerminalFamily.Rio;
            if (MatchIdentifier(rawTerm, "iTerm[2]")) return TerminalFamily.Rio;
            if (MatchIdentifier(rawTerm, "alacritty")) return TerminalFamily.Alacritty;
            if (MatchIdentifier(rawTerm, "tabby")) return TerminalFamily.Tabby;
            if (MatchIdentifier(rawTerm, "foot")) return TerminalFamily.Foot;
            if (MatchIdentifier(rawTerm, "tmux")) return TerminalFamily.Tmux;
            if (MatchIdentifier(rawTerm, "VTE\\(7600\\)")) return TerminalFamily.GnomeTerminal;
            if (MatchIdentifier(rawTerm, "Terminus.*")) return TerminalFamily.Terminus;
            if (MatchIdentifier(rawTerm, "screen")) return TerminalFamily.GnuScreen;
            if (MatchIdentifier(rawTerm, "rxvt")) return TerminalFamily.Rxvt;
        }

        if (environment.GetVariable("MOBANOACL") is { Length: > 0 }) return TerminalFamily.MobaXTerm;
        if (environment.GetVariable("ConEmuPID") is { Length: > 0 }) return TerminalFamily.ConEmu;

        if (environment.GetVariable("KITTY_PID") is { Length: > 0 }) return TerminalFamily.Kitty;
        if (environment.GetVariable("WT_SESSION") is { Length: > 0 }) return TerminalFamily.WindowsTerminal;
        if (environment.GetVariable("ITERM_SESSION_ID") is { Length: > 0 }) return TerminalFamily.ITerm2;

        if (environment.GetVariable("ITERM_SESSION_ID") is { Length: > 0 }) return TerminalFamily.ITerm2;

        if (environment.IsWSL()) return TerminalFamily.GenericWsl;
        if (environment.IsCygwin() || environment.IsMinGW()) return TerminalFamily.GenericAnsi;

        // Second chance rawTerm match
        if (rawTerm is { Length: > 0 })
        {
            if (MatchIdentifier(rawTerm, "xterm")) return TerminalFamily.Xterm;
            if (MatchIdentifier(rawTerm, "ansi")) return TerminalFamily.GenericAnsi;
            if (MatchIdentifier(rawTerm, "vt100")) return TerminalFamily.GenericVt;
        }

        // On Windows, if we got here without a more specific identification AND we're attached
        // to a real console (conhost.exe — stdin is a console handle, not a pipe/file), classify
        // as the legacy console host family. This unlocks Win32 Input Mode and other
        // conhost-aware capability gates. Windows Terminal already returned above via
        // WT_SESSION; remaining cases are conhost-backed (cmd.exe, PowerShell launched without
        // Windows Terminal, etc.) or ConPTY-backed hosts that didn't set WT_SESSION.
        if (environment.IsAttachedToWindowsConsole())
            return TerminalFamily.WindowsConsoleHost;

        if (rawTerm is { Length: > 0 })
        {
            // Anything claiming "color" or known VT lineage falls back to GenericVt.
            return TerminalFamily.GenericVt;
        }

        return TerminalFamily.Unknown;
    }

    // ---- Output capability inference ----

    // ReSharper disable once UnusedParameter.Local
    private static InputCapabilities ResolveInputCapabilities(
        TerminalIdentification identification,
        in AppliedOptIns applied)
    {
        var mouse = new MouseCapabilities(
            ButtonPress: applied.MouseButtons |
                         applied.MouseButtonTracking |
                         applied.MouseMotionTracking |
                         applied.ExtendedMouseTracking,
            ButtonRelease: applied.MouseButtons |
                           applied.MouseButtonTracking |
                           applied.MouseMotionTracking |
                           applied.ExtendedMouseTracking,
            Drag: applied.MouseButtonTracking |
                  applied.MouseMotionTracking |
                  applied.ExtendedMouseTracking,
            Motion: applied.MouseMotionTracking |
                    applied.ExtendedMouseTracking,
            Wheel: applied.ExtendedMouseTracking,
            PixelCoordinates: applied.SgrPixelsMouse,
            ExtendedButtonCount: 4);

        bool kittyEnabled = applied.KittyKeyboard;

        var keyboard = new KeyboardCapabilities(
            DistinguishesKeyUpDown: kittyEnabled &&
                                    applied.KittyFlags.HasFlag(KittyKeyboardFlags.ReportEventTypes),
            ReportsRepeats: kittyEnabled &&
                            applied.KittyFlags.HasFlag(KittyKeyboardFlags.ReportEventTypes) &&
                            applied.KittyFlags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes),
            DetailedModifiers: kittyEnabled,
            TextInput: kittyEnabled &&
                       applied.KittyFlags.HasFlag(KittyKeyboardFlags.ReportAssociatedText));

        var protocol = new ProtocolCapabilities(
            BracketedPaste: applied.BracketedPaste,
            FocusEvents: applied.FocusEvents,
            KittyKeyboardProtocol: kittyEnabled,
            Win32InputMode: applied.Win32InputMode)
        {
            KittyKeyboardFlags = kittyEnabled ? applied.KittyFlags : KittyKeyboardFlags.None,
        };

        return new InputCapabilities(
            Mouse: mouse,
            Keyboard: keyboard,
            Pointer: PointerCapabilities.None,
            Protocol: protocol);
    }

    private OutputCapabilities ResolveOutputCapabilities(
        TerminalIdentification identification,
        in AppliedOptIns applied)
    {
        var color = ResolveColor(identification);
        var styling = ResolveStyling(identification);
        var textSizing = ResolveTextSizing(identification);
        var graphics = ResolveGraphics(identification);
        var cursor = ResolveCursor(identification);
        var window = ResolveWindow(identification);

        // Tmux is the only multiplexer we wrap for today — its DCS passthrough envelope has a
        // well-defined wire format. screen has a similar mechanism (DCS through screen's own
        // multiplexer), but the syntax differs; deferred until someone needs it.
        bool multiplexerPassthrough = identification.Family == TerminalFamily.Tmux ||
                                      (identification.InsideMultiplexer &&
                                       identification.Family != TerminalFamily.GnuScreen);

        var protocol = new OutputProtocolCapabilities(
            BracketedPasteEnable: applied.BracketedPaste,
            FocusReportingEnable: applied.FocusEvents,
            SgrMouseEnable: applied.ExtendedMouseTracking,
            MouseButtonsEnable: applied.MouseButtons |
                                applied.MouseButtonTracking |
                                applied.MouseMotionTracking |
                                applied.ExtendedMouseTracking,
            MouseDragEnable: applied.MouseButtonTracking |
                             applied.MouseMotionTracking |
                             applied.ExtendedMouseTracking,
            MouseMotionEnable: applied.MouseMotionTracking |
                               applied.ExtendedMouseTracking,
            KittyKeyboardPush: applied.KittyKeyboard,
            Win32InputModeEnable: applied.Win32InputMode,
            // OSC 52 clipboard write is family-gated rather than probed — no DECRQM-style
            // verification exists for it, and the write sequence itself is fire-and-forget.
            // Read involves a request / response round-trip and isn't implemented yet, so the
            // Read cap stays false.
            ClipboardWrite: TerminalSupportsClipboardWrite(identification.Family),
            ClipboardRead: false,
            SynchronizedOutput: applied.SynchronizedOutput,
            MultiplexerPassthrough: multiplexerPassthrough,
            MouseCursorShape: TerminalSupportsMouseCursorShape(identification.Family));

        return new OutputCapabilities(
            Color: color,
            Styling: styling,
            TextSizing: textSizing,
            Graphics: graphics,
            Cursor: cursor,
            Window: window,
            Protocol: protocol);
    }

    /// <summary>
    /// Determine which Kitty text-sizing features the identified terminal honors. We deliberately
    /// do NOT use the spec's CPR-based runtime probe: it is destructive (advances the cursor,
    /// leaves visible glyphs on screen, requires save/restore-cursor + erase-line cleanup to be
    /// non-jarring) and only meaningful before the application has rendered anything. Family
    /// gating keeps detection silent and matches the pattern used for Kitty keyboard, Win32 input
    /// mode, and synchronized output.
    /// </summary>
    private static TextSizingCapabilities ResolveTextSizing(TerminalIdentification identification)
    {
        // OSC 66 (Width/Scale) is gated on version because terminals that predate text-sizing
        // support don't strip the unknown OSC cleanly — they render the metadata block as
        // literal text, corrupting the display. Kitty added text sizing in 0.40.0 (Oct 2024);
        // older builds in the wild (0.3x.x) still get reported as Family.Kitty but can't
        // actually render OSC 66. Any family without explicit version-gate support stays off.
        // Wide-glyph rendering — the general ability to lay a wide grapheme across two cells
        // correctly — is a broader baseline that most modern terminal emulators get right. We
        // mark the families known to handle it reliably; everything else (including Unknown)
        // stays false, so the renderer falls back to its pre-paint-then-CUP-back defense.
        bool wideGlyphs = identification.Family switch
                          {
                              TerminalFamily.Kitty           => true,
                              TerminalFamily.Ghostty         => true,
                              TerminalFamily.Rio             => true,
                              TerminalFamily.ITerm2          => false,
                              TerminalFamily.WezTerm         => false,
                              TerminalFamily.Alacritty       => false,
                              TerminalFamily.Konsole         => true,
                              TerminalFamily.GnomeTerminal   => false,
                              TerminalFamily.Terminus        => false,
                              TerminalFamily.Foot            => true,
                              TerminalFamily.WindowsTerminal => true,
                              TerminalFamily.GenericWsl      => true,
                              TerminalFamily.MobaXTerm       => false,
                              // Multiplexers pass wide-glyph rendering through to the host terminal; trust them
                              // when the user is running one (the worst case is the host itself is unreliable,
                              // which is the same as not running through a multiplexer at all).
                              TerminalFamily.Tmux      => true,
                              TerminalFamily.GnuScreen => true,
                              _                        => false
                          };

        bool textSizing = identification.Family == TerminalFamily.Kitty &&
                          TextSizingSupportedVersion(identification.Version);

        return textSizing
            ? new TextSizingCapabilities(Width: true, Scale: true, WideGlyphs: wideGlyphs)
            : TextSizingCapabilities.None with { WideGlyphs = wideGlyphs };
    }

    /// <summary>
    /// Kitty 0.40.0 was the first release to ship the OSC 66 text-sizing protocol; earlier
    /// versions silently render the envelope's metadata block as literal text. Returns true
    /// when the parsed version is &gt;= 0.40.0. When the version string can't be parsed (the
    /// terminal didn't respond to XTVERSION, or reported a non-semver shape), returns false —
    /// safer to assume "no" than to corrupt the display.
    /// </summary>
    private static bool TextSizingSupportedVersion(string? version)
    {
        if (!TryParseVersion(version, out var parsed)) return false;
        return parsed.Major > 0 || parsed.Minor >= 40;
    }

    /// <summary>
    /// Parse a dotted-version string into its major/minor/patch components. Accepts up to
    /// three numeric components (extra components are ignored); patch defaults to 0 when only
    /// 'major.minor' is supplied. Pre-release and build suffixes (after <c>-</c> or <c>+</c>)
    /// are stripped from the patch component.
    /// </summary>
    private static bool TryParseVersion(string? version, out (int Major, int Minor, int Patch) result)
    {
        result = default;
        if (string.IsNullOrEmpty(version)) return false;

        var parts = version.Split('.');
        if (parts.Length < 2) return false;

        if (!int.TryParse(parts[0], out int major)) return false;
        if (!int.TryParse(parts[1], out int minor)) return false;

        int patch = 0;
        if (parts.Length >= 3)
        {
            var patchToken = parts[2];
            // Drop pre-release / build suffix: "0.40.0-rc1" → "0".
            int suffix = patchToken.IndexOfAny(['-', '+']);
            if (suffix >= 0) patchToken = patchToken[..suffix];
            int.TryParse(patchToken, out patch);
        }

        result = (major, minor, patch);
        return true;
    }

    private ColorCapabilities ResolveColor(TerminalIdentification identification)
    {
        var depth = _truecolorVerified && identification.Family is not TerminalFamily.AppleTerminal
                        ? ColorDepth.Truecolor
                        : ResolveColorDepth(identification);

        return new ColorCapabilities(
            Depth: depth,
            TruecolorVerified: _truecolorVerified,
            DefaultColorReset: depth >= ColorDepth.Ansi16,
            OscPaletteSet: depth >= ColorDepth.Ansi256 &&
                           identification.Family is not TerminalFamily.AppleTerminal,
            DefaultForeground: _defaultForeground,
            DefaultBackground: _defaultBackground,
            DefaultCursorColor: _defaultCursorColor);
    }

    private ColorDepth ResolveColorDepth(TerminalIdentification identification)
    {
        // NO_COLOR convention: https://no-color.org/
        if (_environment.GetVariable("NO_COLOR") is { Length: > 0 }) return ColorDepth.NoColor;

        if (_environment.GetVariable("COLORTERM") is { Length: > 0 } colorTerm &&
            (MatchIdentifier(colorTerm, "truecolor") ||
             MatchIdentifier(colorTerm, "24bit")))
        {
            return ColorDepth.Truecolor;
        }

        // Known truecolor families.
        if (identification.Family is TerminalFamily.Kitty or
                                     TerminalFamily.Ghostty or
                                     TerminalFamily.ITerm2 or
                                     TerminalFamily.WezTerm or
                                     TerminalFamily.Alacritty or
                                     TerminalFamily.WindowsTerminal or
                                     TerminalFamily.GenericWsl or
                                     TerminalFamily.WindowsConsoleHost or
                                     TerminalFamily.Foot or
                                     TerminalFamily.Konsole or
                                     TerminalFamily.GnomeTerminal or
                                     TerminalFamily.Terminus or
                                     TerminalFamily.ConEmu or
                                     TerminalFamily.MobaXTerm)
        {
            return ColorDepth.Truecolor;
        }

        if (identification.Family == TerminalFamily.AppleTerminal)
            return ColorDepth.Ansi256;

        if (identification.Family is TerminalFamily.GenericAnsi)
            return ColorDepth.Ansi16;

        var term = identification.RawTermEnv;
        if (term is null) return ColorDepth.NoColor;

        if (term.Contains("256color", StringComparison.OrdinalIgnoreCase)) return ColorDepth.Ansi256;
        if (term.Contains("color", StringComparison.OrdinalIgnoreCase)) return ColorDepth.Ansi16;

        return ColorDepth.NoColor;
    }

    private static TextStylingCapabilities ResolveStyling(TerminalIdentification identification)
    {
        // The xterm baseline (italic, single underline, strikethrough) is supported by every
        // family we recognize. Extended styling (curly underline, OSC 8 hyperlinks, colored
        // underline, overline) is more recent.
        bool extended = identification.Family is TerminalFamily.Kitty or
                                                 TerminalFamily.Ghostty or
                                                 TerminalFamily.ITerm2 or
                                                 TerminalFamily.WezTerm or
                                                 TerminalFamily.Alacritty or
                                                 TerminalFamily.WindowsTerminal or
                                                 TerminalFamily.GenericWsl or
                                                 TerminalFamily.Foot or
                                                 TerminalFamily.Konsole;

        return new TextStylingCapabilities(
            Italic: identification.Family != TerminalFamily.Unknown,
            Underline: identification.Family != TerminalFamily.Unknown,
            ExtendedUnderline: extended,
            ColoredUnderline: extended,
            Strikethrough: identification.Family is not (TerminalFamily.Unknown or TerminalFamily.Rxvt),
            Overline: false, // Almost no terminal honors SGR 53.
            Hyperlinks: extended);
    }

    // @formatter:off
    private static GraphicsCapabilities ResolveGraphics(TerminalIdentification identification)
    {
        // Base capabilities from family knowledge — what we know each family ships.
        var fromFamily = identification.Family switch
                         {
                             TerminalFamily.Kitty   => new GraphicsCapabilities(Sixel: false, KittyGraphics: true,  ITerm2InlineImages: false),
                             TerminalFamily.Ghostty => new GraphicsCapabilities(Sixel: false, KittyGraphics: true,  ITerm2InlineImages: false),
                             TerminalFamily.Rio     => new GraphicsCapabilities(Sixel: false, KittyGraphics: true,  ITerm2InlineImages: true),
                             TerminalFamily.ITerm2  => new GraphicsCapabilities(Sixel: false, KittyGraphics: false, ITerm2InlineImages: true),
                             TerminalFamily.WezTerm => new GraphicsCapabilities(Sixel: true,  KittyGraphics: false, ITerm2InlineImages: true),
                             TerminalFamily.Foot    => new GraphicsCapabilities(Sixel: true,  KittyGraphics: false, ITerm2InlineImages: false),
                             TerminalFamily.Mlterm  => new GraphicsCapabilities(Sixel: true,  KittyGraphics: false, ITerm2InlineImages: false),
                             _                      => GraphicsCapabilities.None,
                         };

        // OR in DA1 advertisement: parameter 4 is the spec signal for Sixel. Lets us detect
        // xterm-with-sixel, modern Kitty (0.41+), modern Konsole (24.04+), modern Alacritty
        // forks, etc., without maintaining a perpetually-stale family allow-list.
        if (identification.AdvertisesSixel)
            return fromFamily with { Sixel = true };

        return fromFamily;
    }
    // @formatter:on

    private static CursorCapabilities ResolveCursor(TerminalIdentification identification)
    {
        bool modern = identification.Family is not (TerminalFamily.Unknown or
                                                    TerminalFamily.Rxvt or
                                                    TerminalFamily.Mlterm);

        return new CursorCapabilities(
            // Apple Terminal does not implement DECSCUSR (CSI Ps SP q) — it mis-parses the
            // space-intermediate form and prints the literal 'q' terminator. Exclude it so the
            // renderer doesn't emit cursor-shape sequences there (same rationale as ColorControl).
            ShapeControl: modern && identification.Family is not TerminalFamily.AppleTerminal,
            VisibilityControl: identification.Family != TerminalFamily.Unknown,
            BlinkControl: modern,
            ColorControl: modern && identification.Family is not TerminalFamily.AppleTerminal);
    }

    private WindowCapabilities ResolveWindow(TerminalIdentification identification)
    {
        bool pixelSize = identification.Family is TerminalFamily.Kitty or
                                                  TerminalFamily.Ghostty or
                                                  TerminalFamily.ITerm2 or
                                                  TerminalFamily.WezTerm or
                                                  TerminalFamily.Foot or
                                                  TerminalFamily.Alacritty or
                                                  TerminalFamily.WindowsTerminal or
                                                  TerminalFamily.GenericWsl or
                                                  TerminalFamily.Konsole;

        return new WindowCapabilities(
            TitleSet: identification.Family != TerminalFamily.Unknown,
            IconSet: identification.Family is TerminalFamily.Xterm or TerminalFamily.Rxvt,
            SizeQueryInPixels: pixelSize,
            AlternateScreenBuffer: identification.Family != TerminalFamily.Unknown,
            ScrollRegion: identification.Family != TerminalFamily.Unknown,
            CellPixelWidth: _mode.CellPixelWidth,
            CellPixelHeight: _mode.CellPixelHeight,
            TextAreaColumns: _mode.TextAreaColumns,
            TextAreaRows: _mode.TextAreaRows);
    }

    // ---- Helpers ----

    private static string? AsciiPayloadOrNull(ReadOnlyMemory<byte> payload) =>
        payload.IsEmpty ? null : Encoding.ASCII.GetString(payload.Span);

    // ---- Probe sequence byte-strings ----

    /// <summary><c>CSI &gt; q</c> — XTVERSION request.</summary>
    private static ReadOnlyMemory<byte> XtVersionRequest { get; } = new byte[] { 0x1B, (byte) '[', (byte) '>', (byte) 'q' };

    /// <summary><c>CSI 16 t</c> — request single character-cell size in pixels.</summary>
    private static ReadOnlyMemory<byte> CellSizeRequest { get; } = new byte[] { 0x1B, (byte) '[', (byte) '1', (byte) '6', (byte) 't' };

    /// <summary><c>CSI 18 t</c> — request text area size in characters (rows / columns).</summary>
    private static ReadOnlyMemory<byte> TextAreaSizeRequest { get; } = new byte[] { 0x1B, (byte) '[', (byte) '1', (byte) '8', (byte) 't' };

    /// <summary><c>CSI c</c> — Primary Device Attributes (DA1) request, used as the sentinel.</summary>
    private static ReadOnlyMemory<byte> Da1Request { get; } = new byte[] { 0x1B, (byte) '[', (byte) 'c' };

    // ---- Internal collaborators ----

    private readonly record struct ProbeResponses(
        DeviceResponseEvent? XtVersion,
        DeviceResponseEvent? PrimaryDeviceAttributes);

    /// <summary>
    /// Tracks which opt-ins were actually emitted to the terminal during negotiation. Used
    /// at restore time to send the matching disable sequences in LIFO order.
    /// </summary>
    private struct AppliedOptIns
    {
        public bool MouseButtons;
        public bool ExtendedMouseTracking;
        public bool MouseButtonTracking;
        public bool MouseMotionTracking;
        public bool SgrPixelsMouse;
        public bool FocusEvents;
        public bool BracketedPaste;
        public bool KittyKeyboard;
        public bool Win32InputMode;
        public bool SynchronizedOutput;
        public KittyKeyboardFlags KittyFlags;

        public bool IsEmpty => !MouseButtons &&
                               !ExtendedMouseTracking &&
                               !MouseButtonTracking &&
                               !MouseMotionTracking &&
                               !SgrPixelsMouse &&
                               !FocusEvents &&
                               !BracketedPaste &&
                               !KittyKeyboard &&
                               !Win32InputMode &&
                               !SynchronizedOutput;
    }

    private sealed class ResponseCollector : IInputEventSink
    {
        private readonly List<DeviceResponseEvent> _responses = [];

        /// <summary>
        /// Total number of DA1 (PrimaryDeviceAttributes) responses observed since construction.
        /// Each probe phase increments this once — phase N completes when the count reaches N.
        /// </summary>
        public int SentinelCount { get; private set; }

        public IReadOnlyList<DeviceResponseEvent> Responses => _responses;

        public void OnInputEvent(InputEvent inputEvent)
        {
            if (inputEvent is not DeviceResponseEvent response) return;

            _responses.Add(response);

            if (response.Kind == DeviceResponseKind.PrimaryDeviceAttributes)
                SentinelCount++;
        }

        public DeviceResponseEvent? FindFirst(DeviceResponseKind kind)
        {
            foreach (var response in _responses)
            {
                if (response.Kind == kind)
                    return response;
            }

            return null;
        }
    }
}
