using System.IO.Pipelines;

using Cursorial.Input;
using Cursorial.Output;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Public factory for <see cref="IStdioTransports"/>. Dispatches to the platform-specific
/// implementation: POSIX (Linux / macOS / FreeBSD) uses the <c>stty</c> subprocess for raw
/// mode; Windows uses <c>SetConsoleMode</c> for raw input + virtual-terminal output.
/// </summary>
public static class StdioTransports
{
    /// <summary>
    /// Opens the platform stdio transports, applying raw mode and (on Windows) enabling VT
    /// processing. Throws when the standard streams are not connected to a terminal.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Thrown on operating systems other than Linux, macOS, FreeBSD, or Windows.</exception>
    /// <exception cref="InvalidOperationException">Thrown when raw mode cannot be applied — typically because stdin or stdout is redirected to a non-terminal.</exception>
    public static IStdioTransports Open()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsStdioTransports.Open();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return PosixStdioTransports.Open();
        }

        throw new PlatformNotSupportedException(
            "Cursorial stdio transports are only implemented for Linux, macOS, FreeBSD, and Windows.");
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
