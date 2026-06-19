using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A node in a <see cref="TreeView"/> (design doc §12.6 — the headered-items recipe). A <see cref="TreeViewItem"/>
/// is both a <b>row</b> (its <see cref="HeaderedItemsControl.Header"/>) and a <b>sub-items host</b> (its
/// <see cref="ItemsControl.Items"/>). It owns <see cref="IsExpanded"/> (a <c>PART_ItemsHost</c> visibility gate +
/// the <c>&gt;</c>/<c>v</c> twisty glyph; <c>:expanded</c>) and <see cref="IsSelected"/> (<c>:selected</c>) — but
/// selection is tree-wide and single, so an <see cref="IsSelected"/> change folds into the owning
/// <see cref="TreeView"/> (which deselects the prior node). A press on the twisty toggles expansion without
/// selecting; any other press selects. Keyboard (only the focused node): Right expands-then-descends, Left
/// collapses-then-ascends, Up/Down walk the <b>visible</b> tree (collapsed subtrees skipped); every directional
/// move selects the newly-focused node. Containers are <see cref="TreeViewItem"/>s; the data-driven
/// <c>HierarchicalDataTemplate</c> hierarchy is a v2 deferral.
/// </summary>
[TemplatePart(PartTwisty, typeof(TextBlock))]
[TemplatePart(PartItemsHost, typeof(ItemsPresenter))]
public class TreeViewItem : HeaderedItemsControl
{
    private const string PartTwisty = "PART_Twisty";
    private const string PartItemsHost = "PART_ItemsHost";

    /// <summary>Whether this node's children are shown (<c>:expanded</c>; gates the <c>PART_ItemsHost</c> visibility + twisty).</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        UIProperty.Register<TreeViewItem, bool>(nameof(IsExpanded), defaultValue: false, changed: OnIsExpandedChanged);

    /// <summary>Whether this node is the tree's single selection (<c>:selected</c>; folds into the owning <see cref="TreeView"/>).</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<TreeViewItem, bool>(nameof(IsSelected), defaultValue: false, changed: OnIsSelectedChanged);

    /// <summary>Raised (bubbling) when the node expands.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ExpandedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Expanded), RoutingStrategy.Bubble, typeof(TreeViewItem));

    /// <summary>Raised (bubbling) when the node collapses.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CollapsedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Collapsed), RoutingStrategy.Bubble, typeof(TreeViewItem));

    private bool _treeDriven; // guards the tree→node IsSelected write so it never echoes back into the tree
    private TextBlock? _twisty;
    private TreeView? _ownerTree; // captured at attach so a detached node can still reach its tree to clear selection

    static TreeViewItem()
    {
        PseudoClassMapping.Register<TreeViewItem>(IsExpandedProperty, ":expanded");
        PseudoClassMapping.Register<TreeViewItem>(IsSelectedProperty, ":selected");
    }

    /// <summary>Creates a tree node (focusable — it is the keyboard cursor target).</summary>
    public TreeViewItem()
    {
        Focusable = true;
        ItemContainerGenerator.ContainersChanged += OnChildContainersChanged;
    }

    /// <inheritdoc cref="IsExpandedProperty"/>
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    /// <summary>Whether this node has child items (a parent rather than a leaf) — drives the twisty.</summary>
    public bool HasItems => ItemContainerGenerator.ContainerCount > 0;

    /// <summary>CLR sugar over <see cref="ExpandedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Expanded
    {
        add => AddHandler(ExpandedEvent, value!);
        remove => RemoveHandler(ExpandedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="CollapsedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Collapsed
    {
        add => AddHandler(CollapsedEvent, value!);
        remove => RemoveHandler(CollapsedEvent, value!);
    }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new TreeViewItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is TreeViewItem;

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(UIElement container, object? item)
    {
        base.PrepareContainerForItemOverride(container, item);
        if (container is TreeViewItem node) // recurse the hierarchical template into child nodes
            TreeHierarchy.Apply(this, node, item);
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(UIElement container, object? item)
    {
        if (container is TreeViewItem node)
            TreeHierarchy.Clear(node);
        base.ClearContainerForItemOverride(container, item);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        _ownerTree = OwnerTree; // cache while the logical chain is live (a detached node can't walk to its tree)

        // Fold a preset IsSelected (set before this node was parented under a TreeView, when OnIsSelectedChanged's
        // OwnerTree was null and dropped it) into the tree now that the owner resolves — else the node renders
        // :selected while SelectedContainer stays null and a later selection leaves two nodes selected (the
        // SelectingItemsControl.ReconcileContainers analog; tree-wide-single invariant, CD-P2C-1).
        if (IsSelected)
            _ownerTree?.ChangeSelection(this);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        _ownerTree?.ClearSelectionIf(this); // a removed selected node clears the tree (no dangling SelectedItem)
        _ownerTree = null;
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); // sets ItemsHost (the PART_ItemsHost ItemsPresenter)
        _twisty = GetTemplatePart<TextBlock>(PartTwisty);
        UpdateExpansionVisuals();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _twisty = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        // A press on the twisty toggles expansion only (the expander is not the row); any other press selects.
        if (HasItems && IsWithin(e.OriginalSource, _twisty))
        {
            SetValue(IsExpandedProperty, !IsExpanded);
        }
        else
        {
            Focus();
            SelectSelf();
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsFocused)
            return; // only the focused node navigates — an unhandled key from a focused descendant bubbles here
                    // (the items are logical descendants); the MenuItem class-handler rule (§12.7)

        switch (e.Key)
        {
            case Key.RightArrow when HasItems && !IsExpanded:
                SetValue(IsExpandedProperty, true); // expand in place (focus stays)
                break;
            case Key.RightArrow when IsExpanded && FirstChild() is { } child:
                FocusAndSelect(child); // already open ⇒ descend to the first child
                break;
            case Key.LeftArrow when IsExpanded:
                SetValue(IsExpandedProperty, false); // collapse in place
                break;
            case Key.LeftArrow when ParentTreeViewItem is { } parent:
                FocusAndSelect(parent); // already collapsed / a leaf ⇒ ascend to the parent
                break;
            case Key.DownArrow when NextVisible() is { } next:
                FocusAndSelect(next);
                break;
            case Key.UpArrow when PrevVisible() is { } prev:
                FocusAndSelect(prev);
                break;
            default:
                if (!IsSpace(e))
                    return; // not a tree-nav key — leave unhandled
                FocusAndSelect(this); // Space (re)selects the focused node
                break;
        }

        e.Handled = true;
    }

    // ── selection (tree-wide; the TreeView is the single source of truth) ──────────────────────────────

    /// <summary>The tree drives <see cref="IsSelected"/> through here so the write never echoes back as an external set.</summary>
    internal void SetIsSelectedFromTree(bool selected)
    {
        _treeDriven = true;
        try
        {
            SetCurrentValue(IsSelectedProperty, selected); // SetCurrentValue preserves a two-way IsSelected binding
        }
        finally
        {
            _treeDriven = false;
        }
    }

    private void SelectSelf() => OwnerTree?.ChangeSelection(this);

    private void FocusAndSelect(TreeViewItem item)
    {
        item.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible (the keyboard cursor cue)
        item.SelectSelf();
    }

    private static void OnIsSelectedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not TreeViewItem { _treeDriven: false } item || item.OwnerTree is not { } tree)
            return;

        if (newValue)
            tree.ChangeSelection(item);   // external select folds in (deselecting the prior node)
        else
            tree.ClearSelectionIf(item);  // external deselect clears the tree iff this was the selection
    }

    // ── expansion ──────────────────────────────────────────────────────────────────────────────────────

    private static void OnIsExpandedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not TreeViewItem item)
            return;

        item.UpdateExpansionVisuals();
        item.RaiseEvent(new RoutedEventArgs(newValue ? ExpandedEvent : CollapsedEvent, item));
    }

    private void OnChildContainersChanged(object? sender, ContainersChangedEventArgs e)
        => UpdateExpansionVisuals(); // a leaf gaining/losing children flips the twisty

    // The twisty glyph (>/v/blank) + the children-host visibility, kept in one place. The host is Collapsed when
    // not expanded so a closed subtree takes no layout space (realization stays eager — virtualization is v2).
    private void UpdateExpansionVisuals()
    {
        if (_twisty is not null)
            _twisty.Text = HasItems ? (IsExpanded ? "v" : ">") : ""; // ASCII-safe (ambiguous-width memory)

        if (ItemsHost is { } host)
            host.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── visible-tree navigation (operates over realized containers) ─────────────────────────────────────

    private TreeViewItem? ChildAt(int index) => ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    private TreeViewItem? FirstChild() => HasItems ? ChildAt(0) : null;
    private TreeViewItem? LastChild() => HasItems ? ChildAt(ItemContainerGenerator.ContainerCount - 1) : null;

    // The next node in visible (pre-order) document order: into an expanded subtree, else the next sibling, else
    // walk up to an ancestor's next sibling.
    private TreeViewItem? NextVisible()
    {
        if (IsExpanded && FirstChild() is { } child)
            return child;

        for (TreeViewItem? node = this; node is not null; node = node.ParentTreeViewItem)
        {
            if (node.ParentItemsControl is not { } owner)
                break;

            var index = owner.ItemContainerGenerator.IndexFromContainer(node);
            if (index >= 0 && index + 1 < owner.ItemContainerGenerator.ContainerCount)
                return owner.ItemContainerGenerator.ContainerFromIndex(index + 1) as TreeViewItem;
        }

        return null;
    }

    // The previous visible node: the prior sibling's deepest last-visible descendant, else the parent.
    private TreeViewItem? PrevVisible()
    {
        if (ParentItemsControl is not { } owner)
            return null;

        var index = owner.ItemContainerGenerator.IndexFromContainer(this);
        if (index <= 0)
            return ParentTreeViewItem; // the first child's predecessor is its parent (null at the top level)

        var prev = owner.ItemContainerGenerator.ContainerFromIndex(index - 1) as TreeViewItem;
        while (prev is { IsExpanded: true } && prev.LastChild() is { } last)
            prev = last;
        return prev;
    }

    // The direct owner (the TreeView for a top-level node, or the parent TreeViewItem) — generated containers are
    // logical children of their ItemsControl (punch 43).
    private ItemsControl? ParentItemsControl
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is ItemsControl owner)
                    return owner;
            return null;
        }
    }

    private TreeViewItem? ParentTreeViewItem
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is TreeViewItem parent)
                    return parent;
            return null;
        }
    }

    private TreeView? OwnerTree
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is TreeView tree)
                    return tree;
            return null;
        }
    }

    private static bool IsWithin(UIElement? node, UIElement? ancestor)
    {
        if (ancestor is null)
            return false;
        for (; node is not null; node = node.VisualParent)
            if (ReferenceEquals(node, ancestor))
                return true;
        return false;
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); the lone real Key.Space wire is NUL→Ctrl+Space.
    // Gate on no-modifiers first (mirroring ButtonBase.IsActivationSpace) so Ctrl+Space (Key.Space + Control) and any
    // modified spacebar are NOT swallowed as a select — they bubble for an ancestor command binding / KeyGesture.
    private static bool IsSpace(KeyEventArgs e)
        => e.Modifiers == KeyModifiers.None
           && (e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' '));
}
