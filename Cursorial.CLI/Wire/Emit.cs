using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cursorial.CLI.Wire;

/// <summary>The wire format for captured variables (docs/cli-design.md §4.3), from <c>--emit</c> or <c>CURIO_EMIT</c>.</summary>
public enum EmitFormat
{
    /// <summary>Default: each accepted step's value streams to stdout as it lands — gum-pipe compatible.</summary>
    Lines,

    /// <summary>Buffered; on full success emits shell-quoted <c>NAME='value'</c> lines — <c>eval "$(curio …)"</c>.</summary>
    Env,

    /// <summary>Buffered; on full success emits one JSON object of the captured variables — <c>curio … | jq</c>.</summary>
    Json,
}

/// <summary>
/// The wire-format emitters. <see cref="WriteLines"/> is the streaming per-step form; the buffered
/// forms (<see cref="WriteEnv"/>, <see cref="WriteJson"/>) walk the whole <see cref="VariableBag"/>
/// on full success and emit nothing on abort. JSON escaping is hand-rolled — the payload is flat
/// strings/numbers/bools, and skipping System.Text.Json keeps the AOT trimmer graph small (§7).
/// </summary>
public static class Emit
{
    /// <summary>
    /// Stream one accepted step's value(s): one line per value — a multi-select emits one line per
    /// label. Indices never appear in <c>lines</c> output; use <c>env</c> or <c>json</c> for them.
    /// </summary>
    public static void WriteLines(TextWriter writer, Variable variable)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(variable);

        foreach (var value in variable.Values)
            writer.WriteLine(value);
    }

    /// <summary>
    /// Emit <c>NAME='value'</c> per variable, single-quote shell-escaped (embedded <c>'</c> becomes
    /// <c>'\''</c>), names upper-cased with non-alphanumerics folded to <c>_</c>. Selections join
    /// multi-values with spaces and additionally emit <c>NAME_INDEX</c> (space-joined positions);
    /// bools emit <c>true</c>/<c>false</c>. Every value is quoted, indices included, so the output
    /// is uniformly <c>eval</c>-safe.
    /// </summary>
    public static void WriteEnv(TextWriter writer, VariableBag vars)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(vars);

        foreach (var variable in vars.Variables)
        {
            var envName = EnvName(variable.Name);

            writer.Write(envName);
            writer.Write('=');
            writer.WriteLine(ShellQuote(string.Join(' ', variable.Values)));

            if (variable.Kind == VariableKind.Selection)
            {
                writer.Write(envName);
                writer.Write("_INDEX=");
                writer.WriteLine(ShellQuote(VariableBag.JoinIndices(variable.Indices)));
            }
        }
    }

    /// <summary>
    /// Emit one JSON object of all captured variables: text as a string, bool as
    /// <c>true</c>/<c>false</c>, a selection as a string plus a <c>"name.index"</c> number when one
    /// item is selected, or as parallel arrays for multi-select.
    /// </summary>
    public static void WriteJson(TextWriter writer, VariableBag vars)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(vars);

        var entries = new List<string>();

        foreach (var variable in vars.Variables)
        {
            switch (variable.Kind)
            {
                case VariableKind.Text:
                case VariableKind.Bool:
                    var scalar = variable.Kind == VariableKind.Bool
                        ? variable.Values[0] // canonical "true"/"false" — the JSON literals
                        : JsonString(variable.Values[0]);
                    entries.Add($"{JsonString(variable.Name)}: {scalar}");
                    break;

                case VariableKind.Selection:
                    entries.Add(variable.Values.Count == 1
                        ? $"{JsonString(variable.Name)}: {JsonString(variable.Values[0])}"
                        : $"{JsonString(variable.Name)}: {JsonStringArray(variable.Values)}");
                    entries.Add(variable.Indices.Count == 1
                        ? $"{JsonString(variable.Name + ".index")}: {variable.Indices[0].ToString(CultureInfo.InvariantCulture)}"
                        : $"{JsonString(variable.Name + ".index")}: {JsonNumberArray(variable.Indices)}");
                    break;
            }
        }

        if (entries.Count == 0)
        {
            writer.WriteLine("{}");
            return;
        }

        writer.WriteLine('{');
        for (var i = 0; i < entries.Count; i++)
        {
            writer.Write("  ");
            writer.Write(entries[i]);
            writer.WriteLine(i < entries.Count - 1 ? "," : "");
        }
        writer.WriteLine('}');
    }

    // NAME transform: ASCII alphanumerics upper-cased, everything else folded to '_' —
    // shell variable names are ASCII, whatever the label alphabet is.
    private static string EnvName(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var c in name)
            result.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');

        return result.ToString();
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string JsonString(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        result.Append(c);
                    break;
            }
        }

        result.Append('"');
        return result.ToString();
    }

    private static string JsonStringArray(IReadOnlyList<string> values)
    {
        var parts = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
            parts[i] = JsonString(values[i]);

        return "[" + string.Join(", ", parts) + "]";
    }

    private static string JsonNumberArray(IReadOnlyList<int> values)
    {
        var parts = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
            parts[i] = values[i].ToString(CultureInfo.InvariantCulture);

        return "[" + string.Join(", ", parts) + "]";
    }
}
