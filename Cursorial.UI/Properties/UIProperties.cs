// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The public, read-only view over the property registry — the enumeration surface inspectors
/// and designers need (which the registry itself, being an internal engine concern, does not
/// expose). Answers "what properties apply to this type?" and "what attached properties does
/// this owner declare?" without reflection over <c>*Property</c> field conventions.
/// </summary>
/// <remarks>
/// All results are snapshots taken under the registry lock; registration order is preserved.
/// Enumeration composes with the per-instance surface: <c>ForType(element.GetType())</c> ×
/// <see cref="UIObject.GetValueSource"/> distinguishes local, styled, inherited, and default
/// contributions — including inherited values that have no local store entry.
/// </remarks>
public static class UIProperties
{
    /// <summary>Every registered property, in registration order.</summary>
    public static IReadOnlyList<UIProperty> All => UIPropertyRegistry.Snapshot();

    /// <summary>
    /// The non-attached properties applicable to <paramref name="type"/>: everything registered
    /// (declared or via <c>AddOwner</c>) on the type or any of its base types.
    /// </summary>
    public static IReadOnlyList<UIProperty> ForType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return UIPropertyRegistry.PropertiesForType(type);
    }

    /// <summary>The attached properties declared by <paramref name="ownerType"/> (which may be a static class).</summary>
    public static IReadOnlyList<UIProperty> AttachedBy(Type ownerType)
    {
        ArgumentNullException.ThrowIfNull(ownerType);
        return UIPropertyRegistry.AttachedByOwner(ownerType);
    }

    /// <summary>
    /// Every registered inheriting property — the set that MIGHT carry an inherited value on any
    /// element (retrieving an inherited value is easy; knowing which properties to ask was not).
    /// Backed by the registry's cached inheriting set, the same one the reparent diff enumerates.
    /// Narrow per-instance with <see cref="UIObject.GetValueSource"/>.
    /// </summary>
    public static IReadOnlyList<UIProperty> Inheriting => UIPropertyRegistry.InheritingProperties;

    /// <summary>
    /// The property registered under <paramref name="name"/> for <paramref name="ownerType"/> or
    /// the nearest base type with a registration, or <see langword="null"/>.
    /// </summary>
    public static UIProperty? Find(Type ownerType, string name)
        => UIPropertyRegistry.Find(ownerType, name);
}
