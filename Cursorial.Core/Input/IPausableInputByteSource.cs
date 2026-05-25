namespace Cursorial.Input;

/// <summary>
/// Optional capability on an <see cref="IInputByteSource"/> — the source can be quiesced
/// without disposal so an external consumer (a child process, a synchronous terminal
/// operation, etc.) can take exclusive ownership of the underlying transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "paused" means.</b> While paused, the source's pump performs no I/O on its backing
/// transport — no <c>read(2)</c>, no <c>ReadAsync</c>, no <c>poll(2)</c>. Critically on POSIX,
/// no native libc syscall is left blocked in the kernel: the pump is parked entirely in user
/// space, and any bytes the OS produces in the meantime accumulate in the kernel's TTY buffer
/// to be picked up after the pause handle is disposed. This is the contract that lets a caller
/// safely hand the terminal to a child process without our pump stealing its first keystroke.
/// </para>
/// <para>
/// <b>Buffered bytes.</b> Bytes already in the source's pipe (read from the transport before
/// pause) remain in the pipe and are visible to consumers during the paused window. The pause
/// does not discard input — it only stops further reads.
/// </para>
/// <para>
/// <b>Scope handle.</b> Pause / resume are coupled into a single <see cref="IAsyncDisposable"/>
/// handle returned by <see cref="PauseAsync"/>. The implementation reference-counts overlapping
/// pauses internally: each <see cref="PauseAsync"/> increments, each handle disposal
/// decrements, and the pump only resumes when the count returns to zero. Callers can't leak
/// an unmatched resume, and nested or overlapping pauses compose without bookkeeping above.
/// </para>
/// </remarks>
public interface IPausableInputByteSource : IInputByteSource
{
    /// <summary>
    /// Stop the source's input pump. The returned <see cref="ValueTask{TResult}"/> completes
    /// only once the pump has definitively parked — no <c>read(2)</c> or equivalent native call
    /// is in flight. The pump resumes when the returned handle is disposed (and every other
    /// outstanding pause handle from overlapping calls has been disposed).
    /// </summary>
    ValueTask<IAsyncDisposable> PauseAsync(CancellationToken cancellationToken = default);
}
