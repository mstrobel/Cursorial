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
/// We wrap stdin / stdout as <see cref="FileStream"/> over a non-owning
/// <see cref="SafeFileHandle"/> rather than calling <see cref="Console.OpenStandardInput()"/> /
/// <see cref="Console.OpenStandardOutput()"/>. The same .NET Console subsystem that manipulates
/// termios on Unix similarly manages console-mode state on Windows — accessing those streams
/// can re-enable line / echo modes behind our backs and revert the raw configuration we just
/// applied via <c>SetConsoleMode</c>. The same fix applies on both platforms: bypass
/// <see cref="Console"/> entirely.
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

    private readonly IntPtr _stdinHandle;
    private readonly IntPtr _stdoutHandle;
    private readonly uint _originalStdinMode;
    private readonly uint _originalStdoutMode;
    private readonly StreamInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
    private int _terminalRestored;
    private int _disposed;

    private WindowsStdioTransports(
        IntPtr stdinHandle,
        IntPtr stdoutHandle,
        uint originalStdinMode,
        uint originalStdoutMode,
        StreamInputByteSource source,
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

        // Wrap stdin / stdout via FileStream(SafeFileHandle) — see remarks on the class for
        // why we deliberately do NOT use Console.OpenStandardInput / Console.OpenStandardOutput.
        // ownsHandle: false because these are process-global handles owned by the OS.
        FileStream? stdinStream = null;
        FileStream? stdoutStream = null;

        try
        {
            var stdinSafeHandle = new SafeFileHandle(stdinHandle, ownsHandle: false);
            var stdoutSafeHandle = new SafeFileHandle(stdoutHandle, ownsHandle: false);
            stdinStream = new FileStream(stdinSafeHandle, FileAccess.Read);
            stdoutStream = new FileStream(stdoutSafeHandle, FileAccess.Write);

            var source = new StreamInputByteSource(stdinStream);
            var sink = new StreamOutputByteSink(stdoutStream);

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
            stdinStream?.Dispose();
            stdoutStream?.Dispose();
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}