using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private PathSegment(PathSegmentKind kind, string? name, int intIndex, Type? qualifierType,
                        ResolvedProperty qualifiedProperty = default)
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

    /// <summary>The resolved owner of a type-qualified segment (the <c>Owner</c> in <c>(Owner.Member)</c>).</summary>
    public Type? QualifierType { get; }

    /// <summary>
    /// The <c>UIProperty</c> or <c>PropertyInfo</c> registered on, declared by, or inherited by
    /// <see cref="QualifierType"/> under <see cref="Name"/> (attached OR regular), when one exists;
    /// <see cref="ResolvedProperty.Unresolved"/> when the segment is not type-qualified, or when the
    /// member was not statically found and resolves by name on the runtime source.</summary>
    public ResolvedProperty QualifiedProperty { get; }

    public static PathSegment Property(string name) => new(PathSegmentKind.Property, name, 0, null);

    public static PathSegment IntIndexer(int index) => new(PathSegmentKind.IntIndexer, null, index, null);

    public static PathSegment StringIndexer(string key) => new(PathSegmentKind.StringIndexer, key, 0, null);

    public static PathSegment TypeQualified(Type owner, string member, ResolvedProperty property)
        => new(PathSegmentKind.TypeQualified, member, 0, owner, property);

    public static PathSegment TypeQualified(Type owner, string member, UIProperty property)
        => new(PathSegmentKind.TypeQualified, member, 0, owner, property);

    public static PathSegment TypeQualified(Type owner, string member, PropertyInfo property)
        => new(PathSegmentKind.TypeQualified, member, 0, owner, property);
}

[StructLayout(LayoutKind.Explicit)]
internal readonly struct ResolvedProperty : IEquatable<ResolvedProperty>
{
    public static readonly ResolvedProperty Unresolved;

    [FieldOffset(0)]
    private readonly object? _property;

    private ResolvedProperty(UIProperty property)
    {
        _property = property;
    }

    private ResolvedProperty(PropertyInfo property)
    {
        _property = property;
    }

    public string? Name
        => UIProperty?.Name ??
           ClrProperty?.Name;

    public Type? OwnerType
        => UIProperty?.OwnerType ??
           ClrProperty?.ReflectedType ??
           ClrProperty?.DeclaringType;

    public Type? PropertyType
        => UIProperty?.PropertyType ??
           ClrProperty?.PropertyType;

    [MemberNotNullWhen(true, nameof(Name))]
    [MemberNotNullWhen(true, nameof(OwnerType))]
    [MemberNotNullWhen(true, nameof(PropertyType))]
    public bool IsResolved => this != Unresolved;

    public bool IsUIProperty => _property is UIProperty;

    public bool IsClrProperty => _property is PropertyInfo;

    public UIProperty? UIProperty => _property as UIProperty;

    public PropertyInfo? ClrProperty => _property as PropertyInfo;

    public static implicit operator ResolvedProperty(UIProperty property) => new(property);

    public static implicit operator ResolvedProperty(PropertyInfo property) => new(property);

    public bool Equals(ResolvedProperty other) => Equals(_property, other._property);

    public override bool Equals(object? obj) => obj is ResolvedProperty other && Equals(other);

    public override int GetHashCode() => _property?.GetHashCode() ?? 0;

    public static bool operator ==(ResolvedProperty left, ResolvedProperty right) => left.Equals(right);

    public static bool operator !=(ResolvedProperty left, ResolvedProperty right) => !left.Equals(right);
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
    internal static BindingPath FromProperties(Span<ResolvedProperty> properties)
    {
        if (properties.Length == 0)
            return Empty;

        var segments = new PathSegment[properties.Length];

        for (var i = 0; i < properties.Length; i++)
        {
            if (properties[i] is not { IsResolved: true } property)
                throw new ArgumentException("A binding-path property step may not be null.", nameof(properties));

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

    internal ref struct Parser(string text, IPathTypeResolver resolver)
    {
        private const BindingFlags ClrPropertyFlags = BindingFlags.Public |
                                                      BindingFlags.NonPublic |
                                                      BindingFlags.Instance;

        // ReSharper disable ReplaceWithPrimaryConstructorParameter
        private readonly string _text = text;
        private readonly IPathTypeResolver _resolver = resolver;
        // ReSharper restore ReplaceWithPrimaryConstructorParameter

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

            if (_resolver.Resolve(typeToken) is not {} ownerType)
                throw Fail(typeStart, $"the type token '{typeToken}' could not be resolved.");

            // A type registers its UIProperties in its static ctor — a COLD qualified parse (an owner
            // nothing else ever touched: a pure attached-behavior class handed in by a resolver) would
            // miss the registration below and silently degrade to by-name runtime resolution, making
            // the binding load-order-dependent. Force the chain first, exactly as
            // StyledProperty.ResolveMetadata does for metadata overrides; same-thread re-entrant
            // RunClassConstructor is a CLR no-op. (The compiled lane is immune by construction — its
            // baked static field reference runs the cctor on first touch.)
            ForceRegistrations(ownerType);

            // A registered UIProperty on the owner (attached OR regular) if one exists; else a CLR property found
            // statically on the owner. Only the OWNER type must resolve at parse time: when neither lookup finds
            // the member, the segment stays unresolved here and the member resolves by name on the runtime source
            // (the disambiguation/clarity case — not necessarily attached; e.g. a member declared on a base
            // interface, or one that exists only on runtime subtypes).
            if (UIPropertyRegistry.Find(ownerType, member) is {} uip)
                return PathSegment.TypeQualified(ownerType, member, uip);

            if (FindClrProperty(ownerType, member) is {} cp)
                return PathSegment.TypeQualified(ownerType, member, cp);

            return PathSegment.TypeQualified(ownerType, member, ResolvedProperty.Unresolved);
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = "The reflective binding lane resolves members on live DataContext instances by RUNTIME type. A " +
                        "fully-lowered app binds through CompiledBinding and never enters it; where it does run, a " +
                        "member the trimmer removed resolves as an unresolvable/no-op segment (traced), not a crash.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2059",
            Justification = "ownerType is a live type a path resolver handed back: a type that exists in the " +
                            "running app keeps its static constructor — the trimmer removes cctors only " +
                            "together with their types.")]
        private static void ForceRegistrations(Type ownerType)
        {
            for (var t = ownerType; t is not null && t != typeof(object); t = t.BaseType)
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle);
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "String-path binding resolves members on a runtime DataContext type; a trimmed member resolves as an unresolvable segment (traced), not a crash — the reflective-lane degrade contract.")]
        private static PropertyInfo? FindClrProperty(Type ownerType, string member)
        {
            try
            {
                return ownerType.GetProperty(member, ClrPropertyFlags);
            }
            catch (AmbiguousMatchException)
            {
                // A shadowed ('new') property is ambiguous under these flags; treat it as a static miss so the
                // member resolves by name on the runtime source instead of escaping as a raw reflection exception.
                return null;
            }
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
