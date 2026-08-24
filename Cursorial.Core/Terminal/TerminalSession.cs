using System.Buffers;
using System.Runtime.InteropServices;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Terminal.Stdio;

using WCV = Cursorial.Terminal.WindowsCapabilityVersions;

namespace Cursorial.Terminal;

/// <summary>
/// A configured terminal session — the orchestrated combination of a negotiator (run once at
/// startup), an <see cref="IAsyncInputDevice"/> over the byte source, and an output sink, all
/// wired against a shared <see cref="VtInputMode"/>. Returned by the
/// <see cref="OpenAsync(IInputByteSource, IOutputByteSink, TerminalSessionOptions?, CancellationToken)"/>
/// and <see cref="OpenAsync(TerminalSessionOptions?, CancellationToken)"/> factories.
/// </summary>
/// <remarks>
/// <para>
/// <b>BYO transport contract.</b> When constructed via the BYO factory, the session does NOT
/// take ownership of the supplied <see cref="IInputByteSource"/> or <see cref="IOutputByteSink"/>.
/// Disposal stops the input pump and reverses every opt-in the negotiator applied, but it does
/// NOT close, complete, or dispose the caller-supplied transports. The caller is responsible
/// for raw-mode handling on stdin and any restoration after the session ends.
/// </para>
/// <para>
/// <b>Happy-path safety net.</b> When constructed via the parameterless overload (which opens
/// stdio transports), the session registers POSIX signal handlers (SIGINT, SIGTERM, SIGHUP,
/// SIGQUIT) and an <see cref="AppDomain.ProcessExit"/> handler. If the process is killed or
/// exits without explicit disposal, these handlers synchronously restore terminal state via
/// <see cref="IStdioTransports.RestoreTerminalState"/> before the process actually goes away —
/// preventing the terminal from being left in raw mode after a Ctrl+C / SIGKILL / unhandled
/// exception. Handlers are unregistered on normal disposal.
/// </para>
/// </remarks>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly IAsyncInputDevice _input;
    private readonly IOutputByteSink _output;
    private readonly IInputByteSource _source;
    private readonly IStdioTransports? _ownedTransports;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];
    private readonly VtInputMode _mode;
    private readonly TerminalSessionOptions _options;

    // Serializes Renegotiate with itself and with DisposeAsync's negotiator-disposal step.
    // EmergencyRestoreAndDispose (signal-handler path) is lock-free by design — it just reads
    // _negotiator and trusts the single-reference write that Renegotiate performs to be atomic.
    private readonly SemaphoreSlim _negotiatorLock = new(1, 1);

    private ITerminalNegotiator _negotiator;
    private TerminalCapabilities _capabilities;
    private EventHandler? _processExitHandler;
    private IResizeMonitor? _resizeMonitor;
    private int _disposed;

    private TerminalSession(TerminalCapabilities capabilities,
                            IInputByteSource source,
                            IAsyncInputDevice input,
                            IOutputByteSink output,
                            ITerminalNegotiator negotiator,
                            VtInputMode mode,
                            TerminalSessionOptions options,
                            IStdioTransports? ownedTransports)
    {
        _capabilities = capabilities;
        _source = source;
        _input = input;
        _output = output;
        _negotiator = negotiator;
        _mode = mode;
        _options = options;
        _ownedTransports = ownedTransports;

        // Only attach safety-net handlers and the resize monitor when we own the transports —
        // i.e., only for the happy-path (parameterless) overload. BYO callers have their own
        // signal-handling strategy and shouldn't be surprised by ours.
        if (_ownedTransports is not null)
        {
            RegisterSafetyHandlers();
            StartResizeMonitor();
        }
    }

    /// <summary>
    /// The realized capabilities returned by the most recent negotiation. Initially set at
    /// session open; replaced atomically by <see cref="RenegotiateAsync"/> if a fresh negotiation
    /// produces a different result.
    /// </summary>
    public TerminalCapabilities Capabilities => Volatile.Read(ref _capabilities);

    /// <summary>The input device — pull-based <see cref="InputEvent"/> stream.</summary>
    public IAsyncInputDevice Input => _input;

    /// <summary>The output sink — bytes written here reach the terminal.</summary>
    public IOutputByteSink Output => _output;

    /// <summary>
    /// Query the terminal's current cell-grid dimensions synchronously on demand, bypassing
    /// the resize-event stream. Returns the size as <c>(Columns, Rows)</c>, or
    /// <see langword="null"/> when the size can't be determined (BYO transport with no
    /// platform resize monitor, the OS query failed, or stdio isn't a real TTY).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On happy-path sessions opened via the parameterless
    /// <see cref="OpenAsync(TerminalSessionOptions?, CancellationToken)">OpenAsync()</see>
    /// overload, the query uses the platform path: <c>stty size</c> on POSIX, the
    /// <c>GetConsoleScreenBufferInfo</c> Win32 API on Windows. Both are cheap (single
    /// subprocess or single API call) and reflect the current size at the moment of the call —
    /// useful for consumers that need the size at construction time before the first
    /// <see cref="Cursorial.Input.Events.ResizeEvent"/> has reached the input stream.
    /// </para>
    /// <para>
    /// On BYO sessions opened via <see cref="OpenAsync(IInputByteSource, IOutputByteSink, TerminalSessionOptions, CancellationToken)"/>,
    /// there is no platform resize monitor — the caller manages their own transports and
    /// resize signaling. This method returns <see langword="null"/>; consumers should query
    /// their own transport layer or send a DSR request themselves.
    /// </para>
    /// <para>
    /// The implementation is synchronous under the hood but exposed as
    /// <see cref="ValueTask{TResult}"/> so a future revision can substitute a wire-level probe
    /// (e.g. <c>CSI 18 t</c>) without changing the signature.
    /// </para>
    /// </remarks>
    public ValueTask<(int Columns, int Rows)?> QueryTerminalSizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var size = _resizeMonitor?.QueryCurrentSize();
        if (size is not null) return ValueTask.FromResult(size);

        // Resize monitor couldn't answer (no host-platform API available — typical for
        // non-console stdout on Windows under MSYS2 / Cygwin / MobaXterm bash, or BYO
        // sessions). Fall back to whatever the negotiator captured via
        // CSI 18 t → CSI 8 ; rows ; cols t at session open. That value is the terminal's
        // own answer; it doesn't update on resize, but it's better than null for callers
        // sizing an initial frame.
        var window = Capabilities.Output.Window;
        if (window is { TextAreaColumns: int cols, TextAreaRows: int rows })
            return ValueTask.FromResult<(int, int)?>((cols, rows));

        return ValueTask.FromResult<(int, int)?>(null);
    }

    /// <summary>
    /// Opens a session over caller-supplied transports. Useful for embedding inside a tool that
    /// already manages the terminal state or for driving the input pipeline from a recorded .
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Caller responsibilities</b> (not handled by this overload):
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Terminal mode.</b> Place the underlying stdin into raw mode (or whatever the source
    /// represents) before calling, and restore it after the session is disposed. This overload
    /// never touches termios / Windows console-mode flags.
    /// </description></item>
    /// <item><description>
    /// <b>Signal handling.</b> Register your own SIGINT / SIGTERM / SIGHUP / SIGQUIT handlers
    /// if you want graceful cleanup on Ctrl+C or a kill signal. The happy-path
    /// <see cref="OpenAsync(TerminalSessionOptions?, CancellationToken)"/> overload registers a
    /// safety net; this overload does NOT, because BYO callers commonly have their own
    /// strategy (and double-registration is loud).
    /// </description></item>
    /// <item><description>
    /// <b>Synchronous mode restore on signal.</b> If you register signal handlers, restore
    /// terminal mode synchronously from the handler before the process exits — async disposal
    /// may not complete in time. Do not rely on <see cref="DisposeAsync"/> alone to clean up
    /// from a signal path.
    /// </description></item>
    /// <item><description>
    /// <b>Disposal scope.</b> <see cref="DisposeAsync"/> stops the input pump and reverses every
    /// opt-in the negotiator applied (writes Kitty pop, mouse-disable, …). It does NOT close,
    /// complete, or dispose the supplied <see cref="IInputByteSource"/> or
    /// <see cref="IOutputByteSink"/>, and it does NOT restore terminal mode — that's still the
    /// caller's job (item 1 above).
    /// </description></item>
    /// </list>
    /// </remarks>
    public static Task<TerminalSession> OpenAsync(
        IInputByteSource source,
        IOutputByteSink sink,
        TerminalSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        return OpenInternalAsync(source, sink, ownedTransports: null, options, cancellationToken);
    }

    /// <summary>
    /// Happy-path overload — opens a session over the process's standard input and output,
    /// taking ownership of terminal-mode state (POSIX termios via stty, Windows console-mode
    /// flags). Disposal restores the prior terminal state. Throws when standard I/O is not
    /// connected to a real terminal (running under a pipe, in CI without a pseudo-tty, etc.) —
    /// use the BYO overload in those cases.
    /// </summary>
    /// <remarks>
    /// Registers signal handlers (SIGINT / SIGTERM / SIGHUP / SIGQUIT) and a
    /// <see cref="AppDomain.ProcessExit"/> handler so terminal state is restored even on
    /// unhandled exit. Handlers are removed on normal disposal.
    /// </remarks>
    public static async Task<TerminalSession> OpenAsync(
        TerminalSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Force System.Console's one-time terminal initialization NOW — before the transports
        // save termios state and before negotiation writes its first byte. On the Unix runtime
        // the process's FIRST Console write (Console.Out/Error) or cursor/window API call runs
        // that init, which emits terminfo smkx (DECCKM + DECKPAM application modes) to the tty
        // and never restores it at exit. Left to chance, that first touch can land AFTER our
        // restore (a post-teardown Console.WriteLine of results is typical), stranding the
        // shell in application cursor-key mode until `reset`. Pinning it here guarantees the
        // ordering: BCL init → our termios save + negotiation → our restore, whose rmkx pair
        // (see VtTerminalNegotiator.RestoreAsync) leaves the terminal clean. WindowWidth is the
        // cheapest deterministic trigger (property reads like IsInputRedirected do NOT init).
        if (!OperatingSystem.IsWindows())
        {
            try { _ = Console.WindowWidth; }
            catch { /* not a console — the transport open below reports it properly */ }
        }

        var transports = StdioTransports.Open();

        try
        {
            return await OpenInternalAsync(
                           transports.Source,
                           transports.Sink,
                           ownedTransports: transports,
                           options,
                           cancellationToken)
                       .ConfigureAwait(false);
        }
        catch
        { 
            // @formatter:off
            
            // OpenInternalAsync handles the negotiator + device cleanup. We're responsible
            // for the transports we just opened.
            try { await transports.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
            
            throw;
            
            // @formatter:on
        }
    }

    private static async Task<TerminalSession> OpenInternalAsync(
        IInputByteSource source,
        IOutputByteSink sink,
        IStdioTransports? ownedTransports,
        TerminalSessionOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new TerminalSessionOptions();

        var mode = new VtInputMode();
        var negotiator = new VtTerminalNegotiator(source, sink, mode);
        VtInputDevice? device = null;

        try
        {
            TerminalCapabilities capabilities;

            if (source is WindowsConsoleInputByteSource { PassThroughVtBytes: false })
            {
                // WindowsTerminal / WindowsConsoleHost only. That translator wraps every
                // conhost INPUT_RECORD — including the bytes the terminal sends back to our
                // probes — in a Win32 Input Mode envelope, which the negotiator's classifier
                // would never recognize as DA1 / XTVERSION / OSC responses. Probing would
                // just block until ProbeTimeout while quietly injecting fake key events for
                // whatever the terminal happened to send back. The substitute capability
                // snapshot mirrors what NegotiateAsync would produce for a known Windows
                // console transport — Win32 Input Mode for input, truecolor on Windows
                // Terminal, fewer colors on classic conhost (detected via WT_SESSION).
                //
                // Third-party emulators atop ConPTY (Alacritty / WezTerm / Ghostty on Windows
                // / MinTTY / …) take the negotiator path even when stdin is a console
                // handle: with PassThroughVtBytes=true the source emits their device
                // responses verbatim, so DA1 / XTVERSION / OSC color queries all work.
                capabilities = BuildWindowsConsoleCapabilities();
            }
            else if (options.CachedCapabilities is { } cached)
            {
                // Capability-cache seed (docs/cli-design.md §6, FW-1): skip the probe rounds
                // entirely and apply the same opt-in enables the full negotiation's opt-in
                // round would emit, recorded so restore emits the matching disables. See
                // TerminalSessionOptions.CachedCapabilities for the caller contract.
                capabilities = await negotiator.ApplyCachedAsync(cached, options.Negotiation, cancellationToken)
                                               .ConfigureAwait(false);
            }
            else
            {
                capabilities = await negotiator.NegotiateAsync(options.Negotiation, cancellationToken)
                                               .ConfigureAwait(false);
            }

            device = new VtInputDevice(
                source,
                capabilities.Input,
                mode,
                escapeAmbiguityTimeout: options.EscapeAmbiguityTimeout);

            return new TerminalSession(capabilities, source, device, sink, negotiator, mode, options, ownedTransports);
        }
        catch
        {
            // @formatter:off

            // Failed somewhere between negotiation and device construction — clean up the
            // negotiator + device. The caller is responsible for the transports they own
            // (BYO) or for transport cleanup happening one level up (parameterless overload).
            if (device is not null)
            {
                try { await device.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow during failed-init cleanup */ }
            }

            try { await negotiator.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow during failed-init cleanup */ }

            throw;

            // @formatter:on
        }
    }

    /// <summary>
    /// Build a capability snapshot for the Windows-console transport without running the
    /// negotiator's probe handshake. The input source already produces Win32-Input-Mode key
    /// sequences, SGR mouse events, and focus events locally from conhost INPUT_RECORDs, so
    /// the input model is fully usable regardless of family. Output color depth is inferred
    /// from the WT_SESSION environment variable (set by Windows Terminal) — present means
    /// truecolor, absent means we conservatively fall back to a 256-color ceiling that even
    /// classic conhost honors.
    /// </summary>
    private static TerminalCapabilities BuildWindowsConsoleCapabilities()
    {
        var environment = EnvironmentReader.Instance;
        var identification = VtTerminalNegotiator.ResolveIdentification(environment);
        var isWindowsTerminal = identification.Family is TerminalFamily.WindowsTerminal;
        var osVersion = Environment.OSVersion.Version;

        var family = isWindowsTerminal
                         ? TerminalFamily.WindowsTerminal
                         : TerminalFamily.WindowsConsoleHost;

        identification = identification with { Name = family.ToString() };

        // Input — describes what our source actually delivers, not what the negotiator would
        // have probed. The translation layer in WindowsConsoleInputByteSource emits Win32
        // Input Mode for keys (full up/down + repeats + modifiers), SGR mouse for clicks /
        // drags / wheel, and CSI I/O for focus changes; KittyKeyboard isn't on the Windows
        // console path, so leave that bit false.
        var input = new InputCapabilities(
            Mouse: new MouseCapabilities(
                ButtonPress: true,
                ButtonRelease: true,
                Drag: true,
                Motion: osVersion >= WCV.SgrMouseConsoleHost,
                Wheel: osVersion >= WCV.SgrMouseConsoleHost,
                PixelCoordinates: false,
                ExtendedButtonCount: 2),
            Keyboard: new KeyboardCapabilities(
                DistinguishesKeyUpDown: true,
                ReportsRepeats: true,
                DetailedModifiers: true,
                TextInput: true),
            Pointer: PointerCapabilities.None,
            Protocol: new ProtocolCapabilities(
                BracketedPaste: false,
                FocusEvents: true,
                KittyKeyboardProtocol: false,
                Win32InputMode: true));

        // Output — conservative defaults. Truecolor on Windows Terminal (confirmed by
        // empirical use of 24-bit SGR in modern WT builds), Ansi256 elsewhere. The renderer's
        // StyleQuantizer downsamples gracefully if a consumer asks for colors outside the
        // depth the snapshot reports.

        var color = isWindowsTerminal
                        ? new ColorCapabilities(
                            Depth: ColorDepth.Truecolor,
                            TruecolorVerified: false,
                            DefaultColorReset: true,
                            OscPaletteSet: true)
                        : new ColorCapabilities(
                            Depth: osVersion >= WCV.TrueColorConsoleHost
                                       ? ColorDepth.Truecolor
                                       : osVersion >= WCV.BasicAnsiConsoleHost
                                           ? ColorDepth.Ansi16
                                           : ColorDepth.NoColor,
                            TruecolorVerified: false,
                            DefaultColorReset: true,
                            OscPaletteSet: false);

        var output = OutputCapabilities.None with { Color = color };

        if (isWindowsTerminal) output = output with { Graphics = output.Graphics with { Sixel = true } };

        return new TerminalCapabilities(
            Terminal: identification,
            Input: input,
            Output: output);
    }
    
    /// <summary>
    /// Run the negotiation handshake again — re-probe the terminal, re-emit opt-in enable
    /// sequences, and atomically replace the active negotiator. Intended for recovery when the
    /// host process (a child program, a custom prompt, a stray <c>tput reset</c>) has clobbered
    /// the opt-in state we applied at session open. The session's
    /// <see cref="Capabilities"/> snapshot is updated atomically to reflect the new realization.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe / opt-in phase.</param>
    /// <remarks>
    /// <para>
    /// <b>Concurrency contract.</b> Serialized with itself and with the negotiator-disposal step
    /// of <see cref="DisposeAsync"/> via an internal lock. A signal-handler-driven
    /// <c>EmergencyRestoreAndDispose</c> is lock-free and reads <see cref="_negotiator"/>
    /// directly — see "EmergencyRestore window" below for the resulting trade-off.
    /// </para>
    /// <para>
    /// <b>Input quiescence.</b> The device's background pump is parked for the duration of the
    /// handshake so the new negotiator can take exclusive ownership of the input byte source's
    /// <see cref="System.IO.Pipelines.PipeReader"/>. The pump's classifier is reset to ground
    /// state at park time, so any post-resume bytes are parsed in a clean context. The source's
    /// own pump (the POSIX poll loop) stays running — probe responses need to flow in.
    /// </para>
    /// <para>
    /// <b>User input during the window.</b> Bytes the user types between
    /// <c>PausePumpAsync</c> and <c>ResumePump</c> are consumed by the new negotiator's
    /// ephemeral classifier instead of the device's, which means any non-probe-response keystrokes
    /// arriving in that window are silently dropped. The window is bounded by the probe timeout
    /// (default ~500&#x202f;ms). Don't call <see cref="RenegotiateAsync"/> while the user is actively
    /// interacting.
    /// </para>
    /// <para>
    /// <b>EmergencyRestore window.</b> Between <c>new.NegotiateAsync</c> writing opt-in enables
    /// to the terminal and the atomic swap of <see cref="_negotiator"/>, a signal-driven
    /// <c>EmergencyRestoreAndDispose</c> would observe the OLD negotiator and emit its tracked
    /// disables. If the new negotiation enabled features the old one didn't (because the caller
    /// passed different options), those extras would linger on the terminal after the emergency
    /// exit. The default behavior — reusing the original options — avoids this entirely. Callers
    /// passing custom options accept the (small) risk.
    /// </para>
    /// <para>
    /// <b>Failure path.</b> If the new probe times out, the transport errors, or the host
    /// process clobbers things mid-handshake, the new negotiator is disposed (rolling back any
    /// opt-ins it managed to apply) and the old negotiator stays in place. The session is
    /// indistinguishable from one where <see cref="RenegotiateAsync"/> was never called.
    /// </para>
    /// </remarks>
    public async ValueTask RenegotiateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Serialize with concurrent Renegotiate and with DisposeAsync's negotiator-disposal step.
        await _negotiatorLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-check after acquiring the lock — DisposeAsync may have run while we waited.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // Park the device pump (NOT the source pump). The new negotiator needs exclusive
            // ownership of the source's PipeReader; the source pump must keep running so probe
            // response bytes flow into the pipe.
            await PausePumpForRenegotiationAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Build a fresh negotiator over the SAME source / sink / mode. The mode is a
                // shared mutable bag that both the negotiator and the device's interpreter read
                // — updates propagate to ongoing decoding once the device pump resumes.
                // Deliberately a FULL re-probe even when the session was opened from a cached
                // snapshot (TerminalSessionOptions.CachedCapabilities): renegotiation is a
                // recovery path, and recovering from clobbered state with a cache replay would
                // just replay whatever assumption already failed.
                var newNegotiator = new VtTerminalNegotiator(_source, _output, _mode);
                TerminalCapabilities newCapabilities;
                try
                {
                    newCapabilities = await newNegotiator.NegotiateAsync(_options.Negotiation, cancellationToken)
                                                         .ConfigureAwait(false);
                }
                catch
                {
                    // Roll back any opt-ins the failed negotiator managed to write. The old
                    // negotiator and its tracked state are still in place; the session is
                    // unchanged from the caller's perspective.
                    try { await newNegotiator.DisposeAsync().ConfigureAwait(false); }
                    catch { /* best-effort */ }
                    throw;
                }

                // Atomic swap. After this line, EmergencyRestoreAndDispose sees the new
                // negotiator and emits the new disables — fully covering the freshly-enabled
                // opt-ins. Volatile.Write to _capabilities pairs with the Volatile.Read in the
                // public Capabilities getter so external observers see a consistent snapshot.
                var oldNegotiator = Interlocked.Exchange(ref _negotiator, newNegotiator);
                Volatile.Write(ref _capabilities, newCapabilities);

                // The Kitty keyboard opt-in is STACK-shaped (push/pop), not an idempotent DECSET
                // toggle: the old negotiator's push and the new negotiation's push are now BOTH
                // on the active screen's stack, and the neutralization below discards the old
                // pop with the rest of the old disables. Emit that one pop here — AFTER the new
                // enables, so key-up/repeat reporting never lapses — or every renegotiation
                // strands a stack entry that outlives the session (flags stay active at the
                // shell until `reset`). Read the flag before neutralization zeroes it.
                if (oldNegotiator is VtTerminalNegotiator { AppliedKittyKeyboard: true })
                {
                    var span = _output.Writer.GetSpan(VtInputSequences.OptInSequences.PopKittyKeyboard.Length);
                    VtInputSequences.OptInSequences.PopKittyKeyboard.CopyTo(span);
                    _output.Writer.Advance(VtInputSequences.OptInSequences.PopKittyKeyboard.Length);
                    await _output.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Neutralize the old negotiator: capture its disables and discard them. This
                // marks old._restored=1 and clears old._applied, so its eventual DisposeAsync is
                // a no-op, and EmergencyRestore (if it somehow still saw the old reference via
                // a torn read or in-flight handler) would emit nothing. The terminal stays in
                // the state the new negotiator just established.
                _ = oldNegotiator.BuildRestoreSequence();

                try { await oldNegotiator.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort — old is neutralized either way */ }
            }
            finally
            {
                // ALWAYS resume the device pump — even on failure — so the consumer's input
                // stream comes back to life.
                if (_input is VtInputDevice device) device.ResumePump();
            }
        }
        finally
        {
            _negotiatorLock.Release();
        }
    }

    /// <summary>
    /// Re-applies the negotiated screen-local opt-ins (notably the Kitty keyboard flag push, whose
    /// stack is per-screen-buffer) on whatever screen is currently active, without changing the
    /// negotiator's restore accounting. A consumer that switched to the alternate screen AFTER
    /// <see cref="OpenAsync(TerminalSessionOptions?, CancellationToken)">OpenAsync</see> negotiated on
    /// the main screen calls this once, after the switch, so key-up / repeat reporting actually
    /// engages on the alt screen. The screen-independent global modes (mouse / focus / paste / Win32)
    /// are re-issued idempotently. Serialized against <see cref="RenegotiateAsync"/> so the two never
    /// write the output sink concurrently.
    /// </summary>
    public async ValueTask ReapplyScreenLocalOptInsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _negotiatorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _negotiator.ReapplyScreenLocalOptInsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _negotiatorLock.Release();
        }
    }

    // Alt-screen scope depth. Ref-counted so nested pushes enter/push once and leave/pop once. The alt
    // buffer + the per-screen-buffer Kitty keyboard push are restorable terminal state the session owns,
    // so EmergencyRestoreAndDispose can close an open scope before BuildRestoreSequence.
    private int _altScreenDepth;

    /// <summary>
    /// Enters the alternate screen buffer (DECSET 1049) and re-applies the negotiated screen-local
    /// opt-ins (the per-screen-buffer Kitty keyboard push) onto it, returning a scope that pops those
    /// opt-ins and leaves the alt buffer on dispose — pop BEFORE leave, so the push never strands on
    /// the alt buffer's Kitty stack for the next program (e.g. <c>less</c>) that uses the alt screen.
    /// Ref-counted; nested pushes enter/push once. <see cref="EmergencyRestoreAndDispose"/> closes an
    /// open scope on a signal kill too.
    /// </summary>
    public async ValueTask<IAsyncDisposable> PushAltScreenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _negotiatorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (++_altScreenDepth == 1)
            {
                // Enter the alt buffer, THEN push the screen-local opt-ins so the Kitty push lands on
                // the now-active alt stack. One ordered write — the re-apply flushes both.
                ScreenWriter.WriteEnterAlternateScreen(_output.Writer);
                await _negotiator.ReapplyScreenLocalOptInsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _negotiatorLock.Release();
        }

        return new AltScreenScope(this);
    }

    private async ValueTask ReleaseAltScreenAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _negotiatorLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_altScreenDepth == 0 || --_altScreenDepth != 0)
                return;

            WriteAltScreenReleaseBytes(_output.Writer);
            await _output.Writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — a half-left alt screen is worse than throwing out of teardown.
        }
        finally
        {
            _negotiatorLock.Release();
        }
    }

    /// <summary>
    /// The bytes that undo <see cref="PushAltScreenAsync"/>: pop the screen-local Kitty push (if one
    /// was applied) WHILE STILL on the alt buffer, then leave the alt buffer. Shared by the async
    /// release and the synchronous emergency path so the pop-before-leave order is defined once.
    /// </summary>
    private void WriteAltScreenReleaseBytes(IBufferWriter<byte> writer)
    {
        if (Capabilities.Output.Protocol.KittyKeyboardPush)
            writer.Write(VtInputSequences.OptInSequences.PopKittyKeyboard);

        ScreenWriter.WriteLeaveAlternateScreen(writer);
    }

    private sealed class AltScreenScope(TerminalSession session) : IAsyncDisposable
    {
        private TerminalSession? _session = session;

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _session, null);
            return owner is null ? ValueTask.CompletedTask : owner.ReleaseAltScreenAsync();
        }
    }

    private Task PausePumpForRenegotiationAsync(CancellationToken cancellationToken)
    {
        // VtInputDevice exposes the pause primitive internally; route through a helper so the
        // Renegotiate body doesn't need to type-check / cast every time we touch the device.
        if (_input is VtInputDevice device)
            return device.PausePumpAsync(cancellationToken);

        // Other IAsyncInputDevice implementations don't necessarily have a pump to pause — the
        // session was constructed BYO with a different device type. Renegotiate is undefined in
        // that case for now; the caller can still drive their own equivalent. Return completed
        // so the rest of the flow proceeds (the negotiator just reads from the same source the
        // caller wired up; if their device contends on it, that's their concern).
        return Task.CompletedTask;
    }

    /// <summary>
    /// Quiesce the session's I/O machinery so an external consumer — a child process, a
    /// synchronous terminal operation, a custom raw-mode prompt — can take exclusive ownership
    /// of the underlying TTY without racing the framework's pumps. The returned handle resumes
    /// the pumps on disposal; use it with <c>await using</c> to guarantee resume on any exit
    /// path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After this method returns:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The input byte source's pump is parked entirely in user space — on POSIX, no
    /// <c>read(2)</c> or <c>poll(2)</c> is left blocked in the kernel. Bytes the user types
    /// accumulate in the kernel TTY buffer until resume; nothing is lost.
    /// </description></item>
    /// <item><description>
    /// The output sink's <see cref="System.IO.Pipelines.PipeWriter"/> has been flushed —
    /// every byte the session has written so far has reached the underlying transport.
    /// </description></item>
    /// <item><description>
    /// Bytes already in the source's input pipe (read from the transport before pause)
    /// remain in the pipe and are visible to consumers iterating <see cref="Input"/>. Pause
    /// does not discard them.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Caller contract.</b> Between this call returning and the handle being disposed, the
    /// caller must refrain from writing to <see cref="Output"/>; the framework does not enforce
    /// quiescence on writers it doesn't own. Concurrent calls to <see cref="PauseIOAsync"/>
    /// from multiple parts of the application compose via reference counting inside the
    /// pausable source itself — the pump only resumes once every handle has been disposed.
    /// </para>
    /// <para>
    /// <b>Platform support.</b> Full pause semantics on POSIX (the
    /// <see cref="IPausableInputByteSource"/> path). On Windows — and on any
    /// <see cref="IInputByteSource"/> that doesn't implement
    /// <see cref="IPausableInputByteSource"/> — this method flushes output and returns, but
    /// the input pump may still read from its transport until its next blocked syscall
    /// returns. Callers requiring strict input quiescence on Windows should avoid initiating
    /// their own console reads until after resume.
    /// </para>
    /// </remarks>
    public async Task<IAsyncDisposable> PauseIOAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IAsyncDisposable? sourceHandle = null;
        if (_source is IPausableInputByteSource pausable)
            sourceHandle = await pausable.PauseAsync(cancellationToken).ConfigureAwait(false);

        // Flush output AFTER pausing input so any input-driven write the caller performed
        // (e.g., a response to a query) is fully on the wire before we hand off the TTY.
        // FlushAsync on a PipeWriter returns once buffered bytes have been pushed through
        // to the underlying stream.
        try
        {
            await _output.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Flush is best-effort during pause — the sink may already be closed or broken.
            // Don't poison the pause path; the caller can still proceed, and the resume path
            // remains reachable via the source handle below.
        }

        return new SessionPauseHandle(sourceHandle);
    }

    private sealed class SessionPauseHandle : IAsyncDisposable
    {
        private IAsyncDisposable? _sourceHandle;

        public SessionPauseHandle(IAsyncDisposable? sourceHandle) => _sourceHandle = sourceHandle;

        public ValueTask DisposeAsync()
        {
            // Interlocked.Exchange enforces single-effect dispose so repeated `await using`
            // unwinds don't double-decrement the source's pause refcount.
            var handle = Interlocked.Exchange(ref _sourceHandle, null);
            return handle?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // @formatter:off
        
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Unregister safety-net handlers first. Doing so early prevents a signal-handler
        // re-entry during the rest of disposal from running through the dispose path again.
        UnregisterSafetyHandlers();

        // Stop the resize monitor before the input pump so we don't inject a final stray
        // ResizeEvent into a channel that's about to complete.
        try { _resizeMonitor?.Dispose(); }
        catch { /* best-effort */ }

        _resizeMonitor = null;

        try { await DisposeNegotiatorAsync(); }
        catch { /* best-effort */ }

        // Give the local round-trip (we write disable → terminal processes → terminal emits
        // any final reports → poll/read picks them up) a moment to complete. 50 ms is the
        // xterm bare-ESC ambiguity convention and is generous for this round-trip on any
        // terminal in the support matrix.
        try { await Task.Delay(50).ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Now stop the input pump.
        try { await _input.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Only dispose transports the session itself created (parameterless overload). BYO
        // transports are caller-owned and stay open.
        if (_ownedTransports is not null)
        {
            try { await _ownedTransports.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        // @formatter:on
    }

    private async ValueTask DisposeNegotiatorAsync()
    {
        // Take the negotiator lock so we don't race a concurrent Renegotiate's swap. The lock
        // is held only across the negotiator's own DisposeAsync — DisposeNegotiatorAsync is just
        // one phase of session disposal.
        await _negotiatorLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Restore opt-ins FIRST, while the input pump is still running. The terminal may
            // emit trailing reports in response to the disable sequences — most notably a Kitty
            // key-release report for whatever key the user pressed to exit, plus any final
            // mouse/focus reports. With the pump still draining fd 0, those bytes are consumed
            // into the device's internal pipe (and dropped on pipe completion) rather than
            // piling up in the TTY input queue for the next cooked-mode `Console.ReadLine` to
            // splice into the user's next command.
            try { await _negotiator.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        finally
        {
            _negotiatorLock.Release();
        }
    }

    // ---- Signal-handler safety net ----

    private void RegisterSafetyHandlers()
    {
        // Each registration may throw PlatformNotSupportedException on Windows (only SIGINT
        // and SIGQUIT are mapped there). Catch per-signal so the rest still register.
        TryRegisterSignal(PosixSignal.SIGINT);
        TryRegisterSignal(PosixSignal.SIGTERM);
        TryRegisterSignal(PosixSignal.SIGHUP);
        TryRegisterSignal(PosixSignal.SIGQUIT);

        _processExitHandler = HandleProcessExit;
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    private void StartResizeMonitor()
    {
        // POSIX delivers terminal resizes via SIGWINCH; Windows reports them as console-buffer-
        // size events. The factory picks the right implementation for the current OS (or
        // returns null on platforms we don't support, which silently turns resize delivery off).
        // Either way, each detected resize is fed back into the input device's event stream so
        // consumers see them interleaved with keyboard / mouse input.
        if (_input is not VtInputDevice device) return;

        try
        {
            _resizeMonitor = ResizeMonitor.Create(
                device.EnqueueExternalEvent,
                sink: _output,
                device: device);
            _resizeMonitor?.Start();
        }
        catch
        {
            // Resize delivery is best-effort — session opening must not fail because we
            // couldn't subscribe to SIGWINCH (sandboxes that block signal registration) or
            // couldn't query the Windows console (no console attached).
            _resizeMonitor?.Dispose();
            _resizeMonitor = null;
        }
    }

    private void TryRegisterSignal(PosixSignal signal)
    {
        try
        {
            _signalRegistrations.Add(PosixSignalRegistration.Create(signal, HandleSignal));
        }
        catch (PlatformNotSupportedException)
        {
            // Signal is not supported on this OS — ignore.
        }
        catch
        {
            // Defensive: never let signal registration break session opening.
        }
    }

    private void UnregisterSafetyHandlers()
    {
        // @formatter:off
        if (_processExitHandler is not null)
        {
            try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; }
            catch { /* best-effort */ }

            _processExitHandler = null;
        }

        foreach (var registration in _signalRegistrations)
        {
            try { registration.Dispose(); }
            catch { /* best-effort */ }
        }

        _signalRegistrations.Clear();
        // @formatter:on
    }

    private void HandleSignal(PosixSignalContext context)
    {
        // Suppress the default action — we'll exit ourselves after cleanup, so terminal state
        // is restored deterministically before the process goes away.
        context.Cancel = true;

        EmergencyRestoreAndDispose();

        // Standard POSIX convention: signal exit code is 128 + signal number.
        Environment.Exit(128 + Math.Abs((int) context.Signal));
    }

    private void HandleProcessExit(object? sender, EventArgs e)
    {
        // ProcessExit is triggered by normal exit paths (return from Main, Environment.Exit
        // from elsewhere). Limited time budget here — restore terminal state synchronously
        // and try best-effort disposal of the rest.
        EmergencyRestoreAndDispose();
    }

    private void EmergencyRestoreAndDispose()
    {
        // @formatter:off

        // Three-phase shutdown when the process is going down. Ordering matters:
        //   Phase 1 — emit VT opt-in disables synchronously WHILE raw mode is still active, via
        //   a direct write(2)/WriteFile syscall that bypasses the async PipeWriter chain. Doing
        //   this before termios restore means the disable bytes don't echo through cooked mode,
        //   and going through a direct syscall means the write can't hang on a stuck async
        //   pipeline. If the negotiator never ran or already restored, BuildRestoreSequence
        //   returns an empty buffer and WriteBytesSync is a no-op.
        if (_ownedTransports is not null)
        {
            try
            {
                var restore = new ArrayBufferWriter<byte>();

                // Close an open alt-screen scope FIRST — pop the alt-buffer Kitty push, then leave the alt
                // buffer — so the following pops/re-enables land on the MAIN screen the shell inherits.
                if (Volatile.Read(ref _altScreenDepth) > 0)
                {
                    Volatile.Write(ref _altScreenDepth, 0);
                    WriteAltScreenReleaseBytes(restore);
                }

                // UI-level restore the frame-loop teardown normally emits but the signal path otherwise
                // skips: show the cursor, reset SGR, reset the cursor color (the app sets OSC 12 for the
                // theme accent), re-enable autowrap. Without it a killed TUI leaves the shell in the raw
                // state it ran in — hidden cursor, app-colored cursor, wrapping off. Each is a
                // restore-to-default that's safe/idempotent even for a BYO session; a terminal lacking a
                // given feature ignores its sequence.
                CursorWriter.WriteShow(restore);
                SgrEncoder.WriteReset(restore);
                PaletteWriter.WriteResetCursor(restore);
                ScreenWriter.WriteEnableAutowrap(restore);
                _ownedTransports.WriteBytesSync(restore.WrittenSpan);

                _ownedTransports.WriteBytesSync(_negotiator.BuildRestoreSequence().Span);
            }
            catch { /* best-effort */ }
        }

        // Phase 2 — synchronously restore termios / Windows console mode so the user's shell
        // isn't left in raw mode. Idempotent.
        if (_ownedTransports is not null)
        {
            try { _ownedTransports.RestoreTerminalState(); }
            catch { /* best-effort */ }
        }

        // Phase 3 — best-effort within a bounded budget: tear down the input pump and close
        // the byte streams. Capped at 2 s so the process can still exit when async cleanup
        // gets stuck. The critical state — opt-in disables and termios — was already handled
        // synchronously above, so timing out here is non-destructive.
        try { DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort */ }

        // @formatter:on
    }
}

internal static class WindowsCapabilityVersions
{
    public static readonly Version TrueColorConsoleHost = new(10, 0, 14393, 0);
    public static readonly Version BasicAnsiConsoleHost = new(10, 0, 10240, 0);
    public static readonly Version PseudoConsoleSupport = new(10, 0, 17763, 0);
    public static readonly Version SgrMouseConsoleHost = PseudoConsoleSupport;
}