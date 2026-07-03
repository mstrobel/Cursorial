namespace Cursorial.UI.Controls;

/// <summary>How an <see cref="ItemContainerGenerator"/> manages container lifetime under virtualization
/// (the WPF <c>VirtualizationMode</c> analog).</summary>
public enum VirtualizationMode
{
    /// <summary>Unrealized containers are discarded; a fresh container is created per realize.</summary>
    Standard,

    /// <summary>Unrealized generated containers return to a pool and are re-prepared for new items (the GC win;
    /// the v1 default). Own-containers (the item itself is a <see cref="UIElement"/>) are never pooled.</summary>
    Recycling
}

/// <summary>The scrolling granularity of a virtualizing panel (the WPF <c>ScrollUnit</c> analog; Cursorial is
/// cell-native, so the physical unit is the terminal cell, not a pixel).</summary>
public enum ScrollUnit
{
    /// <summary>Smooth, cell-by-cell scrolling — the offset is the physical cell row/column.</summary>
    Cell,

    /// <summary>Whole-item (logical) scrolling — a line/page step lands on an item boundary. The offset is still
    /// cells (Cursorial has ONE offset coordinate); item mode only changes the step size + the extent estimate.</summary>
    Item
}

/// <summary>
/// The base class for panels that virtualize their children (design doc §12.6 — the visualization seam). It hosts
/// the WPF-style opt-in attached properties (<see cref="IsVirtualizingProperty"/>,
/// <see cref="VirtualizationModeProperty"/>, <see cref="ScrollUnitProperty"/>) set on an
/// <see cref="ItemsControl"/> and read by its items panel, so a list opts into virtualization without retemplating
/// (once its panel derives from this type). The realization mechanics live in <c>VirtualizingStackPanel</c>
/// (V2); this base only carries the policy surface.
/// </summary>
public abstract class VirtualizingPanel : Panel, IItemsHostPanel
{
    /// <summary>Whether the panel virtualizes its children (default <see langword="false"/> — opt-in; set on the
    /// <see cref="ItemsControl"/>). The default-on flip (WPF parity for <c>ListBox</c>) is a deferred post-soak change.</summary>
    public static readonly AttachedProperty<bool> IsVirtualizingProperty =
        UIProperty.RegisterAttached<VirtualizingPanel, ItemsControl, bool>("IsVirtualizing");

    /// <summary>The container-lifetime policy (default <see cref="VirtualizationMode.Recycling"/>).</summary>
    public static readonly AttachedProperty<VirtualizationMode> VirtualizationModeProperty =
        UIProperty.RegisterAttached<VirtualizingPanel, ItemsControl, VirtualizationMode>(
            "VirtualizationMode", defaultValue: VirtualizationMode.Recycling);

    /// <summary>The scrolling granularity (default <see cref="ScrollUnit.Item"/>).</summary>
    public static readonly AttachedProperty<ScrollUnit> ScrollUnitProperty =
        UIProperty.RegisterAttached<VirtualizingPanel, ItemsControl, ScrollUnit>(
            "ScrollUnit", defaultValue: ScrollUnit.Item);

    /// <summary>Reads <see cref="IsVirtualizingProperty"/> from <paramref name="control"/>.</summary>
    public static bool GetIsVirtualizing(ItemsControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(IsVirtualizingProperty);
    }

    /// <summary>Sets <see cref="IsVirtualizingProperty"/> on <paramref name="control"/>.</summary>
    public static void SetIsVirtualizing(ItemsControl control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(IsVirtualizingProperty, value);
    }

    /// <summary>Reads <see cref="VirtualizationModeProperty"/> from <paramref name="control"/>.</summary>
    public static VirtualizationMode GetVirtualizationMode(ItemsControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(VirtualizationModeProperty);
    }

    /// <summary>Sets <see cref="VirtualizationModeProperty"/> on <paramref name="control"/>.</summary>
    public static void SetVirtualizationMode(ItemsControl control, VirtualizationMode value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(VirtualizationModeProperty, value);
    }

    /// <summary>Reads <see cref="ScrollUnitProperty"/> from <paramref name="control"/>.</summary>
    public static ScrollUnit GetScrollUnit(ItemsControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(ScrollUnitProperty);
    }

    /// <summary>Called by the <see cref="ItemsPresenter"/> when this panel becomes the items host (it has its owning
    /// <see cref="ItemsControl"/> + generator). A virtualizing subclass wires its generator + opt-in here. The base
    /// does nothing.</summary>
    internal virtual void OnItemsHostConnected(ItemsControl owner) { }

    /// <summary>Called by the <see cref="ItemsPresenter"/> before this panel is released (detach / <c>ItemsPanel</c>
    /// swap) so a virtualizing subclass can unhook its generator subscription. The base does nothing.</summary>
    internal virtual void OnItemsHostDisconnected() { }

    // IItemsHostPanel — explicit so the public surface stays clean and VirtualizingStackPanel's internal overrides are
    // untouched. A virtualizing panel owns adoption ONLY while the generator is virtualizing, which the presenter
    // checks separately via ItemContainerGenerator.IsVirtualizing — so ManagesContainerAdoption is false here (a
    // NON-virtualizing virtualizing panel must keep the eager presenter path). The lifecycle hooks forward to the
    // existing internal virtuals.
    bool IItemsHostPanel.ManagesContainerAdoption => false;
    void IItemsHostPanel.OnItemsHostConnected(ItemsControl owner) => OnItemsHostConnected(owner);
    void IItemsHostPanel.OnItemsHostDisconnected() => OnItemsHostDisconnected();

    /// <summary>Sets <see cref="ScrollUnitProperty"/> on <paramref name="control"/>.</summary>
    public static void SetScrollUnit(ItemsControl control, ScrollUnit value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(ScrollUnitProperty, value);
    }
}
