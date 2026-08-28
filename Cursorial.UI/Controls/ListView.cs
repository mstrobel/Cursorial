using System.Collections;
using System.Collections.Specialized;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A selectable list with <b>switchable view modes</b> — the file-manager list control (WPF's
/// <c>ListView</c> + <c>GridView</c> collapsed into one type; Avalonia has no equivalent). One control,
/// four presentations, switched at runtime through <see cref="View"/>:
/// <list type="bullet">
/// <item><see cref="ListViewViewMode.Details"/> — a column grid under a clickable, sortable header row.</item>
/// <item><see cref="ListViewViewMode.List"/> — single-line names wrapping <b>column-major</b> (down, then
/// across) with horizontal scrolling.</item>
/// <item><see cref="ListViewViewMode.SmallIcons"/> — icon + name, column-major, horizontally scrolled.</item>
/// <item><see cref="ListViewViewMode.Tiles"/> — a two-line tile (icon + name + secondary line) wrapping
/// row-major with vertical scrolling.</item>
/// </list>
/// <para>
/// It derives from <see cref="SelectingItemsControl"/> rather than <see cref="ListBox"/> on purpose.
/// Everything a ListBox adds beyond the selector base is keyboard navigation, and a ListView's navigation
/// is <em>not</em> a ListBox's: in a column-major wrap ↓ moves within a column while → jumps a whole
/// column, and ListBox's mover is private, so inheriting it would mean calling <c>base.OnKeyDown</c> only
/// to fight the result. The two behaviors it is worth keeping — re-selecting a survivor after a removal
/// and focusing the type-ahead match — are three lines each and are reimplemented below.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The view-mode seam is <see cref="ItemsControl.ItemsPanelProperty"/>.</b> Assigning a new
/// <see cref="ItemsPanelTemplate"/> makes the <see cref="ItemsPresenter"/> tear the old panel down,
/// rebuild, and re-adopt every container, and pulses the <c>ScrollContentPresenter</c>. So a view switch
/// is literally a panel swap plus a scroll-axis flip plus a per-row cell rebuild.
/// </para>
/// <para>
/// <b>v1 is non-virtualizing.</b> The generator has no way back out of virtualizing mode
/// (<c>EnableVirtualization</c> has no inverse), so a control that swaps between a virtualizing panel and
/// a wrapping one would strand the wrap panel with an empty sparse store. Details therefore uses a plain
/// <see cref="StackPanel"/>; every container is eagerly realized, which is also what the runtime panel
/// swap and the column auto-width negotiation both assume.
/// </para>
/// </remarks>
[TemplatePart(PartHeaderPresenter, typeof(ListViewHeaderPresenter))]
[TemplatePart(PartScrollViewer, typeof(ScrollViewer))]
public class ListView : SelectingItemsControl
{
    private const string PartHeaderPresenter = "PART_HeaderPresenter";
    private const string PartScrollViewer = "PART_ScrollViewer";

    /// <summary>The presentation mode (default <see cref="ListViewViewMode.Details"/>). Changing it swaps the
    /// items panel, the scroll axis and every row's cell composition in one step.</summary>
    public static readonly StyledProperty<ListViewViewMode> ViewProperty =
        UIProperty.Register<ListView, ListViewViewMode>(nameof(View), defaultValue: ListViewViewMode.Details, changed: OnViewChanged);

    /// <summary>The gap in cells between adjacent columns in <see cref="ListViewViewMode.Details"/> (default 1).</summary>
    public static readonly StyledProperty<int> ColumnSpacingProperty =
        UIProperty.Register<ListView, int>(nameof(ColumnSpacing), defaultValue: 1,
            coerce: static (_, value) => Math.Max(0, value), changed: OnColumnLayoutAffectingChanged);

    /// <summary>The column currently driving the sort, or <see langword="null"/> (no indicator). Settable, so a
    /// host restoring persisted state can light the right indicator without faking a click.</summary>
    public static readonly StyledProperty<ListViewColumn?> SortColumnProperty =
        UIProperty.Register<ListView, ListViewColumn?>(nameof(SortColumn), changed: OnSortStateChanged);

    /// <summary>The direction <see cref="SortColumn"/> is sorted in.</summary>
    public static readonly StyledProperty<ListViewSortDirection> SortDirectionProperty =
        UIProperty.Register<ListView, ListViewSortDirection>(nameof(SortDirection),
            defaultValue: ListViewSortDirection.None, changed: OnSortStateChanged);

    /// <summary>Whether header clicks cycle the sort at all (default <see langword="true"/>). Per-column opt-out
    /// lives on <see cref="ListViewColumn.CanUserSort"/>.</summary>
    public static readonly StyledProperty<bool> CanUserSortColumnsProperty =
        UIProperty.Register<ListView, bool>(nameof(CanUserSortColumns), defaultValue: true);

    /// <summary>
    /// Opt-in (default <see langword="false"/>): after <see cref="Sorting"/> is raised unhandled, reorder the
    /// items in place with the built-in comparer keyed on <see cref="ListViewColumn.EffectiveSortMemberPath"/>.
    /// Off by default because the collection belongs to the host — the control raising an event and letting the
    /// view-model own the ordering is the correct default, and an in-place reorder of somebody else's list is a
    /// side effect you should have to ask for.
    /// </summary>
    public static readonly StyledProperty<bool> IsBuiltInSortEnabledProperty =
        UIProperty.Register<ListView, bool>(nameof(IsBuiltInSortEnabled), defaultValue: false);

    /// <summary>The property path supplying an item's primary text in the non-Details views (and the type-ahead
    /// match text). <see langword="null"/> ⇒ the item itself.</summary>
    public static readonly StyledProperty<string?> DisplayMemberPathProperty =
        UIProperty.Register<ListView, string?>(nameof(DisplayMemberPath), changed: OnCellCompositionChanged);

    /// <summary>The property path supplying an item's icon glyph in <see cref="ListViewViewMode.SmallIcons"/> and
    /// <see cref="ListViewViewMode.Tiles"/>. <see langword="null"/> ⇒ no icon cell.</summary>
    public static readonly StyledProperty<string?> IconMemberPathProperty =
        UIProperty.Register<ListView, string?>(nameof(IconMemberPath), changed: OnCellCompositionChanged);

    /// <summary>The property path supplying a tile's second line in <see cref="ListViewViewMode.Tiles"/>.
    /// <see langword="null"/> ⇒ a one-line tile.</summary>
    public static readonly StyledProperty<string?> SecondaryMemberPathProperty =
        UIProperty.Register<ListView, string?>(nameof(SecondaryMemberPath), changed: OnCellCompositionChanged);

    /// <summary>The uniform tile width in cells for the wrapping views, or <see langword="null"/> (default) to
    /// size from the widest item. Forwarded to <see cref="UniformWrapPanel.ItemWidth"/>.</summary>
    public static readonly StyledProperty<int?> ItemWidthProperty =
        UIProperty.Register<ListView, int?>(nameof(ItemWidth), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>The uniform tile height in cells for the wrapping views, or <see langword="null"/> (default).
    /// Forwarded to <see cref="UniformWrapPanel.ItemHeight"/>.</summary>
    public static readonly StyledProperty<int?> ItemHeightProperty =
        UIProperty.Register<ListView, int?>(nameof(ItemHeight), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>The maximum tile width in cells for the wrapping views, or <see langword="null"/> (default) to
    /// size from the widest item. Forwarded to <see cref="UniformWrapPanel.ItemWidth"/>.</summary>
    public static readonly StyledProperty<int?> ItemMaxWidthProperty =
        UIProperty.Register<ListView, int?>(nameof(ItemMaxWidth), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>The maximum tile height in cells for the wrapping views, or <see langword="null"/> (default).
    /// Forwarded to <see cref="UniformWrapPanel.ItemHeight"/>.</summary>
    public static readonly StyledProperty<int?> ItemMaxHeightProperty =
        UIProperty.Register<ListView, int?>(nameof(ItemMaxHeight), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>Raised before a header click changes the sort — the seam a host uses to sort its own collection.
    /// Bubbles; the args are vetoable (<see cref="ListViewSortingEventArgs.Cancel"/>).</summary>
    public static readonly RoutedEvent<ListViewSortingEventArgs> SortingEvent =
        RoutedEvent<ListViewSortingEventArgs>.Register(nameof(Sorting), RoutingStrategy.Bubble, typeof(ListView));

    /// <summary>Raised when an item is invoked (Enter or double-click) — the "open this" gesture, distinct from
    /// selection. Fires for both gestures because it is chained off the base
    /// <see cref="SelectingItemsControl.ItemActivated"/> rather than off the key handler alone.</summary>
    public static readonly RoutedEvent<ItemActivatedEventArgs> ItemInvokedEvent =
        RoutedEvent<ItemActivatedEventArgs>.Register(nameof(ItemInvoked), RoutingStrategy.Bubble, typeof(ListView));

    private static readonly ItemsPanelTemplate DetailsPanelTemplate = CreateStackPanelTemplate();
    private static readonly ItemsPanelTemplate ColumnMajorPanelTemplate = CreateWrapPanelTemplate(Orientation.Vertical);
    private static readonly ItemsPanelTemplate RowMajorPanelTemplate = CreateWrapPanelTemplate(Orientation.Horizontal);

    private readonly ListViewColumnLayout _columnLayout = new();
    private readonly List<ListViewCellsPresenter> _cellsPresenters = [];
    private readonly List<ListViewHeaderPresenter> _headerPresenters = [];
    private ListViewHeaderPresenter? _headerPart;
    private int _columnStructureVersion;
    private bool _sorting; // suppresses the removal re-select while the built-in sort permutes the list

    /// <summary>Creates a list view (Details, single selection; the list itself is not a tab stop — its items host is).</summary>
    public ListView()
    {
        IsTabStop = false;
        Columns = new ListViewColumnCollection(this);
        SetValue(ItemsPanelProperty, DetailsPanelTemplate);

        // ItemInvoked chains off ItemActivated so BOTH gestures reach it: Enter comes through OnKeyDown, but a
        // double-click is raised by SelectingItemsControl.HandleContainerPointerSelect, which is not virtual.
        AddHandler(ItemActivatedEvent, OnItemActivatedRaiseInvoked);
    }

    /// <inheritdoc/>
    protected internal override bool HandlesScrolling => true;

    /// <inheritdoc cref="ViewProperty"/>
    public ListViewViewMode View { get => GetValue(ViewProperty); set => SetValue(ViewProperty, value); }

    /// <summary>The <see cref="ListViewViewMode.Details"/> columns (empty by default — an empty list falls back to
    /// the composed single-cell row, so a ListView is usable before any column is declared).</summary>
    public ListViewColumnCollection Columns { get; }

    /// <inheritdoc cref="ColumnSpacingProperty"/>
    public int ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }

    /// <inheritdoc cref="SortColumnProperty"/>
    public ListViewColumn? SortColumn { get => GetValue(SortColumnProperty); set => SetValue(SortColumnProperty, value); }

    /// <inheritdoc cref="SortDirectionProperty"/>
    public ListViewSortDirection SortDirection { get => GetValue(SortDirectionProperty); set => SetValue(SortDirectionProperty, value); }

    /// <inheritdoc cref="CanUserSortColumnsProperty"/>
    public bool CanUserSortColumns { get => GetValue(CanUserSortColumnsProperty); set => SetValue(CanUserSortColumnsProperty, value); }

    /// <inheritdoc cref="IsBuiltInSortEnabledProperty"/>
    public bool IsBuiltInSortEnabled { get => GetValue(IsBuiltInSortEnabledProperty); set => SetValue(IsBuiltInSortEnabledProperty, value); }

    /// <inheritdoc cref="DisplayMemberPathProperty"/>
    public string? DisplayMemberPath { get => GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }

    /// <inheritdoc cref="IconMemberPathProperty"/>
    public string? IconMemberPath { get => GetValue(IconMemberPathProperty); set => SetValue(IconMemberPathProperty, value); }

    /// <inheritdoc cref="SecondaryMemberPathProperty"/>
    public string? SecondaryMemberPath { get => GetValue(SecondaryMemberPathProperty); set => SetValue(SecondaryMemberPathProperty, value); }

    /// <inheritdoc cref="ItemWidthProperty"/>
    public int? ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }

    /// <inheritdoc cref="ItemHeightProperty"/>
    public int? ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    /// <inheritdoc cref="ItemMaxWidthProperty"/>
    public int? ItemMaxWidth { get => GetValue(ItemMaxWidthProperty); set => SetValue(ItemMaxWidthProperty, value); }

    /// <inheritdoc cref="ItemMaxHeightProperty"/>
    public int? ItemMaxHeight { get => GetValue(ItemMaxHeightProperty); set => SetValue(ItemMaxHeightProperty, value); }

    /// <summary>CLR sugar over <see cref="SortingEvent"/>.</summary>
    public event EventHandler<ListViewSortingEventArgs>? Sorting { add => AddHandler(SortingEvent, value!); remove => RemoveHandler(SortingEvent, value!); }

    /// <summary>CLR sugar over <see cref="ItemInvokedEvent"/>.</summary>
    public event EventHandler<ItemActivatedEventArgs>? ItemInvoked { add => AddHandler(ItemInvokedEvent, value!); remove => RemoveHandler(ItemInvokedEvent, value!); }

    /// <summary>The shared column-width table the header strip and every row read.</summary>
    internal ListViewColumnLayout ColumnLayout => _columnLayout;

    /// <summary>Bumped whenever the cell ELEMENTS must be rebuilt (a column added / retemplated, a view switch).</summary>
    internal int ColumnStructureVersion => _columnStructureVersion;

    /// <summary>The items panel as a <see cref="UniformWrapPanel"/>, or <see langword="null"/> in Details.</summary>
    internal UniformWrapPanel? WrapPanelHost => ItemsPanelFromItemsControl(this) as UniformWrapPanel;

    // ───────────────────────────── container policy ─────────────────────────────

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new ListViewItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is ListViewItem;

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(UIElement container, object? item)
    {
        base.PrepareContainerForItemOverride(container, item);

        // Push the view down BEFORE the container's first measure so its cells presenter builds the right
        // composition on the first pass (a recycled container may be carrying the previous mode).
        if (container is ListViewItem listViewItem)
        {
            listViewItem.SetViewFromOwner(View);
            listViewItem.CellsPresenter?.Rebuild();
        }
    }

    /// <inheritdoc/>
    protected override void ResetRecycledContainerCore(UIElement container)
    {
        base.ResetRecycledContainerCore(container);

        if (container is ListViewItem listViewItem)
            listViewItem.SetViewFromOwner(View);
    }

    // ───────────────────────────── template ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _headerPart = GetTemplatePart<ListViewHeaderPresenter>(PartHeaderPresenter);
        ApplyViewToTemplate();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _headerPart = null;
        base.OnTemplateDetaching(old);
    }

    // ───────────────────────────── presenter registry ─────────────────────────────
    //
    // Rows and the header register themselves on attach so a view / column change can reach them without a
    // visual-tree walk (containers may live under a scroll band, and the header does not).

    internal void RegisterCellsPresenter(ListViewCellsPresenter presenter)
    {
        if (!_cellsPresenters.Contains(presenter))
            _cellsPresenters.Add(presenter);
    }

    internal void UnregisterCellsPresenter(ListViewCellsPresenter presenter) => _cellsPresenters.Remove(presenter);

    internal void RegisterHeaderPresenter(ListViewHeaderPresenter presenter)
    {
        if (!_headerPresenters.Contains(presenter))
            _headerPresenters.Add(presenter);
    }

    internal void UnregisterHeaderPresenter(ListViewHeaderPresenter presenter) => _headerPresenters.Remove(presenter);

    /// <summary>A column's width/header changed — re-run the shared layout, keep the cell elements.</summary>
    internal void InvalidateColumnLayout()
    {
        InvalidateColumnConsumers();
        InvalidateMeasure();
    }

    /// <summary>A column was added/removed/retemplated (or the view flipped) — the cell ELEMENTS must be rebuilt.</summary>
    internal void InvalidateColumnStructure()
    {
        _columnStructureVersion++;

        foreach (var presenter in _cellsPresenters)
            presenter.Rebuild();
        foreach (var presenter in _headerPresenters)
            presenter.Rebuild();

        InvalidateMeasure();
    }

    private void InvalidateColumnConsumers()
    {
        foreach (var presenter in _cellsPresenters)
            presenter.InvalidateMeasure();
        foreach (var presenter in _headerPresenters)
            presenter.InvalidateMeasure();
    }

    // ───────────────────────────── column layout negotiation ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var details = View == ListViewViewMode.Details && Columns.Count > 0;

        if (details)
            _columnLayout.BeginPass(Columns.Count);

        var size = base.MeasureOverride(availableSize);

        // The rows have now reported their unconstrained cell widths and their band width, so the Auto and
        // Star tracks can finally be resolved. If that moved anything, the header + rows are invalidated and
        // the NEXT layout pass renders at the settled widths — one extra pass, never a loop, because the
        // reported widths are measured unbounded and so do not depend on what we hand back.
        if (details && _columnLayout.Reconcile(Columns, ColumnSpacing))
            InvalidateColumnConsumers();

        return size;
    }

    // ───────────────────────────── view switching ─────────────────────────────

    private static void OnViewChanged(UIObject sender, ListViewViewMode oldValue, ListViewViewMode newValue)
    {
        if (sender is not ListView listView)
            return;

        var oldPanel = ItemsPanelFromItemsControl(listView);
        var oldFocus = oldPanel?.GetValue(FocusManager.FocusedElementProperty);
        var oldFocusIndex = oldFocus is not null ? listView.ItemContainerGenerator.IndexFromContainer(oldFocus) : -1;
        var oldFocusItem = oldFocusIndex >= 0 ? listView.ItemContainerGenerator.ItemFromIndex(oldFocusIndex) : null;

        // ① the panel swap — ItemsPresenter.RebuildPanel() tears down, rebuilds and re-adopts every container.
        listView.SetCurrentValue(ItemsPanelProperty, PanelTemplateFor(newValue));

        // ② the scroll axis, ③ the header strip, ④ every container's pseudo-class + cell composition.
        listView.ApplyViewToTemplate();
        listView.PushViewToContainers();
        listView.InvalidateColumnStructure();
        listView.RepairFocus(oldFocusIndex, oldFocusItem);
    }

    private static ItemsPanelTemplate PanelTemplateFor(ListViewViewMode view)
        => view switch
           {
               ListViewViewMode.List or ListViewViewMode.SmallIcons => ColumnMajorPanelTemplate,
               ListViewViewMode.Tiles                               => RowMajorPanelTemplate,
               _                                                    => DetailsPanelTemplate
           };

    // The scroll axis IS the view mode: a column-major wrap grows sideways, a row-major wrap grows downward,
    // and Details must not scroll horizontally at all — its header is pinned outside the viewport, so a
    // horizontally scrolled body would slide out from under its own headings.
    private void ApplyViewToTemplate()
    {
        var view = View;
        var columnMajor = view is ListViewViewMode.List or ListViewViewMode.SmallIcons;

        if (GetValueSource(ScrollViewer.HorizontalScrollBarVisibilityProperty) is { Kind: ValueSourceKind.Default })
        {
            SetCurrentValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                            columnMajor ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        }

        if (GetValueSource(ScrollViewer.VerticalScrollBarVisibilityProperty) is { Kind: ValueSourceKind.Default })
        {
            SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                            columnMajor ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }

        _headerPart?.SetCurrentValue(VisibilityProperty,
                                     view == ListViewViewMode.Details ? Visibility.Visible : Visibility.Collapsed);
    }

    private void PushViewToContainers()
    {
        var view = View;
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem item)
                item.SetViewFromOwner(view);
        }
    }

    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        ClearPendingFocusRepair();
        base.OnDetachedFromTree(in e);
    }

    private static void OnColumnLayoutAffectingChanged(UIObject sender, int oldValue, int newValue)
        => (sender as ListView)?.InvalidateColumnLayout();

    private static void OnCellCompositionChanged(UIObject sender, string? oldValue, string? newValue)
        => (sender as ListView)?.InvalidateColumnStructure();

    private static void OnSortStateChanged<T>(UIObject sender, T oldValue, T newValue)
    {
        if (sender is not ListView listView)
            return;

        foreach (var presenter in listView._headerPresenters)
            presenter.UpdateSortIndicators();
    }

    // ───────────────────────────── cell composition ─────────────────────────────

    /// <summary>Builds one Details cell: <see cref="ListViewColumn.CellTemplate"/> wins, else a one-way binding
    /// on <see cref="ListViewColumn.DisplayMemberPath"/>, else the item itself.</summary>
    internal static UIElement BuildDetailsCell(ListViewColumn column)
    {
        var presenter = new ContentPresenter
                        {
                            HorizontalAlignment = column.HorizontalAlignment,
                            ShowTrimmedContentInToolTip = true
                        };

        if (column.CellTemplate is {} template)
            presenter.ContentTemplate = template;
        else
            presenter.ShowTrimmedContentInToolTip = true;

        presenter.SetBinding(ContentPresenter.ContentProperty,
                             PathBinding(column.CellTemplate is null ? column.DisplayMemberPath : null));

        return presenter;
    }

    /// <summary>Builds the single composed cell used by the three wrapping views (and by Details with no columns).</summary>
    internal UIElement BuildComposedCell()
    {
        // An explicit ItemTemplate is the escape hatch: the caller has said exactly what an item looks like,
        // so the icon/name/secondary composition below must not second-guess it.
        if (ItemTemplate is {} itemTemplate)
        {
            var templated = new ContentPresenter { ContentTemplate = itemTemplate, ShowTrimmedContentInToolTip = true };
            templated.SetBinding(ContentPresenter.ContentProperty, PathBinding(null));
            return templated;
        }

        var primary = BuildTextCell(DisplayMemberPath);

        switch (View)
        {
            case ListViewViewMode.SmallIcons when IconMemberPath is { Length: > 0 }:
                return IconRow(BuildTextCell(IconMemberPath), primary);

            case ListViewViewMode.Tiles:
            {
                var head = IconMemberPath is { Length: > 0 } ? IconRow(BuildTextCell(IconMemberPath), primary) : primary;

                if (SecondaryMemberPath is not { Length: > 0 } secondaryPath)
                    return head;

                var tile = new DockPanel();
                var secondaryCell = BuildTextCell(secondaryPath);
                TextElement.SetTextWeight(secondaryCell, TextWeight.Faint);
                DockPanel.SetDock(secondaryCell, Dock.Bottom);
                tile.Children.Add(secondaryCell);
                tile.Children.Add(head);
                return tile;
            }

            default:
                return primary;
        }
    }

    private static UIElement IconRow(UIElement icon, UIElement primary)
    {
        var row = new DockPanel { LastChildFill = true, Height = 1 };

        icon.Margin = new Margins(0, 0, 1, 0);

        row.Children.Add(icon);
        row.Children.Add(primary);

        return row;
    }

    private static ContentPresenter BuildTextCell(string? path)
    {
        var presenter = new ContentPresenter
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            ForwardsFromTemplatedParent = false,
                            ShowTrimmedContentInToolTip = true
                        };
        presenter.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        presenter.SetBinding(ContentPresenter.ContentProperty, PathBinding(path));
        return presenter;
    }

    // A binding (not an imperative Content set) so a cell tracks an INotifyPropertyChanged item; an empty
    // path is the "source object itself" form, which resolves to the container's DataContext = the item.
    private static Binding PathBinding(string? path)
        => path is { Length: > 0 } ? new Binding(path) : new Binding();

    // ───────────────────────────── sorting ─────────────────────────────

    /// <summary>
    /// Advances <paramref name="column"/> through the sort cycle exactly as a header click does, honouring
    /// <see cref="CanUserSortColumns"/> and <see cref="ListViewColumn.CanUserSort"/>. The header strip calls
    /// this; a host wiring a "Sort by ▸ Name" menu item calls the same thing so both routes stay identical.
    /// </summary>
    /// <param name="column">The column to cycle.</param>
    public void CycleSort(ListViewColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (!CanUserSortColumns || !column.CanUserSort)
            return;

        // The cycle is ascending → descending → ascending: a third state ("unsorted") reads as a broken
        // click in a file list, where SOME order is always in effect.
        var direction = ReferenceEquals(SortColumn, column) && SortDirection == ListViewSortDirection.Ascending
            ? ListViewSortDirection.Descending
            : ListViewSortDirection.Ascending;

        ApplySort(column, direction);
    }

    /// <summary>
    /// Raises <see cref="Sorting"/> and, unless the host canceled or handled it, optionally runs the built-in
    /// comparer sort. Public so a host can drive the same funnel from a menu item or a restored setting.
    /// </summary>
    /// <param name="column">The column to sort by.</param>
    /// <param name="direction">The direction to sort in.</param>
    /// <returns><see langword="false"/> when a handler canceled; otherwise <see langword="true"/>.</returns>
    public bool ApplySort(ListViewColumn column, ListViewSortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(column);

        // A REAL allocation, never a pooled rental: Cancel/Handled must survive the raise (the vetoable rule).
        var args = new ListViewSortingEventArgs(SortingEvent, this, column, direction);
        RaiseEvent(args);

        if (args.Cancel)
            return false;

        SetCurrentValue(SortColumnProperty, column);
        SetCurrentValue(SortDirectionProperty, direction);

        if (!args.Handled && IsBuiltInSortEnabled)
            SortItemsInPlace(column, direction);

        return true;
    }

    // The opt-in convenience sort. It permutes the LIVE list with RemoveAt/Insert so an observable source
    // reports each hop and the generator/selection model stay aligned; a non-observable source is skipped
    // outright (there would be no way to tell anyone the order changed). Selection is captured by ITEM and
    // restored afterward, because the removals would otherwise drag it along index-wise.
    private void SortItemsInPlace(ListViewColumn column, ListViewSortDirection direction)
    {
        if (EffectiveItemList() is not { } list || list.Count < 2)
            return;

        var selected = SelectedItems.ToArray();
        var comparer = new ListViewValueComparer(column.EffectiveSortMemberPath, direction);

        var ordered = new object?[list.Count];
        list.CopyTo(ordered, 0);
        var target = ordered.OrderBy(static item => item, comparer).ToArray(); // OrderBy is stable — equal keys keep order

        _sorting = true;

        try
        {
            for (var i = 0; i < target.Length; i++)
            {
                var current = IndexOfFrom(list, target[i], i);
                if (current < 0 || current == i)
                    continue;

                list.RemoveAt(current);
                list.Insert(i, target[i]);
            }
        }
        finally
        {
            _sorting = false;
        }

        RestoreSelection(selected);
    }

    private static int IndexOfFrom(IList list, object? item, int start)
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item) || Equals(list[i], item))
                return i;
        }

        return -1;
    }

    private void RestoreSelection(IReadOnlyList<object?> items)
    {
        if (items.Count == 0)
            return;

        Selection.Select(-1); // the model's documented "clear" (there is no separate Clear primitive)

        foreach (var item in items)
        {
            var index = IndexFromItem(item);
            if (index < 0)
                continue;

            if (SelectionMode == SelectionMode.Single)
            {
                Selection.Select(index);
                return;
            }

            Selection.Toggle(index);
        }
    }

    // The list the generator is actually reading: the bound ItemsSource when there is one (it must be a
    // mutable, observable IList for an in-place reorder to be visible), else the direct Items lane.
    private IList? EffectiveItemList()
    {
        if (!HasItemsSource)
            return Items;

        return ItemsSource is IList { IsReadOnly: false, IsFixedSize: false } and INotifyCollectionChanged list
            ? (IList) list
            : null;
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    /// <summary>
    /// Per-view arrow navigation. The steps are derived from the live <see cref="UniformWrapPanel"/> geometry,
    /// so they are correct for the CURRENT wrap: in a column-major wrap ↓ walks down the column and → jumps to
    /// the same row of the next column (a stride of <see cref="UniformWrapPanel.ItemsPerLine"/>); in a row-major
    /// tile wrap the two swap. Details is a plain vertical list, so ←/→ stay unhandled and bubble.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var count = ItemContainerGenerator.ContainerCount;
        if (count == 0)
            return;

        var current = ResolveCurrent(e, count);
        var (verticalStep, horizontalStep) = NavigationSteps();

        switch (e.Key)
        {
            case Key.UpArrow:
                MoveCurrent(current < 0 ? 0 : Math.Max(0, current - verticalStep), e.Modifiers);
                break;
            case Key.DownArrow:
                MoveCurrent(current < 0 ? 0 : Math.Min(count - 1, current + verticalStep), e.Modifiers);
                break;
            case Key.LeftArrow:
                if (horizontalStep == 0)
                    return; // Details: leave ←/→ to the scroll viewer / an enclosing navigator
                MoveCurrent(current < 0 ? 0 : Math.Max(0, current - horizontalStep), e.Modifiers);
                break;
            case Key.RightArrow:
                if (horizontalStep == 0)
                    return;
                MoveCurrent(current < 0 ? 0 : Math.Min(count - 1, current + horizontalStep), e.Modifiers);
                break;
            case Key.Home:
                MoveCurrent(0, e.Modifiers);
                break;
            case Key.End:
                MoveCurrent(count - 1, e.Modifiers);
                break;
            case Key.PageUp:
                MoveCurrent(current < 0 ? 0 : Math.Max(0, current - PageStepItems()), e.Modifiers);
                break;
            case Key.PageDown:
                MoveCurrent(current < 0 ? 0 : Math.Min(count - 1, current + PageStepItems()), e.Modifiers);
                break;
            case Key.Enter:
                if (current < 0)
                    return; // nothing anchored — no phantom invocation
                RaiseItemActivated(current);
                break;
            default:
                if (!IsSpace(e) || current < 0)
                    return;
                if (SelectionMode == SelectionMode.Single)
                    Selection.Select(current);
                else
                    Selection.Toggle(current);
                break;
        }

        e.Handled = true;
    }

    /// <summary>The (↑/↓, ←/→) index strides for the current view. A horizontal stride of 0 means "do not handle".</summary>
    private (int Vertical, int Horizontal) NavigationSteps()
    {
        if (WrapPanelHost is not { } panel)
            return (1, 0); // Details — a plain vertical stack

        var perLine = Math.Max(1, panel.ItemsPerLine);
        return panel.Orientation == Orientation.Vertical
            ? (1, perLine)      // column-major: ↓ within the column, → to the next column
            : (perLine, 1);     // row-major tiles: → within the row, ↓ to the next row
    }

    // A page is a viewport's worth of LINES, converted back into item indices. In Details there are no lines,
    // so the inherited item-per-page estimate (extent ÷ count) is the right answer.
    private int PageStepItems()
    {
        if (WrapPanelHost is not { } panel)
            return Math.Max(1, ItemsPerPage());

        var perLine = Math.Max(1, panel.ItemsPerLine);
        var scroll = FindItemsScrollViewer();

        if (scroll is null)
            return Math.Max(1, perLine);

        var visibleLines = panel.Orientation == Orientation.Vertical
            ? scroll.Viewport.Columns / Math.Max(1, panel.CellWidth)
            : scroll.Viewport.Rows / Math.Max(1, panel.CellHeight);

        return Math.Max(1, Math.Max(1, visibleLines) * perLine);
    }

    // The current item is the container that owns the focused element the key bubbled from — authoritative
    // after any structural change. Falls back to the selection when focus is outside the items (verbatim the
    // ListBox rule, CD-P9-16).
    private int ResolveCurrent(KeyEventArgs e, int count)
    {
        for (var node = e.OriginalSource; node is not null; node = node.VisualParent)
        {
            var index = ItemContainerGenerator.IndexFromContainer(node);
            if (index >= 0)
                return index;
        }

        return SelectedIndex >= 0 && SelectedIndex < count ? SelectedIndex : -1;
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); Key.Space is only NUL→Ctrl+Space.
    private static bool IsSpace(KeyEventArgs e)
        => e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ');

    /// <inheritdoc/>
    protected override void OnTextSearchMatch(int containerIndex)
    {
        base.OnTextSearchMatch(containerIndex); // selection-follows
        ItemContainerGenerator.ContainerFromIndex(containerIndex)?.Focus(FocusNavigationMethod.Directional);
    }

    /// <summary>Type-ahead matches the same text the non-Details views display, so
    /// <see cref="DisplayMemberPath"/> is honored before falling back to the shared item rules.</summary>
    protected override string? GetTextSearchText(int index)
    {
        if (DisplayMemberPath is { Length: > 0 } path && ItemFromIndex(index) is { } item)
            return TextSearch.EvaluatePath(item, path);

        return base.GetTextSearchText(index);
    }

    /// <inheritdoc/>
    protected override void OnSelectionEmptiedByRemoval(int removalIndex)
    {
        if (_sorting)
            return; // a sort permutation removes-and-reinserts; RestoreSelection has the real answer

        var count = ItemContainerGenerator.ContainerCount;
        if (count > 0)
            Selection.Select(Math.Min(removalIndex, count - 1));
    }

    private void OnItemActivatedRaiseInvoked(object? sender, ItemActivatedEventArgs e)
        => RaiseEvent(new ItemActivatedEventArgs(ItemInvokedEvent, this, e.Item, e.Index));

    // ───────────────────────────── items-panel templates ─────────────────────────────

    private static ItemsPanelTemplate CreateStackPanelTemplate()
        => new(static _ => ConfigureItemsHost(new StackPanel { Orientation = Orientation.Vertical }));

    private static ItemsPanelTemplate CreateWrapPanelTemplate(Orientation orientation)
        => new(ctx =>
        {
            var panel = new UniformWrapPanel { Orientation = orientation };

            // Bound (not copied) so a runtime ItemWidth/ItemHeight change reaches the live panel without a
            // second panel swap — a swap would tear down and re-adopt every container for a sizing tweak.
            panel.SetBinding(UniformWrapPanel.ItemMaxHeightProperty,
                             CompiledBinding.For(ItemMaxHeightProperty, source: ctx.TemplatedParent));

            panel.SetBinding(UniformWrapPanel.ItemMaxWidthProperty,
                             CompiledBinding.For(ItemMaxWidthProperty, source: ctx.TemplatedParent));

            panel.SetBinding(UniformWrapPanel.ItemWidthProperty,
                             CompiledBinding.For(ItemWidthProperty, source: ctx.TemplatedParent));

            panel.SetBinding(UniformWrapPanel.ItemHeightProperty,
                             CompiledBinding.For(ItemHeightProperty, source: ctx.TemplatedParent));

            return ConfigureItemsHost(panel);
        });

    // The items host is ONE tab stop with arrow navigation inside, and its own focus scope so a Tab-in lands
    // on the selected item rather than item 0 (the ND33 entry ladder reads scope memory).
    private static Panel ConfigureItemsHost(Panel panel)
    {
        panel.IsItemsHost = true;
        panel.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Once);
        panel.SetValue(FocusManager.IsFocusScopeProperty, true);
        return panel;
    }
}
