using System.IO.Pipelines;
using GlowTerm.Core.Input;
using GlowTerm.Core.Input.Parsing;
using GlowTerm.Core.Output;

namespace GlowTerm.Core.Terminal;

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

    private bool _negotiated;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_negotiated)
        {
            throw new InvalidOperationException(
                "NegotiateAsync was already called on this instance. Negotiator instances are single-shot; " +
                "create a new VtTerminalNegotiator for a fresh negotiation.");
        }

        _negotiated = true;

        var responses = await ProbeIdentificationAsync(options, cancellationToken).ConfigureAwait(false);
        var identification = ResolveIdentification(responses);

        var inputCapabilities = InputCapabilities.None;
        var outputCapabilities = ResolveOutputCapabilities(identification);

        return new TerminalCapabilities(
            Terminal: identification,
            Input: inputCapabilities,
            Output: outputCapabilities);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        // No opt-ins are applied yet, so restore is a no-op. When opt-in support lands this
        // will reverse every enable sequence emitted during NegotiateAsync.
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

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
            XtVersion: collector.FindFirst(DeviceResponseKind.XtVersionResponse),
            PrimaryDeviceAttributes: collector.FindFirst(DeviceResponseKind.PrimaryDeviceAttributes));
    }

    private async Task DrainResponsesUntilSentinelAsync(
        VtSequenceClassifier classifier,
        VtInputInterpreter interpreter,
        ResponseCollector collector,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        // Read until the sentinel arrives or the per-batch timeout elapses with no further
        // input. The timeout is per individual read attempt, so a slow but responsive
        // terminal that drips bytes still completes; a silent one bails after one window.
        while (!collector.SeenSentinel)
        {
            using var timeoutCts = new CancellationTokenSource(probeTimeout, _time);
            using var perReadCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            ReadResult result;
            try
            {
                result = await _source.Reader.ReadAsync(perReadCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-read timeout. Whatever we already collected is what we have.
                break;
            }

            var buffer = result.Buffer;
            foreach (var segment in buffer)
            {
                classifier.Process(segment.Span, interpreter);
            }
            _source.Reader.AdvanceTo(buffer.End);

            if (result.IsCompleted) break;
        }

        // Ensure any pending lone-ESC is flushed (otherwise it'd corrupt the next consumer).
        classifier.Flush(interpreter);
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

    private OutputCapabilities ResolveOutputCapabilities(TerminalIdentification identification)
    {
        var color = ResolveColor(identification);
        var styling = ResolveStyling(identification);
        var graphics = ResolveGraphics(identification);
        var cursor = ResolveCursor(identification);
        var window = ResolveWindow(identification);
        var protocol = OutputProtocolCapabilities.None; // Populated when opt-ins land.

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
