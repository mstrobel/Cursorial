using System.IO.Pipelines;
using Cursorial.Core.Input;
using Cursorial.Core.Input.Parsing;
using Cursorial.Core.Output;

namespace Cursorial.Core.Terminal;

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
    private readonly IInputByteSource _source;
    private readonly IOutputByteSink _sink;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;
    private readonly IEnvironmentReader _environment;

    // Lifecycle gates use Interlocked to stay safe when callers drive the negotiator outside
    // of a TerminalSession (which already serializes against itself). 0 = open, 1 = transitioned.
    private int _negotiated;
    private int _disposed;
    private int _restored;
    private AppliedOptIns _applied;

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

        var responses = await ProbeIdentificationAsync(options, cancellationToken).ConfigureAwait(false);
        var identification = ResolveIdentification(responses);

        if (options.OptIns == OptInPolicy.Allowed)
        {
            await ApplyOptInsAsync(options, identification, cancellationToken).ConfigureAwait(false);
            ApplyToInputMode(_applied);
        }

        // _negotiated is now durably 1 even if anything above threw — restore still runs.
        var inputCapabilities = ResolveInputCapabilities(identification, _applied);
        var outputCapabilities = ResolveOutputCapabilities(identification, _applied);

        return new TerminalCapabilities(
            Terminal: identification,
            Input: inputCapabilities,
            Output: outputCapabilities);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent and best-effort: a second call after a successful restore is a no-op,
        // and transport failures are swallowed so disposal stays reliable.
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;

        if (Volatile.Read(ref _negotiated) == 0 || _applied.IsEmpty) return;

        try
        {
            // LIFO order — opt-ins came on as Mouse, Focus, Paste, Kitty, Win32, Sync;
            // turn them off in the reverse order.
            if (_applied.SynchronizedOutput) QueueWrite(VtInputSequences.OptInSequences.DisableSynchronizedOutput);
            if (_applied.Win32InputMode)     QueueWrite(VtInputSequences.OptInSequences.DisableWin32InputMode);
            if (_applied.KittyKeyboard)      QueueWrite(VtInputSequences.OptInSequences.PopKittyKeyboard);
            if (_applied.BracketedPaste)     QueueWrite(VtInputSequences.OptInSequences.DisableBracketedPaste);
            if (_applied.FocusEvents)        QueueWrite(VtInputSequences.OptInSequences.DisableFocusEvents);
            if (_applied.AnyEventMouse)      QueueWrite(VtInputSequences.OptInSequences.DisableAnyEventMouse);
            if (_applied.MouseTracking)      QueueWrite(VtInputSequences.OptInSequences.DisableButtonEventMouse);
            if (_applied.MouseTracking)      QueueWrite(VtInputSequences.OptInSequences.DisableSgrMouse);

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            await RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Restore is best-effort. Failures here are swallowed so disposal is reliable
            // even when the underlying transport has already gone away.
        }
    }

    // ---- Opt-in application ----

    private async ValueTask ApplyOptInsAsync(
        NegotiationOptions options,
        TerminalIdentification identification,
        CancellationToken cancellationToken)
    {
        var applied = default(AppliedOptIns);

        // Mouse: SGR encoding (1006) + button-event tracking (1002) is the standard combo
        // for press / release / drag on every modern terminal. Any-event tracking (1003) is
        // additive on top.
        if (options.EnableMouseTracking)
        {
            QueueWrite(VtInputSequences.OptInSequences.EnableSgrMouse);
            QueueWrite(VtInputSequences.OptInSequences.EnableButtonEventMouse);
            applied.MouseTracking = true;

            if (options.EnableAnyEventMouse)
            {
                QueueWrite(VtInputSequences.OptInSequences.EnableAnyEventMouse);
                applied.AnyEventMouse = true;
            }
        }

        if (options.EnableFocusEvents)
        {
            QueueWrite(VtInputSequences.OptInSequences.EnableFocusEvents);
            applied.FocusEvents = true;
        }

        if (options.EnableBracketedPaste)
        {
            QueueWrite(VtInputSequences.OptInSequences.EnableBracketedPaste);
            applied.BracketedPaste = true;
        }

        // Kitty keyboard: gate on family because pushing on a non-Kitty terminal is silently
        // ignored, which would cause us to wrongly report the protocol as enabled.
        if (options.EnableKittyKeyboard
            && TerminalSupportsKittyKeyboard(identification.Family)
            && options.KittyKeyboardFlags != KittyKeyboardFlags.None)
        {
            QueueKittyPush(options.KittyKeyboardFlags);
            applied.KittyKeyboard = true;
            applied.KittyFlags = options.KittyKeyboardFlags;
        }

        // Win32 input mode: only meaningful on the Windows console host or Windows Terminal.
        if (options.EnableWin32InputMode && TerminalSupportsWin32InputMode(identification.Family))
        {
            QueueWrite(VtInputSequences.OptInSequences.EnableWin32InputMode);
            applied.Win32InputMode = true;
        }

        // Synchronized output: gate on family — older terminals interpret unknown DECSET
        // numbers as set-private-mode no-ops, but the resulting SynchronizedOutput=true
        // capability claim would be a lie.
        if (options.EnableSynchronizedOutput && TerminalSupportsSynchronizedOutput(identification.Family))
        {
            QueueWrite(VtInputSequences.OptInSequences.EnableSynchronizedOutput);
            applied.SynchronizedOutput = true;
        }

        if (!applied.IsEmpty)
        {
            await _sink.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _applied = applied;
    }

    private void QueueKittyPush(KittyKeyboardFlags flags)
    {
        // CSI > <flags> u — flags as decimal ASCII. Bounded small string; allocate once.
        var ascii = ((uint)flags).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = VtInputSequences.OptInSequences.KittyPushPrefix;

        int total = prefix.Length + ascii.Length + 1;
        var span = _sink.Writer.GetSpan(total);
        prefix.CopyTo(span);
        System.Text.Encoding.ASCII.GetBytes(ascii, span[prefix.Length..]);
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
        if (applied.MouseTracking) _mode.MouseEncoding = MouseEncoding.Sgr;
        if (applied.FocusEvents) _mode.FocusReportingEnabled = true;
        if (applied.BracketedPaste) _mode.BracketedPasteEnabled = true;
        if (applied.KittyKeyboard) _mode.KittyKeyboard = applied.KittyFlags;
        if (applied.Win32InputMode) _mode.Win32InputModeEnabled = true;
    }

    private void ResetAppliedToReflectRestore()
    {
        // Reflect the restored state on the shared mode bag. The interpreter reads these,
        // so leaving them set after restore would mis-report capabilities.
        if (_applied.MouseTracking) _mode.MouseEncoding = MouseEncoding.None;
        if (_applied.FocusEvents) _mode.FocusReportingEnabled = false;
        if (_applied.BracketedPaste) _mode.BracketedPasteEnabled = false;
        if (_applied.KittyKeyboard) _mode.KittyKeyboard = KittyKeyboardFlags.None;
        if (_applied.Win32InputMode) _mode.Win32InputModeEnabled = false;
        _applied = default;
    }

    private static bool TerminalSupportsKittyKeyboard(TerminalFamily family) =>
        family is TerminalFamily.Kitty
            or TerminalFamily.WezTerm
            or TerminalFamily.Konsole
            or TerminalFamily.Foot
            or TerminalFamily.Iterm2; // Partial support since iTerm2 3.5.

    private static bool TerminalSupportsWin32InputMode(TerminalFamily family) =>
        family is TerminalFamily.WindowsTerminal
            or TerminalFamily.WindowsConsoleHost;

    private static bool TerminalSupportsSynchronizedOutput(TerminalFamily family) =>
        family is TerminalFamily.Kitty
            or TerminalFamily.Iterm2
            or TerminalFamily.WezTerm
            or TerminalFamily.Alacritty
            or TerminalFamily.WindowsTerminal
            or TerminalFamily.Konsole
            or TerminalFamily.Foot;

    // ---- Probe orchestration ----

    private async Task<ProbeResponses> ProbeIdentificationAsync(
        NegotiationOptions options,
        CancellationToken cancellationToken)
    {
        // Send XTVERSION then DA1 (the sentinel). We wait for DA1's response; whatever
        // arrived before it is taken as the response set.
        await WriteAsync(XtVersionRequest, cancellationToken).ConfigureAwait(false);
        await WriteAsync(Da1Request, cancellationToken).ConfigureAwait(false);

        var collector = new ResponseCollector();
        var classifier = new VtSequenceClassifier();
        var interpreter = new VtInputInterpreter(_mode, collector, _time);

        await DrainResponsesUntilSentinelAsync(
                classifier, interpreter, collector, options.ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        return new ProbeResponses(
            XtVersion: collector.FindFirst(DeviceResponseKind.XtVersion),
            PrimaryDeviceAttributes: collector.FindFirst(DeviceResponseKind.PrimaryDeviceAttributes));
    }

    private async Task DrainResponsesUntilSentinelAsync(
        VtSequenceClassifier classifier,
        VtInputInterpreter interpreter,
        ResponseCollector collector,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        // Total-elapsed timeout for the probe phase. We use Task.WhenAny rather than the
        // CancellationToken passed to ReadAsync because Stream.ReadAsync over POSIX stdin
        // (`__ConsoleStream` on a thread-pool worker performing a synchronous `read(2)`)
        // doesn't honor cancellation mid-syscall — the token fires but the read doesn't
        // wake up. Task.WhenAny lets us return regardless. Any bytes the abandoned read
        // eventually produces land in the StreamPipeReader's buffer for the input device
        // pump to consume.
        var timeoutTask = Task.Delay(probeTimeout, _time, cancellationToken);
        Task<ReadResult>? pendingRead = null;

        try
        {
            while (!collector.SeenSentinel
                   && !cancellationToken.IsCancellationRequested
                   && !timeoutTask.IsCompleted)
            {
                pendingRead ??= _source.Reader.ReadAsync(cancellationToken).AsTask();

                var completed = await Task.WhenAny(pendingRead, timeoutTask).ConfigureAwait(false);

                if (completed != pendingRead)
                {
                    // Timeout (or outer cancel) fired before bytes arrived.
                    break;
                }

                ReadResult result;
                try
                {
                    result = await pendingRead.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    pendingRead = null;
                }

                var buffer = result.Buffer;
                foreach (var segment in buffer)
                {
                    classifier.Process(segment.Span, interpreter);
                }
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

    private TerminalIdentification ResolveIdentification(ProbeResponses responses)
    {
        string? rawTerm = _environment.GetVariable("TERM");
        string? rawTermProgram = _environment.GetVariable("TERM_PROGRAM");
        string? termVersion = _environment.GetVariable("TERM_PROGRAM_VERSION");
        bool insideMultiplexer = DetectMultiplexer(rawTerm);

        string? xtVersionPayload = responses.XtVersion is { } x ? AsciiPayloadOrNull(x.Payload) : null;
        var (familyFromXt, nameFromXt, versionFromXt) = ParseXtVersionPayload(xtVersionPayload);

        var family = familyFromXt;
        if (family == TerminalFamily.Unknown)
        {
            family = ClassifyFromEnvironment(rawTerm, rawTermProgram);
        }

        return new TerminalIdentification(
            Family: family,
            Name: nameFromXt ?? rawTermProgram,
            Version: versionFromXt ?? termVersion,
            RawTermEnv: rawTerm,
            RawTermProgramEnv: rawTermProgram,
            InsideMultiplexer: insideMultiplexer);
    }

    private bool DetectMultiplexer(string? rawTerm)
    {
        if (_environment.GetVariable("TMUX") is { Length: > 0 }) return true;
        if (_environment.GetVariable("STY") is { Length: > 0 }) return true; // GNU Screen
        if (rawTerm is null) return false;
        return rawTerm.StartsWith("screen", StringComparison.OrdinalIgnoreCase)
            || rawTerm.StartsWith("tmux", StringComparison.OrdinalIgnoreCase);
    }

    private static (TerminalFamily family, string? name, string? version) ParseXtVersionPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return (TerminalFamily.Unknown, null, null);

        // Common shapes: "iTerm2 3.4.5", "WezTerm 20240127", "kitty 0.34.1", "tmux 3.4".
        // Split on the first run of whitespace; first chunk is the name, rest is version.
        int sepIndex = payload.IndexOfAny([' ', '\t']);
        string name = sepIndex < 0 ? payload : payload[..sepIndex];
        string? version = sepIndex < 0 ? null : payload[(sepIndex + 1)..].TrimStart();

        var family = ClassifyByName(name);
        return (family, name, version);
    }

    private static TerminalFamily ClassifyByName(string name)
    {
        // Match case-insensitively on substrings — terminals self-report with varied casing.
        if (name.Contains("kitty", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Kitty;
        if (name.Contains("iTerm", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Iterm2;
        if (name.Contains("WezTerm", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.WezTerm;
        if (name.Contains("Alacritty", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Alacritty;
        if (name.Contains("foot", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Foot;
        if (name.Contains("Konsole", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Konsole;
        if (name.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows Terminal", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.WindowsTerminal;
        if (name.Contains("xterm", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Xterm;
        return TerminalFamily.Unknown;
    }

    private TerminalFamily ClassifyFromEnvironment(string? rawTerm, string? rawTermProgram)
    {
        if (_environment.GetVariable("KITTY_PID") is { Length: > 0 }) return TerminalFamily.Kitty;
        if (_environment.GetVariable("WT_SESSION") is { Length: > 0 }) return TerminalFamily.WindowsTerminal;
        if (_environment.GetVariable("ITERM_SESSION_ID") is { Length: > 0 }) return TerminalFamily.Iterm2;

        if (rawTermProgram is { Length: > 0 })
        {
            var byName = ClassifyByName(rawTermProgram);
            if (byName != TerminalFamily.Unknown) return byName;
            if (rawTermProgram.Equals("Apple_Terminal", StringComparison.OrdinalIgnoreCase))
                return TerminalFamily.AppleTerminal;
            if (rawTermProgram.Equals("ghostty", StringComparison.OrdinalIgnoreCase))
                return TerminalFamily.Unknown; // No enum entry yet for Ghostty.
        }

        if (rawTerm is { Length: > 0 })
        {
            if (rawTerm.Contains("kitty", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Kitty;
            if (rawTerm.Contains("alacritty", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Alacritty;
            if (rawTerm.Contains("foot", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Foot;
            if (rawTerm.StartsWith("tmux", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Tmux;
            if (rawTerm.StartsWith("screen", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.GnuScreen;
            if (rawTerm.StartsWith("xterm", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Xterm;
            if (rawTerm.StartsWith("rxvt", StringComparison.OrdinalIgnoreCase)) return TerminalFamily.Rxvt;
            // Anything claiming "color" or known VT lineage falls back to GenericVt.
            return TerminalFamily.GenericVt;
        }

        return TerminalFamily.Unknown;
    }

    // ---- Output capability inference ----

    private static InputCapabilities ResolveInputCapabilities(
        TerminalIdentification identification,
        in AppliedOptIns applied)
    {
        var mouse = applied.MouseTracking
            ? new MouseCapabilities(
                ButtonPress: true,
                ButtonRelease: true,
                Drag: true,
                Motion: applied.AnyEventMouse,
                Wheel: true,
                PixelCoordinates: false,
                ExtendedButtonCount: 4)
            : MouseCapabilities.None;

        bool kittyEnabled = applied.KittyKeyboard;
        var keyboard = new KeyboardCapabilities(
            DistinguishesKeyUpDown: kittyEnabled
                && (applied.KittyFlags & KittyKeyboardFlags.ReportEventTypes) != 0,
            ReportsRepeats: kittyEnabled
                && (applied.KittyFlags & KittyKeyboardFlags.ReportEventTypes) != 0,
            DetailedModifiers: kittyEnabled,
            TextInput: kittyEnabled
                && (applied.KittyFlags & KittyKeyboardFlags.ReportAssociatedText) != 0);

        var protocol = new ProtocolCapabilities(
            BracketedPaste: applied.BracketedPaste,
            FocusEvents: applied.FocusEvents,
            KittyKeyboardProtocol: kittyEnabled,
            Win32InputMode: applied.Win32InputMode);

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
        var graphics = ResolveGraphics(identification);
        var cursor = ResolveCursor(identification);
        var window = ResolveWindow(identification);
        var protocol = new OutputProtocolCapabilities(
            BracketedPasteEnable: applied.BracketedPaste,
            FocusReportingEnable: applied.FocusEvents,
            SgrMouseEnable: applied.MouseTracking,
            AnyEventMouseEnable: applied.AnyEventMouse,
            KittyKeyboardPush: applied.KittyKeyboard,
            Win32InputModeEnable: applied.Win32InputMode,
            // Clipboard support is not negotiated yet — leaving false until a probe lands.
            ClipboardWrite: false,
            ClipboardRead: false,
            SynchronizedOutput: applied.SynchronizedOutput);

        return new OutputCapabilities(
            Color: color,
            Styling: styling,
            Graphics: graphics,
            Cursor: cursor,
            Window: window,
            Protocol: protocol);
    }

    private ColorCapabilities ResolveColor(TerminalIdentification identification)
    {
        var depth = ResolveColorDepth(identification);
        bool truecolorClaimed = depth == ColorDepth.Truecolor;

        return new ColorCapabilities(
            Depth: depth,
            // Empirical truecolor verification (OSC 11 round-trip) is a follow-up; for now
            // report the claimed depth without empirical confirmation.
            TruecolorVerified: false,
            DefaultColorReset: depth >= ColorDepth.Ansi16,
            OscPaletteSet: depth >= ColorDepth.Ansi256
                && identification.Family is not TerminalFamily.AppleTerminal);
    }

    private ColorDepth ResolveColorDepth(TerminalIdentification identification)
    {
        var colorTerm = _environment.GetVariable("COLORTERM");
        if (colorTerm is not null
            && (colorTerm.Equals("truecolor", StringComparison.OrdinalIgnoreCase)
                || colorTerm.Equals("24bit", StringComparison.OrdinalIgnoreCase)))
        {
            return ColorDepth.Truecolor;
        }

        // Known truecolor families.
        if (identification.Family is TerminalFamily.Kitty
            or TerminalFamily.Iterm2
            or TerminalFamily.WezTerm
            or TerminalFamily.Alacritty
            or TerminalFamily.WindowsTerminal
            or TerminalFamily.Foot
            or TerminalFamily.Konsole)
        {
            return ColorDepth.Truecolor;
        }

        var term = identification.RawTermEnv;
        if (term is null) return ColorDepth.NoColor;

        if (term.Contains("256color", StringComparison.OrdinalIgnoreCase)) return ColorDepth.Ansi256;
        if (term.Contains("color", StringComparison.OrdinalIgnoreCase)) return ColorDepth.Ansi16;

        return identification.Family == TerminalFamily.AppleTerminal
            ? ColorDepth.Ansi256
            : ColorDepth.NoColor;
    }

    private static TextStylingCapabilities ResolveStyling(TerminalIdentification identification)
    {
        // The xterm baseline (italic, single underline, strikethrough) is supported by every
        // family we recognize. Extended styling (curly underline, OSC 8 hyperlinks, colored
        // underline, overline) is more recent.
        bool extended = identification.Family is TerminalFamily.Kitty
            or TerminalFamily.Iterm2
            or TerminalFamily.WezTerm
            or TerminalFamily.Alacritty
            or TerminalFamily.WindowsTerminal
            or TerminalFamily.Foot
            or TerminalFamily.Konsole;

        return new TextStylingCapabilities(
            Italic: identification.Family != TerminalFamily.Unknown,
            Underline: identification.Family != TerminalFamily.Unknown,
            ExtendedUnderline: extended,
            ColoredUnderline: extended,
            Strikethrough: identification.Family is not (TerminalFamily.Unknown or TerminalFamily.Rxvt),
            Overline: false, // Almost no terminal honors SGR 53.
            Hyperlinks: extended);
    }

    private static GraphicsCapabilities ResolveGraphics(TerminalIdentification identification) =>
        identification.Family switch
        {
            TerminalFamily.Kitty => new GraphicsCapabilities(Sixel: false, KittyGraphics: true, Iterm2InlineImages: false),
            TerminalFamily.Iterm2 => new GraphicsCapabilities(Sixel: false, KittyGraphics: false, Iterm2InlineImages: true),
            TerminalFamily.WezTerm => new GraphicsCapabilities(Sixel: true, KittyGraphics: false, Iterm2InlineImages: true),
            TerminalFamily.Foot => new GraphicsCapabilities(Sixel: true, KittyGraphics: false, Iterm2InlineImages: false),
            TerminalFamily.Mlterm => new GraphicsCapabilities(Sixel: true, KittyGraphics: false, Iterm2InlineImages: false),
            _ => GraphicsCapabilities.None,
        };

    private static CursorCapabilities ResolveCursor(TerminalIdentification identification)
    {
        bool modern = identification.Family is not (TerminalFamily.Unknown
            or TerminalFamily.Rxvt
            or TerminalFamily.Mlterm);

        return new CursorCapabilities(
            ShapeControl: modern,
            VisibilityControl: identification.Family != TerminalFamily.Unknown,
            BlinkControl: modern,
            ColorControl: modern && identification.Family is not TerminalFamily.AppleTerminal);
    }

    private static WindowCapabilities ResolveWindow(TerminalIdentification identification)
    {
        bool pixelSize = identification.Family is TerminalFamily.Kitty
            or TerminalFamily.Iterm2
            or TerminalFamily.WezTerm
            or TerminalFamily.Foot
            or TerminalFamily.Alacritty
            or TerminalFamily.WindowsTerminal
            or TerminalFamily.Konsole;

        return new WindowCapabilities(
            TitleSet: identification.Family != TerminalFamily.Unknown,
            IconSet: identification.Family is TerminalFamily.Xterm or TerminalFamily.Rxvt,
            SizeQueryInPixels: pixelSize,
            AlternateScreenBuffer: identification.Family != TerminalFamily.Unknown,
            ScrollRegion: identification.Family != TerminalFamily.Unknown);
    }

    // ---- Helpers ----

    private static string? AsciiPayloadOrNull(ReadOnlyMemory<byte> payload) =>
        payload.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(payload.Span);

    // ---- Probe sequence byte-strings ----

    /// <summary><c>CSI &gt; q</c> — XTVERSION request.</summary>
    private static ReadOnlyMemory<byte> XtVersionRequest { get; } = new byte[] { 0x1B, (byte)'[', (byte)'>', (byte)'q' };

    /// <summary><c>CSI c</c> — Primary Device Attributes (DA1) request, used as the sentinel.</summary>
    private static ReadOnlyMemory<byte> Da1Request { get; } = new byte[] { 0x1B, (byte)'[', (byte)'c' };

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
        public bool MouseTracking;
        public bool AnyEventMouse;
        public bool FocusEvents;
        public bool BracketedPaste;
        public bool KittyKeyboard;
        public bool Win32InputMode;
        public bool SynchronizedOutput;
        public KittyKeyboardFlags KittyFlags;

        public bool IsEmpty =>
            !MouseTracking
            && !AnyEventMouse
            && !FocusEvents
            && !BracketedPaste
            && !KittyKeyboard
            && !Win32InputMode
            && !SynchronizedOutput;
    }

    private sealed class ResponseCollector : IInputEventSink
    {
        private readonly List<DeviceResponseEvent> _responses = [];

        public bool SeenSentinel { get; private set; }

        public void OnInputEvent(InputEvent inputEvent)
        {
            if (inputEvent is not DeviceResponseEvent response) return;

            _responses.Add(response);
            if (response.Kind == DeviceResponseKind.PrimaryDeviceAttributes)
            {
                SeenSentinel = true;
            }
        }

        public DeviceResponseEvent? FindFirst(DeviceResponseKind kind)
        {
            foreach (var response in _responses)
            {
                if (response.Kind == kind) return response;
            }
            return null;
        }
    }
}
