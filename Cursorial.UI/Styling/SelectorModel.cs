using System.Text;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>The combinator joining two adjacent compounds in a selector branch (design doc §3.1).</summary>
internal enum SelectorCombinator : byte
{
    /// <summary>Whitespace — any ancestor on the styling-parent chain (<c>LogicalParent ?? VisualParent</c>, SD7).</summary>
    Descendant = 0,

    /// <summary><c>&gt;</c> — the immediate styling parent.</summary>
    Child = 1,

    /// <summary><c>/template/</c> — crosses exactly one <c>TemplatedParent</c> stamp edge (SD8).</summary>
    Template = 2
}

/// <summary>The kind of a simple selector inside a compound.</summary>
internal enum SimpleSelectorKind : byte
{
    /// <summary><c>.class</c> — counts toward the classLike specificity field.</summary>
    Class = 0,

    /// <summary><c>#name</c> — counts toward the names specificity field.</summary>
    Name = 1,

    /// <summary><c>:pseudo</c> — counts toward the classLike specificity field.</summary>
    PseudoClass = 2
}

/// <summary>One simple selector (<c>.class</c> / <c>#name</c> / <c>:pseudo</c>); pseudo values are stored without the leading colon.</summary>
internal readonly record struct SimpleSelector(SimpleSelectorKind Kind, string Value)
{
    /// <summary>Appends the canonical text (<c>.x</c> / <c>#x</c> / <c>:x</c>).</summary>
    internal void AppendTo(StringBuilder builder)
        => builder.Append(Kind switch
                          {
                              SimpleSelectorKind.Class => '.',
                              SimpleSelectorKind.Name  => '#',
                              _                        => ':'
                          })
                  .Append(Value);
}

/// <summary>
/// One compound selector: optional nesting anchor (<c>^</c>), optional type test (exact or
/// <c>:is()</c>-assignable), then simples in declaration order (SD4 — order preserved, never
/// sorted). Immutable after construction.
/// </summary>
internal sealed class SelectorCompound : IEquatable<SelectorCompound>
{
    /// <summary>Whether the compound carries the <c>^</c> nesting anchor (leftmost-only, enforced by the parser).</summary>
    internal readonly bool IsNesting;

    /// <summary>The resolved CLR type test, or <see langword="null"/> when the compound has no type token.</summary>
    internal readonly Type? Type;

    /// <summary>Whether the type test is <c>:is(T)</c> (T or derived) rather than a bare exact-type token.</summary>
    internal readonly bool IsAssignableType;

    /// <summary>The canonical type token text (the simple name as written / <c>Type.Name</c> for fluent builders).</summary>
    internal readonly string? TypeName;

    /// <summary>The simple selectors in declaration order.</summary>
    internal readonly SimpleSelector[] Simples;

    internal SelectorCompound(bool isNesting, Type? type, bool isAssignableType, string? typeName, SimpleSelector[] simples)
    {
        IsNesting = isNesting;
        Type = type;
        IsAssignableType = isAssignableType;
        TypeName = typeName;
        Simples = simples;
    }

    /// <summary>Appends the canonical compound text (SD4: <c>^</c>, type, simples in order).</summary>
    internal void AppendTo(StringBuilder builder)
    {
        if (IsNesting)
            builder.Append('^');

        if (TypeName is not null)
        {
            if (IsAssignableType)
                builder.Append(":is(").Append(TypeName).Append(')');
            else
                builder.Append(TypeName);
        }

        foreach (var simple in Simples)
            simple.AppendTo(builder);
    }

    /// <inheritdoc/>
    public bool Equals(SelectorCompound? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (IsNesting != other.IsNesting || Type != other.Type || IsAssignableType != other.IsAssignableType)
            return false;

        if (Type is null && !string.Equals(TypeName, other.TypeName, StringComparison.Ordinal))
            return false;

        return Simples.AsSpan().SequenceEqual(other.Simples);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SelectorCompound);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(IsNesting);
        hash.Add(Type);
        hash.Add(IsAssignableType);

        foreach (var simple in Simples)
            hash.Add(simple);

        return hash.ToHashCode();
    }
}

/// <summary>
/// One member of a selector list: compounds left-to-right with the combinators joining them
/// (<c>Combinators[i]</c> joins <c>Compounds[i]</c> → <c>Compounds[i + 1]</c>). Each branch of a
/// style's selector compiles to its own rule with its own specificity (doc §3.1).
/// </summary>
internal sealed class SelectorBranch : IEquatable<SelectorBranch>
{
    /// <summary>The compounds, left-to-right as written; the last compound is the subject.</summary>
    internal readonly SelectorCompound[] Compounds;

    /// <summary>The combinators between adjacent compounds (<c>Compounds.Length − 1</c> entries).</summary>
    internal readonly SelectorCombinator[] Combinators;

    internal SelectorBranch(SelectorCompound[] compounds, SelectorCombinator[] combinators)
    {
        Compounds = compounds;
        Combinators = combinators;
    }

    /// <summary>The subject (rightmost) compound.</summary>
    internal SelectorCompound Subject => Compounds[^1];

    /// <summary>Appends the canonical branch text (SD4: <c>" > "</c> / <c>" "</c> / <c>" /template/ "</c>).</summary>
    internal void AppendTo(StringBuilder builder)
    {
        for (var i = 0; i < Compounds.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(Combinators[i - 1] switch
                               {
                                   SelectorCombinator.Child    => " > ",
                                   SelectorCombinator.Template => " /template/ ",
                                   _                           => " "
                               });
            }

            Compounds[i].AppendTo(builder);
        }
    }

    /// <summary>The canonical branch text.</summary>
    internal string ToCanonicalString()
    {
        var builder = new StringBuilder();
        AppendTo(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Counts the branch's specificity components over <b>all</b> compounds (SD5 — both sides of
    /// <c>/template/</c>, nesting-composed parents included by construction since composition
    /// happens on the flattened branch).
    /// </summary>
    internal (int Names, int ClassLike, int Types) CountSpecificity()
    {
        int names = 0, classLike = 0, types = 0;

        foreach (var compound in Compounds)
        {
            if (compound.TypeName is not null)
                types++;

            foreach (var simple in compound.Simples)
            {
                if (simple.Kind == SimpleSelectorKind.Name)
                    names++;
                else
                    classLike++;
            }
        }

        return (names, classLike, types);
    }

    /// <inheritdoc/>
    public bool Equals(SelectorBranch? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (Compounds.Length != other.Compounds.Length || !Combinators.AsSpan().SequenceEqual(other.Combinators))
            return false;

        for (var i = 0; i < Compounds.Length; i++)
        {
            if (!Compounds[i].Equals(other.Compounds[i]))
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SelectorBranch);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var compound in Compounds)
            hash.Add(compound);

        foreach (var combinator in Combinators)
            hash.Add(combinator);

        return hash.ToHashCode();
    }
}