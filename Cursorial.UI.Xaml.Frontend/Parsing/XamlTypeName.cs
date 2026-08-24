using System;
using System.Collections.Generic;
using System.Text;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The XAML 2009 <c>x:TypeArguments</c> type-name grammar (W3, design doc <c>xaml-conversion-routes.md</c>
/// §1 W3): comma-separated names with System.Xaml's parenthesized nesting —
/// <c>x:String</c>, <c>sys:Int32</c>, <c>scg:List(x:String)</c>, <c>scg:Dictionary(x:String, x:Int32)</c>.
/// The 2009-compatible core is oracle-pinned on Windows CI; the Cursorial extensions (array <c>[]</c> and
/// nullable <c>?</c> suffixes in type-argument position) are separately-marked rows — XML forbids raw
/// <c>&lt;</c> in attribute values, so the parenthesized form is the nesting syntax, never angle brackets.
/// </summary>
/// <remarks>
/// This is pure GRAMMAR — prefixes stay unresolved strings (the parser binds them against the live reader
/// scope, the same timing as every other prefix-bearing token). One node per name:
/// <c>Prefix?</c> + <c>Name</c> + <c>TypeArguments</c> (empty for non-generic) + the extension suffixes.
/// </remarks>
public sealed class XamlTypeName
{
    private XamlTypeName(string? prefix, string name, IReadOnlyList<XamlTypeName> typeArguments, bool isArray, bool isNullable)
    {
        Prefix = prefix;
        Name = name;
        TypeArguments = typeArguments;
        IsArray = isArray;
        IsNullable = isNullable;
    }

    /// <summary>The xmlns prefix, or null for the in-scope default namespace.</summary>
    public string? Prefix { get; }

    /// <summary>The local type name (no arity suffix — arity is implied by <see cref="TypeArguments"/>).</summary>
    public string Name { get; }

    /// <summary>The type arguments (empty for a non-generic name).</summary>
    public IReadOnlyList<XamlTypeName> TypeArguments { get; }

    /// <summary>The Cursorial <c>[]</c> extension: the name denotes a single-dimensional array of itself.</summary>
    public bool IsArray { get; }

    /// <summary>The Cursorial <c>?</c> extension: the name denotes a <c>Nullable&lt;T&gt;</c> of itself.</summary>
    public bool IsNullable { get; }

    /// <summary>
    /// Parses a comma-separated type-name LIST (the <c>x:TypeArguments</c> attribute value). Returns
    /// false with a positioned message (0-based <paramref name="errorOffset"/> into
    /// <paramref name="text"/>) on malformed input — empty input, unbalanced parens, empty arguments,
    /// trailing garbage.
    /// </summary>
    public static bool TryParseList(
        string text,
        out IReadOnlyList<XamlTypeName> names,
        out string? error,
        out int errorOffset)
    {
        names = Array.Empty<XamlTypeName>();
        error = null;
        errorOffset = 0;

        var position = 0;
        var result = new List<XamlTypeName>();

        while (true)
        {
            if (!TryParseOne(text, ref position, result.Count == 0, out var name, out error, out errorOffset))
                return false;

            result.Add(name!);
            SkipWhitespace(text, ref position);

            if (position >= text.Length)
                break;

            if (text[position] != ',')
            {
                error = $"Unexpected character '{text[position]}' — expected ',' between type names.";
                errorOffset = position;
                return false;
            }

            position++; // consume ','
        }

        names = result;
        return true;
    }

    private static bool TryParseOne(
        string text,
        ref int position,
        bool isFirst,
        out XamlTypeName? name,
        out string? error,
        out int errorOffset)
    {
        name = null;
        error = null;
        errorOffset = 0;

        SkipWhitespace(text, ref position);

        int nameStart = position;
        int colon = -1;

        while (position < text.Length && IsNameChar(text[position]))
        {
            if (text[position] == ':')
            {
                if (colon >= 0)
                {
                    error = "A type name may carry at most one prefix.";
                    errorOffset = position;
                    return false;
                }
                colon = position;
            }
            position++;
        }

        if (position == nameStart || (colon >= 0 && (colon == nameStart || colon == position - 1)))
        {
            error = isFirst && position >= text.Length && nameStart >= text.Length
                        ? "Empty type-name list."
                        : "Expected a type name.";
            errorOffset = nameStart;
            return false;
        }

        string? prefix = colon >= 0 ? text.Substring(nameStart, colon - nameStart) : null;
        string localName = colon >= 0 ? text.Substring(colon + 1, position - colon - 1) : text.Substring(nameStart, position - nameStart);

        // Nested type arguments — the System.Xaml parenthesized form.
        var arguments = (IReadOnlyList<XamlTypeName>)Array.Empty<XamlTypeName>();
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == '(')
        {
            position++; // consume '('
            var nested = new List<XamlTypeName>();

            while (true)
            {
                if (!TryParseOne(text, ref position, isFirst: false, out var argument, out error, out errorOffset))
                    return false;

                nested.Add(argument!);
                SkipWhitespace(text, ref position);

                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (position < text.Length && text[position] == ')')
                {
                    position++; // consume ')'
                    break;
                }

                error = "Unbalanced '(' — expected ',' or ')'.";
                errorOffset = position < text.Length ? position : text.Length - 1;
                return false;
            }

            arguments = nested;
        }

        // The Cursorial suffix extensions (separately-marked rows; applied outermost-last: `x:Double?`
        // is Nullable<double>, `x:String[]` is string[]; `x:Double?[]` is double?[]).
        bool isNullable = false, isArray = false;
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == '?')
        {
            isNullable = true;
            position++;
            SkipWhitespace(text, ref position);
        }

        if (position + 1 < text.Length && text[position] == '[' && text[position + 1] == ']')
        {
            isArray = true;
            position += 2;
        }

        name = new XamlTypeName(prefix, localName, arguments, isArray, isNullable);
        return true;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }

    // Name characters: NCName-ish (letters/digits/underscore/dot for nested CLR names) + ':' for the
    // prefix split (validated to at most one). '.'-bearing names address nested types (Owner.Nested).
    private static bool IsNameChar(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '.' or ':';

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Prefix is not null)
            sb.Append(Prefix).Append(':');
        sb.Append(Name);
        if (TypeArguments.Count > 0)
        {
            sb.Append('(');
            for (int i = 0; i < TypeArguments.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeArguments[i]);
            }
            sb.Append(')');
        }
        if (IsNullable) sb.Append('?');
        if (IsArray) sb.Append("[]");
        return sb.ToString();
    }
}
