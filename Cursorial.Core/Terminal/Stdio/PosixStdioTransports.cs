using System.Diagnostics;
using System.Runtime.InteropServices;

using Cursorial.Input;
using Cursorial.Output;

using Microsoft.Win32.SafeHandles;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// POSIX implementation of <see cref="IStdioTransports"/>. Applies raw terminal mode via termios
/// (<c>tcgetattr</c> / <c>cfmakeraw</c> / <c>tcsetattr</c>), falling back to the <c>stty</c> subprocess
/// only if termios is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redirected I/O.</b> When stdin and/or stdout is not a terminal — a pipe or a file, e.g.
/// <c>app | less</c>, <c>app &gt; log</c>, <c>echo x | app</c> — the process's fd 0 / fd 1 point at the
/// pipe, not the terminal. We open the controlling terminal <c>/dev/tty</c> and route the redirected
/// direction(s) through it, so the UI keeps rendering on (and reading from) the real terminal. A single
/// <c>O_RDWR</c> fd serves both directions; we OWN it and <c>close</c> it on dispose — fd 0 / fd 1 are
/// process-global and are never closed. When there is no controlling terminal at all (a daemon / CI with
/// no tty), the open fails and we throw, pointing the caller at the BYO
/// <c>TerminalSession.OpenAsync(source, sink)</c> overload.
/// </para>
/// <para>
/// <b>Critical: do not call <see cref="Console.OpenStandardInput()"/> /
/// <see cref="Console.OpenStandardOutput()"/>.</b> The .NET Console subsystem on Unix manages its own
/// termios state — it ensures Ctrl+C generates SIGINT, that the cursor is visible at exit, etc. — and
/// accessing those streams silently mutates termios, reverting our raw mode. We wrap the resolved fds as
/// <see cref="FileStream"/> over a non-owning <see cref="SafeFileHandle"/> instead, which goes through
/// generic file I/O and doesn't touch Console internals.
/// </para>
/// <para>
/// <b>The stty fallback must not redirect stty's streams via .NET.</b> Even redirecting stderr makes the
/// mode change silently no-op (stty exits 0 but the termios bits read back unchanged). When the fallback
/// must target the controlling terminal it uses a shell redirect (<c>sh -c "stty … &lt; /dev/tty"</c>), a
/// real fd — not a .NET pipe.
/// </para>
/// </remarks>
internal sealed partial class PosixStdioTransports : IStdioTransports
{
    // termios optional_actions "apply immediately" — 0 on Linux, macOS, and FreeBSD.
    private const int TCSANOW = 0;

    // tcflush(fd, queue_selector) for "discard input queue" (TCIFLUSH): 0 on Linux, 1 on macOS/BSD.
    private const int LinuxTciFlush = 0;
    private const int MacosTciFlush = 1;

    // open(2) flags. O_RDWR is 2 everywhere; O_NOCTTY differs per platform (so opening /dev/tty never
    // accidentally becomes the controlling terminal — belt-and-braces, since /dev/tty already IS it).
    private const int ORdwr = 2;

    // A buffer comfortably larger than struct termios on any supported platform (Linux ~60, macOS ~72,
    // FreeBSD ~44 bytes). We never interpret the struct — cfmakeraw does the platform-specific flag
    // surgery in place — so its exact layout doesn't matter here.
    private const int TermiosBufferSize = 128;

    private readonly byte[]? _savedTermios;   // non-null when raw mode was applied via termios
    private readonly string? _savedSttyState; // non-null when applied via the stty fallback
    private readonly bool _fallbackViaControllingTty;

    private readonly int _inputFd;
    private readonly int _outputFd;
    private readonly int _ownedTtyFd;         // -1 when we opened no /dev/tty fd

    private readonly IInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
    private int _terminalRestored;
    private int _disposed;

    private PosixStdioTransports(
        byte[]? savedTermios, string? savedSttyState, bool fallbackViaControllingTty,
        int inputFd, int outputFd, int ownedTtyFd,
        IInputByteSource source, StreamOutputByteSink sink)
    {
        _savedTermios = savedTermios;
        _savedSttyState = savedSttyState;
        _fallbackViaControllingTty = fallbackViaControllingTty;
        _inputFd = inputFd;
        _outputFd = outputFd;
        _ownedTtyFd = ownedTtyFd;
        _source = source;
        _sink = sink;
    }

    public IInputByteSource Source => _source;
    public IOutputByteSink Sink => _sink;

    public static PosixStdioTransports Open()
    {
        bool stdinIsTty = IsTty(0);
        bool stdoutIsTty = IsTty(1);

        int ownedTtyFd = -1;
        int inputFd = 0;
        int outputFd = 1;

        // If either side is redirected, attach to the controlling terminal for it. One O_RDWR fd serves
        // both directions; we own and close it. If /dev/tty can't be opened there is no terminal to
        // attach to — throw and point at the BYO overload.
        if (!stdinIsTty || !stdoutIsTty)
        {
            int ttyFd = OpenControllingTty();

            if (ttyFd < 0 || !IsTty(ttyFd))
            {
                if (ttyFd >= 0) Close(ttyFd);
                throw new InvalidOperationException(
                    "Standard input/output is redirected and no controlling terminal (/dev/tty) is " +
                    "available (running under a pipe or in CI). Use the BYO " +
                    "TerminalSession.OpenAsync(source, sink) overload for non-terminal scenarios.");
            }

            ownedTtyFd = ttyFd;
            if (!stdinIsTty) inputFd = ttyFd;
            if (!stdoutIsTty) outputFd = ttyFd;
        }

        // Raw mode is set on the input terminal — the fd we read keys from, always a real tty here.
        // When the fallback has to drive stty, it targets the controlling terminal by a shell redirect
        // iff the input fd is not the process's own fd 0 (i.e. stdin was redirected).
        bool fallbackViaTty = inputFd != 0;
        byte[]? savedTermios = null;
        string? savedStty = null;

        if (!TryApplyRawModeViaTermios(inputFd, out savedTermios))
        {
            savedStty = CaptureSttyState(fallbackViaTty);

            if (savedStty is null)
            {
                if (ownedTtyFd >= 0) Close(ownedTtyFd);
                throw new InvalidOperationException(
                    "Failed to enter raw mode: termios is unavailable and `stty -g` failed. Standard " +
                    "input is likely not a terminal. Use the BYO TerminalSession.OpenAsync(source, sink) " +
                    "overload for non-TTY scenarios.");
            }

            ApplySttyMode("-icanon -echo -isig -iexten -ixon -opost min 1 time 0", fallbackViaTty);
        }

        try
        {
            // Output: wrap the resolved output fd. ownsHandle: false — fd 1 is process-global, and the
            // /dev/tty fd (when it is the output fd) is closed by us via _ownedTtyFd exactly once.
            var stdoutHandle = new SafeFileHandle((IntPtr) outputFd, ownsHandle: false);
            var stdoutStream = new FileStream(stdoutHandle, FileAccess.Write);
            var sink = new StreamOutputByteSink(stdoutStream);

            // Input: poll(2)-based source with self-pipe wakeup over the resolved input fd. We do NOT wrap
            // the fd in a FileStream/PipeReader chain — on POSIX that leaves a zombie read(2) blocked in
            // the kernel after disposal, eating the next byte typed at the following shell prompt.
            var source = new PosixPollInputByteSource(fd: inputFd);

            return new PosixStdioTransports(savedTermios, savedStty, fallbackViaTty,
                                            inputFd, outputFd, ownedTtyFd, source, sink);
        }
        catch
        {
            // @formatter:off
            // Best-effort revert if anything went wrong after raw mode was applied.
            try { RestoreRawMode(inputFd, savedTermios, savedStty, fallbackViaTty); } catch { /* best-effort */ }
            if (ownedTtyFd >= 0) { try { Close(ownedTtyFd); } catch { /* best-effort */ } }
            throw;
            // @formatter:on
        }
    }

    public void RestoreTerminalState()
    {
        // Idempotent — guarded so signal-handler invocations don't run multiple times.
        if (Interlocked.Exchange(ref _terminalRestored, 1) != 0) return;

        // Discard bytes still in the TTY's input queue BEFORE restoring cooked mode. Without this,
        // trailing protocol reports the terminal emitted in response to our opt-in-disable sequences
        // (most visibly a Kitty key-release for the exit key) survive the raw→cooked transition and get
        // delivered to the next cooked-mode ReadLine as part of the user's next line. A no-op when empty.
        FlushInputQueue();

        RestoreRawMode(_inputFd, _savedTermios, _savedSttyState, _fallbackViaControllingTty);
    }

    private static void RestoreRawMode(int fd, byte[]? savedTermios, string? savedStty, bool viaControllingTty)
    {
        // @formatter:off
        if (savedTermios is not null)
        {
            try { TcSetAttr(fd, TCSANOW, ref savedTermios[0]); } catch { /* best-effort — terminal may have detached */ }
        }
        else if (savedStty is not null)
        {
            try { ApplySttyMode(savedStty, viaControllingTty); } catch { /* best-effort */ }
        }
        // @formatter:on
    }

    private void FlushInputQueue()
    {
        int selector = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()
                           ? MacosTciFlush
                           : LinuxTciFlush;

        // @formatter:off
        try { TcFlush(_inputFd, selector); } catch { /* best-effort */ }
        // @formatter:on
    }

    public void WriteBytesSync(ReadOnlySpan<byte> bytes)
    {
        // Loop in case write(2) returns a short count (TTYs in raw mode generally don't, but POSIX
        // permits it). EINTR is retried — we're often called from a signal-handler path ourselves.
        while (!bytes.IsEmpty)
        {
            nint n = Write(_outputFd, ref MemoryMarshal.GetReference(bytes), (nuint) bytes.Length);
            if (n < 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 4 /* EINTR */) continue;
                return; // best-effort — broken pipe / unwriteable fd
            }

            if (n == 0) return;
            bytes = bytes[(int) n..];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Restore terminal state BEFORE closing transports (the sync method covers the critical termios
        // restore; safe whether or not a signal handler already triggered it).
        RestoreTerminalState();

        // @formatter:off
        try { await _sink.DisposeAsync().ConfigureAwait(false); }   catch { /* best-effort */ }
        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }

        // Close the controlling-terminal fd we opened, if any. fd 0 / fd 1 are process-global and left
        // untouched. Done last, after both transports over the fd have been torn down.
        if (_ownedTtyFd >= 0) { try { Close(_ownedTtyFd); } catch { /* best-effort */ } }
        // @formatter:on
    }

    // ---- raw mode: termios primary, stty fallback ----

    /// <summary>
    /// Enter raw mode on <paramref name="fd"/> via termios, capturing the prior state in
    /// <paramref name="saved"/> for restore. Returns false (leaving <paramref name="saved"/> null) when
    /// termios is unavailable, so the caller can fall back to stty. cfmakeraw does the platform-specific
    /// flag surgery — including VMIN=1 / VTIME=0 — so we never interpret the struct.
    /// </summary>
    private static bool TryApplyRawModeViaTermios(int fd, out byte[]? saved)
    {
        saved = null;

        var current = new byte[TermiosBufferSize];

        try
        {
            if (TcGetAttr(fd, ref current[0]) != 0)
                return false;

            var raw = (byte[]) current.Clone();
            CfMakeRaw(ref raw[0]);

            if (TcSetAttr(fd, TCSANOW, ref raw[0]) != 0)
                return false;
        }
        catch (DllNotFoundException)       { return false; } // no libc
        catch (EntryPointNotFoundException) { return false; } // cfmakeraw / tc* missing

        saved = current;
        return true;
    }

    /// <summary>
    /// Capture stty state via <c>stty -g</c> for the fallback path. When
    /// <paramref name="viaControllingTty"/> is set, redirect the controlling terminal into stty with a
    /// shell (a real fd) so it works even when the app's own stdin is redirected.
    /// </summary>
    private static string? CaptureSttyState(bool viaControllingTty)
    {
        try
        {
            var psi = viaControllingTty
                          ? new ProcessStartInfo { FileName = "sh", ArgumentList = { "-c", "stty -g < /dev/tty" } }
                          : new ProcessStartInfo { FileName = "stty", Arguments = "-g" };

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = false;
            psi.RedirectStandardInput = false;

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Apply a stty mode change for the fallback path. The apply must not have stty's streams redirected
    /// by .NET; when targeting the controlling terminal we redirect <c>/dev/tty</c> in with a shell (a
    /// real fd). Errors are swallowed — a half-modified terminal is worse than carrying on.
    /// </summary>
    private static void ApplySttyMode(string arguments, bool viaControllingTty)
    {
        var psi = viaControllingTty
                      ? new ProcessStartInfo { FileName = "sh", ArgumentList = { "-c", $"stty {arguments} < /dev/tty" } }
                      : new ProcessStartInfo { FileName = "stty", Arguments = arguments };

        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = false;
        psi.RedirectStandardError = false;
        psi.RedirectStandardInput = false;

        using var process = Process.Start(psi);
        process?.WaitForExit(3000);
    }

    // ---- controlling-terminal / fd helpers ----

    private static bool IsTty(int fd)
    {
        // @formatter:off
        try { return IsAtty(fd) == 1; }
        catch (DllNotFoundException) { return false; }
        // @formatter:on
    }

    private static int OpenControllingTty()
    {
        int oNoCtty = OperatingSystem.IsMacOS()   ? 0x20000 :
                      OperatingSystem.IsFreeBSD() ? 0x8000  :
                                                    0x100;   // Linux

        // Prefer the REAL terminal device (a tty fd's ttyname) over the /dev/tty clone device. macOS
        // poll(2) returns POLLNVAL for /dev/tty — an input pump polling it busy-spins and never reads a
        // byte — but poll works on the real pty slave (which is why the un-redirected fd 0 path is fine).
        // stdin/stdout/stderr, when a tty, all name the same controlling terminal, so resolve the path from
        // whichever is still attached and open THAT.
        foreach (int stdFd in new[] { 0, 1, 2 })
        {
            if (!IsTty(stdFd) || !TryTtyName(stdFd, out var path))
                continue;

            int viaName = OpenPath(path, oNoCtty);
            if (viaName >= 0 && IsTty(viaName))
                return viaName;

            if (viaName >= 0)
                Close(viaName);
        }

        // Every standard stream is redirected (no tty to name) — fall back to /dev/tty. macOS poll will
        // still fail here, but that fully-headless case belongs on the BYO TerminalSession path anyway.
        return OpenPath("/dev/tty", oNoCtty);
    }

    private static int OpenPath(string path, int oNoCtty)
    {
        // @formatter:off
        try { return Open(path, ORdwr | oNoCtty); }
        catch (DllNotFoundException) { return -1; }
        // @formatter:on
    }

    /// <summary>Resolve the terminal device path backing a tty <paramref name="fd"/> via <c>ttyname_r</c>.</summary>
    private static bool TryTtyName(int fd, out string path)
    {
        path = string.Empty;

        try
        {
            var buffer = new byte[256];
            if (TtyNameR(fd, ref buffer[0], (nuint) buffer.Length) != 0)
                return false;

            int length = Array.IndexOf(buffer, (byte) 0);
            if (length <= 0)
                return false;

            path = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
            return true;
        }
        catch (DllNotFoundException)        { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static void Close(int fd)
    {
        // @formatter:off
        try { CloseNative(fd); } catch { /* best-effort */ }
        // @formatter:on
    }

    // ReSharper disable UnusedMethodReturnValue.Local

    /// <summary><c>isatty(int fd)</c> — 1 if fd refers to a terminal.</summary>
    [LibraryImport("libc", EntryPoint = "isatty", SetLastError = true)]
    private static partial int IsAtty(int fd);

    /// <summary><c>open(const char *path, int flags)</c> — open a file/device; returns an fd or -1.</summary>
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    /// <summary><c>ttyname_r(int fd, char *buf, size_t buflen)</c> — the terminal device path for a tty fd; 0 on success.</summary>
    [LibraryImport("libc", EntryPoint = "ttyname_r", SetLastError = true)]
    private static partial int TtyNameR(int fd, ref byte buf, nuint buflen);

    /// <summary><c>close(int fd)</c>.</summary>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fd);

    /// <summary><c>tcgetattr(int fd, struct termios *)</c> — read the terminal attributes into the buffer.</summary>
    [LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static partial int TcGetAttr(int fd, ref byte termios);

    /// <summary><c>tcsetattr(int fd, int optional_actions, const struct termios *)</c>.</summary>
    [LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    private static partial int TcSetAttr(int fd, int optionalActions, ref byte termios);

    /// <summary><c>cfmakeraw(struct termios *)</c> — set the raw-mode flags in place, platform-aware.</summary>
    [LibraryImport("libc", EntryPoint = "cfmakeraw")]
    private static partial void CfMakeRaw(ref byte termios);

    /// <summary><c>tcflush(int fd, int queue_selector)</c> — discard pending TTY data.</summary>
    [LibraryImport("libc", EntryPoint = "tcflush", SetLastError = true)]
    private static partial int TcFlush(int fd, int queueSelector);

    /// <summary><c>write(int fd, const void *buf, size_t count)</c> — write up to count bytes.</summary>
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, ref byte buf, nuint count);

    // ReSharper restore UnusedMethodReturnValue.Local
}
