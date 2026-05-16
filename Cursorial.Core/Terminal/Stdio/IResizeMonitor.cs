using Cursorial.Input.Events;

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
}

/// <summary>
/// Factory that returns the resize monitor implementation appropriate for the current OS, or
/// <see langword="null"/> on platforms without a supported mechanism.
/// </summary>
internal static class ResizeMonitor
{
    public static IResizeMonitor? Create(Action<ResizeEvent> onResize, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(onResize);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return new PosixResizeMonitor(onResize, timeProvider);

        if (OperatingSystem.IsWindows())
            return new WindowsResizeMonitor(onResize, timeProvider);

        return null;
    }
}
