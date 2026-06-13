using System;
using System.Collections.Generic;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The structured, position-carrying parse of a markup extension <c>{Type pos1, pos2, Name=val,
/// Nested={...}}</c>. The frontend's recursive-descent grammar produces this; built-in folding
/// (<c>x:Null</c>/<c>x:Type</c>/<c>x:Static</c>) consumes it immediately, while <c>Binding</c>/
/// <c>StaticResource</c>/custom extensions retain it for the loader (X2) to resolve against the type
/// system. The node is immutable.
/// </summary>
/// <remarks>
/// Internal: surfaced through <c>InternalsVisibleTo</c> for tests (matrix §0.2 — the content is the
/// contract). The grammar (matrix §4):
/// <code>
/// Extension := '{' Name ( WS Positional (',' Positional)* )? ( ',' Named )* '}'
/// Named     := Name '=' Value
/// Value     := Extension | "'" QuotedChars "'" | BareChars      // '\' escapes within both
/// Literal   := '{}' rest-of-text                                 // brace-escape prefix
/// </code>
/// </remarks>
internal sealed class MarkupExtensionNode
{
    public MarkupExtensionNode(
        string name,
        IReadOnlyList<MarkupExtensionArgumentValue> positionalArguments,
        IReadOnlyList<MarkupExtensionNamedArgument> namedArguments,
        int line,
        int column)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        PositionalArguments = positionalArguments ?? Array.Empty<MarkupExtensionArgumentValue>();
        NamedArguments = namedArguments ?? Array.Empty<MarkupExtensionNamedArgument>();
        Line = line;
        Column = column;
    }

    /// <summary>The extension type name as written (e.g. <c>Binding</c>, <c>x:Static</c>, <c>StaticResource</c>).</summary>
    public string Name { get; }

    /// <summary>The positional arguments in source order.</summary>
    public IReadOnlyList<MarkupExtensionArgumentValue> PositionalArguments { get; }

    /// <summary>The named arguments in source order.</summary>
    public IReadOnlyList<MarkupExtensionNamedArgument> NamedArguments { get; }

    /// <summary>The 1-based line of the opening brace.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the opening brace.</summary>
    public int Column { get; }

    /// <summary>Looks up a named argument by case-sensitive name, or <c>null</c>.</summary>
    public MarkupExtensionArgumentValue? FindNamed(string name)
    {
        foreach (var arg in NamedArguments)
            if (string.Equals(arg.Name, name, StringComparison.Ordinal))
                return arg.Value;
        return null;
    }
}

/// <summary>A named argument <c>Name=Value</c> within an extension.</summary>
internal readonly struct MarkupExtensionNamedArgument
{
    public MarkupExtensionNamedArgument(string name, MarkupExtensionArgumentValue value, int line, int column)
    {
        Name = name;
        Value = value;
        Line = line;
        Column = column;
    }

    /// <summary>The argument name (the part before <c>=</c>).</summary>
    public string Name { get; }

    /// <summary>The argument value (a string or a nested extension).</summary>
    public MarkupExtensionArgumentValue Value { get; }

    /// <summary>The 1-based line of the argument name.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the argument name.</summary>
    public int Column { get; }
}

/// <summary>
/// An extension argument value: either a literal string (the bare/quoted form, with escapes already
/// resolved) or a nested <see cref="MarkupExtensionNode"/>.
/// </summary>
internal readonly struct MarkupExtensionArgumentValue
{
    private MarkupExtensionArgumentValue(string? text, MarkupExtensionNode? nested)
    {
        Text = text;
        Nested = nested;
    }

    /// <summary>The literal string value (escapes resolved), or <c>null</c> when this is a nested extension.</summary>
    public string? Text { get; }

    /// <summary>The nested extension, or <c>null</c> when this is a literal string.</summary>
    public MarkupExtensionNode? Nested { get; }

    /// <summary>True when this value is a nested extension rather than a literal.</summary>
    public bool IsNested => Nested is not null;

    /// <summary>Wraps a literal string value.</summary>
    public static MarkupExtensionArgumentValue FromText(string text) => new(text, null);

    /// <summary>Wraps a nested extension.</summary>
    public static MarkupExtensionArgumentValue FromNested(MarkupExtensionNode nested) => new(null, nested);
}
