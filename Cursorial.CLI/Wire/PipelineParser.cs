using System;
using System.Collections.Generic;

namespace Cursorial.CLI.Wire;

/// <summary>
/// Splits a full <c>curio</c> argv into per-step argv arrays on the pipeline separator
/// (docs/cli-design.md §4.1): <c>curio &lt;step&gt; ++ &lt;step&gt; [++ &lt;step&gt; …]</c>.
/// </summary>
/// <remarks>
/// The separator is matched as an <em>exact, whole token</em> — a token that merely contains the
/// separator text (<c>a++b</c>) never splits. Matching happens at the top level of the argv scan,
/// deliberately ignoring any step-local <c>--</c>: the shell user writes
/// <c>curio choose -- a b ++ confirm ok</c> and expects the split, so <c>--</c> scoping is left to
/// <see cref="StepArgs"/> within each step. An argv that must pass a literal separator token uses
/// the global <c>--sep &lt;tok&gt;</c> override, recognized only at the very front of argv (before
/// the first step name).
/// </remarks>
public static class PipelineParser
{
    /// <summary>
    /// Split <paramref name="argv"/> into one argv array per pipeline step.
    /// A leading, trailing, or doubled separator (an empty step) is a usage error.
    /// An empty argv yields an empty list — "no steps" is the caller's decision to report.
    /// </summary>
    /// <param name="argv">The full process argv (excluding the program name).</param>
    /// <param name="separator">The step separator token; overridden by a leading <c>--sep &lt;tok&gt;</c>.</param>
    /// <exception cref="UsageException">Empty step, or a malformed <c>--sep</c> override.</exception>
    public static IReadOnlyList<string[]> Split(string[] argv, string separator = "++")
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentException.ThrowIfNullOrEmpty(separator);

        var start = 0;

        // Global `--sep <tok>` / `--sep=<tok>` before the first step name overrides the separator.
        if (argv.Length > 0 && (argv[0] == "--sep" || argv[0].StartsWith("--sep=", StringComparison.Ordinal)))
        {
            if (argv[0] == "--sep")
            {
                if (argv.Length < 2)
                    throw new UsageException("--sep requires a separator token.");

                separator = argv[1];
                start = 2;
            }
            else
            {
                separator = argv[0]["--sep=".Length..];
                start = 1;
            }

            if (separator.Length == 0)
                throw new UsageException("--sep requires a non-empty separator token.");
        }

        var steps = new List<string[]>();
        var current = new List<string>();
        var sawSeparator = false;

        for (var i = start; i < argv.Length; i++)
        {
            if (!string.Equals(argv[i], separator, StringComparison.Ordinal))
            {
                current.Add(argv[i]);
                continue;
            }

            if (current.Count == 0)
            {
                throw new UsageException(sawSeparator
                    ? $"Empty pipeline step: two '{separator}' separators in a row."
                    : $"The pipeline separator '{separator}' cannot appear before the first step.");
            }

            steps.Add(current.ToArray());
            current.Clear();
            sawSeparator = true;
        }

        if (current.Count > 0)
            steps.Add(current.ToArray());
        else if (sawSeparator)
            throw new UsageException($"The pipeline separator '{separator}' cannot appear after the last step.");

        return steps;
    }
}
