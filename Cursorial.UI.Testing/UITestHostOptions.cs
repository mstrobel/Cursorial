using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.UI.Testing;

/// <summary>Options for <see cref="UITestHost.Create"/> (design doc §10.10).</summary>
public sealed record UITestHostOptions
{
    /// <summary>The scripted terminal size (default 80×24). <see cref="UITestHost.SendResize"/> changes it later.</summary>
    public Size InitialSize { get; init; } = new(80, 24);

    /// <summary>The scripted capability snapshot (default <see cref="TestCapabilities.KittyTruecolor"/>).</summary>
    public TerminalCapabilities Capabilities { get; init; } = TestCapabilities.KittyTruecolor;

    /// <summary>The fake-clock step used by <see cref="UITestHost.AdvanceTime"/> (default 33 ms ≈ 30 fps).</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>Capture each frame's emitted wire bytes into <see cref="UITestHost.LastFrameBytes"/> (default off).</summary>
    public bool CaptureFrameBytes { get; init; }

    /// <summary>Enter the alternate screen at startup (default true — mirrors production).</summary>
    public bool UseAlternateScreen { get; init; } = true;

    /// <summary>
    /// Indicates whether transitions for inactive windows are disabled during UI tests.
    /// This prevents animations from blocking <see cref="UITestHost.RunUntilIdle"/>
    /// from exiting successfully (default true)."/>
    /// </summary>
    public bool DisableInactiveWindowTransitions { get; init; } = true;
}
