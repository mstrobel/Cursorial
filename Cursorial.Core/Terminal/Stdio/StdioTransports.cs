using System.IO.Pipelines;

using Cursorial.Input;
using Cursorial.Output;

using Microsoft.Win32.SafeHandles;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Public factory for <see cref="IStdioTransports"/>. Dispatches to the platform-specific
/// implementation: POSIX (Linux / macOS / FreeBSD) uses the <c>stty</c> subprocess for raw
/// mode; Windows uses <c>SetConsoleMode</c> for raw input + virtual-terminal output.
/// </summary>
public static class StdioTransports
{
    /// <summary>
    /// Opens the process's <b>redirected</b> standard input as a readable <see cref="Stream"/> — the
    /// piped or file DATA on fd 0 (<c>git branch | app</c>, <c>app &lt; items.txt</c>). Returns
    /// <see langword="null"/> when standard input is <b>not</b> redirected (a real terminal — there is no
    /// piped data to read, and the tty belongs to the interactive session <see cref="Open"/> drives).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the redirected-DATA counterpart to <see cref="Open"/>: <see cref="Open"/> attaches to the
    /// controlling terminal for keystrokes, while a redirected fd 0 carries pipeline data the UI wants to
    /// consume before it takes over the screen. The two are independent fds — reading this stream never
    /// disturbs the interactive terminal.
    /// </para>
    /// <para>
    /// <b>Cross-platform handle resolution.</b> The real standard-input handle is wrapped in a
    /// <b>non-owning</b> <see cref="SafeFileHandle"/> (the process keeps ownership of fd 0 — dispose the
    /// returned <see cref="Stream"/>, but the underlying handle stays open). On Windows the handle comes
    /// from <c>GetStdHandle(STD_INPUT_HANDLE)</c> — the raw fd number <c>0</c> is <b>not</b> a valid
    /// Win32 kernel handle there, so <c>new SafeFileHandle((IntPtr)0, …)</c> throws
    /// <see cref="ArgumentException"/> ("Invalid handle"). On POSIX the file descriptor <c>0</c> <i>is</i>
    /// the kernel handle. <see cref="Console"/> is deliberately bypassed — its stream plumbing can mutate
    /// terminal mode — which is safe precisely because a redirected fd 0 is a pipe/file, never the tty.
    /// </para>
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">Thrown on operating systems other than Linux, macOS, FreeBSD, or Windows.</exception>
    public static Stream? TryOpenRedirectedInput()
    {
        if (!Console.IsInputRedirected)
            return null;

        IntPtr handle;
        if (OperatingSystem.IsWindows())
        {
            handle = WindowsStdioTransports.GetStandardInputHandle();
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            handle = IntPtr.Zero; // POSIX: file descriptor 0 IS the kernel handle
        }
        else
        {
            throw new PlatformNotSupportedException("Cursorial stdio transports are only implemented for " +
                                                    "Linux, macOS, FreeBSD, and Windows.");
        }

        return new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Read);
    }

    /// <summary>
    /// Opens the platform stdio transports, applying raw mode and (on Windows) enabling VT processing.
    /// When stdin and/or stdout is redirected (a pipe or a file — <c>app | less</c>, <c>app &gt; log</c>,
    /// <c>echo x | app</c>), attaches to the controlling terminal for the redirected direction —
    /// <c>/dev/tty</c> on POSIX, <c>CONIN$</c> / <c>CONOUT$</c> on Windows — so the UI keeps driving the
    /// real terminal. Throws only when there is no controlling terminal at all (a daemon / CI with no tty).
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Thrown on operating systems other than Linux, macOS, FreeBSD, or Windows.</exception>
    /// <exception cref="InvalidOperationException">Thrown when neither the standard streams nor a controlling terminal are usable — use the BYO <see cref="TerminalSession.OpenAsync(Input.IInputByteSource, Output.IOutputByteSink, TerminalSessionOptions?, System.Threading.CancellationToken)"/> overload for headless scenarios.</exception>
    public static IStdioTransports Open()
    {
        if (OperatingSystem.IsWindows())
            return WindowsStdioTransports.Open();

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return PosixStdioTransports.Open();

        throw new PlatformNotSupportedException("Cursorial stdio transports are only implemented for " +
                                                "Linux, macOS, FreeBSD, and Windows.");
    }
}

/// <summary>
/// Generic <see cref="IInputByteSource"/> over an arbitrary <see cref="Stream"/>. Used to
/// wrap the process standard input stream from <see cref="Console.OpenStandardInput()"/>;
/// also useful for tests that drive input from a memory or file stream.
/// </summary>
internal sealed class StreamInputByteSource : IInputByteSource
{
    private readonly PipeReader _reader;
    private int _disposed;

    public StreamInputByteSource(Stream stream)
    {
        // leaveOpen: true — the stream is process stdin; we don't own its lifecycle.
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    }

    public PipeReader Reader => _reader;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _reader.CompleteAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Generic <see cref="IOutputByteSink"/> over an arbitrary <see cref="Stream"/>. Used to
/// wrap the process standard output stream from <see cref="Console.OpenStandardOutput()"/>.
/// </summary>
internal sealed class StreamOutputByteSink : IOutputByteSink
{
    private readonly PipeWriter _writer;
    private int _disposed;

    public StreamOutputByteSink(Stream stream)
    {
        // leaveOpen: true — the stream is process stdout; we don't own its lifecycle.
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public PipeWriter Writer => _writer;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort flush — the underlying stream may have closed already.
        }

        await _writer.CompleteAsync().ConfigureAwait(false);
    }
}