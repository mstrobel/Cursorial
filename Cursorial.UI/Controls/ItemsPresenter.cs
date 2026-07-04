using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// Hosts an <see cref="ItemsControl"/>'s generated containers (design doc §12.6). It is the <c>PART_ItemsHost</c>
/// of the items-control template, reachable as <see cref="ItemsControl.ItemsHost"/>. It builds the control's
/// <see cref="ItemsControl.ItemsPanel"/> (default a vertical <see cref="StackPanel"/>), marks it
/// <see cref="Panel.IsItemsHost"/> so the panel adopts the containers <b>visually only</b> (they stay logical
/// children of the <see cref="ItemsControl"/> — punch 43), and keeps the panel's <see cref="Panel.Children"/> in
/// sync with the generator. Layout delegates to the panel.
/// </summary>
public sealed class ItemsPresenter : UIElement, ILogicalScrollHost
{
    private ItemContainerGenerator? _generator;
    private Panel? _panel;
    private ScrollContentPresenter? _scrollOwner;

    // The panel owns container adoption (the presenter must NOT run its index-aligned Children.Insert/Remove/Move)
    // when EITHER the generator is virtualizing (a VirtualizingStackPanel drives its Children off the materialization
    // channel) OR the items panel is an IItemsHostPanel that claims unconditional ownership (the bars Toolbar's
    // overflow panel, which splits containers across a row band + a popup band — index-aligned sync would fight it).
    private bool PanelOwnsAdoption
        => _generator?.IsVirtualizing == true || _panel is IItemsHostPanel { ManagesContainerAdoption: true };

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        if (FindOwner() is not {} owner)
            return;

        _generator = owner.ItemContainerGenerator;

        EnsurePanel(owner);
        _generator.ContainersChanged += OnContainersChanged;

        SyncAll(); // adopt whatever is already realized (the generator may have run before this part existed)
    }

    // The owning ItemsControl: normally this presenter's TemplatedParent, but a content host (ScrollViewer's
    // ScrollContentPresenter, or a MenuItem's submenu Popup) clears TemplatedParent when it adopts the presenter as
    // its content — so fall back to walking the UIParent bridge (LogicalParent ?? TemplatedParent; a Popup overrides
    // it to PlacementTarget). That chain crosses a popup surface boundary back to the owning control (the menu's
    // submenu ItemsPresenter resolves to its MenuItem), where a visual-tree walk would dead-end at the popup root.
    private ItemsControl? FindOwner()
    {
        if (TemplatedParent is ItemsControl templatedOwner)
            return templatedOwner;

        for (var node = UIParent; node is not null; node = node.UIParent)
        {
            if (node is ItemsControl owner)
                return owner;
        }

        return null;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_generator is not null)
        {
            _generator.ContainersChanged -= OnContainersChanged;
            _generator = null;
        }

        // Free the containers (logical parentage stays the ItemsControl's) so a re-attach / re-template re-adopts.
        if (_panel is not null)
        {
            (_panel as IItemsHostPanel)?.OnItemsHostDisconnected();
            _panel.Children.Clear();
            RemoveVisualChild(_panel);
            _panel = null;
        }

        base.OnDetachedFromTree(in e);
    }

    /// <summary>Rebuilds the items panel after <see cref="ItemsControl.ItemsPanel"/> changes (v1: full re-host).</summary>
    internal void RebuildPanel()
    {
        if (!IsAttachedToTree || FindOwner() is not {} owner)
            return;

        if (_panel is not null)
        {
            (_panel as IItemsHostPanel)?.OnItemsHostDisconnected();
            if (_panel is ILogicalScrollHost oldHost)
                oldHost.ScrollOwner = null; // symmetric with the SCP's Content-setter disown — no stale back-channel
            _panel.Children.Clear();
            RemoveVisualChild(_panel);
            _panel = null;
        }

        EnsurePanel(owner); // wires the new panel's ScrollOwner from the retained _scrollOwner
        SyncAll();

        // Pulse the SCP so an ItemsPanel swap to a virtualizing host (ILogicalScrollHost) re-engages the delegation
        // without a content-identity change (VV1.8) — the new panel's back-channel was just established by EnsurePanel.
        _scrollOwner?.InvalidateScrollExtent();

        InvalidateMeasure();
    }

    private void EnsurePanel(ItemsControl owner)
    {
        if (_panel is not null)
            return;

        var template = owner.ItemsPanel ?? new ItemsPanelTemplate(_ => new StackPanel());
        _panel = template.Build(new TemplateBuildContext(owner, new NameScopeDictionary())); // throws if the root isn't a Panel

        _panel.IsItemsHost = true; // its Children adopt the containers visually only (logical parent = the ItemsControl)
        AddVisualChild(_panel);

        if (PanelHost is { } host)
            host.ScrollOwner = _scrollOwner;

        // An items-host panel wires its generator + opt-in here (a VirtualizingPanel owns realization off the
        // materialization channel; the bars overflow panel owns its row/popup distribution) — not this presenter.
        if (_panel is IItemsHostPanel itemsHost)
            itemsHost.OnItemsHostConnected(owner);
    }

    private void OnContainersChanged(object? sender, ContainersChangedEventArgs e)
    {
        if (_generator is null || _panel is null)
            return;

        // When the panel owns adoption the presenter no-ops the structural channel (PanelOwnsAdoption): a
        // VirtualizingStackPanel drives its Children off the materialization channel (ContainersRealizedChanged) —
        // the sparse store would make the index-aligned adoption below Remove(null) / Insert past the end — and the
        // bars overflow panel splits containers across two bands, which index-aligned sync would fight. Both maintain
        // their own ordered container list from the generator and reconcile their bands themselves.
        if (PanelOwnsAdoption)
            return;

        switch (e.Action)
        {
            case ContainersChangedAction.Realized:
                for (var i = 0; i < e.Count; i++)
                {
                    if (_generator.ContainerFromIndex(e.StartIndex + i) is {} container)
                        _panel.Children.Insert(e.StartIndex + i, container);
                }

                break;

            case ContainersChangedAction.Unrealized:
                // The generator has already dropped the range from its index list, so remove the carried instances
                // directly (the visual detach IS the store-retraction trigger and still precedes the logical removal,
                // which the generator runs after this event — CD-P9-3).
                if (e.RemovedContainers is {} removed)
                {
                    foreach (var container in removed)
                        _panel.Children.Remove(container);
                }

                break;

            case ContainersChangedAction.Moved:
            {
                // Mirror the generator's block move exactly: lift the moved containers OUT of the panel, then
                // re-insert them contiguously at the new index. A per-slot in-place Move() is wrong for a forward
                // multi-item move — UIElementCollection.Move is remove-then-insert, so placing the 2nd block element
                // past the already-placed 1st shifts the 1st back. (After removal the non-moved children keep their
                // relative order — identical to the generator — so Insert(StartIndex + i) reproduces its order.)
                var block = new UIElement[e.Count];
                for (var i = 0; i < e.Count; i++)
                    block[i] = _generator.ContainerFromIndex(e.StartIndex + i)!;

                foreach (var container in block)
                    _panel.Children.Remove(container);
                for (var i = 0; i < block.Length; i++)
                    _panel.Children.Insert(e.StartIndex + i, block[i]);

                break;
            }

            default:
            case ContainersChangedAction.Reset:
                _panel.Children.Clear();
                SyncAll();
                break;
        }
    }

    // Reconcile the panel's children to the generator's containers (attach / Reset). Idempotent.
    private void SyncAll()
    {
        if (_generator is null || _panel is null)
            return;

        if (PanelOwnsAdoption)
            return; // the panel owns adoption (virtualizing sparse store, or the bars overflow split) — see OnContainersChanged

        for (var i = 0; i < _generator.ContainerCount; i++)
        {
            if (_generator.ContainerFromIndex(i) is not {} container)
                continue;

            var current = _panel.Children.IndexOf(container);

            if (current < 0)
                _panel.Children.Insert(i, container);
            else if (current != i)
                _panel.Children.Move(current, i);
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_panel is null && FindOwner() is {} owner)
            EnsurePanel(owner);

        if (_panel is null)
            return new Size(0, 0);

        _panel.Measure(availableSize);
        return _panel.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _panel?.Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));
        return finalSize;
    }

    // ───────────────────────────── IScrollContentHost / ILogicalScrollHost forwarding (VV1.8) ─────────────────────────────
    //
    // The SCP discovers the ItemsPresenter as its content's scroll host; the presenter forwards every member to its
    // panel WHEN that panel is a virtualizing host (ILogicalScrollHost — the V2 VirtualizingStackPanel). A plain
    // StackPanel is not a host, so PanelHost is null and IsScrollClient is false ⇒ the SCP runs its legacy path
    // (the OFF-path stays byte-identical for every existing list). _scrollOwner is retained so RebuildPanel can
    // re-establish the back-channel on a panel swap.

    internal ILogicalScrollHost? PanelHost => _panel as ILogicalScrollHost;

    bool IScrollContentHost.IsScrollClient => PanelHost?.IsScrollClient ?? false;

    bool IScrollContentHost.IsLogicalScroll => PanelHost?.IsLogicalScroll ?? false;

    ScrollContentPresenter? IScrollContentHost.ScrollOwner
    {
        get => _scrollOwner;
        set
        {
            _scrollOwner = value;

            if (PanelHost is {} host)
                host.ScrollOwner = value;
        }
    }

    bool IScrollContentHost.CanScrollHorizontally
    {
        get => PanelHost?.CanScrollHorizontally ?? false;
        set { if (PanelHost is { } host) host.CanScrollHorizontally = value; }
    }

    bool IScrollContentHost.CanScrollVertically
    {
        get => PanelHost?.CanScrollVertically ?? false;
        set { if (PanelHost is { } host) host.CanScrollVertically = value; }
    }

    Size IScrollContentHost.GetExtent() => PanelHost?.GetExtent() ?? Size.Empty;

    void IScrollContentHost.SetViewport(Size viewport) => PanelHost?.SetViewport(viewport);

    void IScrollContentHost.InvalidateRealization() => PanelHost?.InvalidateRealization();

    int IScrollContentHost.LineStep(int currentOffset, int sign, bool vertical)
        => PanelHost?.LineStep(currentOffset, sign, vertical) ?? 1;

    int IScrollContentHost.PageStep(int currentOffset, int sign, bool vertical)
        => PanelHost?.PageStep(currentOffset, sign, vertical) ?? 0;

    Rect ILogicalScrollHost.BringItemIntoView(int itemIndex) => PanelHost?.BringItemIntoView(itemIndex) ?? default;

    int ILogicalScrollHost.ItemCount => PanelHost?.ItemCount ?? 0;

    int ILogicalScrollHost.EstimateItemAt(int offsetRow) => PanelHost?.EstimateItemAt(offsetRow) ?? 0;
}