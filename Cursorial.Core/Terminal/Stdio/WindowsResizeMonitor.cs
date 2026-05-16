using System.Runtime.InteropServices;

using Cursorial.Input.Events;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Watches the Windows console for window-resize notifications by polling
/// <c>GetConsoleScreenBufferInfo</c> at a small interval and emitting a
/// <see cref="ResizeEvent"/> whenever the visible window dimensions change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why polling and not <c>ReadConsoleInput</c>.</b> Windows reports buffer-size changes as
/// <c>WINDOW_BUFFER_SIZE_EVENT</c> records that <c>ReadConsoleInput</c> drains from the console
/// input queue. But our happy-path session also reads the input stream via the
/// <see cref="StreamInputByteSource"/> wrapper, and the two consumers can't both drain the
/// same input queue without coordination (whoever reads first gets the event; the other
/// blocks indefinitely). Polling <c>GetConsoleScreenBufferInfo</c> on a background task is
/// independent of the input pipeline, costs about one syscall every 100 ms (negligible), and
/// has at-most-100-ms latency which is well below the perception threshold for a resize.
/// </para>
/// <para>
/// <b>Initial size.</b> Like <see cref="PosixResizeMonitor"/>, one <see cref="ResizeEvent"/>
/// is delivered synchronously at <see cref="Start"/> so consumers see the starting
/// dimensions.
/// </para>
/// </remarks>
internal sealed partial class WindowsResizeMonitor : IResizeMonitor
{
    /// <summary>Poll interval. Trade-off between latency on resize and idle wakeups.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private const int STD_OUTPUT_HANDLE = -11;

    private readonly Action<ResizeEvent> _onResize;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private int _disposed;

    public WindowsResizeMonitor(Action<ResizeEvent> onResize, TimeProvider? timeProvider = null)
    {
        _onResize = onResize ?? throw new ArgumentNullException(nameof(onResize));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsResizeMonitor));

        if (!OperatingSystem.IsWindows()) return;

        // If we can't even query the initial size, there's no console attached and polling
        // will never produce a useful result — silently no-op.
        if (!TryReadSize(out int rows, out int cols)) return;

        EmitResize(rows, cols);

        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(rows, cols, _cts.Token));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts?.Cancel(); } catch { /* best-effort */ }
        try { _pollTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
    }

    private async Task PollLoopAsync(int initialRows, int initialCols, CancellationToken ct)
    {
        int lastRows = initialRows;
        int lastCols = initialCols;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (!TryReadSize(out int rows, out int cols)) continue;
                if (rows == lastRows && cols == lastCols) continue;

                lastRows = rows;
                lastCols = cols;
                EmitResize(rows, cols);
            }
        }
        catch
        {
            // The poll loop is best-effort; any unexpected failure stops resize delivery but
            // mustn't bubble up to crash the session.
        }
    }

    private void EmitResize(int rows, int cols)
    {
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
            // Consumer faults swallowed — see PosixResizeMonitor for the same rationale.
        }
    }

    /// <summary>
    /// Query the current visible-window cell dimensions via the Windows console API. The
    /// "window" size (<c>srWindow</c>) is what users see; <c>dwSize</c> is the buffer size,
    /// which on legacy conhost is larger than the window to support scrollback.
    /// </summary>
    private static bool TryReadSize(out int rows, out int cols)
    {
        rows = 0;
        cols = 0;

        if (!OperatingSystem.IsWindows()) return false;

        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

        if (!GetConsoleScreenBufferInfo(handle, out var info)) return false;

        cols = info.srWindow.Right - info.srWindow.Left + 1;
        rows = info.srWindow.Bottom - info.srWindow.Top + 1;
        return cols > 0 && rows > 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput,
                                                           out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }
}
