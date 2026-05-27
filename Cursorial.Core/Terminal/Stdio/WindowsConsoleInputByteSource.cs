using System.IO.Pipelines;
using System.Runtime.InteropServices;

using Cursorial.Input;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Windows <see cref="IInputByteSource"/> that pumps bytes from a console input handle into a
/// <see cref="System.IO.Pipelines.Pipe"/> using <c>WaitForMultipleObjects</c> with an
/// auto-reset wakeup event. This is the Win32 analogue of the POSIX self-pipe / <c>poll(2)</c>
/// pattern in <see cref="PosixPollInputByteSource"/>: disposal signals the event, the blocked
/// <c>WaitForMultipleObjects</c> wakes immediately, and the pump exits without leaving a
/// pending <c>ReadFile</c> in the console subsystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The natural alternative — wrapping the stdin handle in a
/// <see cref="FileStream"/> and using <c>PipeReader.Create(stream)</c> — has the same fatal
/// flaw on Windows as the equivalent path does on POSIX. A blocked <c>ReadFile</c> against a
/// console handle ignores .NET cancellation tokens; on disposal the managed pump task
/// completes, but the underlying syscall stays blocked in a thread-pool worker, and the next
/// keystroke the user types gets consumed by that zombie read and dropped. With the
/// wait-then-read pattern there's no zombie because the pump blocks in <c>WaitForMultipleObjects</c>
/// (cancellable via our event handle), not inside <c>ReadFile</c>.
/// </para>
/// <para>
/// <b>Signaling semantics.</b> With <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> set and line / echo /
/// processed-input cleared (the mode the happy-path session applies), a console handle is
/// signaled when the input-record queue contains records that meet the current input mode —
/// in practice, key events that translate to VT byte sequences. Those bytes are available to
/// <c>ReadFile</c> immediately when the handle signals, so the read returns quickly. Records
/// that don't translate (e.g., <c>WINDOW_BUFFER_SIZE_EVENT</c>) don't satisfy the mode and
/// don't signal the handle, so they can't cause a spurious wake.
/// </para>
/// <para>
/// <b>Handle isolation via DuplicateHandle.</b> The constructor duplicates the supplied
/// stdin handle and uses the duplicate for every <c>ReadFile</c> and <c>CancelIoEx</c> call
/// the class makes. The original handle is left alone for the rest of the process —
/// crucially the .NET <see cref="System.Console"/> subsystem, which reads from the same
/// process-global stdin via its own <see cref="System.IO.StreamReader"/>. <c>CancelIoEx</c>
/// targets a specific file handle (not the underlying kernel object), so cancelling our
/// duplicate's in-flight reads doesn't leak any cancel state onto the original. Without
/// this isolation, a <c>CancelIoEx</c> against the global stdin would queue an abort that
/// the next reader's <c>ReadFile</c> would surface as <c>ERROR_OPERATION_ABORTED</c>, and
/// the byte that resolved the cancellation would be consumed in the resolution — the "first
/// keystroke after exit is swallowed" symptom this class was built to fix.
/// </para>
/// <para>
/// <b>Why CancelIoEx is still required on dispose.</b> SetEvent on the cancel event reliably
/// wakes the pump when it's parked in <c>WaitForMultipleObjects</c>, which is most of the
/// time. But a busy session disabling several opt-ins (Kitty keyboard, mouse, focus) emits a
/// stream of trailing-report bytes during the negotiator-restore window — enough that the
/// pump cycles <c>WFMO → ReadFile → loop</c> rapidly, and "happens to be inside ReadFile
/// when dispose runs" stops being microsecond-rare. <c>SetEvent</c> doesn't wake a blocked
/// <c>ReadFile</c>; <c>CancelIoEx</c> does. Because the call is against the duplicate it
/// doesn't pollute the original handle for the next reader.
/// </para>
/// <para>
/// <b>Handle ownership.</b> The supplied stdin handle is not closed at disposal — it's
/// process-global (the value returned by <c>GetStdHandle(STD_INPUT_HANDLE)</c>), and we
/// mustn't close it. The auto-reset event handle IS owned and closed.
/// </para>
/// <para>
/// <b>BYO transports.</b> Callers using <c>TerminalSession.OpenAsync(source, sink)</c> with a
/// hand-built <c>StreamInputByteSource</c> over <see cref="Console.OpenStandardInput()"/> or a
/// <see cref="FileStream"/> wrapping the stdin handle inherit the zombie-read bug — the
/// caller chose that transport. The fix here applies only to the happy-path
/// <c>TerminalSession.OpenAsync()</c> path that constructs this source itself.
/// </para>
/// </remarks>
// ReSharper disable once RedundantExtendsListEntry
internal sealed partial class WindowsConsoleInputByteSource : IInputByteSource, IPausableInputByteSource
{
    private const int ReadBufferSize = 4096;

    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_OBJECT_1 = WAIT_OBJECT_0 + 1;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const int ERROR_OPERATION_ABORTED = 995;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    private readonly IntPtr _stdinHandle;
    private readonly IntPtr _cancelEvent;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;

    // Pause-state plumbing — mirrors PosixPollInputByteSource exactly. _pauseRefCount tracks
    // outstanding PauseScope handles; the pump is parked iff the count is greater than zero.
    // The pump enters user-space wait via _runEvent (a managed manual-reset event), so no Win32
    // syscall is left pending while paused. _pauseCompleted notifies callers of PauseAsync
    // once the pump has definitively parked; it's recreated on each zero→one transition and
    // cleared on each one→zero transition.
    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _runEvent = new(initialState: true);
    private int _pauseRefCount;
    private TaskCompletionSource? _pauseCompleted;

    private int _disposed;

    public WindowsConsoleInputByteSource(IntPtr stdinHandle)
    {
        // Duplicate the supplied handle for our exclusive use. Every read this class issues —
        // and every CancelIoEx we may need to call to abort one of those reads — goes through
        // the duplicate. The original handle (which is the process-global stdin for the
        // happy-path session) keeps being usable by other consumers, most importantly the
        // .NET Console subsystem's StreamReader, without any cancel state our dispose path
        // might emit leaking onto it.
        //
        // CancelIoEx is documented as per-handle: it cancels operations issued against the
        // specific handle, not against the underlying kernel object. Two handles referring to
        // the same console connection have independent in-flight I/O. So `CancelIoEx(_stdinHandle,
        // NULL)` against our duplicate aborts only the pump's blocked ReadFile and does not
        // queue an abort for the next ReadFile that .NET Console will issue against the
        // original handle. That's what avoids the "first keystroke after exit is swallowed"
        // symptom — without isolation, CancelIoEx on the global handle could surface as
        // ERROR_OPERATION_ABORTED on the next reader's ReadFile, and the byte that resolved
        // the cancellation would be consumed in the resolution.
        if (!DuplicateHandle(
                hSourceProcessHandle: GetCurrentProcess(),
                hSourceHandle:        stdinHandle,
                hTargetProcessHandle: GetCurrentProcess(),
                lpTargetHandle:       out IntPtr duplicate,
                dwDesiredAccess:      0,
                bInheritHandle:       false,
                dwOptions:            DUPLICATE_SAME_ACCESS))
        {
            throw new InvalidOperationException(
                $"DuplicateHandle failed for the stdin handle (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _stdinHandle = duplicate;

        // Auto-reset event. Initial state: non-signaled. SetEvent makes the next
        // WaitForMultipleObjects on this handle return; the wait itself implicitly resets the
        // event back to non-signaled, so subsequent waits block again until the next SetEvent.
        _cancelEvent = CreateEvent(IntPtr.Zero, bManualReset: false, bInitialState: false, lpName: null);
        if (_cancelEvent == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(_stdinHandle);
            throw new InvalidOperationException(
                $"CreateEvent failed for the input-pump wakeup event (Win32 error {err}).");
        }

        _pipe = new Pipe();
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <inheritdoc/>
    public PipeReader Reader => _pipe.Reader;

    private async Task PumpAsync()
    {
        Exception? completion = null;
        try
        {
            var handles = new[] { _stdinHandle, _cancelEvent };

            while (true)
            {
                // Pause check (runs unconditionally at the top of each iteration). If the
                // refcount is non-zero, signal the pause-completion TCS and park on the
                // managed run event until ResumeIO releases us — same user-space wait the
                // POSIX source uses, no Win32 syscall in flight while parked.
                bool shouldPark;
                TaskCompletionSource? pauseTcs = null;
                lock (_stateLock)
                {
                    shouldPark = _pauseRefCount > 0;
                    if (shouldPark) pauseTcs = _pauseCompleted;
                }

                if (shouldPark)
                {
                    pauseTcs?.TrySetResult();
                    _runEvent.Wait();
                    if (Volatile.Read(ref _disposed) != 0) break;
                    continue;
                }

                uint waitResult = WaitForMultipleObjects(
                    nCount: 2,
                    lpHandles: ref handles[0],
                    bWaitAll: false,
                    dwMilliseconds: INFINITE);

                if (Volatile.Read(ref _disposed) != 0) break;

                if (waitResult == WAIT_FAILED)
                {
                    int err = Marshal.GetLastWin32Error();
                    completion = new IOException($"WaitForMultipleObjects failed on stdin (Win32 error {err}).");
                    break;
                }

                if (waitResult == WAIT_OBJECT_1)
                {
                    // Cancel/wakeup event — disposal or pause requested. The state checks at
                    // the top of the next iteration figure out which.
                    continue;
                }

                if (waitResult != WAIT_OBJECT_0)
                {
                    // Should not happen — either WAIT_ABANDONED (we don't use mutexes) or an
                    // unexpected return code. Bail rather than spin.
                    completion = new IOException($"WaitForMultipleObjects returned unexpected value 0x{waitResult:X8}.");
                    break;
                }

                // stdin is signaled — at least one input record meets the console mode, and
                // bytes should be available to ReadFile immediately.
                var memory = _pipe.Writer.GetMemory(ReadBufferSize);
                bool readSucceeded = ReadFile(_stdinHandle,
                                              ref MemoryMarshal.GetReference(memory.Span),
                                              (uint) memory.Length,
                                              out uint bytesRead,
                                              IntPtr.Zero);
                if (!readSucceeded)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_OPERATION_ABORTED)
                    {
                        // CancelIoEx fired against this ReadFile. Two callers issue it:
                        // DisposeAsync (exit immediately) and PauseAsync (loop back to the
                        // top so the pause check parks). Preserve any partial bytes the read
                        // managed to transfer before being aborted so pause doesn't drop
                        // already-delivered data — the POSIX side has the equivalent property
                        // by virtue of poll(2) returning bytes-ready before read(2) starts.
                        if (bytesRead > 0)
                        {
                            _pipe.Writer.Advance((int) bytesRead);
                            var partialFlush = await _pipe.Writer.FlushAsync().ConfigureAwait(false);
                            if (partialFlush.IsCompleted || partialFlush.IsCanceled) break;
                        }

                        if (Volatile.Read(ref _disposed) != 0) break;
                        continue;
                    }
                    completion = new IOException($"ReadFile failed on stdin (Win32 error {err}).");
                    break;
                }

                if (bytesRead == 0)
                {
                    // EOF — console handle closed, or the input stream was redirected and
                    // EOF reached. Either way, we're done.
                    break;
                }

                _pipe.Writer.Advance((int) bytesRead);
                var flush = await _pipe.Writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        catch (Exception ex)
        {
            completion = ex;
        }
        finally
        {
            // @formatter:off
            try { await _pipe.Writer.CompleteAsync(completion).ConfigureAwait(false); }
            catch { /* best-effort */ }
            // @formatter:on
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> PauseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsConsoleInputByteSource));

        cancellationToken.ThrowIfCancellationRequested();

        Task? completion;
        bool wakeupNeeded;
        lock (_stateLock)
        {
            // Tentative increment — we'll decrement on any failure between here and successful
            // completion of the pause-await, so cancellation can't strand the refcount.
            _pauseRefCount++;

            if (_pauseRefCount == 1)
            {
                _runEvent.Reset();
                _pauseCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = _pauseCompleted.Task;
                wakeupNeeded = true;
            }
            else if (_pauseCompleted is { Task.IsCompleted: false } pending)
            {
                completion = pending.Task;
                wakeupNeeded = false;
            }
            else
            {
                completion = null;
                wakeupNeeded = false;
            }
        }

        if (wakeupNeeded)
        {
            // SetEvent wakes the pump if it's parked in WaitForMultipleObjects (the common case
            // on a real console handle, where WFMO blocks until input records meet the mode).
            // CancelIoEx wakes it if it happens to be inside ReadFile — a microsecond window on
            // a console handle, but a wide-open window on non-console handles where WFMO returns
            // immediately. Without CancelIoEx, PauseAsync could deadlock on the latter; with it,
            // pause is reliable regardless of which syscall the pump is currently blocked in.
            SetEvent(_cancelEvent);
            CancelIoEx(_stdinHandle, IntPtr.Zero);
        }

        try
        {
            if (completion is not null)
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DecrementPause();
            throw;
        }

        return new PauseScope(this);
    }

    private void DecrementPause()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        bool wake;
        lock (_stateLock)
        {
            if (_pauseRefCount == 0) return;

            _pauseRefCount--;
            wake = _pauseRefCount == 0;

            if (wake)
                _pauseCompleted = null;
        }

        if (wake) _runEvent.Set();
    }

    private sealed class PauseScope : IAsyncDisposable
    {
        private WindowsConsoleInputByteSource? _source;

        public PauseScope(WindowsConsoleInputByteSource source) => _source = source;

        public ValueTask DisposeAsync()
        {
            var source = Interlocked.Exchange(ref _source, null);
            source?.DecrementPause();
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // @formatter:off

        // Ordering matters — set the disposed flag FIRST so the pump observes it on any wake;
        // then wake the pump from WaitForMultipleObjects; then abort any in-flight ReadFile;
        // then release the user-space pause park; then fail any in-flight pause TCS so callers
        // don't hang.
        //
        // CancelIoEx targets our DUPLICATED handle, not the process-global stdin. The cancel
        // affects only operations issued against this handle, so .NET Console's
        // StreamReader (which reads against the original stdin handle) keeps its next
        // ReadFile clean — no cancel state leaks into the next reader. That handle isolation
        // is the whole point of duplicating the handle in the constructor; without it,
        // CancelIoEx against the global stdin would queue an abort that the next reader's
        // ReadFile would surface as ERROR_OPERATION_ABORTED, and the byte that resolved the
        // cancellation would be consumed in the resolution — the "first keystroke after exit
        // is swallowed" symptom.
        //
        // CancelIoEx is necessary because in practice the pump CAN be inside ReadFile when
        // dispose runs: a busy session disabling several opt-ins (mouse, focus, Kitty
        // keyboard) generates a stream of trailing-report bytes during the negotiator-restore
        // window, and the pump cycles WFMO → ReadFile rapidly enough that "in ReadFile at the
        // dispose moment" stops being microsecond-rare. SetEvent alone doesn't wake a blocked
        // ReadFile; CancelIoEx does.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        SetEvent(_cancelEvent);
        CancelIoEx(_stdinHandle, IntPtr.Zero);
        _runEvent.Set();

        TaskCompletionSource? pendingPause;
        lock (_stateLock) { pendingPause = _pauseCompleted; _pauseCompleted = null; }
        pendingPause?.TrySetException(new ObjectDisposedException(nameof(WindowsConsoleInputByteSource)));

        try { await _pumpTask.ConfigureAwait(false); }
        catch { /* best-effort */ }

        _runEvent.Dispose();

        // Close the cancel event AND the duplicated stdin handle (the constructor took
        // ownership of the duplicate; closing it doesn't affect the original handle the
        // caller supplied).
        if (_cancelEvent != IntPtr.Zero)
            CloseHandle(_cancelEvent);
        if (_stdinHandle != IntPtr.Zero)
            CloseHandle(_stdinHandle);
        // @formatter:on
    }

    // ---- P/Invokes ----

    // ReSharper disable UnusedMethodReturnValue.Local

    /// <summary><c>CreateEventW</c> — create a Win32 event object.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    /// <summary><c>SetEvent</c> — transition an event object to the signaled state.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    /// <summary><c>WaitForMultipleObjects</c> — wait until any (bWaitAll=false) handle is signaled.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForMultipleObjects(
        uint nCount,
        ref IntPtr lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    /// <summary><c>ReadFile</c> — read bytes from a file or console handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(
        IntPtr hFile,
        ref byte lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    /// <summary><c>CancelIoEx</c> — abort outstanding I/O operations on a handle. Works on
    /// synchronous I/O issued from any thread in the current process since Windows Vista.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    /// <summary><c>CloseHandle</c> — release a Win32 handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    /// <summary><c>DuplicateHandle</c> — create an independent handle referring to the same
    /// underlying kernel object. Used to isolate this class's CancelIoEx cancellations from
    /// the original process-global stdin handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    /// <summary><c>GetCurrentProcess</c> — pseudo-handle for the current process, suitable as
    /// the source / target process arguments to <see cref="DuplicateHandle"/>.</summary>
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    // ReSharper restore UnusedMethodReturnValue.Local
}
