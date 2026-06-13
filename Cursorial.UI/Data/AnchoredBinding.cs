namespace Cursorial.UI.Data;

/// <summary>
/// The anchor surface shared by both path-bearing lanes (reflection <see cref="Binding"/> and
/// <see cref="CompiledBinding{TSource,TValue}"/>). <c>TemplateBinding</c> deliberately does not
/// inherit it — its anchor is fixed (the templated parent). The three anchors are mutually
/// exclusive, validated at <c>CreateExpression</c> (BD13).
/// </summary>
public abstract class AnchoredBinding : BindingBase
{
    /// <summary>An explicit fixed source root; never re-resolved (BD3).</summary>
    public object? Source { get; init; }

    /// <summary>A named element source, resolved through <c>NameScope.FindEnclosing</c> (B1).</summary>
    public string? ElementName { get; init; }

    /// <summary>A relative anchor (Self / TemplatedParent / FindAncestor).</summary>
    public RelativeSource? RelativeSource { get; init; }

    /// <summary>
    /// Validates the mutual exclusivity of <see cref="Source"/> / <see cref="ElementName"/> /
    /// <see cref="RelativeSource"/> (BD13) — throws <see cref="InvalidOperationException"/> naming
    /// the conflict.
    /// </summary>
    private protected void ValidateAnchors()
    {
        var count = 0;
        if (Source is not null)
            count++;
        if (ElementName is not null)
            count++;
        if (RelativeSource is not null)
            count++;

        if (count > 1)
        {
            throw new InvalidOperationException(
                "A binding's Source, ElementName, and RelativeSource are mutually exclusive (BD13); " +
                $"set at most one (Source={(Source is not null)}, ElementName={(ElementName is not null)}, " +
                $"RelativeSource={(RelativeSource is not null)}).");
        }
    }
}
