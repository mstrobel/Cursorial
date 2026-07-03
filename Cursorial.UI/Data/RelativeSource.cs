namespace Cursorial.UI.Data;

/// <summary>The anchoring mode for a <see cref="RelativeSource"/> (design doc §6.1).</summary>
public enum RelativeSourceMode : byte
{
    /// <summary>The target element itself.</summary>
    Self,

    /// <summary>The target element's <c>TemplatedParent</c>.</summary>
    TemplatedParent,

    /// <summary>The nth logical ancestor assignable to <see cref="RelativeSource.AncestorType"/>.</summary>
    FindAncestor
}

/// <summary>
/// A relative anchor for an <see cref="AnchoredBinding"/> — Self, TemplatedParent, or
/// FindAncestor(type, level). Logical tree only in v1 (design doc §6.1/§6.14;
/// <c>FindVisualAncestor</c> is deferred). Construction-immutable and instance-shareable.
/// </summary>
public sealed class RelativeSource
{
    private static readonly RelativeSource SelfInstance = new() { Mode = RelativeSourceMode.Self };
    private static readonly RelativeSource TemplatedParentInstance = new() { Mode = RelativeSourceMode.TemplatedParent };

    /// <summary>The anchor relative to the target element itself.</summary>
    public static RelativeSource Self => SelfInstance;

    /// <summary>The anchor relative to the target element's <c>TemplatedParent</c>.</summary>
    public static RelativeSource TemplatedParent => TemplatedParentInstance;

    /// <summary>The anchoring mode.</summary>
    public RelativeSourceMode Mode { get; init; }

    /// <summary>The ancestor type for <see cref="RelativeSourceMode.FindAncestor"/> (else <see langword="null"/>).</summary>
    public Type? AncestorType { get; init; }

    /// <summary>
    /// The 1-based count of assignable ancestors to skip for <see cref="RelativeSourceMode.FindAncestor"/>
    /// (level counts matches, not hops — matrix B51). Default 1 (the nearest match).
    /// </summary>
    public int AncestorLevel { get; init; } = 1;

    /// <summary>
    /// A <see cref="RelativeSourceMode.FindAncestor"/> anchor matching the nth (<paramref name="level"/>)
    /// logical ancestor assignable to <typeparamref name="T"/>.
    /// </summary>
    public static RelativeSource Ancestor<T>(int level = 1) where T : UIElement
    {
        if (level < 1)
            throw new ArgumentOutOfRangeException(nameof(level), level, "Ancestor level must be at least 1.");
        return new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(T), AncestorLevel = level };
    }

    /// <inheritdoc/>
    public override string ToString() => Mode switch
    {
        RelativeSourceMode.FindAncestor => $"FindAncestor({AncestorType?.Name}, {AncestorLevel})",
        _ => Mode.ToString()
    };
}
