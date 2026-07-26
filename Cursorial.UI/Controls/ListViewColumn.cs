using System.Collections.ObjectModel;

namespace Cursorial.UI.Controls;

/// <summary>
/// The presentation mode a <see cref="ListView"/> is currently in (the file-manager view menu:
/// Details / List / Small Icons / Tiles). Switching the mode swaps the items panel, the per-item cell
/// composition and the scroll axis in one step — see <see cref="ListView.ViewProperty"/>.
/// </summary>
public enum ListViewViewMode : byte
{
    /// <summary>A vertical column grid with a clickable, sortable header row (WPF's <c>GridView</c> shape).</summary>
    Details,

    /// <summary>Single-line names, wrapping <b>column-major</b> (fill down, then across) with horizontal scrolling.</summary>
    List,

    /// <summary>An icon plus the name, wrapping column-major with horizontal scrolling.</summary>
    SmallIcons,

    /// <summary>A larger cell (icon + name + a secondary line), wrapping row-major with vertical scrolling.</summary>
    Tiles
}

/// <summary>The sort order a <see cref="ListViewColumn"/> is displaying.</summary>
public enum ListViewSortDirection : byte
{
    /// <summary>The column is not part of the sort (no indicator).</summary>
    None,

    /// <summary>Ascending — the <c>▲</c> indicator.</summary>
    Ascending,

    /// <summary>Descending — the <c>▼</c> indicator.</summary>
    Descending
}

/// <summary>
/// One column of a <see cref="ListView"/> in <see cref="ListViewViewMode.Details"/> (the lightweight
/// analog of WPF's <c>GridViewColumn</c>; Avalonia's <c>DataGridColumn</c> is the nearer cousin). A
/// <see cref="UIObject"/>-backed description object — <b>not</b> a <see cref="UIElement"/>: it never
/// enters the visual tree, it only tells the header presenter and every row's cells presenter what to
/// build. Being a <see cref="UIObject"/> makes it XAML-instantiable and gives every member the property
/// system (bindings and dynamic resources can arrive later without a breaking change).
/// <para>
/// Cell content resolves in this order: <see cref="CellTemplate"/> (applied to the row's item), else
/// <see cref="DisplayMemberPath"/> (a one-way binding against the item), else the item itself.
/// </para>
/// </summary>
public class ListViewColumn : UIObject
{
    /// <summary>The header content (usually a string). Displayed by the clickable <see cref="ListViewColumnHeader"/>.</summary>
    public static readonly StyledProperty<object?> HeaderProperty =
        UIProperty.Register<ListViewColumn, object?>(nameof(Header), changed: OnStructureAffectingChanged);

    /// <summary>
    /// The column's track sizing: a fixed cell count, <see cref="GridLength.Auto"/> (the default — as wide
    /// as the widest realized cell and the header), or a <see cref="GridLength.Star"/> share of the slack
    /// left over after the fixed and auto tracks. Reuses <see cref="GridLength"/> so column authoring reads
    /// exactly like <see cref="Grid"/> authoring.
    /// </summary>
    public static readonly StyledProperty<GridLength> WidthProperty =
        UIProperty.Register<ListViewColumn, GridLength>(nameof(Width), defaultValue: GridLength.Auto, changed: OnLayoutAffectingChanged);

    /// <summary>The column's minimum width in cells (default 1). Floors every sizing mode, including star.</summary>
    public static readonly StyledProperty<int> MinWidthProperty =
        UIProperty.Register<ListViewColumn, int>(nameof(MinWidth), defaultValue: 1,
            coerce: static (_, value) => Math.Max(0, value), changed: OnLayoutAffectingChanged);

    /// <summary>The template applied to the ROW ITEM to build this column's cell (wins over <see cref="DisplayMemberPath"/>).</summary>
    public static readonly StyledProperty<DataTemplate?> CellTemplateProperty =
        UIProperty.Register<ListViewColumn, DataTemplate?>(nameof(CellTemplate), changed: OnStructureAffectingChanged);

    /// <summary>The property path on the row item whose value this column shows (e.g. <c>"Size"</c>). Ignored when
    /// <see cref="CellTemplate"/> is set; <see langword="null"/> ⇒ the item itself.</summary>
    public static readonly StyledProperty<string?> DisplayMemberPathProperty =
        UIProperty.Register<ListViewColumn, string?>(nameof(DisplayMemberPath), changed: OnStructureAffectingChanged);

    /// <summary>How the cell content sits in its column slot (default <see cref="HorizontalAlignment.Left"/>).
    /// <see cref="HorizontalAlignment.Right"/> is the numeric-column setting the "Size" column wants.</summary>
    public static readonly StyledProperty<HorizontalAlignment> HorizontalAlignmentProperty =
        UIProperty.Register<ListViewColumn, HorizontalAlignment>(nameof(HorizontalAlignment),
            defaultValue: HorizontalAlignment.Left, changed: OnStructureAffectingChanged);

    /// <summary>The property path the built-in sort orders by; <see langword="null"/> ⇒ <see cref="DisplayMemberPath"/>.
    /// A host handling the <see cref="ListView.Sorting"/> event itself may read this as the sort key name.</summary>
    public static readonly StyledProperty<string?> SortMemberPathProperty =
        UIProperty.Register<ListViewColumn, string?>(nameof(SortMemberPath));

    /// <summary>Whether clicking this column's header cycles the sort (default <see langword="true"/>).</summary>
    public static readonly StyledProperty<bool> CanUserSortProperty =
        UIProperty.Register<ListViewColumn, bool>(nameof(CanUserSort), defaultValue: true);

    /// <inheritdoc cref="HeaderProperty"/>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <inheritdoc cref="WidthProperty"/>
    public GridLength Width { get => GetValue(WidthProperty); set => SetValue(WidthProperty, value); }

    /// <inheritdoc cref="MinWidthProperty"/>
    public int MinWidth { get => GetValue(MinWidthProperty); set => SetValue(MinWidthProperty, value); }

    /// <inheritdoc cref="CellTemplateProperty"/>
    public DataTemplate? CellTemplate { get => GetValue(CellTemplateProperty); set => SetValue(CellTemplateProperty, value); }

    /// <inheritdoc cref="DisplayMemberPathProperty"/>
    public string? DisplayMemberPath { get => GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }

    /// <inheritdoc cref="HorizontalAlignmentProperty"/>
    public HorizontalAlignment HorizontalAlignment { get => GetValue(HorizontalAlignmentProperty); set => SetValue(HorizontalAlignmentProperty, value); }

    /// <inheritdoc cref="SortMemberPathProperty"/>
    public string? SortMemberPath { get => GetValue(SortMemberPathProperty); set => SetValue(SortMemberPathProperty, value); }

    /// <inheritdoc cref="CanUserSortProperty"/>
    public bool CanUserSort { get => GetValue(CanUserSortProperty); set => SetValue(CanUserSortProperty, value); }

    /// <summary>The effective sort key path: <see cref="SortMemberPath"/> when set, else <see cref="DisplayMemberPath"/>.</summary>
    public string? EffectiveSortMemberPath => SortMemberPath ?? DisplayMemberPath;

    /// <summary>The owning collection's <see cref="ListView"/> (null while unparented). Set by <see cref="ListViewColumnCollection"/>.</summary>
    internal ListView? Owner { get; set; }

    // Two grades of change, because rebuilding every row's cell elements on a width tweak would be
    // gratuitous: a WIDTH/MinWidth change only re-runs the shared column layout, whereas a
    // header/template/path/alignment change actually changes what the header or cell elements ARE (the
    // header carries its column's Header as its Content, so a Header edit has to reach the rebuild).
    private static void OnLayoutAffectingChanged<T>(UIObject sender, T oldValue, T newValue)
        => (sender as ListViewColumn)?.Owner?.InvalidateColumnLayout();

    private static void OnStructureAffectingChanged<T>(UIObject sender, T oldValue, T newValue)
        => (sender as ListViewColumn)?.Owner?.InvalidateColumnStructure();
}

/// <summary>
/// The <see cref="ListView.Columns"/> collection: an observable list that stamps each
/// <see cref="ListViewColumn"/> with its owning <see cref="ListView"/> (so a column property change can
/// reach the control) and notifies the owner of every structural edit.
/// </summary>
public sealed class ListViewColumnCollection : ObservableCollection<ListViewColumn>
{
    private readonly ListView _owner;

    internal ListViewColumnCollection(ListView owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, ListViewColumn item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Owner = _owner;
        base.InsertItem(index, item);
        _owner.InvalidateColumnStructure();
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, ListViewColumn item)
    {
        ArgumentNullException.ThrowIfNull(item);
        this[index].Owner = null;
        item.Owner = _owner;
        base.SetItem(index, item);
        _owner.InvalidateColumnStructure();
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        this[index].Owner = null;
        base.RemoveItem(index);
        _owner.InvalidateColumnStructure();
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        foreach (var column in this)
            column.Owner = null;
        base.ClearItems();
        _owner.InvalidateColumnStructure();
    }
}
