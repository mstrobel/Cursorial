using System.Runtime.InteropServices;
using System.Text;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;

// ReSharper disable RedundantJumpStatement

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Watches the Windows console for window-resize notifications. Two paths are wired:
/// </summary>
/// <remarks>
/// <para>
/// <b>Console-API polling (primary).</b> When stdout is a real console
/// (<c>GetConsoleScreenBufferInfo</c> answers), <see cref="WindowsResizeMonitor"/> polls the
/// console buffer at <see cref="ConsoleApiPollInterval"/> and emits a <see cref="ResizeEvent"/>
/// whenever the visible dimensions change. Windows reports resizes as
/// <c>WINDOW_BUFFER_SIZE_EVENT</c> records on <c>ReadConsoleInput</c>, but our happy-path
/// session also reads input via the <see cref="WindowsConsoleInputByteSource"/> wrapper, and
/// the two consumers can't both drain the same record queue without coordination — polling on
/// a background task is independent of the input pipeline, costs one syscall every 100 ms,
/// and has at-most-100-ms latency which is well below the perception threshold for a resize.
/// </para>
/// <para>
/// <b>Wire-protocol polling (fallback).</b> When stdout isn't a console — MSYS2 / Cygwin /
/// MobaXterm bash, BYO sessions on non-TTY streams — the monitor falls back to issuing
/// <c>CSI 18 t</c> (XTWINOPS report-text-area) on a slower cadence and observes the
/// <c>CSI 8 ; rows ; cols t</c> responses via <see cref="VtInputDevice.DeviceResponseEmitted"/>.
/// Cadence is <see cref="LocalProbeInterval"/> for local sessions and
/// <see cref="SshProbeInterval"/> when <see cref="IEnvironmentReader.IsSSH"/> reports a
/// remote session (mirrors Terminal.GUI's 500 ms throttle, slowed for SSH to keep the round-
/// trip traffic reasonable on high-latency links). This path requires both an
/// <see cref="IOutputByteSink"/> (to send the probe) and a <see cref="VtInputDevice"/>
/// (to observe the response); BYO sessions that don't provide one or the other silently
/// no-op.
/// </para>
/// <para>
/// <b>Initial size.</b> Like <see cref="PosixResizeMonitor"/>, one <see cref="ResizeEvent"/>
/// is delivered as soon as a size is known. On the console-API path that's synchronous at
/// <see cref="Start"/>; on the wire-probe path it follows the first round-trip.
/// </para>
/// </remarks>
internal sealed partial class WindowsResizeMonitor : IResizeMonitor
{
    /// <summary>Console-API poll interval. Trade-off between resize latency and idle wakeups.</summary>
    private static readonly TimeSpan ConsoleApiPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Wire-probe interval for local sessions — matches Terminal.GUI's 500 ms throttle.</summary>
    private static readonly TimeSpan LocalProbeInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Wire-probe interval for SSH sessions — slowed to avoid spamming high-latency links.</summary>
    private static readonly TimeSpan SshProbeInterval = TimeSpan.FromSeconds(2);

    private const int STD_OUTPUT_HANDLE = -11;

    /// <summary><c>CSI 18 t</c> — request text-area size in characters.</summary>
    private static readonly ReadOnlyMemory<byte> TextAreaSizeRequest =
        new byte[] { 0x1B, (byte) '[', (byte) '1', (byte) '8', (byte) 't' };

    private readonly Action<ResizeEvent> _onResize;
    private readonly TimeProvider _time;
    private readonly IOutputByteSink? _sink;
    private readonly VtInputDevice? _device;
    private readonly TimeSpan _probeInterval;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private int _lastProbedCols;
    private int _lastProbedRows;
    private int _disposed;

    public WindowsResizeMonitor(
        Action<ResizeEvent> onResize,
        TimeProvider? timeProvider = null,
        IOutputByteSink? sink = null,
        VtInputDevice? device = null,
        IEnvironmentReader? environmentReader = null)
    {
        _onResize = onResize ?? throw new ArgumentNullException(nameof(onResize));
        _time = timeProvider ?? TimeProvider.System;
        _sink = sink;
        _device = device;

        var env = environmentReader ?? IEnvironmentReader.Default;
        _probeInterval = env.IsSSH() ? SshProbeInterval : LocalProbeInterval;
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsResizeMonitor));

        if (!OperatingSystem.IsWindows()) return;

        if (TryReadSize(out int cols, out int rows))
        {
            // Console-API path: emit initial size and start the cheap poll loop.
            EmitResize(cols, rows);

            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => ConsoleApiPollLoopAsync(cols, rows, _cts.Token));
            return;
        }

        if (_sink is not null && _device is not null)
        {
            // Wire-probe path: subscribe to device responses (the first CSI 8 t reply will
            // drive the initial EmitResize) and start the probe pump.
            _device.DeviceResponseEmitted += OnDeviceResponse;
            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => WireProbePollLoopAsync(_cts.Token));
            return;
        }

        // Nothing to watch — silently no-op.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_device is not null)
            _device.DeviceResponseEmitted -= OnDeviceResponse;

        try { _cts?.Cancel(); } catch { /* best-effort */ }
        try { _pollTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* best-effort */ }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
    }

    private async Task ConsoleApiPollLoopAsync(int initialCols, int initialRows, CancellationToken ct)
    {
        int lastCols = initialCols;
        int lastRows = initialRows;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(ConsoleApiPollInterval, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (!TryReadSize(out int cols, out int rows)) continue;
                if (rows == lastRows && cols == lastCols) continue;

                lastCols = cols;
                lastRows = rows;
                EmitResize(cols, rows);
            }
        }
        catch
        {
            // The poll loop is best-effort; any unexpected failure stops resize delivery but
            // mustn't bubble up to crash the session.
        }
    }

    private async Task WireProbePollLoopAsync(CancellationToken ct)
    {
        try
        {
            // Issue an immediate probe so consumers see a size as soon as the round-trip
            // completes, instead of waiting one interval.
            await SendProbeAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_probeInterval, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                await SendProbeAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Wire-probe loop is best-effort; failures stop resize delivery but mustn't bubble.
        }
    }

    private async Task SendProbeAsync(CancellationToken ct)
    {
        if (_sink is null) return;
        try
        {
            await _sink.Writer.WriteAsync(TextAreaSizeRequest, ct).ConfigureAwait(false);
            await _sink.Writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Sink can have gone away mid-session (terminal closed, pipe broken).
        }
    }

    /// <summary>
    /// Observer for <see cref="VtInputDevice.DeviceResponseEmitted"/>. Filters for
    /// <see cref="DeviceResponseKind.TextAreaSizeInChars"/>, parses the
    /// <c>8 ; rows ; cols</c> payload, and emits a <see cref="ResizeEvent"/> when the size
    /// changes from the previously observed value.
    /// </summary>
    private void OnDeviceResponse(DeviceResponseEvent e)
    {
        if (e.Kind != DeviceResponseKind.TextAreaSizeInChars) return;
        if (!TryParseTextAreaSize(e.Payload.Span, out int cols, out int rows)) return;

        if (cols == _lastProbedCols && rows == _lastProbedRows) return;

        _lastProbedCols = cols;
        _lastProbedRows = rows;
        EmitResize(cols, rows);
    }

    /// <summary>
    /// Parse a "<c>8;rows;cols</c>" payload — the leading "8;" sentinel is included in the
    /// classifier-emitted parameter run, so we re-parse rather than treat it as just
    /// "<c>rows;cols</c>".
    /// </summary>
    private static bool TryParseTextAreaSize(ReadOnlySpan<byte> payload, out int cols, out int rows)
    {
        cols = 0;
        rows = 0;

        int firstSep = payload.IndexOf((byte) ';');
        if (firstSep < 0) return false;
        var afterFirst = payload[(firstSep + 1)..];

        int secondSep = afterFirst.IndexOf((byte) ';');
        if (secondSep < 0) return false;

        if (!int.TryParse(Encoding.ASCII.GetString(afterFirst[..secondSep]), out rows)) return false;
        if (!int.TryParse(Encoding.ASCII.GetString(afterFirst[(secondSep + 1)..]), out cols)) return false;

        return cols > 0 && rows > 0;
    }

    private void EmitResize(int cols, int rows)
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

    /// <inheritdoc/>
    public (int Columns, int Rows)? QueryCurrentSize()
    {
        if (TryReadSize(out int cols, out int rows)) return (cols, rows);

        // Wire-probe path: the most recently observed CSI 8 t response is our authoritative
        // value. May be (0, 0) before the first round-trip completes — treat that as "not yet
        // known" and return null so callers can fall back further (Capabilities snapshot,
        // 80×24 default).
        if (_lastProbedCols > 0 && _lastProbedRows > 0)
            return (_lastProbedCols, _lastProbedRows);

        return null;
    }

    /// <summary>
    /// Query the current visible-window cell dimensions. Primary source is the Windows console
    /// API (<c>GetConsoleScreenBufferInfo</c>); <c>srWindow</c> is what users see (the visible
    /// region), <c>dwSize</c> is the buffer size which on legacy conhost is larger to support
    /// scrollback. When the primary source fails — typically because stdout isn't connected to
    /// a real console (an MSYS2 / Cygwin / MobaXterm bash session, a CI pipe, …) — fall back
    /// to the <c>COLUMNS</c> / <c>LINES</c> environment variables that POSIX-style shells set
    /// when launching child processes. The env-var fallback gives an accurate initial size but
    /// becomes stale after resizes (shells don't re-export on SIGWINCH unless a hook does it);
    /// callers needing live resize on non-console stdout will need to query the terminal
    /// itself (<c>CSI 18 t</c>) rather than rely on this monitor.
    /// </summary>
    private static bool TryReadSize(out int cols, out int rows)
    {
        rows = 0;
        cols = 0;

        if (!OperatingSystem.IsWindows()) return false;

        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1) &&
            GetConsoleScreenBufferInfo(handle, out var info))
        {
            cols = info.srWindow.Right - info.srWindow.Left + 1;
            rows = info.srWindow.Bottom - info.srWindow.Top + 1;
            if (cols > 0 && rows > 0) return true;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out int envCols) &&
            int.TryParse(Environment.GetEnvironmentVariable("LINES"), out int envRows) &&
            envCols > 0 && envRows > 0)
        {
            cols = envCols;
            rows = envRows;
            return true;
        }

        return false;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput,
                                                           out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    // ReSharper disable InconsistentNaming

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

    // ReSharper restore InconsistentNaming
}
