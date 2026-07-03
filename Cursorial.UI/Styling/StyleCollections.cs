using System.Collections.ObjectModel;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The ordered setter list of a <see cref="Style"/>; freezes with its owner — mutation after
/// <see cref="Style.Seal"/> throws (WPF-style sealable, style matrix S42).
/// </summary>
public sealed class SetterCollection : Collection<Setter>
{
    private readonly Style _owner;

    internal SetterCollection(Style owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, Setter item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.InsertItem(index, item);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, Setter item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.SetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        _owner.ThrowIfSealed();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        _owner.ThrowIfSealed();
        base.ClearItems();
    }
}

/// <summary>
/// The nested-style list of a <see cref="Style"/> (design doc §3.1): every child's selector must
/// start with the <c>^</c> nesting anchor (validated at seal — SD17) and AND-composes with the
/// parent's selector into one flattened rule. Freezes with its owner.
/// </summary>
public sealed class StyleCollection : Collection<Style>
{
    private readonly Style _owner;

    internal StyleCollection(Style owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, Style item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.InsertItem(index, item);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, Style item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.SetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        _owner.ThrowIfSealed();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        _owner.ThrowIfSealed();
        base.ClearItems();
    }
}

/// <summary>
/// The data-condition conjunction of a <see cref="Style"/> (<see cref="Style.When"/>, design doc
/// §3.1): all conditions must hold for the style's rules to be active (there is no <c>Or</c> —
/// disjunction is two styles via <c>BasedOn</c> or a selector list). An empty collection means
/// "always" (the structural-and-pseudo verdict alone decides). Freezes with its owner — mutation
/// after <see cref="Style.Seal"/> throws (S42).
/// </summary>
public sealed class WhenCollection : Collection<DataCondition>
{
    private readonly Style _owner;

    internal WhenCollection(Style owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, DataCondition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.InsertItem(index, item);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, DataCondition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.SetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        _owner.ThrowIfSealed();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        _owner.ThrowIfSealed();
        base.ClearItems();
    }
}

/// <summary>
/// The edge-action list of a <see cref="Style"/> (<see cref="Style.Enter"/> /
/// <see cref="Style.Exit"/>, ledger B5). Actions run in document order on every activation /
/// retraction edge (SD16). Freezes with its owner.
/// </summary>
public sealed class EdgeActionCollection : Collection<IStyleEdgeAction>
{
    private readonly Style _owner;

    internal EdgeActionCollection(Style owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, IStyleEdgeAction item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.InsertItem(index, item);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, IStyleEdgeAction item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.ThrowIfSealed();
        base.SetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        _owner.ThrowIfSealed();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        _owner.ThrowIfSealed();
        base.ClearItems();
    }
}
