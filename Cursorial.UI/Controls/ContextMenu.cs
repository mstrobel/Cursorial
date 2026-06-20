using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A popup-rooted vertical context menu (design doc §12.7) — an <see cref="ItemsControl"/> of
/// <see cref="MenuItem"/>s shown on a light-dismiss <see cref="Popup"/> at the pointer (right-click) or
/// below an element (the keyboard <see cref="Key.Menu"/> context key). Attach one to any element via the
/// <see cref="MenuProperty"/> attached property and the input router opens it (the "router default", §12.7);
/// or drive it directly with <see cref="Open"/>/<see cref="Close"/>. The menu hosts itself as the popup's
/// <c>Child</c>, so its items realize in the popup surface and inherit DataContext/resources through the
/// placement target. A leaf invoke dismisses the menu (via <see cref="MenuItem"/>'s chain close); an outside
/// click or <c>Escape</c> light-dismisses it.
/// </summary>
public sealed class ContextMenu : ItemsControl
{
    /// <summary>The context menu to open on a right-click / <see cref="Key.Menu"/> over the element (router default).</summary>
    public static readonly AttachedProperty<ContextMenu?> MenuProperty =
        UIProperty.RegisterAttached<ContextMenu, UIElement, ContextMenu?>("Menu");

    /// <summary>Gets <see cref="MenuProperty"/> — the context menu attached to <paramref name="element"/>.</summary>
    public static ContextMenu? GetMenu(UIElement element) => element.GetValue(MenuProperty);

    /// <summary>Sets <see cref="MenuProperty"/>.</summary>
    public static void SetMenu(UIElement element, ContextMenu? value) => element.SetValue(MenuProperty, value);

    private static readonly UIPropertyKey<bool> IsOpenPropertyKey =
        UIProperty.RegisterReadOnly<ContextMenu, bool>(nameof(IsOpen));

    /// <summary>Whether the menu is currently shown — read-only; drive it with <see cref="Open"/>/<see cref="Close"/>.</summary>
    public static readonly StyledProperty<bool> IsOpenProperty = IsOpenPropertyKey.Property;

    private Popup? _popup;
    private UIElement? _watchedTarget; // the owner whose tree-detach must close this menu (no stranded surface)

    /// <summary>Creates a context menu (the host is not a focus stop; its items are).</summary>
    public ContextMenu() => Focusable = false;

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new MenuItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is MenuItem or Separator;

    /// <inheritdoc cref="IsOpenProperty"/>
    public bool IsOpen => GetValue(IsOpenProperty);

    /// <inheritdoc/>
    protected internal override bool HandlesScrolling => true;

    /// <summary>
    /// Opens the menu. When <paramref name="position"/> is <see langword="null"/> it lands at the pointer
    /// (the right-click case); otherwise it drops below <paramref name="target"/> offset by the (column, row)
    /// of <paramref name="position"/> (the keyboard / programmatic case). Re-opening relocates an open menu.
    /// </summary>
    public void Open(UIElement target, CellPosition? position = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        _popup ??= CreatePopup();

        // Re-opening must relocate: a Popup re-places its surface only on the closed→open edge (OpenCore →
        // PlacePopup), so setting IsOpen=true on an already-open popup is a no-op and would strand it at the
        // old position. Close first, then re-open at the new placement.
        if (_popup.IsOpen)
            _popup.SetCurrentValue(Popup.IsOpenProperty, false);

        _popup.PlacementTarget = target;

        if (position is { } p)
        {
            _popup.Placement = PlacementMode.Bottom; // keyboard / explicit: drop below the element
            _popup.SetCurrentValue(Popup.HorizontalOffsetProperty, p.Column);
            _popup.SetCurrentValue(Popup.VerticalOffsetProperty, p.Row);
        }
        else
        {
            _popup.Placement = PlacementMode.Pointer; // right-click: land at the cursor cell
            _popup.SetCurrentValue(Popup.HorizontalOffsetProperty, 0);
            _popup.SetCurrentValue(Popup.VerticalOffsetProperty, 0);
        }

        _popup.SetCurrentValue(Popup.IsOpenProperty, true);
        SetValue(IsOpenPropertyKey, true);
        WatchTarget(target);            // close the menu if its owner leaves the tree (no stranded popup surface)
        FocusFirstItem();               // keyboard nav can begin immediately on the realized items
    }

    /// <summary>Closes the menu (a no-op when already closed).</summary>
    public void Close() => _popup?.SetCurrentValue(Popup.IsOpenProperty, false); // OnPopupClosed flips IsOpen

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // Defensive backstop: the menu is attached only to its own WM-owned popup surface (never the host's
        // visual tree), so this fires when that surface tears down — Close() is then idempotent. The owner-detach
        // leak (the host element leaving the tree while the menu is open) is handled by the target watch below,
        // since the owner's detach does NOT reach this surface-rooted element.
        Close();
        base.OnDetachedFromTree(in e);
    }

    // The owner (placement target) lives in the host tree; if it detaches while the menu is open, nothing else
    // would close the separate popup surface. Watch its logical-tree detach and close on it.
    private void WatchTarget(UIElement target)
    {
        if (ReferenceEquals(_watchedTarget, target))
            return;

        UnwatchTarget();
        _watchedTarget = target;
        target.DetachedFromLogicalTree += OnTargetDetached;
    }

    private void UnwatchTarget()
    {
        if (_watchedTarget is { } target)
            target.DetachedFromLogicalTree -= OnTargetDetached;
        _watchedTarget = null;
    }

    private void OnTargetDetached(object? sender, LogicalTreeAttachmentEventArgs e) => Close();

    private Popup CreatePopup()
    {
        var popup = new Popup { Child = this, StaysOpen = false }; // StaysOpen=false ⇒ light-dismiss participant
        popup.SetValue(MenuItem.IsWithinMenuPropertyKey, true);
        popup.Closed += OnPopupClosed;
        return popup;
    }

    // Light-dismiss / Escape / programmatic close all route through the Popup's Closed event → sync IsOpen and
    // drop the owner-detach watch (every close path funnels here).
    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        SetValue(IsOpenPropertyKey, false);
        UnwatchTarget();
    }

    private void FocusFirstItem()
    {
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Focusable: true } first)
            {
                first.Focus(FocusNavigationMethod.Directional);
                return;
            }
    }
}
