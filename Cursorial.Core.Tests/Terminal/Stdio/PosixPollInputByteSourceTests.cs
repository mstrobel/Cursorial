using System.Runtime.InteropServices;

using Cursorial.Terminal.Stdio;

namespace Cursorial.Tests.Terminal.Stdio;

/// <summary>
/// Integration tests for <see cref="PosixPollInputByteSource"/> using real OS pipes. POSIX-only;
/// every test early-returns on Windows so the class can still live in the cross-platform
/// test project.
/// </summary>
public partial class PosixPollInputByteSourceTests
{
    [Fact]
    public async Task PauseAsync_StopsBytesFromReachingReader_UntilHandleDisposed()
    {
        if (!IsPosix()) return;

        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new PosixPollInputByteSource(readEnd);

            try
            {
                // Pre-pause: byte should flow through.
                WriteByte(writeEnd, 0x41);
                var pre = await ReadOneByteAsync(source);
                Assert.Equal((byte) 0x41, pre);

                var pause = await source.PauseAsync();

                // During pause: kernel pipe buffers the byte; PosixPollInputByteSource must
                // NOT have a read(2) in flight, so the byte stays in the kernel.
                WriteByte(writeEnd, 0x42);

                // The reader.ReadAsync should NOT return our pause-window byte. Give the
                // pump a generous window to (incorrectly) read if it were going to.
                var raceTask = ReadOneByteAsync(source).AsTask();
                var winner = await Task.WhenAny(raceTask, Task.Delay(150));
                Assert.NotSame(raceTask, winner);

                await pause.DisposeAsync();

                // After resume: the buffered byte plus any subsequent byte both arrive.
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
            Close(readEnd);
            Close(writeEnd);
        }
    }

    [Fact]
    public async Task PauseAsync_OverlappingHandles_PumpResumesOnlyAfterAllDisposed()
    {
        if (!IsPosix()) return;

        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new PosixPollInputByteSource(readEnd);
            try
            {
                var outer = await source.PauseAsync();
                var inner = await source.PauseAsync(); // second pause while already paused

                // Write a byte; it should be buffered by the kernel, not by our pump.
                WriteByte(writeEnd, 0x55);

                await inner.DisposeAsync();

                // Even after inner releases, outer keeps the pump parked — the byte still
                // hasn't reached our reader.
                var raceTask = ReadOneByteAsync(source).AsTask();
                var winner = await Task.WhenAny(raceTask, Task.Delay(100));
                Assert.NotSame(raceTask, winner);

                await outer.DisposeAsync();

                // Now the pump resumes — the buffered byte arrives.
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
            Close(readEnd);
            Close(writeEnd);
        }
    }

    [Fact]
    public async Task PauseScope_DoubleDispose_DoesNotDoubleDecrement()
    {
        if (!IsPosix()) return;

        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new PosixPollInputByteSource(readEnd);
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
            Close(readEnd);
            Close(writeEnd);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhilePaused_UnblocksPumpAndCompletes()
    {
        if (!IsPosix()) return;

        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new PosixPollInputByteSource(readEnd);
            _ = await source.PauseAsync();

            // Pump is parked on the run event. Dispose must wake it without leaving a libc
            // operation pending (the read(2) wasn't started anyway) and without deadlocking.
            var disposeTask = source.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Close(readEnd);
            Close(writeEnd);
        }
    }

    [Fact]
    public async Task PauseAsync_AfterDispose_Throws()
    {
        if (!IsPosix()) return;

        var (readEnd, writeEnd) = CreatePipe();
        try
        {
            var source = new PosixPollInputByteSource(readEnd);
            await source.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await source.PauseAsync());
        }
        finally
        {
            Close(readEnd);
            Close(writeEnd);
        }
    }

    private static bool IsPosix() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    private static (int Read, int Write) CreatePipe()
    {
        Span<int> ends = stackalloc int[2];
        int rc = Pipe(ref ends[0]);
        if (rc != 0)
            throw new InvalidOperationException($"pipe(2) failed (errno {Marshal.GetLastWin32Error()}).");
        return (ends[0], ends[1]);
    }

    private static void WriteByte(int fd, byte b)
    {
        byte local = b;
        var written = Write(fd, ref local, 1);
        if (written != 1)
            throw new InvalidOperationException($"write(2) returned {written}.");
    }

    private static async ValueTask<byte> ReadOneByteAsync(PosixPollInputByteSource source)
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

    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static partial int Pipe(ref int fds);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, ref byte buf, nuint count);

    // ReSharper disable once UnusedMethodReturnValue.Local
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
