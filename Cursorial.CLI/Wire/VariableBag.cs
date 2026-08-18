using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cursorial.CLI.Wire;

/// <summary>The shape of a captured step result.</summary>
public enum VariableKind
{
    /// <summary>A single text value (e.g. <c>input</c>).</summary>
    Text,

    /// <summary>A boolean (an optional <c>confirm</c> with <c>--var</c>); the value is <c>true</c>/<c>false</c>.</summary>
    Bool,

    /// <summary>A selection (<c>choose</c>): labels plus their 0-based positions.</summary>
    Selection,
}

/// <summary>
/// One captured variable. <see cref="Values"/> holds the text value, the canonical
/// <c>true</c>/<c>false</c> for a bool, or the selected labels; <see cref="Indices"/> holds the
/// 0-based selected positions for a <see cref="VariableKind.Selection"/> (empty otherwise).
/// </summary>
public sealed record Variable(string Name, VariableKind Kind, IReadOnlyList<string> Values, IReadOnlyList<int> Indices);

/// <summary>
/// The pipeline's captured step results (docs/cli-design.md §4.2): each accepted step with
/// <c>--var NAME</c> binds here, later steps interpolate via <see cref="TryResolve"/>, and the
/// buffered emitters (<see cref="Emit"/>) walk <see cref="Variables"/> on full success.
/// </summary>
public sealed class VariableBag
{
    private readonly List<Variable> _variables = new();
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    /// <summary>The captured variables in first-bound order (rebinding a name keeps its position).</summary>
    public IReadOnlyList<Variable> Variables => _variables;

    /// <summary>Bind a text result.</summary>
    public void BindText(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Bind(new Variable(name, VariableKind.Text, new[] { value }, Array.Empty<int>()));
    }

    /// <summary>Bind a boolean result (an optional <c>confirm</c> binds <c>true</c>/<c>false</c>, §4.4).</summary>
    public void BindBool(string name, bool value)
        => Bind(new Variable(name, VariableKind.Bool, new[] { value ? "true" : "false" }, Array.Empty<int>()));

    /// <summary>Bind a selection result: the chosen labels and their 0-based positions, pairwise.</summary>
    public void BindSelection(string name, IReadOnlyList<string> labels, IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(indices);

        if (labels.Count != indices.Count)
            throw new ArgumentException($"Selection '{name}' has {labels.Count} labels but {indices.Count} indices.", nameof(indices));

        var labelsCopy = new string[labels.Count];
        var indicesCopy = new int[indices.Count];

        for (var i = 0; i < labels.Count; i++)
        {
            labelsCopy[i] = labels[i] ?? throw new ArgumentException($"Selection '{name}' has a null label at position {i}.", nameof(labels));
            indicesCopy[i] = indices[i];
        }

        Bind(new Variable(name, VariableKind.Selection, labelsCopy, indicesCopy));
    }

    /// <summary>
    /// Resolve an interpolation accessor (§4.2): <c>name</c> yields the text value,
    /// <c>true</c>/<c>false</c> for a bool, or the labels space-joined for a selection;
    /// <c>name.index</c> yields a selection's positions space-joined (0-based). An exact-name
    /// binding wins over the <c>.index</c> reading; <c>name.index</c> on a non-selection does not
    /// resolve. Unbound accessors resolve false with an empty <paramref name="value"/>.
    /// </summary>
    public bool TryResolve(string accessor, out string value)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        if (_byName.TryGetValue(accessor, out var at))
        {
            value = string.Join(' ', _variables[at].Values);
            return true;
        }

        const string indexSuffix = ".index";
        if (accessor.EndsWith(indexSuffix, StringComparison.Ordinal))
        {
            var name = accessor[..^indexSuffix.Length];
            if (_byName.TryGetValue(name, out at) && _variables[at].Kind == VariableKind.Selection)
            {
                value = JoinIndices(_variables[at].Indices);
                return true;
            }
        }

        value = "";
        return false;
    }

    internal static string JoinIndices(IReadOnlyList<int> indices)
    {
        if (indices.Count == 1)
            return indices[0].ToString(CultureInfo.InvariantCulture);

        var parts = new string[indices.Count];
        for (var i = 0; i < indices.Count; i++)
            parts[i] = indices[i].ToString(CultureInfo.InvariantCulture);

        return string.Join(' ', parts);
    }

    private void Bind(Variable variable)
    {
        ArgumentException.ThrowIfNullOrEmpty(variable.Name);

        if (_byName.TryGetValue(variable.Name, out var at))
            _variables[at] = variable;
        else
        {
            _byName.Add(variable.Name, _variables.Count);
            _variables.Add(variable);
        }
    }
}
