using System.Runtime.InteropServices;
using GlowTerm.Core.Input;
using GlowTerm.Core.Output;

namespace GlowTerm.Core.Terminal.Stdio;

/// <summary>
/// Windows implementation of <see cref="IStdioTransports"/>. Disables echo / line / processed
/// input on the console input handle, enables virtual-terminal input/output processing, and
/// restores the prior console mode flags on disposal. Targets the modern ConPTY model where
/// stdin and stdout are byte streams that carry VT sequences in both directions.
/// </summary>
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
            throw new InvalidOperationException(
                $"SetConsoleMode failed for the console output handle (Win32 error {err}).");
        }

        var source = new StreamInputByteSource(Console.OpenStandardInput());
        var sink = new StreamOutputByteSink(Console.OpenStandardOutput());

        return new WindowsStdioTransports(
            stdinHandle, stdoutHandle, originalStdinMode, originalStdoutMode, source, sink);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Restore prior console modes BEFORE closing transports.
        try { SetConsoleMode(_stdinHandle, _originalStdinMode); } catch { }
        try { SetConsoleMode(_stdoutHandle, _originalStdoutMode); } catch { }

        try { await _sink.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { }
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
