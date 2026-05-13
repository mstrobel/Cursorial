using System.Diagnostics;
using System.Runtime.InteropServices;

using Cursorial.Input.Events;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Watches for terminal-window resize notifications via SIGWINCH on POSIX (Linux / macOS /
/// FreeBSD). On each signal, queries the new size via the <c>stty size</c> subprocess and
/// invokes the supplied callback with a fresh <see cref="ResizeEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why stty rather than ioctl(TIOCGWINSZ).</b> The TIOCGWINSZ request value differs across
/// Linux (<c>0x5413</c>), macOS / BSD (<c>_IOR('t', 104, struct winsize)</c>), and other
/// POSIX systems. A subprocess to <c>stty size</c> is uniformly portable, runs only on
/// resize events (typically a handful per session), and we already depend on stty for raw
/// mode. The performance trade-off is irrelevant at human-driven resize cadence.
/// </para>
/// <para>
/// <b>Initial size.</b> One <see cref="ResizeEvent"/> is delivered synchronously at
/// <see cref="Start"/> so consumers see the starting dimensions without having to query the
/// terminal themselves.
/// </para>
/// </remarks>
internal sealed class PosixResizeMonitor : IDisposable
{
    private readonly Action<ResizeEvent> _onResize;
    private readonly TimeProvider _time;
    private PosixSignalRegistration? _registration;
    private int _disposed;

    public PosixResizeMonitor(Action<ResizeEvent> onResize, TimeProvider? timeProvider = null)
    {
        _onResize = onResize ?? throw new ArgumentNullException(nameof(onResize));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Register the SIGWINCH handler and deliver an initial <see cref="ResizeEvent"/> with the
    /// current terminal dimensions.
    /// </summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PosixResizeMonitor));
        }

        // SIGWINCH is POSIX-only. Caller (TerminalSession) gates instantiation on the platform
        // already, but guard once more so a stray instantiation on Windows degrades to "no
        // resize events" instead of throwing.
        if (!OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()
            && !OperatingSystem.IsFreeBSD())
        {
            return;
        }

        try
        {
            _registration = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, HandleSignal);
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        // Emit the initial size so consumers don't have to issue a separate query.
        EmitCurrentSize();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration?.Dispose();
        _registration = null;
    }

    private void HandleSignal(PosixSignalContext context)
    {
        // SIGWINCH is informational — no default action to suppress.
        EmitCurrentSize();
    }

    private void EmitCurrentSize()
    {
        if (!TryReadSize(out int rows, out int cols)) return;

        try
        {
            _onResize(new ResizeEvent
                      {
                          Timestamp = _time.GetUtcNow(),
                          Rows = rows,
                          Columns = cols,
                      });
        }
        catch
        {
            // Consumer callback faults are swallowed — a single bad subscriber must not break
            // future resize delivery.
        }
    }

    /// <summary>
    /// Read the current cell-grid size via <c>stty size</c>, which prints "ROWS COLS".
    /// Returns false on any failure (subprocess error, parse failure, stdin not a TTY).
    /// </summary>
    private static bool TryReadSize(out int rows, out int cols)
    {
        rows = 0;
        cols = 0;

        try
        {
            var psi = new ProcessStartInfo
                      {
                          FileName = "stty",
                          Arguments = "size",
                          UseShellExecute = false,
                          RedirectStandardOutput = true,
                          RedirectStandardError = false,
                          RedirectStandardInput = false,
                      };

            using var process = Process.Start(psi);
            if (process is null) return false;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0) return false;

            var parts = output.Trim().Split(' ', 2);
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out rows) || rows <= 0) return false;
            if (!int.TryParse(parts[1], out cols) || cols <= 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}