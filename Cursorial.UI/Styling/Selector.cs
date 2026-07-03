using System.Text;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A structural style selector over the deliberately small grammar of design doc §3.1: compounds of
/// type / <c>:is(type)</c> / <c>.class</c> / <c>#name</c> / <c>:pseudo</c> simples joined by the
/// descendant (whitespace), child (<c>&gt;</c>), and template (<c>/template/</c>) combinators, with
/// <c>^</c> as the leftmost-only nesting anchor and <c>,</c>-separated selector lists (each member
/// compiles to its own rule with its own specificity). <b>Explicitly absent by decision</b>
/// (§3.10): <c>:not()</c>, <c>:nth-*</c>/positional selectors, sibling combinators, attribute and
/// property-value selectors — <see cref="Parse"/> rejects them with errors stating they are
/// unsupported by design.
/// </summary>
/// <remarks>
/// Selectors are immutable; the fluent builders in <see cref="Selectors"/> return new instances.
/// <see cref="ToString"/> emits the canonical form (SD4) and round-trips through
/// <see cref="Parse"/>; equality is structural. There is no public matching API — the styling
/// engine owns matching.
/// </remarks>
public sealed class Selector : IEquatable<Selector>
{
    private readonly SelectorBranch[] _branches;
    private readonly SelectorCombinator? _pendingCombinator; // fluent-builder state: the next simple/type starts a new compound
    private string? _canonicalText;

    internal Selector(SelectorBranch[] branches, SelectorCombinator? pendingCombinator = null)
    {
        _branches = branches;
        _pendingCombinator = pendingCombinator;
    }

    /// <summary>The selector-list members (one branch per member; a single selector has one branch).</summary>
    internal SelectorBranch[] Branches => _branches;

    /// <summary>The fluent-builder pending combinator (set between <c>.Child()</c> and the next compound part).</summary>
    internal SelectorCombinator? PendingCombinator => _pendingCombinator;

    /// <summary>
    /// The framework default <see cref="ISelectorTypeResolver"/> (SD1: registry-known + exported element
    /// types, matched by simple name) — the resolver <see cref="Parse(string, ISelectorTypeResolver?)"/>
    /// uses when passed <see langword="null"/>. Exposed so a custom resolver can compose with it (e.g. the
    /// XAML loader's namespace-aware resolver delegates bare, unqualified type tokens here).
    /// </summary>
    public static ISelectorTypeResolver DefaultTypeResolver => DefaultSelectorTypeResolver.Instance;

    /// <summary>
    /// Parses selector text over the §3.1 grammar. Type tokens are simple (undotted) identifiers
    /// resolved through <paramref name="resolver"/>; <see langword="null"/> selects the framework
    /// default (registry-known + exported element types — SD1). All matching and lexing is ordinal
    /// case-sensitive (SD2); whitespace (spaces/tabs) is insignificant around combinators and
    /// <c>,</c>, and a whitespace run between compounds is the descendant combinator.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="SelectorParseException">
    /// Malformed text, an unresolvable or ambiguous type token, or a construct that is unsupported
    /// by design (§3.10) — the exception's <see cref="SelectorParseException.Position"/> points at
    /// the offending token.
    /// </exception>
    public static Selector Parse(string text, ISelectorTypeResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Selector(SelectorParser.Parse(text, resolver ?? DefaultSelectorTypeResolver.Instance));
    }

    /// <summary>
    /// The canonical selector text (SD4): compounds joined by <c>" &gt; "</c> / <c>" "</c> /
    /// <c>" /template/ "</c>; within a compound <c>^</c>, then the type, then simples in
    /// declaration order; list members joined by <c>", "</c>. <c>Parse(s).ToString()</c> is the
    /// canonical form of <c>s</c>, and <c>Parse(x.ToString())</c> is structurally equal to
    /// <c>x</c>.
    /// </summary>
    public override string ToString()
    {
        if (_canonicalText is not null)
            return _canonicalText;

        var builder = new StringBuilder();

        for (var i = 0; i < _branches.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");

            _branches[i].AppendTo(builder);
        }

        return _canonicalText = builder.ToString();
    }

    /// <summary>
    /// Structural equality over the branch list (combinators, compounds, simples in order).
    /// Builder-internal state does not participate: an in-progress fluent chain with an open
    /// combinator (<see cref="Selectors.Child"/> etc. without a following compound) compares equal
    /// to its completed-so-far form — complete selectors (parsed or fully built) never carry that
    /// state, and <see cref="Style"/> rejects dangling combinators at its boundary.
    /// </summary>
    public bool Equals(Selector? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (_branches.Length != other._branches.Length)
            return false;

        for (var i = 0; i < _branches.Length; i++)
        {
            if (!_branches[i].Equals(other._branches[i]))
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Selector);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var branch in _branches)
            hash.Add(branch);

        return hash.ToHashCode();
    }

    /// <summary>Whether every branch is rooted at the <c>^</c> nesting anchor (explicit-style / <c>Children</c> placement checks, SD17).</summary>
    internal bool IsNestingRooted
    {
        get
        {
            foreach (var branch in _branches)
            {
                if (!branch.Compounds[0].IsNesting)
                    return false;
            }

            return true;
        }
    }

    /// <summary>Whether any branch is rooted at the <c>^</c> nesting anchor (scoped-collection placement checks, SD17).</summary>
    internal bool HasNestingRoot
    {
        get
        {
            foreach (var branch in _branches)
            {
                if (branch.Compounds[0].IsNesting)
                    return true;
            }

            return false;
        }
    }
}