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
    /// The input device. <b>Single-shot per host lifetime</b> — exactly one
    /// <see cref="IAsyncInputDevice.ReadAllAsync"/> enumeration, owned by the application's pump.
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
}
