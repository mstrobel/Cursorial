using System;
using System.Collections.Generic;

namespace Cursorial.CLI.Wire;

/// <summary>
/// One pipeline step's parsed argv (docs/cli-design.md §4.1): the commandlet name, its options, and
/// its positional arguments. This is a schema-less collector — which options a commandlet actually
/// accepts (and which are flags vs. valued) is validated later, per commandlet.
/// </summary>
/// <remarks>
/// <para>Grammar: the first token is the commandlet name; then <c>--name value</c>,
/// <c>--name=value</c>, and bare <c>--flag</c> options mixed with positionals; <c>--</c> ends
/// option parsing (everything after it is positional, verbatim). curio uses long options only in
/// v1, so a lone short token (<c>-x</c>) is a usage error; a bare <c>-</c> is kept as a positional
/// (the conventional stdin placeholder).</para>
/// <para>Because parsing is schema-less, value attachment is greedy: <c>--name</c> followed by a
/// token that does not start with <c>-</c> (or that is exactly <c>-</c>) takes it as the option's
/// value. A flag immediately followed by a positional therefore misattaches
/// (<c>confirm --optional yes</c> parses <c>yes</c> as the value of <c>optional</c>) — put
/// positionals first or after <c>--</c>. <see cref="HasFlag"/> reports presence regardless of
/// whether a value got attached, so flag semantics survive the ambiguity; values that must start
/// with <c>-</c> use the <c>--name=value</c> form.</para>
/// </remarks>
public sealed class StepArgs
{
    // Option occurrences in argv order; a null entry is a bare-flag occurrence (no value).
    private readonly Dictionary<string, List<string?>> _options;
    private readonly List<string> _positionals;

    private StepArgs(string commandletName, Dictionary<string, List<string?>> options, List<string> positionals)
    {
        CommandletName = commandletName;
        _options = options;
        _positionals = positionals;
    }

    /// <summary>The commandlet name (the step's first token).</summary>
    public string CommandletName { get; }

    /// <summary>Positional arguments, in order.</summary>
    public IReadOnlyList<string> Positionals => _positionals;

    /// <summary>The step's <c>--var</c> binding name, if any (§4.2).</summary>
    public string? Var => GetOption("var");

    /// <summary>Whether the step is marked <c>--optional</c> (§4.4 soft-cancel continuation).</summary>
    public bool Optional => HasFlag("optional");

    /// <summary>The step's <c>--default</c> value, if any (§4.4 / §4.5).</summary>
    public string? Default => GetOption("default");

    /// <summary>Parse one step's argv (as produced by <see cref="PipelineParser.Split"/>).</summary>
    /// <exception cref="UsageException">Missing commandlet name, or a short-option token.</exception>
    public static StepArgs Parse(string[] stepArgv)
    {
        ArgumentNullException.ThrowIfNull(stepArgv);

        if (stepArgv.Length == 0)
            throw new UsageException("Missing commandlet name.");

        var name = stepArgv[0];
        if (name.Length == 0 || name[0] == '-')
            throw new UsageException($"Expected a commandlet name, got '{name}'.");

        var options = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var positionals = new List<string>();
        var optionsEnded = false;

        for (var i = 1; i < stepArgv.Length; i++)
        {
            var token = stepArgv[i];

            if (optionsEnded)
            {
                positionals.Add(token);
                continue;
            }

            if (token == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string optionName;
                string? value;

                var eq = token.IndexOf('=');
                if (eq >= 0)
                {
                    optionName = token[2..eq];
                    value = token[(eq + 1)..];
                }
                else
                {
                    optionName = token[2..];
                    value = i + 1 < stepArgv.Length && CanBeOptionValue(stepArgv[i + 1])
                        ? stepArgv[++i]
                        : null;
                }

                if (optionName.Length == 0)
                    throw new UsageException($"Empty option name in '{token}'.");

                if (!options.TryGetValue(optionName, out var occurrences))
                    options[optionName] = occurrences = new List<string?>();
                occurrences.Add(value);
                continue;
            }

            if (token.Length > 1 && token[0] == '-')
                throw new UsageException($"Unknown option '{token}': curio uses long options only (--name).");

            positionals.Add(token);
        }

        return new StepArgs(name, options, positionals);
    }

    /// <summary>
    /// The option's value — the last occurrence that carried one — or null if the option is absent
    /// or appeared only as a bare flag.
    /// </summary>
    public string? GetOption(string name)
    {
        if (_options.TryGetValue(name, out var occurrences))
        {
            for (var i = occurrences.Count - 1; i >= 0; i--)
            {
                if (occurrences[i] is { } value)
                    return value;
            }
        }

        return null;
    }

    /// <summary>Whether the option appeared at all, with or without a value.</summary>
    public bool HasFlag(string name) => _options.ContainsKey(name);

    /// <summary>All values of a repeatable option, in argv order (bare-flag occurrences excluded).</summary>
    public IReadOnlyList<string> GetAll(string name)
    {
        if (!_options.TryGetValue(name, out var occurrences))
            return Array.Empty<string>();

        var values = new List<string>(occurrences.Count);
        foreach (var value in occurrences)
        {
            if (value is not null)
                values.Add(value);
        }

        return values;
    }

    // A token can be consumed as `--name value` iff it isn't option-shaped: anything not starting
    // with '-', plus the bare "-" stdin placeholder. Values that must start with '-' use --name=value.
    private static bool CanBeOptionValue(string token) => token.Length <= 1 || token[0] != '-';
}
