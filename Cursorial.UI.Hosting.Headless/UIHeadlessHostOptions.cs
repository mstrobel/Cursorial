using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.UI.Hosting.Headless;

/// <summary>Options for <see cref="UIHeadlessHost.Create"/> (design doc §10.10).</summary>
public sealed record UIHeadlessHostOptions
{
    /// <summary>The scripted terminal size (default 80×24). <see cref="UIHeadlessHost.SendResize"/> changes it later.</summary>
    public Size InitialSize { get; init; } = new(80, 24);

    /// <summary>The scripted capability snapshot (default <see cref="HeadlessCapabilities.KittyTruecolor"/>).</summary>
    public TerminalCapabilities Capabilities { get; init; } = HeadlessCapabilities.KittyTruecolor;

    /// <summary>The fake-clock step used by <see cref="UIHeadlessHost.AdvanceTime"/> (default 33 ms ≈ 30 fps).</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>Capture each frame's emitted wire bytes into <see cref="UIHeadlessHost.LastFrameBytes"/> (default off).</summary>
    public bool CaptureFrameBytes { get; init; }

    /// <summary>Enter the alternate screen at startup (default true — mirrors production).</summary>
    public bool UseAlternateScreen { get; init; } = true;

    /// <summary>
    /// Indicates whether transitions for inactive windows are disabled during UI tests.
    /// This prevents animations from blocking <see cref="UIHeadlessHost.RunUntilIdle"/>
    /// from exiting successfully (default true)."/>
    /// </summary>
    public bool DisableInactiveWindowTransitions { get; init; } = true;

    /// <summary>
    /// An optional hook over the application builder before <see cref="UIApplicationBuilder.Build"/> —
    /// for builder-level features the host does not model directly (e.g.
    /// <see cref="UIApplicationBuilder.WithUserConfiguration"/>). Runs after the host's own builder
    /// calls, so it may also override them deliberately.
    /// </summary>
    public Action<UIApplicationBuilder>? ConfigureBuilder { get; init; }
}
