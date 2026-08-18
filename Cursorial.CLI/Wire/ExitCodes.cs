using System;

namespace Cursorial.CLI.Wire;

/// <summary>
/// Process exit codes for curio (docs/cli-design.md §4.4). Signal deaths follow the framework's
/// existing 128+n convention and are not enumerated here.
/// </summary>
public static class ExitCodes
{
    /// <summary>Accepted / confirmed; a pipeline completed (skipped optionals included).</summary>
    public const int Accepted = 0;

    /// <summary>Declined (<c>confirm</c> "no") or backed out (Esc) on a required step.</summary>
    public const int Canceled = 1;

    /// <summary>Usage error — malformed argv, unknown commandlet or option, empty pipeline step.</summary>
    public const int Usage = 2;

    /// <summary>Hard abort via Ctrl+C; buffered emits are suppressed.</summary>
    public const int CtrlC = 130;
}

/// <summary>
/// An argv-level usage error. The host catches it at top level, prints <see cref="Exception.Message"/>
/// to stderr, and exits with <see cref="ExitCodes.Usage"/>.
/// </summary>
public sealed class UsageException(string message) : Exception(message);
