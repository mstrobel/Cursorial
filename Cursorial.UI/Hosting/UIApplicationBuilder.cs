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

    /// <summary>
    /// OPT-IN key event transformer that translates numpad keys to <see cref="Key.Character"/> events with
    /// corresponding to their main keyboard area counterparts (<c>0-9, Enter, '+', '/'</c>, etc.)
    /// </summary>
    public UIApplicationBuilder WithNumpadKeyTranslation()
    {
        _options.TranslateNumpadKeys = true;
        return this;
    }

    /// <summary>Enter the alternate screen buffer at startup when the terminal supports it (default true).
    /// Ignored by an inline application (<see cref="UseInline"/>), which never leaves the main screen.</summary>
    public UIApplicationBuilder UseAlternateScreen(bool enabled = true)
    {
        _options.UseAlternateScreen = enabled;
        return this;
    }

    /// <summary>
    /// Run as an INLINE application: instead of taking over the screen, the application renders in
    /// a region of the main screen buffer that begins where the shell left the cursor. The region
    /// spans the full terminal width and tracks the root content's desired height frame-to-frame —
    /// growing (scrolling the shell history up when it runs out of rows below) and shrinking (the
    /// vacated rows are wiped) — capped at <paramref name="maxHeight"/> and the terminal height.
    /// The alternate screen is never entered; <see cref="UseAlternateScreen"/> is ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On exit the region is erased (<see cref="InlineExitBehavior.Clear"/> — the shell prompt
    /// resumes where the application started) or the last rendered frame is left standing
    /// (<see cref="InlineExitBehavior.Retain"/> — the prompt resumes on the line below). The
    /// choice is re-assignable at runtime via <see cref="UIApplication.InlineExitBehavior"/>, so
    /// an application can decide as it exits (retain on accept, clear on cancel).
    /// </para>
    /// <para>
    /// The region's screen position is discovered with a cursor-position query (DSR-CPR) at
    /// startup and re-queried on terminal resize; mouse coordinates are translated into region
    /// space, and mouse activity outside the region is not delivered. On a terminal that never
    /// answers DSR the application falls back, after a short timeout, to scrolling the region into
    /// the bottom rows of the screen.
    /// </para>
    /// </remarks>
    /// <param name="maxHeight">Optional cap on the region height in rows (≥ 1); the terminal
    /// height always caps regardless. <see langword="null"/> = terminal height only.</param>
    /// <param name="exitBehavior">The initial <see cref="UIApplication.InlineExitBehavior"/>.</param>
    /// <param name="relativeMoves">Render the region with RELATIVE cursor moves (a floating region) rather
    /// than an absolute origin, so it survives an unobserved terminal clear (a manual <c>cmd+K</c>, a
    /// program printing above the region, a terminal-side reflow). This is the <b>default and standard</b>
    /// inline behavior — validated across kitty / Ghostty / WezTerm / Apple Terminal. Pass
    /// <see langword="false"/> to select the legacy absolute-origin path, which is <b>deprecated and slated
    /// for removal before v1.0</b>.</param>
    public UIApplicationBuilder UseInline(int? maxHeight = null, InlineExitBehavior exitBehavior = InlineExitBehavior.Clear,
                                          bool relativeMoves = true)
    {
        if (maxHeight is < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHeight), maxHeight, "The inline region height cap must be at least one row.");

        _options.Inline = true;
        _options.Model = ApplicationModel.Inline;
        _options.InlineMaxHeight = maxHeight;
        _options.InlineExitBehavior = exitBehavior;
        _options.InlineRelativeMoves = relativeMoves;
        return this;
    }

    /// <summary>
    /// Presents inline like <see cref="UseInline"/>, but escalates to the alternate screen while any
    /// <b>window</b> is open and returns to the inline region when the last one closes
    /// (<see cref="ApplicationModel.InlineWithSwitching"/>, design doc §3.1). Popups never escalate.
    /// </summary>
    /// <param name="maxHeight">Optional cap on the region height in rows (≥ 1); the terminal
    /// height always caps regardless. <see langword="null"/> = terminal height only.</param>
    /// <param name="exitBehavior">The initial <see cref="UIApplication.InlineExitBehavior"/>.</param>
    public UIApplicationBuilder UseInlineWithSwitching(int? maxHeight = null, InlineExitBehavior exitBehavior = InlineExitBehavior.Clear)
    {
        UseInline(maxHeight, exitBehavior);
        _options.Model = ApplicationModel.InlineWithSwitching;
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
    public bool Inline;

    /// <summary>The presentation model; <see cref="ApplicationModel.FullScreen"/> unless a UseInline* call says otherwise.</summary>
    public ApplicationModel Model = ApplicationModel.FullScreen;
    public int? InlineMaxHeight;
    public InlineExitBehavior InlineExitBehavior = InlineExitBehavior.Clear;

    /// <summary>Render the inline region with RELATIVE cursor moves (a floating region) rather than an
    /// absolute origin, so it survives an unobserved terminal clear. The default/standard inline behavior
    /// (<see cref="UIApplicationBuilder.UseInline"/> sets it true); the <see langword="false"/> legacy
    /// absolute-origin path is deprecated and slated for removal before v1.0.</summary>
    public bool InlineRelativeMoves = true;
    public bool OrderedDither;
    public bool ExitOnUnhandledCtrlC = true;
    public bool TranslateNumpadKeys = false;
    public UserConfigurationOptions? UserConfiguration;

    public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(1000.0 / FramesPerSecond);
}
