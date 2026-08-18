using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Terminal;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The S6-owned abstraction over "a thing that gives us capabilities, an input device, and an
/// output sink" (design doc §10.9 — invariant-7-clean: no Core change). <see cref="TerminalSessionHost"/>
/// adapts a real <see cref="TerminalSession"/> for both production paths (owned happy-path and BYO);
/// <c>SyntheticTerminalHost</c> in <c>Cursorial.UI.Testing</c> implements it headlessly.
/// </summary>
public interface ITerminalHost : IAsyncDisposable
{
    /// <summary>The negotiated capability snapshot. Replaced by <see cref="RenegotiateAsync"/>.</summary>
    TerminalCapabilities Capabilities { get; }

    /// <summary>
    /// The input device. One <see cref="IAsyncInputDevice.ReadAllAsync"/> enumeration at a time,
    /// owned by the running application's pump; a shared host (one session, sequential
    /// applications — the pipeline pattern) re-enumerates after the previous app's pump unwinds.
    /// </summary>
    IAsyncInputDevice Input { get; }

    /// <summary>The output byte sink. The frame loop is its only writer once the loop owns the pipe.</summary>
    IOutputByteSink Output { get; }

    /// <summary>
    /// <see langword="true"/> when the host registered its own signal net (the owned happy-path
    /// session); <see langword="false"/> for BYO hosts — the embedder owns its signal strategy and
    /// S6 registers nothing (design doc §10.7).
    /// </summary>
    bool OwnsSignalHandling { get; }

    /// <summary>Queries the terminal size, or <see langword="null"/> when the host cannot.</summary>
    ValueTask<(int Columns, int Rows)?> QuerySizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-runs capability negotiation. Hosts that cannot renegotiate no-op.</summary>
    ValueTask RenegotiateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-applies the negotiated screen-local opt-ins (notably the per-screen-buffer Kitty keyboard
    /// flag push) on the currently-active screen, without disturbing restore accounting. The
    /// application calls this once after it enters the alternate screen, so key-up / repeat reporting
    /// (and the access-key Alt gate) engage on the alt screen rather than being stranded on the main
    /// screen where negotiation ran. Hosts with no screen-local protocol state (headless) no-op.
    /// </summary>
    ValueTask ReapplyScreenLocalOptInsAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Enters the alternate screen buffer and pushes the per-screen-buffer screen-local opt-ins (the
    /// Kitty keyboard flag) onto it, returning a scope that pops them and leaves the alt buffer on
    /// dispose — pop BEFORE leave, so the push never strands on the alt buffer's Kitty stack for the
    /// next program that uses the alt screen (e.g. <c>less</c>). Subsumes the alt-screen use of
    /// <see cref="ReapplyScreenLocalOptInsAsync"/>; ref-counted; the owned session's emergency signal
    /// handler closes an open scope too. Hosts with no alt-buffer state return a no-op scope.
    /// </summary>
    ValueTask<IAsyncDisposable> PushAltScreenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IAsyncDisposable>(NoOpAsyncDisposable.Instance);
}

/// <summary>A shared no-op <see cref="IAsyncDisposable"/> — the default alt-screen scope for hosts
/// without alt-buffer state.</summary>
internal sealed class NoOpAsyncDisposable : IAsyncDisposable
{
    public static readonly NoOpAsyncDisposable Instance = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
