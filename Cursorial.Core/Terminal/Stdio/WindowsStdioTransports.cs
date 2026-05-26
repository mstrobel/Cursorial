using System.Runtime.InteropServices;

using Cursorial.Input;
using Cursorial.Output;

using Microsoft.Win32.SafeHandles;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Windows implementation of <see cref="IStdioTransports"/>. Disables echo / line / processed
/// input on the console input handle, enables virtual-terminal input/output processing, and
/// restores the prior console mode flags on disposal. Targets the modern ConPTY model where
/// stdin and stdout are byte streams that carry VT sequences in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bypassing the .NET Console subsystem.</b> We wrap stdout as <see cref="FileStream"/>
/// over a non-owning <see cref="SafeFileHandle"/> rather than calling
/// <see cref="Console.OpenStandardOutput()"/>. The .NET Console subsystem manages
/// console-mode state on Windows — accessing it can re-enable line / echo modes behind our
/// backs and revert the raw configuration we just applied via <c>SetConsoleMode</c>. The same
/// fix applies on both platforms: bypass <see cref="Console"/> entirely.
/// </para>
/// <para>
/// <b>Stdin source selection.</b> The stdin path is selected at <see cref="Open"/> time based
/// on the handle's file type: a real console (<c>FILE_TYPE_CHAR</c>) goes through
/// <see cref="WindowsConsoleInputByteSource"/>, which uses a <c>WaitForMultipleObjects</c>
/// pattern that's cancellable on disposal. A non-console handle (pipe / disk / remote — seen
/// under tmux, ssh, MSYS2, CI runners, or when stdin is otherwise redirected) falls back to
/// the older <see cref="StreamInputByteSource"/> wrapping a <see cref="FileStream"/>. The
/// fallback path retains the legacy "first keystroke after disposal can be swallowed" bug,
/// because there's no console-style "input ready" signal to wait on for arbitrary
/// pipes / streams.
/// </para>
/// </remarks>
internal sealed partial class WindowsStdioTransports : IStdioTransports
{
    // Standard handle indices for GetStdHandle.
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;

    // Console input mode flags we manipulate.
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    // Console output mode flags we set.
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    // GetFileType return values used to pick the stdin source.
    private const uint FILE_TYPE_CHAR = 0x0002;

    private readonly IntPtr _stdinHandle;
    private readonly IntPtr _stdoutHandle;
    private readonly uint _originalStdinMode;
    private readonly uint _originalStdoutMode;
    private readonly IInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
    private int _terminalRestored;
    private int _disposed;

    private WindowsStdioTransports(
        IntPtr stdinHandle,
        IntPtr stdoutHandle,
        uint originalStdinMode,
        uint originalStdoutMode,
        IInputByteSource source,
        StreamOutputByteSink sink)
    {
        _stdinHandle = stdinHandle;
        _stdoutHandle = stdoutHandle;
        _originalStdinMode = originalStdinMode;
        _originalStdoutMode = originalStdoutMode;
        _source = source;
        _sink = sink;
    }

    public IInputByteSource Source => _source;
    public IOutputByteSink Sink => _sink;

    public static WindowsStdioTransports Open()
    {
        var stdinHandle = GetStdHandle(STD_INPUT_HANDLE);
        var stdoutHandle = GetStdHandle(STD_OUTPUT_HANDLE);

        if (!GetConsoleMode(stdinHandle, out uint originalStdinMode))
        {
            throw new InvalidOperationException(
                "Standard input is not connected to a console. Use the BYO " +
                "TerminalSession.OpenAsync(source, sink) overload for non-console scenarios.");
        }

        if (!GetConsoleMode(stdoutHandle, out uint originalStdoutMode))
        {
            throw new InvalidOperationException(
                "Standard output is not connected to a console.");
        }

        // Raw input: clear processed / line / echo. Enable VT input so modern terminals
        // (Windows Terminal, ConPTY-backed conhost) deliver VT sequences instead of console
        // input records.
        uint stdinMode = originalStdinMode;
        stdinMode &= ~(ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
        stdinMode |= ENABLE_VIRTUAL_TERMINAL_INPUT;

        if (!SetConsoleMode(stdinHandle, stdinMode))
        {
            throw new InvalidOperationException(
                $"SetConsoleMode failed for the console input handle (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        // Output: enable VT processing so SGR / cursor / OSC sequences are interpreted
        // rather than printed literally. DISABLE_NEWLINE_AUTO_RETURN keeps the terminal
        // from auto-converting LF to CRLF on our behalf.
        uint stdoutMode = originalStdoutMode;
        stdoutMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;

        if (!SetConsoleMode(stdoutHandle, stdoutMode))
        {
            // Revert input mode if output couldn't be configured.
            int err = Marshal.GetLastWin32Error();
            SetConsoleMode(stdinHandle, originalStdinMode);
            throw new InvalidOperationException($"SetConsoleMode failed for the console output handle (Win32 error {err}).");
        }

        // Wrap stdout via FileStream(SafeFileHandle) — see the class remarks for why we
        // deliberately do NOT use Console.OpenStandardOutput. ownsHandle: false because the
        // standard output handle is process-global and owned by the OS.
        FileStream? stdoutStream = null;
        FileStream? stdinStreamFallback = null;
        IInputByteSource? source = null;

        try
        {
            var stdoutSafeHandle = new SafeFileHandle(stdoutHandle, ownsHandle: false);
            stdoutStream = new FileStream(stdoutSafeHandle, FileAccess.Write);
            var sink = new StreamOutputByteSink(stdoutStream);

            // Stdin source selection. The console-handle path uses
            // WaitForMultipleObjects + ReadFile so disposal can wake the pump cleanly
            // without leaving a blocked ReadFile to consume the user's next keystroke.
            // Anything that isn't a real console (FILE_TYPE_PIPE under tmux / ssh / MSYS2 /
            // a CI pipe, FILE_TYPE_DISK if stdin was redirected from a file, etc.) falls
            // back to the stream-based source — same behavior as today, including the
            // legacy zombie-read bug; non-console transports don't expose an "input ready"
            // signal we could wait on instead.
            uint stdinType = GetFileType(stdinHandle);
            if (stdinType == FILE_TYPE_CHAR)
            {
                source = new WindowsConsoleInputByteSource(stdinHandle);
            }
            else
            {
                var stdinSafeHandle = new SafeFileHandle(stdinHandle, ownsHandle: false);
                stdinStreamFallback = new FileStream(stdinSafeHandle, FileAccess.Read);
                source = new StreamInputByteSource(stdinStreamFallback);
            }

            return new WindowsStdioTransports(stdinHandle,
                                              stdoutHandle,
                                              originalStdinMode,
                                              originalStdoutMode,
                                              source,
                                              sink);
        }
        catch
        {
            // @formatter:off
            // Revert the console mode changes we made above, then surface the failure.
            try { SetConsoleMode(stdinHandle, originalStdinMode); } catch { /* best-effort */ }
            try { SetConsoleMode(stdoutHandle, originalStdoutMode); } catch { /* best-effort */ }
            stdoutStream?.Dispose();
            stdinStreamFallback?.Dispose();
            // Best-effort dispose of a partially constructed console source so its cancel
            // event handle and pump task don't leak.
            if (source is IAsyncDisposable asyncDisposable)
            {
                try { asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* best-effort */ }
            }
            throw;
            // @formatter:on
        }
    }

    public void RestoreTerminalState()
    {
        // Idempotent — guarded so signal-handler invocations don't run multiple times.
        if (Interlocked.Exchange(ref _terminalRestored, 1) != 0) return;

        // @formatter:off
        try { SetConsoleMode(_stdinHandle, _originalStdinMode); } catch { /* best-effort */ }
        try { SetConsoleMode(_stdoutHandle, _originalStdoutMode); } catch { /* best-effort */ }
        // @formatter:on
    }

    public void WriteBytesSync(ReadOnlySpan<byte> bytes)
    {
        // Loop in case WriteFile returns a short count (rare on a console handle, but the API
        // permits it). Best-effort: errors are swallowed.
        while (!bytes.IsEmpty)
        {
            if (!WriteFile(_stdoutHandle,
                           ref MemoryMarshal.GetReference(bytes),
                           (uint) bytes.Length,
                           out uint written,
                           IntPtr.Zero))
            {
                return; // broken handle / closed pipe
            }

            if (written == 0) return;
            bytes = bytes[(int) written..];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Restore prior console modes BEFORE closing transports.
        RestoreTerminalState();

        // @formatter:off
        try { await _sink.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        // @formatter:on
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFileType(IntPtr hFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        IntPtr hFile,
        ref byte lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);
}