using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Terminal;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// <see cref="ITerminalHost"/> over a real <see cref="TerminalSession"/> — the only host that runs
/// actual negotiation/probes. Used for both the owned happy path
/// (<see cref="TerminalSession.OpenAsync(TerminalSessionOptions?, CancellationToken)"/>, opened by
/// <see cref="UIApplication.RunAsync(Func{UIElement}, CancellationToken)"/>) and BYO sessions
/// supplied through <see cref="UIApplicationBuilder.WithSession"/>.
/// </summary>
/// <remarks>
/// <b>Signal-net interplay (design doc §10.7).</b> The owned happy-path session registers
/// SIGINT/SIGTERM/SIGHUP/SIGQUIT handlers that restore opt-ins and termios — but it knows nothing
/// of the UI layer's alternate screen or hidden cursor. The designed fix is the additive Core seam
/// <c>TerminalSessionOptions.EmergencyRestoreBytes</c> (opaque show-cursor + SGR-reset + leave-alt
/// + autowrap bytes written signal-safely before termios restore); that seam has <b>not landed in
/// Core yet</b>, so until it does, a signal-killed P1 app restores cooked mode but may leave the
/// shell on the alternate screen. Normal and crash exits are unaffected — the canonical teardown
/// always runs. BYO sessions get no signal involvement at all
/// (<see cref="OwnsSignalHandling"/> is <see langword="false"/>).
/// </remarks>
public sealed class TerminalSessionHost : ITerminalHost
{
    private readonly TerminalSession _session;
    private readonly bool _disposeSession;

    /// <summary>
    /// Wraps <paramref name="session"/>. <paramref name="disposeSession"/> controls whether
    /// <see cref="DisposeAsync"/> disposes it (true for owned sessions, the
    /// <c>disposeWithApp</c> flag for BYO). <paramref name="ownsSignalHandling"/> records whether
    /// the session registered the process signal net (true only for the owned happy path).
    /// </summary>
    public TerminalSessionHost(TerminalSession session, bool disposeSession = false, bool ownsSignalHandling = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _disposeSession = disposeSession;
        OwnsSignalHandling = ownsSignalHandling;
    }

    /// <inheritdoc/>
    public TerminalCapabilities Capabilities => _session.Capabilities;

    /// <inheritdoc/>
    public IAsyncInputDevice Input => _session.Input;

    /// <inheritdoc/>
    public IOutputByteSink Output => _session.Output;

    /// <inheritdoc/>
    public bool OwnsSignalHandling { get; }

    /// <inheritdoc/>
    public ValueTask<(int Columns, int Rows)?> QuerySizeAsync(CancellationToken cancellationToken = default)
        => _session.QueryTerminalSizeAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask RenegotiateAsync(CancellationToken cancellationToken = default)
        => _session.RenegotiateAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReapplyScreenLocalOptInsAsync(CancellationToken cancellationToken = default)
        => _session.ReapplyScreenLocalOptInsAsync(cancellationToken);

    public ValueTask<IAsyncDisposable> PushAltScreenAsync(CancellationToken cancellationToken = default)
        => _session.PushAltScreenAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
        => _disposeSession ? _session.DisposeAsync() : ValueTask.CompletedTask;
}
