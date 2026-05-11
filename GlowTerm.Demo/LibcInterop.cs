using System.Runtime.InteropServices;

namespace GlowTerm.Demo;

/// <summary>
/// Direct libc P/Invoke for the diagnostic commands that need to bypass <see cref="Console"/>
/// — read / write on fd 0 / 1 without ever touching the .NET Console subsystem.
/// </summary>
internal static unsafe partial class LibcInterop
{
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int poll(PollFd* fds, nuint nfds, int timeoutMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private const short POLLIN = 0x0001;

    public static int ReadFd(int fd, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return (int)read(fd, p, (nuint)buffer.Length);
        }
    }

    public static int WriteFd(int fd, ReadOnlySpan<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return (int)write(fd, p, (nuint)buffer.Length);
        }
    }

    public static bool PollFdHasInput(int fd, int timeoutMs)
    {
        var p = new PollFd { Fd = fd, Events = POLLIN };
        int rc = poll(&p, 1, timeoutMs);
        return rc > 0 && (p.Revents & POLLIN) != 0;
    }
}
