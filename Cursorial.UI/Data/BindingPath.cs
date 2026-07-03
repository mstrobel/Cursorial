using System.Text;

namespace Cursorial.UI.Data;

/// <summary>The kind of a <see cref="PathSegment"/>.</summary>
internal enum PathSegmentKind : byte
{
    /// <summary>A CLR property or registered <c>UIProperty</c> step (<c>Name</c>).</summary>
    Property,

    /// <summary>An integer indexer (<c>[0]</c>).</summary>
    IntIndexer,

    /// <summary>A string indexer (<c>[key]</c> / <c>['key']</c>).</summary>
    StringIndexer,

    /// <summary>A type-qualified property segment (<c>(Owner.Member)</c>) — the owner type resolves at parse time
    /// (attached property like <c>(Grid.Row)</c>, OR a regular property qualified by owner type for
    /// disambiguation/clarity); the member itself is resolved on the source at runtime.</summary>
    TypeQualified
}

/// <summary>One parsed hop of a <see cref="BindingPath"/> (design doc §6.3 / spec §3.2).</summary>
internal readonly struct PathSegment
{
    private PathSegment(PathSegmentKind kind, string? name, int intIndex, Type? qualifierType, UIProperty? qualifiedProperty)
    {
        Kind = kind;
        Name = name;
        IntIndex = intIndex;
        QualifierType = qualifierType;
        QualifiedProperty = qualifiedProperty;
    }

    public PathSegmentKind Kind { get; }

    /// <summary>The property/member name (<see cref="PathSegmentKind.Property"/>) or string key (<see cref="PathSegmentKind.StringIndexer"/>); the member name for <see cref="PathSegmentKind.TypeQualified"/>.</summary>
    public string? Name { get; }

    /// <summary>The integer index (<see cref="PathSegmentKind.IntIndexer"/>).</summary>
    public int IntIndex { get; }

    /// <summary>The resolved owner type of a type-qualified segment (the <c>Owner</c> in <c>(Owner.Member)</c>).</summary>
    public Type? QualifierType { get; }

    /// <summary>The <c>UIProperty</c> registered on <see cref="QualifierType"/> under <see cref="Name"/> (attached OR
    /// regular), when one exists; <see langword="null"/> when the member is a plain CLR property qualified for clarity.</summary>
    public UIProperty? QualifiedProperty { get; }

    public static PathSegment Property(string name) => new(PathSegmentKind.Property, name, 0, null, null);

    public static PathSegment IntIndexer(int index) => new(PathSegmentKind.IntIndexer, null, index, null, null);

    public static PathSegment StringIndexer(string key) => new(PathSegmentKind.StringIndexer, key, 0, null, null);

    public static PathSegment TypeQualified(Type owner, string member, UIProperty? property)
        => new(PathSegmentKind.TypeQualified, member, 0, owner, property);
}

/// <summary>
/// A parsed binding path (design doc §6.3) — property chains, single-argument int/string indexers,
/// and type-qualified property segments. Construction-immutable; parsed once per descriptor and
/// cached on it (matrix B16). Grammar v1:
/// <code>
/// path     := '' | '.' | step ( '.' step | indexer )*
/// step     := identifier | '(' Type '.' identifier ')'
/// indexer  := '[' ( integer | string ) ']'
/// </code>
/// The parenthesized <c>(Type.Member)</c> form is a TYPE-QUALIFIED property: only the owner type resolves at
/// parse time (via the resolver / xmlns). It is NOT assumed to be an attached property — it may be a regular
/// property qualified by owner type for disambiguation or clarity (WPF <c>PropertyPath</c> semantics); the
/// member is resolved on the runtime source (a registered <c>UIProperty</c>, or a CLR property by name).
/// A non-integer indexer token is a string key, but at resolution time it also coerces to an
/// <c>Item[SomeEnum]</c> parameter when the source exposes one: <c>[Active]</c> or the qualified
/// <c>[Status.Active]</c> binds an enum-keyed dictionary / indexer (case-insensitive; see
/// <c>AccessorCache.ResolveStringIndexer</c>). This is the ergonomic alternative to WPF's
/// <c>[(local:Status)Active]</c> cast form (the cast lane itself stays deferred, below).
/// Out (recorded, throw with position): multi-argument indexers, source casts <c>(local:T)x</c>,
/// slash/XPath, <c>Path=/</c> current-item.
/// </summary>
public sealed class BindingPath
{
    private readonly PathSegment[] _segments;
    private string? _toString;

    private BindingPath(PathSegment[] segments) => _segments = segments;

    /// <summary>The empty path (<c>""</c> / <c>"."</c>) — the source object itself.</summary>
    public static readonly BindingPath Empty = new([]);

    /// <summary>The number of hops (0 for <see cref="Empty"/>).</summary>
    public int SegmentCount => _segments.Length;

    /// <summary>Whether this is the empty path (the source itself).</summary>
    public bool IsEmpty => _segments.Length == 0;

    internal ReadOnlySpan<PathSegment> Segments => _segments;

    /// <summary>
    /// Parses <paramref name="text"/>. Type-qualified segments (<c>(Grid.Row)</c>) require a resolver;
    /// <paramref name="resolver"/> <see langword="null"/> uses <see cref="DefaultPathTypeResolver"/>.
    /// Throws <see cref="FormatException"/> (carrying the offending <c>Position</c> in its message)
    /// on a malformed path; <see cref="ArgumentNullException"/> on null text.
    /// </summary>
    public static BindingPath Parse(string text, IPathTypeResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0 || text == ".")
            return Empty;

        var parser = new Parser(text, resolver ?? DefaultPathTypeResolver.Instance);
        var segments = parser.ParseAll();
        return new BindingPath(segments);
    }

    /// <summary>
    /// Builds a path directly from resolved <see cref="UIProperty"/> steps — the compile-time-checked form
    /// (<c>PropertyPath(ContentProperty, BackgroundProperty)</c>): each property is a fully-resolved
    /// type-qualified hop (its exact <c>UIProperty</c> baked in, read via <c>UIPropertyAccessor</c> at runtime),
    /// so nothing parses or resolves lazily. Zero properties ⇒ <see cref="Empty"/>.
    /// </summary>
    internal static BindingPath FromProperties(UIProperty[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0)
            return Empty;

        var segments = new PathSegment[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i]
                           ?? throw new ArgumentException("A binding-path property step may not be null.", nameof(properties));
            segments[i] = PathSegment.TypeQualified(property.OwnerType, property.Name, property);
        }

        return new BindingPath(segments);
    }

    /// <summary>
    /// Renders the parsed path back to its <b>canonical</b> text. String indexers are always emitted
    /// single-quoted (<c>['key']</c>), so an unquoted-string-indexer input (<c>[key]</c>) canonicalizes
    /// to the quoted form on round-trip — <c>Parse("[key]").ToString()</c> is <c>"['key']"</c>. Both
    /// spellings parse to the identical segment; the single-quoted form is the canonical contract.
    /// </summary>
    public override string ToString()
    {
        if (_toString is { } cached)
            return cached;

        if (_segments.Length == 0)
            return _toString = string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < _segments.Length; i++)
        {
            var seg = _segments[i];
            switch (seg.Kind)
            {
                case PathSegmentKind.Property:
                    if (i > 0)
                        sb.Append('.');
                    sb.Append(seg.Name);
                    break;
                case PathSegmentKind.TypeQualified:
                    if (i > 0)
                        sb.Append('.');
                    sb.Append('(').Append(seg.QualifierType!.Name).Append('.').Append(seg.Name).Append(')');
                    break;
                case PathSegmentKind.IntIndexer:
                    sb.Append('[').Append(seg.IntIndex).Append(']');
                    break;
                case PathSegmentKind.StringIndexer:
                    sb.Append("['").Append(seg.Name).Append("']");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown path segment kind {seg.Kind}.");
            }
        }

        return _toString = sb.ToString();
    }

    private ref struct Parser(string text, IPathTypeResolver resolver)
    {
        private readonly string _text = text;
        private readonly IPathTypeResolver _resolver = resolver;
        private int _pos;

        public PathSegment[] ParseAll()
        {
            var segments = new List<PathSegment>(4);
            var expectStep = true; // the first token must be a step (not an indexer continuation)

            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                switch (c)
                {
                    case '.':
                        // A dot introduces the next step; it must be followed by a step.
                        _pos++;
                        ParseStep(segments);
                        expectStep = false;
                        break;
                    case '[':
                        segments.Add(ParseIndexer());
                        expectStep = false;
                        break;
                    case '/':
                        throw Fail(_pos, "slash / current-item (Path=/) syntax is unsupported by design (no collection views in v1).");
                    default:
                        if (expectStep)
                        {
                            ParseStep(segments);
                            expectStep = false;
                        }
                        else
                        {
                            throw Fail(_pos, $"unexpected character '{c}'; expected '.', '[', or end of path.");
                        }

                        break;
                }
            }

            return [.. segments];
        }

        private void ParseStep(List<PathSegment> segments)
        {
            if (_pos >= _text.Length)
                throw Fail(_pos, "the path ends with a trailing '.'; a step name is required.");

            var c = _text[_pos];
            if (c == '(')
            {
                segments.Add(ParseTypeQualified());
                return;
            }

            if (c == '[')
                throw Fail(_pos, "an indexer cannot follow a '.'; remove the dot before the '['.");

            var start = _pos;
            while (_pos < _text.Length && IsIdentifierChar(_text[_pos]))
                _pos++;

            if (_pos == start)
                throw Fail(start, $"empty step; expected an identifier but found '{c}'.");

            segments.Add(PathSegment.Property(_text[start.._pos]));
        }

        private PathSegment ParseTypeQualified()
        {
            var open = _pos;
            _pos++; // consume '('

            // The type token may be namespace-qualified — (prefix:Type.Member) — so a ':' is part of the token, not a
            // cast marker. A source cast is (Type)rest with NO '.member' inside the parens; we detect that by hitting
            // ')' before any '.'. The prefix is resolved by the IPathTypeResolver (an xmlns-aware one from the XAML
            // loader; the code-first default resolves simple owner names only).
            var typeStart = _pos;
            while (_pos < _text.Length && _text[_pos] != '.' && _text[_pos] != ')')
                _pos++;

            if (_pos < _text.Length && _text[_pos] == ')')
                throw Fail(open, "source casts ((local:T)x) are unsupported by design; a type-qualified segment is '(Type.Member)'.");

            if (_pos >= _text.Length || _text[_pos] != '.')
                throw Fail(open, "malformed type-qualified segment; expected '(Type.Member)'.");

            var typeToken = _text[typeStart.._pos];
            if (typeToken.Length == 0)
                throw Fail(open, "empty type token in type-qualified segment.");

            _pos++; // consume '.'
            var memberStart = _pos;
            while (_pos < _text.Length && _text[_pos] != ')')
                _pos++;

            if (_pos >= _text.Length)
                throw Fail(open, "unterminated type-qualified segment; missing ')'.");

            var member = _text[memberStart.._pos];
            if (member.Length == 0)
                throw Fail(memberStart, "empty member in type-qualified segment.");

            _pos++; // consume ')'

            var ownerType = _resolver.Resolve(typeToken)
                            ?? throw Fail(typeStart, $"the type token '{typeToken}' could not be resolved.");

            // A registered UIProperty on the owner (attached OR regular) if one exists; else null and the member
            // resolves as a plain CLR property at runtime (the disambiguation/clarity case — not necessarily attached).
            var property = UIPropertyRegistry.Find(ownerType, member);
            return PathSegment.TypeQualified(ownerType, member, property);
        }

        private PathSegment ParseIndexer()
        {
            var open = _pos;
            _pos++; // consume '['

            if (_pos >= _text.Length)
                throw Fail(open, "unterminated indexer; missing ']'.");

            // Single-quoted string indexer.
            if (_text[_pos] == '\'')
            {
                _pos++;
                var qStart = _pos;
                while (_pos < _text.Length && _text[_pos] != '\'')
                    _pos++;
                if (_pos >= _text.Length)
                    throw Fail(open, "unterminated quoted string indexer.");
                var key = _text[qStart.._pos];
                _pos++; // consume closing quote
                ExpectIndexerClose(open);
                return PathSegment.StringIndexer(key);
            }

            var start = _pos;
            while (_pos < _text.Length && _text[_pos] != ']' && _text[_pos] != ',')
                _pos++;

            if (_pos < _text.Length && _text[_pos] == ',')
                throw Fail(_pos, "multi-argument indexers are unsupported by design.");

            if (_pos >= _text.Length)
                throw Fail(open, "unterminated indexer; missing ']'.");

            var inner = _text[start.._pos].Trim();
            _pos++; // consume ']'

            if (inner.Length == 0)
                throw Fail(open, "empty indexer argument.");

            return int.TryParse(inner, out var index)
                ? PathSegment.IntIndexer(index)
                : PathSegment.StringIndexer(inner);
        }

        private void ExpectIndexerClose(int open)
        {
            if (_pos < _text.Length && _text[_pos] == ',')
                throw Fail(_pos, "multi-argument indexers are unsupported by design.");
            if (_pos >= _text.Length || _text[_pos] != ']')
                throw Fail(open, "unterminated indexer; missing ']'.");
            _pos++; // consume ']'
        }

        private readonly FormatException Fail(int position, string message)
            => new($"Invalid binding path at position {position}: {message} (path: '{_text}')");

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
