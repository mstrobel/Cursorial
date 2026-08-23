using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A parse-time placeholder for an <c>{x:Self}</c> self-reference token, carried as a typed constant (the
/// sibling of <see cref="XamlStaticReference"/>/<see cref="XamlTypeReference"/>). <c>{x:Self}</c> resolves at
/// BUILD time, per lane, to the object the value is being assigned onto — always the assignment TARGET, seeing
/// through any enclosing markup extensions (a <c>Source={x:Self}</c> inside a Binding is the binding's target
/// object, never the Binding). Construction-time and read-only: the object resolves as it exists at assignment
/// (later members unset), a stable identity — not reactive, not a live binding, and never able to see the
/// (attach-time, inherited) <c>DataContext</c>.
/// </summary>
public readonly struct XamlSelfReference : IEquatable<XamlSelfReference>
{
    /// <summary>Creates a self reference. <paramref name="level"/> 0 is the immediate target object; N walks
    /// the construction-object stack outward (Level &gt; 0 is reserved — not yet supported).</summary>
    public XamlSelfReference(int level)
        => Level = level >= 0 ? level : throw new ArgumentOutOfRangeException(nameof(level));

    /// <summary>How many construction-stack hops above the immediate target (0 = the target itself).</summary>
    public int Level { get; }

    /// <inheritdoc/>
    public bool Equals(XamlSelfReference other) => Level == other.Level;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XamlSelfReference other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Level;

    /// <inheritdoc/>
    public override string ToString() => Level == 0 ? "{x:Self}" : $"{{x:Self Level={Level}}}";
}
