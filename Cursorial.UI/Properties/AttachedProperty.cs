// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A styled property settable on instances other than its declaring type — the
/// <c>Grid.Row</c> / <c>Tab.Index</c> shape. Storage is instance-keyed by dense id, so attached
/// values need nothing special at the store; what distinguishes the kind is the
/// <see cref="HostType"/> write validation (DEBUG-asserted at the store's mouth) and the reliance on
/// the <em>global</em> effects lane for invalidation (ledger A1 — a host type's per-type effects
/// table structurally never sees the declaring type's registration).
/// </summary>
public sealed class AttachedProperty<T> : StyledProperty<T>
{
    internal AttachedProperty(string name, Type ownerType, Type hostType, PropertyMetadata<T> metadata, bool inherits, bool isReadOnly, bool targetsChildren = false)
        : base(name, ownerType, metadata, inherits, isAttached: true, isReadOnly, targetsChildren)
    {
        ArgumentNullException.ThrowIfNull(hostType);
        HostType = hostType;
    }

    /// <summary>The kind of instance the property may be set on (DEBUG-asserted; no release check).</summary>
    public override Type HostType { get; }
}
