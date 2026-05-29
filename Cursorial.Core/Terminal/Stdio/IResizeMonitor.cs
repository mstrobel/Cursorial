using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Watches the terminal for window-resize notifications and invokes a callback with a fresh
/// <see cref="ResizeEvent"/> each time the cell dimensions change. Implementations are
/// platform-specific (POSIX SIGWINCH, Windows console-buffer polling) and selected at runtime
/// via <see cref="ResizeMonitor.Create"/>.
/// </summary>
internal interface IResizeMonitor : IDisposable
{
    /// <summary>
    /// Begin watching for resizes. Implementations also emit an initial <see cref="ResizeEvent"/>
    /// with the current dimensions so consumers see the starting size without having to query
    /// the terminal themselves.
    /// </summary>
    void Start();

    /// <summary>
    /// One-shot synchronous read of the current cell-grid dimensions, bypassing the resize
    /// event stream. Returns <see langword="null"/> when the size can't be determined (the
    /// underlying query failed, stdin isn't a TTY, etc.). Used by
    /// <c>TerminalSession.QueryTerminalSizeAsync</c> to satisfy callers that need the size up
    /// front without waiting for a resize signal.
    /// </summary>
    (int Columns, int Rows)? QueryCurrentSize();
}

/// <summary>
/// Factory that returns the resize monitor implementation appropriate for the current OS, or
/// <see langword="null"/> on platforms without a supported mechanism.
/// </summary>
internal static class ResizeMonitor
{
    /// <param name="onResize">Callback invoked with each detected resize.</param>
    /// <param name="timeProvider">Optional clock for tests; defaults to system time.</param>
    /// <param name="sink">
    /// Output sink for the Windows non-console wire-probe path (<c>CSI 18 t</c>). Unused on
    /// POSIX (SIGWINCH is push-based) and on the Windows console-API path (the buffer query
    /// is local). Optional; the wire-probe fallback silently no-ops if either this or
    /// <paramref name="device"/> is null.
    /// </param>
    /// <param name="device">
    /// Input device whose <c>DeviceResponseEmitted</c> hook the Windows wire-probe path
    /// subscribes to so it can observe <c>CSI 8 t</c> replies without competing with the
    /// consumer for the channel.
    /// </param>
    /// <param name="environmentReader">
    /// Used by the Windows wire-probe path to pick the SSH cadence
    /// (<see cref="IEnvironmentReader.IsSSH"/>). Defaults to the live OS reader.
    /// </param>
    public static IResizeMonitor? Create(
        Action<ResizeEvent> onResize,
        TimeProvider? timeProvider = null,
        IOutputByteSink? sink = null,
        VtInputDevice? device = null,
        IEnvironmentReader? environmentReader = null)
    {
        ArgumentNullException.ThrowIfNull(onResize);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return new PosixResizeMonitor(onResize, timeProvider);

        if (OperatingSystem.IsWindows())
            return new WindowsResizeMonitor(onResize, timeProvider, sink, device, environmentReader);

        return null;
    }
}
