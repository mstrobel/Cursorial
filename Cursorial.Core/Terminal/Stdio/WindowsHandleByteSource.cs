using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

using Cursorial.Input;

namespace Cursorial.Terminal.Stdio;

/// <summary>
/// Windows <see cref="IInputByteSource"/> over an arbitrary handle — pipe, file, char device —
/// that does its own <c>ReadFile</c> via P/Invoke instead of going through
/// <see cref="FileStream"/>. Used on Windows when stdin is NOT a console handle (so
/// <see cref="WindowsConsoleInputByteSource"/> doesn't apply) — typically a pty pipe under
/// MSYS2 / Cygwin / MobaXterm bash, an SSH-piped stdin, or a CI-runner pipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why we don't use <see cref="FileStream"/> here.</b> Inherited standard handles whose
/// kernel-level mode is "neither <c>FILE_SYNCHRONOUS_IO_NONALERT</c> nor
/// <c>FILE_SYNCHRONOUS_IO_ALERT</c>" land in a hard spot under .NET's FileStream pipeline:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The default ctor uses async mode (because <see cref="System.IO.Pipelines.PipeReader"/>
/// calls <c>ReadAsync</c>) → <c>ThreadPoolBoundHandle.BindHandle</c> fails because the handle
/// wasn't created with <c>FILE_FLAG_OVERLAPPED</c> → IOException("BindHandle for ThreadPool
/// failed on this handle").
/// </description></item>
/// <item><description>
/// Forcing <c>isAsync: false</c> at the FileStream layer (even when paired with a reflection
/// override of <c>SafeFileHandle._fileOptions</c> to bypass .NET's validation) only fools
/// .NET's bookkeeping — at runtime sync <c>ReadFile</c> with a null <c>OVERLAPPED</c> on an
/// overlapped-mode kernel handle fails with <c>ERROR_IO_PENDING</c> ("Overlapped I/O operation
/// is in progress").
/// </description></item>
/// </list>
/// <para>
/// Both failure modes resolve by reaching for the syscalls directly and always passing an
/// <c>OVERLAPPED</c> structure with our own event. For genuinely sync handles, the
/// <c>OVERLAPPED</c> parameter is ignored by the kernel and <c>ReadFile</c> blocks until done
/// — fine. For overlapped handles, <c>ReadFile</c> returns
/// <c>ERROR_IO_PENDING</c>, we wait on the event via <c>GetOverlappedResult</c>, and
/// disposal-time <c>CancelIoEx</c> wakes the pump cleanly via <c>ERROR_OPERATION_ABORTED</c>.
/// </para>
/// <para>
/// <b>Handle ownership.</b> The supplied handle is not closed at disposal — it's the
/// process-global value returned by <c>GetStdHandle</c>, and we mustn't close it. The read
/// event we create IS owned and closed.
/// </para>
/// </remarks>
internal sealed partial class WindowsHandleByteSource : IInputByteSource
{
    // ReSharper disable UnusedMember.Local

    private const int ERROR_BROKEN_PIPE = 109;
    private const int ERROR_HANDLE_EOF = 38;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;

    private const int ReadBufferSize = 4096;

    // ReSharper restore UnusedMember.Local

    private readonly IntPtr _handle;
    private readonly IntPtr _readEvent;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;
    private int _disposed;

    public WindowsHandleByteSource(IntPtr handle)
    {
        _handle = handle;

        // Manual-reset event paired with each OVERLAPPED read. Initial state non-signaled;
        // ResetEvent is called immediately before each ReadFile so a previous completion can't
        // mask a fresh wait.
        _readEvent = CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (_readEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateEvent failed for the input-pump completion event (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _pipe = new Pipe();
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <inheritdoc/>
    public PipeReader Reader => _pipe.Reader;

    private async Task PumpAsync()
    {
        Exception? completion = null;
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                int bytesRead = ReadOnce(buffer);

                if (Volatile.Read(ref _disposed) != 0) break;
                if (bytesRead <= 0) break;   // EOF or cancelled

                var dst = _pipe.Writer.GetSpan(bytesRead);
                buffer.AsSpan(0, bytesRead).CopyTo(dst);
                _pipe.Writer.Advance(bytesRead);

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
            ArrayPool<byte>.Shared.Return(buffer);
            // @formatter:on
        }
    }

    /// <summary>
    /// One <c>ReadFile</c> + completion wait. Returns the byte count, or 0 on EOF / cancellation.
    /// </summary>
    private unsafe int ReadOnce(byte[] buffer)
    {
        // OVERLAPPED on the stack — the kernel only writes to it during this call (and during
        // GetOverlappedResult, which we call inline if ReadFile pends). The stack frame stays
        // alive for both phases.
        var overlapped = default(OVERLAPPED);
        overlapped.hEvent = _readEvent;
        ResetEvent(_readEvent);

        fixed (byte* bufPtr = buffer)
        {
            bool synchronous = ReadFile(_handle, bufPtr, (uint) buffer.Length, out uint bytesRead, &overlapped);
            if (synchronous) return (int) bytesRead;

            int err = Marshal.GetLastWin32Error();

            if (err == ERROR_IO_PENDING)
            {
                if (GetOverlappedResult(_handle, &overlapped, out bytesRead, bWait: true))
                    return (int) bytesRead;

                int err2 = Marshal.GetLastWin32Error();
                if (err2 is ERROR_OPERATION_ABORTED or ERROR_BROKEN_PIPE or ERROR_HANDLE_EOF)
                    return 0;
                throw new IOException($"GetOverlappedResult failed (Win32 error {err2}).");
            }

            if (err is ERROR_OPERATION_ABORTED or ERROR_BROKEN_PIPE or ERROR_HANDLE_EOF)
                return 0;

            throw new IOException($"ReadFile failed (Win32 error {err}).");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Cancel any pending I/O on the handle — wakes a blocking ReadFile (sync handle case)
        // or aborts the overlapped read with ERROR_OPERATION_ABORTED (async case). Passing
        // IntPtr.Zero as the OVERLAPPED scope cancels all I/O on this handle regardless of
        // thread.
        try { CancelIoEx(_handle, IntPtr.Zero); }
        catch { /* best-effort */ }

        // @formatter:off
        try { await _pumpTask.ConfigureAwait(false); }
        catch { /* best-effort */ }
        // @formatter:on

        if (_readEvent != IntPtr.Zero) CloseHandle(_readEvent);
    }

    // ---- OVERLAPPED layout ----

    // ReSharper disable InconsistentNaming

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    // ReSharper restore InconsistentNaming

    // ---- P/Invokes ----

    // ReSharper disable UnusedMethodReturnValue.Local

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ResetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        OVERLAPPED* lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetOverlappedResult(
        IntPtr hFile,
        OVERLAPPED* lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    // ReSharper restore UnusedMethodReturnValue.Local
}
