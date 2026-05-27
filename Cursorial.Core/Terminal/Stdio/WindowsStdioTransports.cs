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
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    // Console output mode flags we set.
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    // GetFileType return values used to pick the stdin source.
    private const uint FILE_TYPE_CHAR = 0x0002;

    private const uint CP_UTF8 = 65001;

    private readonly IntPtr _stdinHandle;
    private readonly IntPtr _stdoutHandle;
    private readonly uint _originalStdinMode;
    private readonly uint _originalStdoutMode;
    private readonly uint _originalOutputCodePage;
    private readonly uint _originalInputCodePage;
    private readonly IInputByteSource _source;
    private readonly StreamOutputByteSink _sink;
    private int _terminalRestored;
    private int _disposed;

    private WindowsStdioTransports(
        IntPtr stdinHandle,
        IntPtr stdoutHandle,
        uint originalStdinMode,
        uint originalStdoutMode,
        uint originalOutputCodePage,
        uint originalInputCodePage,
        IInputByteSource source,
        StreamOutputByteSink sink)
    {
        _stdinHandle = stdinHandle;
        _stdoutHandle = stdoutHandle;
        _originalStdinMode = originalStdinMode;
        _originalStdoutMode = originalStdoutMode;
        _originalOutputCodePage = originalOutputCodePage;
        _originalInputCodePage = originalInputCodePage;
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

        // Raw input — clear processed / line / echo / quick-edit; enable extended flags (so
        // quick-edit can actually be turned off), mouse input, and window-buffer-size events.
        // We DELIBERATELY DO NOT enable ENABLE_VIRTUAL_TERMINAL_INPUT — the conhost-translated
        // VT byte view it would populate leaks orphan bytes across the raw → cooked transition
        // at session end, which is what eats the user's first post-exit keystroke. Instead the
        // input source reads raw INPUT_RECORDs via ReadConsoleInputW and translates them to
        // VT sequences locally (Win32 Input Mode for keys, SGR for mouse, CSI I/O for focus).
        // This matches the pattern Terminal.Gui, Consolonia, and crossterm all use on Windows.
        uint stdinMode = originalStdinMode;
        stdinMode &= ~(ENABLE_PROCESSED_INPUT
                       | ENABLE_LINE_INPUT
                       | ENABLE_ECHO_INPUT
                       | ENABLE_QUICK_EDIT_MODE
                       | ENABLE_VIRTUAL_TERMINAL_INPUT);
        stdinMode |= ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT;

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

        // Snapshot and switch the console codepages to UTF-8. Without this, multi-byte UTF-8
        // sequences emitted by the renderer (box-drawing characters, accented Latin letters,
        // CJK, emoji, …) get reinterpreted through whatever OEM codepage the console was
        // launched with (typically CP437 / CP850 on conhost), and the result is mojibake. The
        // old ENABLE_VIRTUAL_TERMINAL_INPUT path nudged conhost into a UTF-8-aware state as a
        // side effect; with VT_INPUT_MODE disabled we have to do the codepage switch
        // explicitly. RestoreTerminalState reverts both to whatever the original values were.
        uint originalOutputCodePage = GetConsoleOutputCP();
        uint originalInputCodePage = GetConsoleCP();
        SetConsoleOutputCP(CP_UTF8);
        SetConsoleCP(CP_UTF8);

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
                                              originalOutputCodePage,
                                              originalInputCodePage,
                                              source,
                                              sink);
        }
        catch
        {
            // @formatter:off
            // Revert every change we made above, then surface the failure.
            try { SetConsoleMode(stdinHandle, originalStdinMode); } catch { /* best-effort */ }
            try { SetConsoleMode(stdoutHandle, originalStdoutMode); } catch { /* best-effort */ }
            try { SetConsoleOutputCP(originalOutputCodePage); } catch { /* best-effort */ }
            try { SetConsoleCP(originalInputCodePage); } catch { /* best-effort */ }
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

        // Discard any input records still sitting in the console's input queue BEFORE
        // restoring cooked mode. This mirrors the POSIX tcflush(0, TCIFLUSH) call: trailing
        // protocol reports the terminal emitted in response to our opt-in-disable sequences
        // (most visibly a Kitty key-release for whatever key exited the application, plus
        // any final mouse / focus disable responses) can otherwise survive the
        // raw → cooked transition. The leading ESC of an orphaned sequence enters the
        // console's CSI parser in cooked mode and silently consumes whatever character the
        // user types next — the "first keystroke after exit is swallowed" symptom.
        // FlushConsoleInputBuffer is a no-op when the queue is already empty.
        // @formatter:off
        try { FlushConsoleInputBuffer(_stdinHandle); } catch { /* best-effort */ }

        try { SetConsoleMode(_stdinHandle, _originalStdinMode); } catch { /* best-effort */ }
        try { SetConsoleMode(_stdoutHandle, _originalStdoutMode); } catch { /* best-effort */ }

        // Restore the codepages we replaced with UTF-8 at session open.
        try { SetConsoleOutputCP(_originalOutputCodePage); } catch { /* best-effort */ }
        try { SetConsoleCP(_originalInputCodePage); } catch { /* best-effort */ }
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

        // Order is load-bearing on Windows. The source pump uses WaitForMultipleObjects on the
        // stdin handle, and the handle's signaled-state semantics change with the console
        // mode: in raw / VT input mode the handle signals when any byte is ready (microsecond
        // WFMO → ReadFile cycle); in cooked mode it signals only on a complete line. If the
        // pump enters ReadFile after the mode flip to cooked, ReadFile blocks for an Enter
        // press, and SetEvent on the cancel handle can't wake it — dispose hangs. Stopping the
        // source pump while the mode is still raw keeps the pump in WFMO, where the cancel
        // event reliably breaks it out.
        //
        // Sink dispose runs before the mode flip too, so any pending output is flushed while
        // the terminal is still in VT-processing mode — protects against (in principle —
        // empty in practice) trailing SGR / cursor bytes being rendered literally.
        //
        // RestoreTerminalState runs last because that's where FlushConsoleInputBuffer drains
        // any input records still sitting in the conhost queue. The pump is gone by then, so
        // anything in the queue (trailing protocol reports from the negotiator's disable
        // sequences, plus anything the user has typed during the dispose window) is safely
        // discarded before cooked mode takes over.
        // @formatter:off
        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { await _sink.DisposeAsync().ConfigureAwait(false); }   catch { /* best-effort */ }

        RestoreTerminalState();
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

    /// <summary><c>FlushConsoleInputBuffer</c> — discard every input record currently in the
    /// console's input queue. The Windows analogue of POSIX <c>tcflush(fd, TCIFLUSH)</c>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static partial bool FlushConsoleInputBuffer(IntPtr hConsoleHandle);

    /// <summary><c>GetConsoleOutputCP</c> — current codepage the console uses to interpret bytes
    /// written via <c>WriteFile</c> / <c>WriteConsole</c>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleOutputCP();

    /// <summary><c>SetConsoleOutputCP</c> — switch the console's output codepage.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static partial bool SetConsoleOutputCP(uint wCodePageID);

    /// <summary><c>GetConsoleCP</c> — current codepage the console uses for input via
    /// <c>ReadFile</c> / <c>ReadConsoleA</c>. (<c>ReadConsoleW</c> / <c>ReadConsoleInputW</c>
    /// bypass it, but other readers may not.)</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleCP();

    /// <summary><c>SetConsoleCP</c> — switch the console's input codepage.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static partial bool SetConsoleCP(uint wCodePageID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        IntPtr hFile,
        ref byte lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);
}