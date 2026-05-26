using System.Runtime.InteropServices;

using Cursorial.Terminal.Stdio;

namespace Cursorial.Tests.Terminal.Stdio;

/// <summary>
/// Integration tests for <see cref="WindowsConsoleInputByteSource"/> using Win32 anonymous
/// pipes as the input transport. The tests use pipe handles rather than real console handles
/// because tests can't reliably acquire one in an automated environment; the production code
/// paths are exercised structurally the same way on a pipe, with one Win32-specific quirk —
/// <c>WaitForMultipleObjects</c> on a synchronous pipe handle isn't a real "wait for input
/// ready" the way it is on a console handle, so the pump can end up briefly blocked inside
/// <c>ReadFile</c> rather than <c>WaitForMultipleObjects</c>. The source's <c>CancelIoEx</c>
/// belt-and-braces during pause and dispose covers that case, which these tests verify.
///
/// Windows-only; every test is gated on <see cref="WindowsFactAttribute"/> so the class can
/// live in the cross-platform test project without affecting non-Windows runs.
/// </summary>
public partial class WindowsConsoleInputByteSourceTests
{
    [WindowsFact]
    public async Task Pump_ReadsBytesFromHandle()
    {
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            try
            {
                WriteByte(writeEnd, 0x41);
                var b = await ReadOneByteAsync(source);
                Assert.Equal((byte) 0x41, b);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task DisposeAsync_AbortsBlockedReadFile_AndCompletesPromptly()
    {
        // Create source with no data in the pipe. The pump immediately enters ReadFile and
        // blocks waiting for input. DisposeAsync must wake it via CancelIoEx (the cancel
        // event alone won't help because the pump isn't in WaitForMultipleObjects).
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);

            // Give the pump a moment to actually enter ReadFile so we're exercising the
            // CancelIoEx path, not just the "dispose before pump runs" trivial path.
            await Task.Delay(50);

            var disposeTask = source.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task DisposeAsync_DoesNotConsumeBytesWrittenAfterDispose()
    {
        // The core "zombie-read fix" property: bytes written to the handle AFTER the source
        // is disposed must remain available for the next reader. A pre-fix
        // PipeReader.Create(FileStream) chain would have a thread-pool worker stuck inside
        // ReadFile that would consume the post-dispose byte and drop it on the floor.
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            await Task.Delay(50); // let the pump enter ReadFile
            await source.DisposeAsync();

            WriteByte(writeEnd, 0x55);

            // Read directly from the pipe handle — the source's pump shouldn't have stolen
            // this byte. If the fix is broken, ReadFile here would either block (the pump's
            // zombie read got the byte) or return EOF.
            byte b = ReadOneByteFromHandle(readEnd);
            Assert.Equal((byte) 0x55, b);
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task PauseAsync_StopsBytesFromReachingReader_UntilHandleDisposed()
    {
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            try
            {
                // Pre-pause: byte should flow through normally.
                WriteByte(writeEnd, 0x41);
                var pre = await ReadOneByteAsync(source);
                Assert.Equal((byte) 0x41, pre);

                var pause = await source.PauseAsync();

                // During pause: the kernel pipe buffers the byte; the source's pump is parked
                // (on the run event) so the byte never reaches the source's PipeReader.
                WriteByte(writeEnd, 0x42);

                var raceTask = ReadOneByteAsync(source).AsTask();
                var winner = await Task.WhenAny(raceTask, Task.Delay(150));
                Assert.NotSame(raceTask, winner);

                await pause.DisposeAsync();

                // After resume the buffered byte flows through, followed by anything we
                // write afterward.
                var afterResume = await raceTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal((byte) 0x42, afterResume);

                WriteByte(writeEnd, 0x43);
                var post = await ReadOneByteAsync(source);
                Assert.Equal((byte) 0x43, post);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task PauseAsync_OverlappingHandles_PumpResumesOnlyAfterAllDisposed()
    {
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            try
            {
                var outer = await source.PauseAsync();
                var inner = await source.PauseAsync();

                WriteByte(writeEnd, 0x55);

                await inner.DisposeAsync();

                // Outer share still holds — the buffered byte still hasn't reached our reader.
                var raceTask = ReadOneByteAsync(source).AsTask();
                var winner = await Task.WhenAny(raceTask, Task.Delay(100));
                Assert.NotSame(raceTask, winner);

                await outer.DisposeAsync();

                var b = await raceTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal((byte) 0x55, b);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task PauseScope_DoubleDispose_DoesNotDoubleDecrement()
    {
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            try
            {
                var outer = await source.PauseAsync();
                var inner = await source.PauseAsync();

                // Disposing inner twice should NOT also release outer's share.
                await inner.DisposeAsync();
                await inner.DisposeAsync();

                WriteByte(writeEnd, 0x66);
                var raceTask = ReadOneByteAsync(source).AsTask();
                var winner = await Task.WhenAny(raceTask, Task.Delay(100));
                Assert.NotSame(raceTask, winner); // outer still holds the pause

                await outer.DisposeAsync();
                var b = await raceTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal((byte) 0x66, b);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task DisposeAsync_WhilePaused_UnblocksPumpAndCompletes()
    {
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            _ = await source.PauseAsync();

            // The pump is parked on the run event. Dispose must wake it without leaving an
            // unfinished syscall (no in-flight ReadFile to abort, since pause already aborted
            // any in-progress read) and without deadlocking on the run event.
            var disposeTask = source.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    [WindowsFact]
    public async Task PauseAsync_AfterDispose_Throws()
    {
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            await source.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await source.PauseAsync());
        }
        finally
        {
            CloseHandle(readEnd);
            CloseHandle(writeEnd);
        }
    }

    // ---- Helpers ----

    private const uint PIPE_BUFFER_SIZE = 4096;

    private static (IntPtr Read, IntPtr Write) CreatePipe()
    {
        if (!CreatePipe(out IntPtr read, out IntPtr write, IntPtr.Zero, PIPE_BUFFER_SIZE))
            throw new InvalidOperationException($"CreatePipe failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return (read, write);
    }

    private static void WriteByte(IntPtr handle, byte b)
    {
        byte local = b;
        if (!WriteFile(handle, ref local, 1, out uint written, IntPtr.Zero) || written != 1)
            throw new InvalidOperationException($"WriteFile failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static byte ReadOneByteFromHandle(IntPtr handle)
    {
        Span<byte> buf = stackalloc byte[1];
        if (!ReadFile(handle, ref buf[0], 1, out uint read, IntPtr.Zero) || read != 1)
            throw new InvalidOperationException($"ReadFile failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return buf[0];
    }

    private static async ValueTask<byte> ReadOneByteAsync(WindowsConsoleInputByteSource source)
    {
        var result = await source.Reader.ReadAsync();
        try
        {
            var buf = result.Buffer;
            if (buf.IsEmpty)
                throw new InvalidOperationException("Reader returned empty buffer.");

            byte first = buf.First.Span[0];
            source.Reader.AdvanceTo(buf.GetPosition(1));
            return first;
        }
        catch
        {
            source.Reader.AdvanceTo(result.Buffer.Start);
            throw;
        }
    }

    // ---- P/Invokes ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(
        IntPtr hFile,
        ref byte lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        IntPtr hFile,
        ref byte lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    // ReSharper disable once UnusedMethodReturnValue.Local
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
