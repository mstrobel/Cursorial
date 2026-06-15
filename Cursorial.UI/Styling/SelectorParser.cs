// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The span-based recursive-descent parser behind <see cref="Selector.Parse"/> (grammar: design doc
/// §3.1; lexing/case/position rules: style matrix SD2/SD3). Structural and fence errors surface
/// during the scan; type tokens are collected with their positions and resolved <em>after</em> the
/// structural parse completes, so a fence violation is always reported even when an earlier type
/// token would not resolve.
/// </summary>
internal static class SelectorParser
{
    private const string TemplateCombinatorText = "/template/";

    internal static SelectorBranch[] Parse(string text, ISelectorTypeResolver resolver)
    {
        var branches = new List<BranchDraft>(1);
        var position = 0;

        while (true)
        {
            SkipWhitespace(text, ref position);
            branches.Add(ParseBranch(text, ref position));
            SkipWhitespace(text, ref position);

            if (position >= text.Length)
                break;

            if (text[position] == ',')
            {
                position++;
                continue;
            }

            throw new SelectorParseException($"Unexpected character '{text[position]}' in selector.", position);
        }

        return ResolveTypes(branches, resolver);
    }

    // ───────────────────────────── branch / combinators ─────────────────────────────

    private static BranchDraft ParseBranch(string text, ref int position)
    {
        var branch = new BranchDraft();
        branch.Compounds.Add(ParseCompound(text, ref position, isLeftmost: true));

        while (true)
        {
            var beforeWhitespace = position;
            SkipWhitespace(text, ref position);
            var whitespaceSeen = position > beforeWhitespace;

            if (position >= text.Length)
            {
                position = beforeWhitespace; // leave trailing whitespace to the caller
                break;
            }

            var c = text[position];
            SelectorCombinator combinator;

            switch (c)
            {
                case ',':
                    position = beforeWhitespace;
                    return branch;

                case '>':
                    position++;
                    SkipWhitespace(text, ref position);
                    combinator = SelectorCombinator.Child;
                    break;

                case '/':
                    ExpectTemplateCombinator(text, ref position);
                    SkipWhitespace(text, ref position);
                    combinator = SelectorCombinator.Template;
                    break;

                case '+' or '~':
                    throw new SelectorParseException(
                        $"The sibling combinator '{c}' is unsupported by design (design doc §3.10) — " +
                        "use classes assigned by the owning panel instead.", position);

                default:
                    if (whitespaceSeen && IsCompoundStart(c))
                    {
                        combinator = SelectorCombinator.Descendant;
                        break;
                    }

                    position = beforeWhitespace;
                    return branch;
            }

            branch.Combinators.Add(combinator);
            branch.Compounds.Add(ParseCompound(text, ref position, isLeftmost: false));
        }

        return branch;
    }

    private static void ExpectTemplateCombinator(string text, ref int position)
    {
        if (!text.AsSpan(position).StartsWith(TemplateCombinatorText, StringComparison.Ordinal))
            throw new SelectorParseException("Expected the '/template/' combinator.", position);

        position += TemplateCombinatorText.Length;
    }

    // ───────────────────────────── compounds ─────────────────────────────

    private static CompoundDraft ParseCompound(string text, ref int position, bool isLeftmost)
    {
        if (position >= text.Length || !IsCompoundStart(text[position]))
        {
            throw new SelectorParseException(
                position >= text.Length ? "Expected a selector." : $"Expected a selector, found '{text[position]}'.",
                position);
        }

        var compound = new CompoundDraft();

        if (text[position] == '^')
        {
            if (!isLeftmost)
            {
                throw new SelectorParseException(
                    "The nesting anchor '^' is only valid as the leftmost compound of a selector.", position);
            }

            compound.IsNesting = true;
            position++;
        }

        // Optional type token: a bare identifier, or ':is(' type ')'.
        if (position < text.Length)
        {
            var c = text[position];

            if (IsIdentifierStart(c))
            {
                compound.TypeTokenPosition = position;
                compound.TypeName = ParseTypeToken(text, ref position);
            }
            else if (c == ':' && text.AsSpan(position).StartsWith(":is(", StringComparison.Ordinal))
            {
                position += 4;
                SkipWhitespace(text, ref position);
                compound.TypeTokenPosition = position;
                compound.TypeName = ParseTypeToken(text, ref position);
                compound.IsAssignableType = true;
                SkipWhitespace(text, ref position);

                if (position >= text.Length || text[position] != ')')
                    throw new SelectorParseException("Expected ')' to close ':is('.", position);

                position++;
            }
        }

        // Simples, in declaration order.
        while (position < text.Length)
        {
            var c = text[position];

            switch (c)
            {
                case '.':
                    position++;
                    compound.Simples.Add(new SimpleSelector(SimpleSelectorKind.Class, ParseIdentifier(text, ref position)));
                    break;

                case '#':
                    position++;
                    compound.Simples.Add(new SimpleSelector(SimpleSelectorKind.Name, ParseIdentifier(text, ref position)));
                    break;

                case ':':
                {
                    var colonPosition = position;
                    position++;
                    var name = ParseIdentifier(text, ref position);
                    ThrowIfFencedPseudoClass(name, colonPosition);

                    if (string.Equals(name, "is", StringComparison.Ordinal) && position < text.Length && text[position] == '(')
                    {
                        throw new SelectorParseException(
                            "':is()' is a type test and must appear at the start of a compound.", colonPosition);
                    }

                    compound.Simples.Add(new SimpleSelector(SimpleSelectorKind.PseudoClass, name));
                    break;
                }

                case '[':
                    throw new SelectorParseException(
                        "Attribute selectors ('[...]') are unsupported by design (design doc §3.10) — " +
                        "use PseudoClassMapping or a class instead.", position);

                case '^':
                    throw new SelectorParseException(
                        "The nesting anchor '^' is only valid as the leftmost compound of a selector.", position);

                default:
                    if (!compound.HasContent)
                        throw new SelectorParseException($"Expected a selector, found '{c}'.", position);

                    return compound;
            }
        }

        if (!compound.HasContent)
            throw new SelectorParseException("Expected a selector.", position);

        return compound;
    }

    /// <summary>The NO-fence for pseudo-class-shaped constructs (SD3; design doc §3.10).</summary>
    private static void ThrowIfFencedPseudoClass(string name, int position)
    {
        string? construct = name switch
                            {
                                "not"                                                    => "':not()'",
                                "first-child"                                            => "':first-child'",
                                "last-child"                                             => "':last-child'",
                                _ when name.StartsWith("nth-", StringComparison.Ordinal) => $"':{name}()'",
                                _                                                        => null
                            };

        if (construct is not null)
        {
            throw new SelectorParseException(
                $"{construct} is unsupported by design (design doc §3.10) — positional and negation " +
                "selectors break element-local invalidation; use classes as the escape hatch.", position);
        }
    }

    // ───────────────────────────── lexing (SD2) ─────────────────────────────

    /// <summary>
    /// Reads a type token: a bare identifier, optionally namespace-qualified as <c>prefix|Local</c>. The
    /// pipe is the CSS / Avalonia namespace-selector separator (<c>:</c> is reserved for pseudo-classes), so
    /// it is recognized ONLY in type-token position — never in a <c>.class</c>/<c>#name</c>/<c>:pseudo</c>
    /// token. A qualified token is resolved by a namespace-aware <see cref="ISelectorTypeResolver"/> (the
    /// XAML loader supplies one binding the prefix to its xmlns); the default resolver matches simple names
    /// only and rejects a qualified token as unknown.
    /// </summary>
    private static string ParseTypeToken(string text, ref int position)
    {
        var name = ParseIdentifier(text, ref position);

        if (position < text.Length && text[position] == '|')
        {
            position++;
            if (position >= text.Length || !IsIdentifierStart(text[position]))
            {
                throw new SelectorParseException(
                    "Expected a local type name after '|' in a namespace-qualified selector type.", position);
            }

            return name + "|" + ParseIdentifier(text, ref position);
        }

        return name;
    }

    private static string ParseIdentifier(string text, ref int position)
    {
        if (position >= text.Length || !IsIdentifierStart(text[position]))
        {
            throw new SelectorParseException(
                position >= text.Length
                    ? "Expected an identifier."
                    : $"Expected an identifier, found '{text[position]}' (identifiers are [A-Za-z_][A-Za-z0-9_-]*).",
                position);
        }

        var start = position;

        position++;

        while (position < text.Length && IsIdentifierPart(text[position]))
            position++;

        return text[start..position];
    }

    private static bool IsIdentifierStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || c is (>= '0' and <= '9') or '-';

    private static bool IsCompoundStart(char c) => IsIdentifierStart(c) || c is '^' or '.' or '#' or ':';

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t')
            position++;
    }

    // ───────────────────────────── type resolution (post-parse, SD1) ─────────────────────────────

    private static SelectorBranch[] ResolveTypes(List<BranchDraft> branches, ISelectorTypeResolver resolver)
    {
        var result = new SelectorBranch[branches.Count];

        for (var i = 0; i < branches.Count; i++)
        {
            var draft = branches[i];
            var compounds = new SelectorCompound[draft.Compounds.Count];

            for (var j = 0; j < compounds.Length; j++)
            {
                var compound = draft.Compounds[j];
                Type? type = null;

                if (compound.TypeName is {} typeName)
                {
                    type = resolver.Resolve(typeName, out var ambiguousCandidates);

                    // SD1: an ambiguous simple name is a parse error listing the candidate full names.
                    if (ambiguousCandidates is { Count: > 0 } candidates)
                    {
                        throw new SelectorParseException(
                            $"The selector type token '{typeName}' is ambiguous between: " +
                            $"{string.Join(", ", candidates.Select(static t => $"'{t.FullName}'").Order(StringComparer.Ordinal))}. " +
                            "Use a custom ISelectorTypeResolver to disambiguate.",
                            compound.TypeTokenPosition);
                    }

                    if (type is null)
                    {
                        throw new SelectorParseException(
                            typeName.Contains('|', StringComparison.Ordinal)
                                ? $"Unknown namespace-qualified selector type '{typeName}' — the prefix is unbound or " +
                                  "the type does not exist in its namespace (a 'prefix|Type' token needs a " +
                                  "namespace-aware resolver; the default resolver matches simple names)."
                                : $"Unknown selector type token '{typeName}' — the resolver knows no element type by that name.",
                            compound.TypeTokenPosition);
                    }
                }

                compounds[j] = new SelectorCompound(
                    compound.IsNesting, type, compound.IsAssignableType, compound.TypeName, [.. compound.Simples]);
            }

            result[i] = new SelectorBranch(compounds, [.. draft.Combinators]);
        }

        return result;
    }

    private sealed class BranchDraft
    {
        internal readonly List<CompoundDraft> Compounds = [];
        internal readonly List<SelectorCombinator> Combinators = [];
    }

    private sealed class CompoundDraft
    {
        internal bool IsNesting;
        internal string? TypeName;
        internal bool IsAssignableType;
        internal int TypeTokenPosition;
        internal readonly List<SimpleSelector> Simples = [];

        internal bool HasContent => IsNesting || TypeName is not null || Simples.Count > 0;
    }
}