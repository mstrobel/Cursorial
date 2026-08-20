// frontend node graph (internals via InternalsVisibleTo)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>The lexical kind of one path token.</summary>
internal enum XamlPathTokenKind
{
    /// <summary>A bare member segment: <c>Name</c>.</summary>
    Member,

    /// <summary>A parenthesized type-qualified segment: <c>(ns:Type.Member)</c>.</summary>
    QualifiedMember,

    /// <summary>An indexer segment: <c>[3]</c> / <c>['key']</c> / <c>[EnumType.Value]</c>. Binds to the
    /// PRECEDING segment — the grammar never puts a dot before <c>[</c>.</summary>
    Indexer,
}

/// <summary>How a <c>(...)</c> qualified segment splits its type token from its member name.</summary>
internal enum XamlQualifiedSplit
{
    /// <summary>Split at the FIRST dot — the runtime <c>BindingPath.ParseTypeQualified</c> rule
    /// (<c>prefix:Type</c> tokens only; dotted CLR namespaces do not work reflectively). Authoritative
    /// for anything that can fall back to the reflective lane.</summary>
    FirstDot,

    /// <summary>Split at the LAST dot — the compiled-lane superset that also accepts dotted type names.</summary>
    LastDot,
}

/// <summary>One lexical token of a path — see <see cref="XamlPathParser.TryTokenize"/>.</summary>
internal readonly struct XamlPathToken
{
    public XamlPathToken(XamlPathTokenKind kind, string? typeToken, string? member, string? key, bool keyWasQuoted)
    {
        Kind = kind;
        TypeToken = typeToken;
        Member = member;
        Key = key;
        KeyWasQuoted = keyWasQuoted;
    }

    public XamlPathTokenKind Kind { get; }

    /// <summary>The qualified segment's type token (<c>ns:Type</c> / dotted name), null otherwise.</summary>
    public string? TypeToken { get; }

    /// <summary>The member name (Member / QualifiedMember), null for indexers.</summary>
    public string? Member { get; }

    /// <summary>The indexer key text (quotes stripped, escapes applied), null otherwise.</summary>
    public string? Key { get; }

    /// <summary>Whether the key was single-quoted — a quoted key is ALWAYS a string key.</summary>
    public bool KeyWasQuoted { get; }
}

/// <summary>Which indexer key shape a resolved indexer segment uses.</summary>
internal enum XamlIndexKeyKind { Int, String, Enum }

/// <summary>
/// One RESOLVED path segment: the symbols behind a token, the type the walk continues on, and — for a
/// member whose owner registers a <c>&lt;Name&gt;Property</c> UIProperty — the registration field, so
/// emission can carry the property identity instead of reconstructing it by naming convention.
/// </summary>
internal sealed class XamlPathSegment
{
    public XamlPathTokenKind Kind { get; set; }

    /// <summary>The raw member name (Member / QualifiedMember); null for indexers.</summary>
    public string? MemberName { get; set; }

    /// <summary>The resolved member: <see cref="IPropertySymbol"/> or <see cref="IFieldSymbol"/>; for an
    /// indexer segment, the <c>this[]</c> property symbol.</summary>
    public ISymbol? Member { get; set; }

    /// <summary>The type this segment is accessed ON (the qualifier type for qualified segments).</summary>
    public INamedTypeSymbol? AccessedOn { get; set; }

    /// <summary>The segment's value type — what the walk continues on.</summary>
    public ITypeSymbol ValueType { get; set; } = null!;

    /// <summary>Whether this is a <c>(Type.Member)</c> cast-qualified access.</summary>
    public bool Qualified { get; set; }

    /// <summary>Whether the member resolved as a STATIC member — only ever true for the first segment
    /// (a static-rooted chain; the compiled-binding walk in <c>LoweringEmitter</c> produces it).</summary>
    public bool IsStaticRoot { get; set; }

    /// <summary>The <c>&lt;MemberName&gt;Property</c> UIProperty registration field on the owner's base
    /// chain, when one exists — the CLR member's property-system identity.</summary>
    public IFieldSymbol? UIPropertyField { get; set; }

    /// <summary>The member has NO CLR wrapper (an attached property, a wrapper-less registration): reads
    /// and writes go through <c>GetValue</c>/<c>SetValue</c> with <see cref="UIPropertyField"/>.</summary>
    public bool UIPropertyOnly { get; set; }

    // ── indexer segments ──

    public XamlIndexKeyKind KeyKind { get; set; }

    /// <summary>The typed key: boxed int, string, or the enum constant's <see cref="IFieldSymbol"/>.</summary>
    public object? Key { get; set; }

    /// <summary>The key as an emittable C# expression: <c>3</c>, <c>"name"</c>, <c>global::Ns.Enum.Member</c>.</summary>
    public string? KeyExpression { get; set; }
}

/// <summary>
/// The shared path parser for the build-time lanes (x:Static and bindings): one bracket- and
/// quote-aware tokenizer, one symbol-resolution walk, one indexer-key ladder, and one UIProperty
/// registration probe — so the lanes cannot disagree about what a path means. The x:Static walk is
/// contract-bound (X174) to mirror <c>ReflectionXamlMetadata.TryResolveStatic</c> in shape; the
/// binding walk mirrors <c>BindingPath</c>'s runtime grammar for everything that can fall back to
/// the reflective lane.
/// </summary>
internal static class XamlPathParser
{
    // ───────────────────────────── tokenizer ─────────────────────────────

    /// <summary>
    /// Tokenizes <paramref name="path"/> into member / qualified / indexer tokens. Grammar (the runtime
    /// <c>BindingPath</c> rules): <c>path := segment (('.' segment) | indexer)*</c> — an indexer binds
    /// WITHOUT a dot (<c>.[</c> is malformed); keys are <c>[int]</c>, <c>['quoted']</c> (backslash
    /// escapes), or bare text; a bare comma rejects (multi-argument indexers are unsupported).
    /// </summary>
    public static bool TryTokenize(string path, XamlQualifiedSplit split, out List<XamlPathToken> tokens)
    {
        tokens = new List<XamlPathToken>();
        int i = 0, n = path.Length;

        var expectSegment = true; // at path start / after a dot

        while (i < n)
        {
            char ch = path[i];

            if (expectSegment)
            {
                if (ch == '(')
                {
                    int close = path.IndexOf(')', i + 1);
                    if (close < 0)
                        return false; // unterminated qualifier

                    var content = path.Substring(i + 1, close - i - 1);
                    int dot = split == XamlQualifiedSplit.FirstDot
                        ? IndexOfQualifierDot(content, first: true)
                        : IndexOfQualifierDot(content, first: false);
                    if (dot <= 0 || dot >= content.Length - 1)
                        return false; // no member half

                    var typeToken = content.Substring(0, dot);
                    var member = content.Substring(dot + 1);
                    if (typeToken.Length == 0 || member.Length == 0 || member.IndexOf('.') >= 0 && split == XamlQualifiedSplit.FirstDot)
                        return false; // runtime rule: (Type.Member) holds exactly one member

                    tokens.Add(new XamlPathToken(XamlPathTokenKind.QualifiedMember, typeToken, member, null, false));
                    i = close + 1;
                }
                else
                {
                    int start = i;
                    while (i < n && path[i] != '.' && path[i] != '[' && path[i] != '(' && path[i] != ')')
                        i++;
                    if (i == start)
                        return false; // empty segment ('.' at start, '..', dangling)
                    if (i < n && (path[i] == '(' || path[i] == ')'))
                        return false; // parens only open a segment

                    tokens.Add(new XamlPathToken(XamlPathTokenKind.Member, null, path.Substring(start, i - start), null, false));
                }

                expectSegment = false;
                continue;
            }

            if (ch == '.')
            {
                if (i + 1 < n && path[i + 1] == '[')
                    return false; // the runtime grammar: an indexer never follows a dot
                i++;
                expectSegment = true;
                continue;
            }

            if (ch == '[')
            {
                i++;
                string key;
                bool quoted = false;

                if (i < n && path[i] == '\'')
                {
                    quoted = true;
                    i++;
                    var sb = new System.Text.StringBuilder();
                    while (i < n && path[i] != '\'')
                    {
                        if (path[i] == '\\' && i + 1 < n)
                            i++;
                        sb.Append(path[i]);
                        i++;
                    }
                    if (i >= n)
                        return false; // unterminated quote
                    i++; // closing quote
                    if (i >= n || path[i] != ']')
                        return false;
                    key = sb.ToString();
                }
                else
                {
                    int start = i;
                    while (i < n && path[i] != ']')
                    {
                        if (path[i] == ',')
                            return false; // multi-argument indexers are unsupported (runtime rule)
                        i++;
                    }
                    if (i >= n || i == start)
                        return false; // unterminated / empty key
                    key = path.Substring(start, i - start).Trim();
                    if (key.Length == 0)
                        return false;
                }

                i++; // ']'
                tokens.Add(new XamlPathToken(XamlPathTokenKind.Indexer, null, null, key, quoted));
                continue;
            }

            return false; // anything else after a segment is malformed
        }

        return !expectSegment && tokens.Count > 0; // must not end on a dangling dot
    }

    private static int IndexOfQualifierDot(string content, bool first)
        => first ? content.IndexOf('.') : content.LastIndexOf('.');

    // ───────────────────────────── the x:Static chain ─────────────────────────────

    /// <summary>
    /// Resolves an x:Static member path (<c>Type.Static.Instance[key]…</c> — the TYPE is the first
    /// segment) to its emitted expression and final value type. Rules kept byte-identical in shape to
    /// <c>ReflectionXamlMetadata.TryResolveStatic</c> (X174): the first member is a public static
    /// field / readable property DIRECTLY declared on the type; deeper members are public instance,
    /// base walk allowed; indexers use the <see cref="TryResolveIndexer"/> ladder anywhere after the
    /// first member.
    /// </summary>
    public static bool TryResolveStaticChain(
        XamlSymbolResolver resolver, string xmlNamespace, string memberPath,
        out string? expression, out ITypeSymbol? valueType)
    {
        expression = null;
        valueType = null;

        if (!TryTokenize(memberPath, XamlQualifiedSplit.FirstDot, out var tokens))
            return false;

        // Token 0 = the type name; token 1 = the static member. Both plain members, no indexers.
        if (tokens.Count < 2 ||
            tokens[0].Kind != XamlPathTokenKind.Member ||
            tokens[1].Kind != XamlPathTokenKind.Member)
        {
            return false;
        }

        var type = resolver.Resolve(xmlNamespace, tokens[0].Member!, out _);
        if (type is null)
            return false;

        var current = StaticMemberType(type, tokens[1].Member!);
        if (current is null)
            return false;

        var text = new System.Text.StringBuilder();
        text.Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('.').Append(tokens[1].Member);

        for (int t = 2; t < tokens.Count; t++)
        {
            var token = tokens[t];

            if (token.Kind == XamlPathTokenKind.Member)
            {
                current = InstanceMemberType(current, token.Member!);
                if (current is null)
                    return false;
                text.Append('.').Append(token.Member);
                continue;
            }

            if (token.Kind != XamlPathTokenKind.Indexer ||
                TryResolveIndexer(current, token.Key!, token.KeyWasQuoted, out var indexer, out _, out _, out var keyExpr) is false)
            {
                return false;
            }

            current = indexer!.Type;
            text.Append('[').Append(keyExpr).Append(']');
        }

        expression = text.ToString();
        valueType = current;
        return true;
    }

    /// <summary>Public static field / readable property DIRECTLY declared on <paramref name="type"/> (x:Static rule 1).</summary>
    public static ITypeSymbol? StaticMemberType(ITypeSymbol type, string name)
    {
        foreach (var m in type.GetMembers(name))
        {
            if (m is IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } f)
                return f.Type;
            if (m is IPropertySymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } p)
                return p.Type;
        }

        return null;
    }

    /// <summary>Public instance field / readable property on <paramref name="type"/> or a base (x:Static rule 2).</summary>
    public static ITypeSymbol? InstanceMemberType(ITypeSymbol type, string name)
    {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
            foreach (var m in t.GetMembers(name))
            {
                if (m is IFieldSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } f)
                    return f.Type;
                if (m is IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } p)
                    return p.Type;
            }

        return null;
    }

    // ───────────────────────────── the indexer ladder ─────────────────────────────

    /// <summary>
    /// Matches an indexer key against the single-parameter <c>this[]</c> indexers of
    /// <paramref name="type"/> (base walk; metadata name <c>Item</c> only — the reflective lane probes
    /// <c>GetProperty("Item", …)</c>, so an <c>[IndexerName]</c>-renamed indexer must not match). The
    /// ladder is deterministic and mirrored by the runtime: (1) an UNQUOTED int-parsable key takes an
    /// <c>Item[int]</c>; (2) a key whose last dot-segment names a constant of an <c>Item[TEnum]</c>'s
    /// key type (case-insensitive) takes it; (3) anything else takes <c>Item[string]</c> (or
    /// <c>Item[object]</c>). Quoted keys skip (1) — a quoted key is always textual.
    /// </summary>
    public static bool TryResolveIndexer(
        ITypeSymbol type, string key, bool keyWasQuoted,
        out IPropertySymbol? indexer, out XamlIndexKeyKind keyKind, out object? typedKey, out string? keyExpression)
    {
        indexer = null;
        keyKind = default;
        typedKey = null;
        keyExpression = null;

        IPropertySymbol? intIndexer = null, stringIndexer = null, objectIndexer = null;
        var enumIndexers = new List<IPropertySymbol>();

        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers("this[]"))
            {
                if (member is not IPropertySymbol { IsIndexer: true, Parameters.Length: 1, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } p)
                    continue;
                if (p.MetadataName != "Item")
                    continue; // the reflective lane resolves by metadata name "Item" only

                var param = p.Parameters[0].Type;
                if (param.SpecialType == SpecialType.System_Int32)
                    intIndexer ??= p;
                else if (param.SpecialType == SpecialType.System_String)
                    stringIndexer ??= p;
                else if (param.SpecialType == SpecialType.System_Object)
                    objectIndexer ??= p;
                else if (param.TypeKind == TypeKind.Enum)
                    enumIndexers.Add(p);
            }
        }

        // (1) int
        if (!keyWasQuoted && int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intKey) && intIndexer is not null)
        {
            indexer = intIndexer;
            keyKind = XamlIndexKeyKind.Int;
            typedKey = intKey;
            keyExpression = intKey.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        // (2) enum — the token's LAST dot-segment, case-insensitive (the runtime AccessorCache rule)
        if (!keyWasQuoted)
        {
            var lastSegment = key.LastIndexOf('.') is var dot && dot >= 0 ? key.Substring(dot + 1) : key;

            foreach (var candidate in enumIndexers)
            {
                var enumType = candidate.Parameters[0].Type;
                var constant = enumType.GetMembers()
                                       .OfType<IFieldSymbol>()
                                       .FirstOrDefault(f => f.HasConstantValue &&
                                                            string.Equals(f.Name, lastSegment, StringComparison.OrdinalIgnoreCase));
                if (constant is null)
                    continue;

                indexer = candidate;
                keyKind = XamlIndexKeyKind.Enum;
                typedKey = constant;
                keyExpression = $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{constant.Name}";
                return true;
            }
        }

        // (3) string (Item[string], else Item[object] — the dictionary shape)
        if ((stringIndexer ?? objectIndexer) is { } textual)
        {
            indexer = textual;
            keyKind = XamlIndexKeyKind.String;
            typedKey = key;
            keyExpression = $"\"{EscapeLiteral(key)}\"";
            return true;
        }

        return false;
    }

    // ───────────────────────────── UIProperty registration ─────────────────────────────

    /// <summary>
    /// The <c>&lt;memberName&gt;Property</c> UIProperty registration FIELD on <paramref name="type"/>'s
    /// base chain, or null — the field-returning sibling of
    /// <c>SymbolXamlModel.FindRegisteredPropertyOwner</c> (which discards the field). Matching rules are
    /// identical: public static field whose type's base chain is a Cursorial UIProperty shape.
    /// </summary>
    public static IFieldSymbol? FindRegisteredPropertyField(ITypeSymbol type, string memberName)
    {
        var fieldName = memberName + "Property";

        for (ITypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(fieldName))
            {
                if (member is IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } field &&
                    IsUIPropertyType(field.Type))
                {
                    return field;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The registration field's VALUE type when the field is StyledProperty/AttachedProperty-shaped —
    /// the shapes whose typed <c>GetValue&lt;T&gt;</c>/<c>SetValue&lt;T&gt;</c> overloads C# resolves
    /// from the field reference alone (AttachedProperty&lt;T&gt; derives StyledProperty&lt;T&gt;).
    /// Null for DirectProperty / plain-UIProperty registrations, whose wrapper-less form would read
    /// as <c>object</c> — those decline rather than emit an untyped chain.
    /// </summary>
    public static ITypeSymbol? StyledRegistrationValueType(IFieldSymbol field)
    {
        for (var t = field.Type as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            if (t.TypeArguments.Length == 1 &&
                t.OriginalDefinition.ToDisplayString().StartsWith("Cursorial.UI.StyledProperty", StringComparison.Ordinal))
            {
                return t.TypeArguments[0];
            }
        }

        return null;
    }

    private static bool IsUIPropertyType(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = t.OriginalDefinition.ToDisplayString();

            if (name.StartsWith("Cursorial.UI.StyledProperty", StringComparison.Ordinal) ||
                name.StartsWith("Cursorial.UI.DirectProperty", StringComparison.Ordinal) ||
                name.StartsWith("Cursorial.UI.AttachedProperty", StringComparison.Ordinal) ||
                name == "Cursorial.UI.UIProperty")
            {
                return true;
            }
        }

        return false;
    }

    private static string EscapeLiteral(string text) => text
        .Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\0", "\\0");
}
