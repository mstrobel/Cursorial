using Cursorial.Input;
using Cursorial.Terminal;
using Cursorial.UI.Configuration;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Configures and constructs a <see cref="UIApplication"/> (design doc §10.2). All methods are
/// no-I/O — the terminal host opens inside <see cref="UIApplication.RunAsync(Func{UIElement}, CancellationToken)"/>.
/// <see cref="Build"/> is single-use: it constructs the <see cref="UIDispatcher"/> (owner = the
/// calling thread) and sets the thread-local <see cref="UIApplication.Current"/>, making pre-run
/// <see cref="UIObject"/> construction legal on this thread from that point.
/// </summary>
public sealed class UIApplicationBuilder
{
    private readonly UIApplicationOptions _options = new();
    private bool _built;

    internal UIApplicationBuilder()
    {
    }

    /// <summary>Options for the owned happy-path <see cref="TerminalSession.OpenAsync(TerminalSessionOptions?, CancellationToken)"/>.</summary>
    public UIApplicationBuilder WithSessionOptions(TerminalSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options.SessionOptions = options;
        return this;
    }

    /// <summary>
    /// BYO session sugar — wrapped in a <see cref="TerminalSessionHost"/>. The session receives no
    /// S6 signal handling (the embedder owns its signal strategy); it is disposed with the
    /// application only when <paramref name="disposeWithApp"/> is set.
    /// </summary>
    public UIApplicationBuilder WithSession(TerminalSession session, bool disposeWithApp = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        _options.Session = session;
        _options.DisposeSession = disposeWithApp;
        return this;
    }

    /// <summary>Full BYO host (tests, embedding). Disposed with the application when <paramref name="disposeWithApp"/>.</summary>
    public UIApplicationBuilder WithTerminalHost(ITerminalHost host, bool disposeWithApp = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        _options.Host = host;
        _options.DisposeHost = disposeWithApp;
        return this;
    }

    /// <summary>The frame pacing target, clamped to [1, 120]. Default 30.</summary>
    public UIApplicationBuilder WithFrameRate(int framesPerSecond)
    {
        _options.FramesPerSecond = Math.Clamp(framesPerSecond, 1, 120);
        return this;
    }

    /// <summary>The clock everything derives from (frame time, gesture thresholds). Default <see cref="TimeProvider.System"/>.</summary>
    public UIApplicationBuilder WithTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options.TimeProvider = timeProvider;
        return this;
    }

    /// <summary>
    /// Click-recognition options for the input pipeline. The default —
    /// <see cref="ClickCountTarget.ButtonDown"/>, <c>SynthesizeClickEvents = false</c> — is the S3
    /// router contract (design doc §10.2): S3 acts on button-down with the count riding it;
    /// enabling Click synthesis violates that contract and is for non-S3 consumers only.
    /// </summary>
    public UIApplicationBuilder WithClickOptions(MouseClickOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options.ClickOptions = options;
        return this;
    }

    /// <summary>
    /// OPT-IN timer-based key-release synthesis for terminals without native key-up reporting.
    /// Off by default: the held view flickers at OS auto-repeat granularity, its capability claims
    /// are best-effort, and it never produces modifier events.
    /// <see cref="UIApplication.SupportsAltKeyTracking"/> is immune by provenance (undecorated
    /// snapshot); <see cref="UIApplication.EffectiveInputCapabilities"/> does reflect it.
    /// </summary>
    public UIApplicationBuilder WithKeyReleaseSynthesis(TimeSpan? upTimeout = null, TimeSpan? repeatTimeout = null)
    {
        _options.KeyReleaseSynthesis = (upTimeout, repeatTimeout);
        return this;
    }

    /// <summary>Enter the alternate screen buffer at startup when the terminal supports it (default true).</summary>
    public UIApplicationBuilder UseAlternateScreen(bool enabled = true)
    {
        _options.UseAlternateScreen = enabled;
        return this;
    }

    /// <summary>Frame-renderer options (ordered dither; <c>RestrictToDirtyRegions</c> is a recorded deferral).</summary>
    public UIApplicationBuilder WithRendererOptions(bool orderedDither = false)
    {
        _options.OrderedDither = orderedDither;
        return this;
    }

    /// <summary>
    /// The default exit gesture (default true): an <b>unhandled</b> Ctrl+C key event triggers
    /// <c>Shutdown(0)</c>. Raw mode makes Ctrl+C input, not SIGINT; it routes through S3 first
    /// (P2), so a handler can claim it (e.g. copy in a text box).
    /// </summary>
    public UIApplicationBuilder ExitOnUnhandledCtrlC(bool enabled = true)
    {
        _options.ExitOnUnhandledCtrlC = enabled;
        return this;
    }

    /// <summary>
    /// OPT-IN user configuration (FB-17 Stage A): load the <c>~/.cursorial</c> options store
    /// (global + per-app overlay) during startup and apply the resolved options — theme
    /// preference, color tier, Nerd Font/emoji opt-ins, capability overrides, animations —
    /// <b>before the first capability-class stamp</b>. Without this call nothing is loaded and
    /// <see cref="UIApplication.UserOptions"/> stays <see langword="null"/>. The store is loaded
    /// once at startup; files are not watched in v1.
    /// </summary>
    /// <param name="options">App-id override and path-provider seam; <see langword="null"/> = defaults
    /// (entry-assembly name under <c>~/.cursorial</c>).</param>
    public UIApplicationBuilder WithUserConfiguration(UserConfigurationOptions? options = null)
    {
        _options.UserConfiguration = options ?? new UserConfigurationOptions();
        return this;
    }

    /// <summary>
    /// Constructs the application. Single-use. <see cref="UIApplication.Current"/> and
    /// <see cref="UIApplication.Dispatcher"/> exist from here; no I/O happens until
    /// <c>RunAsync</c>.
    /// </summary>
    public UIApplication Build()
    {
        if (_built)
            throw new InvalidOperationException("Build() is single-use; create a new builder for another application.");

        _built = true;
        return new UIApplication(_options);
    }
}

/// <summary>The builder's option snapshot (internal carrier).</summary>
internal sealed class UIApplicationOptions
{
    public TerminalSessionOptions? SessionOptions;
    public TerminalSession? Session;
    public bool DisposeSession;
    public ITerminalHost? Host;
    public bool DisposeHost;
    public int FramesPerSecond = 30;
    public TimeProvider TimeProvider = TimeProvider.System;
    public MouseClickOptions ClickOptions = new();
    public (TimeSpan? UpTimeout, TimeSpan? RepeatTimeout)? KeyReleaseSynthesis;
    public bool UseAlternateScreen = true;
    public bool OrderedDither;
    public bool ExitOnUnhandledCtrlC = true;
    public UserConfigurationOptions? UserConfiguration;

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / FramesPerSecond);
}
