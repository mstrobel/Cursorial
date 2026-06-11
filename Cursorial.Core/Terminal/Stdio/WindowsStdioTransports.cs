using System.Reflection;
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
/// <b>Stdin source selection.</b> A console-typed stdin (where <c>GetConsoleMode</c> succeeds)
/// always goes through <see cref="WindowsConsoleInputByteSource"/> — the WFMO + ReadConsoleInputW
/// pump that sidesteps the "first keystroke after disposal can be swallowed" bug that any
/// <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c>-based byte-stream view of conhost exhibits in the
/// raw → cooked transition. The console mode leaves <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> off
/// and turns on <c>ENABLE_MOUSE_INPUT</c> + <c>ENABLE_WINDOW_INPUT</c> so records carry mouse
/// and resize events directly.
/// </para>
/// <para>
/// The resolved <see cref="TerminalFamily"/> (via
/// <see cref="VtTerminalNegotiator.ResolveIdentification(IEnvironmentReader?, TerminalFamily?)"/>)
/// only changes
/// the translator's behavior, not the read path:
/// <see cref="TerminalFamily.WindowsTerminal"/> / <see cref="TerminalFamily.WindowsConsoleHost"/>
/// get the lossless conhost translation (every key, including IME composition with
/// <c>vk=0</c>, becomes a Win32 Input Mode envelope); any other family — third-party emulator
/// running atop ConPTY (Alacritty, WezTerm, Ghostty on Windows, MinTTY, …) — gets the
/// <c>passThroughVtBytes</c> path, where the byte-per-record shape ConPTY uses for VT
/// sequences it can't classify as a key is emitted as the raw ASCII byte. That lets the
/// negotiator's classifier read DA1 / XTVERSION / OSC color responses / Kitty keyboard
/// reports verbatim while real keys still ride the Win32 Input Mode envelope.
/// </para>
/// <para>
/// Non-console stdin (<c>GetConsoleMode</c> fails — a pipe / disk / remote handle seen under
/// MSYS2, native Git Bash without ConPTY, tmux over SSH, CI runners, or any redirection)
/// falls through to <see cref="StreamInputByteSource"/> over a <see cref="FileStream"/>.
/// There's no conhost in the middle on that path, so the pipe carries the emulator's VT
/// bytes directly. The same console-vs-pipe distinction applies to stdout. The
/// <see cref="TerminalFamily.WindowsTerminal"/> / <see cref="TerminalFamily.WindowsConsoleHost"/>
/// families REQUIRE a console handle (by definition); attempting the parameterless open under
/// those families against a pipe throws so callers reach for the BYO overload instead.
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
        var family = VtTerminalNegotiator.ResolveIdentification().Family;

        bool isNativeConsole = family is TerminalFamily.WindowsTerminal
                                      or TerminalFamily.WindowsConsoleHost;

        var stdinHandle = GetStdHandle(STD_INPUT_HANDLE);
        var stdoutHandle = GetStdHandle(STD_OUTPUT_HANDLE);

        // GetConsoleMode succeeds iff the handle is a real console (conhost / ConPTY). For
        // pipe-backed stdin (MSYS2 / native Git Bash / tmux over SSH / CI redirection) it
        // fails — we treat that as "not a console, no mode setup needed" and fall through to
        // a pure FileStream-based transport. The native console families REQUIRE a real
        // console — throw early so callers reach for the BYO overload instead.
        bool stdinIsConsole = GetConsoleMode(stdinHandle, out uint originalStdinMode);
        bool stdoutIsConsole = GetConsoleMode(stdoutHandle, out uint originalStdoutMode);

        if (isNativeConsole && !stdinIsConsole)
        {
            throw new InvalidOperationException(
                "Standard input is not connected to a console. Use the BYO " +
                "TerminalSession.OpenAsync(source, sink) overload for non-console scenarios.");
        }

        if (isNativeConsole && !stdoutIsConsole)
            throw new InvalidOperationException("Standard output is not connected to a console.");

        uint originalOutputCodePage = 0;
        uint originalInputCodePage = 0;
        bool codepagesSwitched = false;
        bool stdinModeSet = false;
        bool stdoutModeSet = false;

        FileStream? stdoutStream = null;
        IInputByteSource? source = null;

        try
        {
            // Console-input mode setup. Disabled flags: line / echo / processed / quick-edit
            // / VT_INPUT (we deliberately avoid the VT byte-stream view — see the class
            // remarks for why). Enabled flags: extended flags (so quick-edit can actually be
            // turned off), mouse input, and window-buffer-size events.
            if (stdinIsConsole)
            {
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
                stdinModeSet = true;
            }

            // Console-output mode setup. ENABLE_VIRTUAL_TERMINAL_PROCESSING so SGR / cursor /
            // OSC sequences are interpreted rather than printed literally;
            // DISABLE_NEWLINE_AUTO_RETURN keeps the terminal from auto-converting LF to CRLF.
            if (stdoutIsConsole)
            {
                uint stdoutMode = originalStdoutMode;
                stdoutMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;

                if (!SetConsoleMode(stdoutHandle, stdoutMode))
                {
                    throw new InvalidOperationException(
                        $"SetConsoleMode failed for the console output handle (Win32 error {Marshal.GetLastWin32Error()}).");
                }
                stdoutModeSet = true;
            }

            // Snapshot and switch the console codepages to UTF-8. Without this, multi-byte
            // UTF-8 sequences emitted by the renderer (box-drawing characters, accented Latin
            // letters, CJK, emoji, …) get reinterpreted through whatever OEM codepage the
            // console was launched with (typically CP437 / CP850 on conhost), and the result
            // is mojibake. Codepage is a process-global setting on Windows — switching it
            // affects all consoles attached to the process — so we only touch it when we have
            // at least one console-typed handle. RestoreTerminalState reverts to the original
            // values.
            if (stdinIsConsole || stdoutIsConsole)
            {
                originalOutputCodePage = GetConsoleOutputCP();
                originalInputCodePage = GetConsoleCP();
                SetConsoleOutputCP(CP_UTF8);
                SetConsoleCP(CP_UTF8);
                codepagesSwitched = true;
            }

            // Wrap stdout. See the class remarks for why we deliberately do NOT use
            // Console.OpenStandardOutput, and see OpenStdHandleAsSyncStream for the
            // _fileOptions-override gymnastics required to make this work across the full
            // matrix of Windows console / pipe / MSYS2 ptys.
            stdoutStream = OpenStdHandleAsSyncStream(stdoutHandle, FileAccess.Write);
            var sink = new StreamOutputByteSink(stdoutStream);

            // Stdin source selection. Console handles always get the WFMO-based
            // WindowsConsoleInputByteSource — that pump is non-blocking and cancellable, so it
            // never sits in a zombie ReadFile that would consume the user's next keystroke
            // after disposal. The family controls only the translator's behavior:
            // pass-through ASCII bytes from vk=0 records under third-party emulators,
            // Win32-Input-Mode envelopes everywhere under conhost / WT. Pipe-backed stdin
            // takes the plain stream-based path — no conhost in the middle, the emulator's
            // bytes already arrive verbatim.
            if (stdinIsConsole)
            {
                source = new WindowsConsoleInputByteSource(stdinHandle, passThroughVtBytes: !isNativeConsole);
            }
            else
            {
                // Non-console stdin (pipe / disk / remote). The handle's kernel-level async-ness
                // is whatever the parent process inherited to us — we can't predict it, and
                // FileStream's pipeline traps every combination of overlapped/sync handle vs.
                // explicit/implicit isAsync. WindowsHandleByteSource sidesteps the trap by
                // always reading via OVERLAPPED ReadFile + manual-reset event, which works
                // uniformly across sync and overlapped handles.
                source = new WindowsHandleByteSource(stdinHandle);
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
            if (stdinModeSet)  { try { SetConsoleMode(stdinHandle, originalStdinMode); }   catch { /* best-effort */ } }
            if (stdoutModeSet) { try { SetConsoleMode(stdoutHandle, originalStdoutMode); } catch { /* best-effort */ } }

            if (codepagesSwitched)
            {
                try { SetConsoleOutputCP(originalOutputCodePage); } catch { /* best-effort */ }
                try { SetConsoleCP(originalInputCodePage); }        catch { /* best-effort */ }
            }

            stdoutStream?.Dispose();

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

    /// <summary>
    /// Reflection handle for <c>SafeFileHandle._fileOptions</c>. Set once at type-init; if the
    /// field is ever renamed in a future SDK, this becomes null and
    /// <see cref="OpenStdHandleAsSyncStream"/> falls back to the default ctor's behavior (which
    /// re-surfaces the original BindHandle / sync-validation error so the fix is obviously
    /// needed again).
    /// </summary>
    private static readonly FieldInfo? SafeFileHandleFileOptionsField =
        typeof(SafeFileHandle).GetField("_fileOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Wrap a process standard handle as a synchronous <see cref="FileStream"/>, suppressing
    /// .NET's automatic async-handle detection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SafeFileHandle.IsAsync"/> is a one-time kernel probe via
    /// <c>NtQueryInformationFile(FileModeInformation)</c>; .NET treats "neither
    /// <c>FILE_SYNCHRONOUS_IO_NONALERT</c> nor <c>FILE_SYNCHRONOUS_IO_ALERT</c> set" as
    /// async-capable. For most Windows console handles that classification is accurate and
    /// <c>ThreadPoolBoundHandle.BindHandle</c> happily attaches them to an I/O completion
    /// port. For inherited MSYS2 / Cygwin pty handles (notably MobaXterm's bash session, but
    /// also other ConPTY-less third-party emulators that bring their own pty layer) the kernel
    /// reports the same async-capable shape but <c>BindHandle</c> rejects them because they
    /// weren't created with <c>FILE_FLAG_OVERLAPPED</c> — the <c>FileStream</c> default ctor
    /// blows up with <c>"BindHandle for ThreadPool failed on this handle."</c> Passing
    /// <c>isAsync: false</c> doesn't help either: the explicit-mode validation rejects with
    /// <c>"Handle does not support synchronous operations"</c> when the handle's
    /// <see cref="SafeFileHandle.IsAsync"/> says true.
    /// </para>
    /// <para>
    /// The fix is to short-circuit <see cref="SafeFileHandle.IsAsync"/> before
    /// <see cref="FileStream"/> reads it. <c>IsAsync</c> resolves to
    /// <c>(GetFileOptions() &amp; FileOptions.Asynchronous) != 0</c>, and
    /// <c>GetFileOptions</c> returns the cached <c>_fileOptions</c> field if it's been set —
    /// the kernel probe only runs when <c>_fileOptions</c> is the sentinel
    /// <c>(FileOptions)(-1)</c>. Writing <see cref="FileOptions.None"/> directly into
    /// <c>_fileOptions</c> via reflection makes <see cref="SafeFileHandle.IsAsync"/> return
    /// false, the threadpool binding is skipped, and the sync-mode validation passes.
    /// </para>
    /// <para>
    /// At the OS level, sync <c>ReadFile</c> / <c>WriteFile</c> works on any handle regardless
    /// of how it was opened — Windows accepts a null <c>OVERLAPPED</c> on overlapped handles
    /// too, just serializing internally — so the forced sync-mode FileStream is functionally
    /// fine for both shapes. PipeReader / PipeWriter (which sit above this FileStream) handle
    /// async on top of sync streams without trouble.
    /// </para>
    /// </remarks>
    private static FileStream OpenStdHandleAsSyncStream(IntPtr handle, FileAccess access)
    {
        var safeHandle = new SafeFileHandle(handle, ownsHandle: false);
        SafeFileHandleFileOptionsField?.SetValue(safeHandle, FileOptions.None);
        return new FileStream(safeHandle, access, bufferSize: 4096, isAsync: false);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

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