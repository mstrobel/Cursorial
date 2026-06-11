// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One timestamp per frame — the single time truth (design doc §10.2). Mid-frame code never
/// re-samples the clock: S5 ticks, gesture thresholds, and diagnostics all read this value.
/// </summary>
/// <param name="FrameNumber">The monotonically increasing frame counter.</param>
/// <param name="Elapsed">Wall-clock time since application start (TimeProvider-derived; fake-clock friendly).</param>
/// <param name="Delta">Elapsed time since the previous frame's timestamp.</param>
public readonly record struct FrameTime(long FrameNumber, TimeSpan Elapsed, TimeSpan Delta);

/// <summary>When the application shuts down in response to window closes (design doc §10.7).</summary>
/// <remarks>
/// At P1 no windowing exists, so neither window-driven mode has a trigger — the loop exits via
/// <see cref="UIApplication.Shutdown"/>, the Ctrl+C default gesture, or input-stream EOF. The
/// enum ships now because it is public surface the S4 phase (P7) consumes without an API change.
/// </remarks>
public enum ShutdownMode
{
    /// <summary>Shut down when the main window closes (default).</summary>
    OnMainWindowClose,

    /// <summary>Shut down when the last open window closes.</summary>
    OnLastWindowClose,

    /// <summary>Shut down only on an explicit <see cref="UIApplication.Shutdown"/> call.</summary>
    OnExplicitShutdown,
}

/// <summary>
/// Carries an exception escaping user code through the funnel (design doc §10.8). Setting
/// <see cref="Handled"/> lets the frame continue; leaving it false records the exception as fatal —
/// the canonical teardown runs and <c>RunAsync</c> rethrows.
/// </summary>
public sealed class DispatcherUnhandledExceptionEventArgs : EventArgs
{
    /// <summary>The exception that escaped user code.</summary>
    public required Exception Exception { get; init; }

    /// <summary><see langword="true"/> ⇒ the loop continues; <see langword="false"/> ⇒ teardown + rethrow.</summary>
    public bool Handled { get; set; }
}

/// <summary>Raised after <see cref="UIApplication.RenegotiateAsync"/> swaps the capability snapshot.</summary>
public sealed class CapabilitiesChangedEventArgs : EventArgs
{
    /// <summary>The snapshot before renegotiation.</summary>
    public required Terminal.TerminalCapabilities OldCapabilities { get; init; }

    /// <summary>The snapshot after renegotiation.</summary>
    public required Terminal.TerminalCapabilities NewCapabilities { get; init; }
}
