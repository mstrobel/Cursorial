using System.Runtime.InteropServices;

using Cursorial.Terminal.Stdio;

namespace Cursorial.Tests.Terminal.Stdio;

/// <summary>
/// Integration tests for <see cref="WindowsConsoleInputByteSource"/> using Win32 anonymous
/// pipes as the input transport. The tests use pipe handles rather than real console handles
/// because tests can't reliably acquire one in an automated environment.
///
/// <para>
/// <b>Pipe-vs-console semantics.</b> A synchronous pipe handle behaves differently from a
/// console handle under <c>WaitForMultipleObjects</c> — pipe handles are essentially
/// "always-signaled" for WFMO, so the pump can end up blocked inside <c>ReadFile</c> waiting
/// for data rather than parked in WFMO. On a real console handle, the pump is reliably parked
/// in WFMO because the handle only signals when input bytes are actually ready. The tests
/// work around the pipe difference by closing the write end before disposing the source — the
/// resulting EOF unblocks the pump's ReadFile, which then exits cleanly. Production console
/// behavior is verified by manual testing against the demo loop on real Windows consoles.
/// </para>
///
/// <para>
/// Windows-only; every test is gated on <see cref="WindowsFactAttribute"/> so the class can
/// live in the cross-platform test project without affecting non-Windows runs.
/// </para>
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
                CloseHandle(writeEnd); // EOF — unblocks pump's ReadFile so dispose can exit cleanly
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
        }
    }

    [WindowsFact]
    public async Task DisposeAsync_AfterEofFromWriter_CompletesPromptly()
    {
        // The pump enters ReadFile and blocks waiting for pipe data. Closing the write end
        // signals EOF to the read end; the pump's ReadFile returns 0 bytes and the pump exits.
        // Disposal completes without needing any cancellation primitive against the handle.
        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new WindowsConsoleInputByteSource(readEnd);
            await Task.Delay(50); // let the pump enter ReadFile

            CloseHandle(writeEnd); // EOF

            var disposeTask = source.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            CloseHandle(readEnd);
        }
    }

    [WindowsFact]
    public async Task DisposeAsync_DoesNotPoisonReadHandle()
    {
        // Regression test for the "CancelIoEx leaks abort state to the next reader" bug. The
        // earlier dispose path called CancelIoEx(handle, NULL) as a belt-and-braces against
        // a pump stuck in ReadFile. On modern Windows console handles that cancel state can
        // persist into the next reader's ReadFile — the user's first post-dispose keystroke
        // arrives as the wakeup for the aborted read and is consumed in the resolution.
        // .NET Console's StreamReader treats ERROR_OPERATION_ABORTED as a retry-able
        // zero-byte read, so the byte vanishes between us and the next ReadLine.
        //
        // After the fix, dispose performs no cancellation against the handle. A subsequent
        // ReadFile on the same handle should succeed cleanly with whatever the pipe / console
        // surfaces — not ERROR_OPERATION_ABORTED.
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
                CloseHandle(writeEnd); // EOF for the pump
                await source.DisposeAsync();
            }

            // After dispose, a direct ReadFile on the read end should return EOF (0 bytes)
            // — NOT ERROR_OPERATION_ABORTED. If dispose called CancelIoEx(handle, NULL) the
            // cancel state could surface here as ABORTED instead of the clean EOF the closed
            // write end should produce.
            Span<byte> buf = stackalloc byte[1];
            bool ok = ReadFile(readEnd, ref buf[0], 1, out uint read, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();

            Assert.True(ok,
                $"ReadFile after dispose returned false (Win32 error {err}); expected clean EOF, not " +
                "ERROR_OPERATION_ABORTED (995) which would indicate dispose poisoned the handle.");
            Assert.Equal(0u, read);
        }
        finally
        {
            CloseHandle(readEnd);
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
                CloseHandle(writeEnd); // EOF — unblocks pump's ReadFile so dispose can exit
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
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
                CloseHandle(writeEnd);
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
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
                CloseHandle(writeEnd);
                await source.DisposeAsync();
            }
        }
        finally
        {
            CloseHandle(readEnd);
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

            // The pump is parked on the run event — no syscall in flight. Dispose just needs
            // to set the run event so the pump wakes, observes the disposed flag, and exits.
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

            CloseHandle(writeEnd); // EOF so the idle pump exits cleanly on dispose
            await source.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await source.PauseAsync());
        }
        finally
        {
            CloseHandle(readEnd);
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
