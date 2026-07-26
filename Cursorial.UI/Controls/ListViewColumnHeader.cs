namespace Cursorial.UI.Controls;

/// <summary>
/// The clickable header cell of one <see cref="ListViewColumn"/> (WPF's <c>GridViewColumnHeader</c>).
/// It is a <see cref="ButtonBase"/>, so press/hover/focus states, the access-key folding and
/// <c>Click</c> all come from the button family for free; the only thing it adds is the sort-direction
/// indicator, which the template shows in <c>PART_SortIndicator</c>.
/// <para>
/// The header does not sort anything itself — it raises <c>Click</c>, the owning
/// <see cref="ListViewHeaderPresenter"/> forwards to <see cref="ListView"/>, and the ListView runs the
/// one sort funnel (raise <see cref="ListView.Sorting"/>, then optionally the built-in comparer sort).
/// </para>
/// </summary>
[TemplatePart(PartSortIndicator, typeof(TextBlock))]
public class ListViewColumnHeader : ButtonBase
{
    private const string PartSortIndicator = "PART_SortIndicator";

    /// <summary>The column this header describes (set by the <see cref="ListViewHeaderPresenter"/> that builds it).</summary>
    public static readonly DirectProperty<ListViewColumnHeader, ListViewColumn?> ColumnProperty =
        UIProperty.RegisterDirect<ListViewColumnHeader, ListViewColumn?>(nameof(Column), static h => h._column, static (h, v) => h.SetColumn(v));

    /// <summary>The sort order this header is displaying (<c>:sort-ascending</c> / <c>:sort-descending</c>).</summary>
    public static readonly DirectProperty<ListViewColumnHeader, ListViewSortDirection> SortDirectionProperty =
        UIProperty.RegisterDirect<ListViewColumnHeader, ListViewSortDirection>(nameof(SortDirection), static h => h._sortDirection, static (h, v) => h.SetSortDirection(v));

    /// <summary>The ascending indicator (default <c>▲</c>). A theme targeting a narrow-glyph terminal can
    /// re-set it to an ASCII caret without retemplating.</summary>
    public static readonly StyledProperty<string?> AscendingGlyphProperty =
        UIProperty.Register<ListViewColumnHeader, string?>(nameof(AscendingGlyph), defaultValue: "▲", changed: OnGlyphChanged);

    /// <summary>The descending indicator (default <c>▼</c>); see <see cref="AscendingGlyphProperty"/>.</summary>
    public static readonly StyledProperty<string?> DescendingGlyphProperty =
        UIProperty.Register<ListViewColumnHeader, string?>(nameof(DescendingGlyph), defaultValue: "▼", changed: OnGlyphChanged);

    private ListViewColumn? _column;
    private ListViewSortDirection _sortDirection;
    private TextBlock? _sortIndicator;

    /// <summary>Creates a column header (not a tab stop — the header strip is chrome, the items are the focus target).</summary>
    public ListViewColumnHeader()
    {
        Focusable = false;
        IsTabStop = false;
    }

    /// <inheritdoc cref="ColumnProperty"/>
    public ListViewColumn? Column { get => _column; set => SetColumn(value); }

    /// <inheritdoc cref="SortDirectionProperty"/>
    public ListViewSortDirection SortDirection { get => _sortDirection; set => SetSortDirection(value); }

    /// <inheritdoc cref="AscendingGlyphProperty"/>
    public string? AscendingGlyph { get => GetValue(AscendingGlyphProperty); set => SetValue(AscendingGlyphProperty, value); }

    /// <inheritdoc cref="DescendingGlyphProperty"/>
    public string? DescendingGlyph { get => GetValue(DescendingGlyphProperty); set => SetValue(DescendingGlyphProperty, value); }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _sortIndicator = GetTemplatePart<TextBlock>(PartSortIndicator);
        PushSortIndicator(); // a fresh template starts blank — push the live state in (SetCurrentValue, so styles survive)
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _sortIndicator = null;
        base.OnTemplateDetaching(old);
    }

    private void SetColumn(ListViewColumn? column)
    {
        if (SetAndRaise(ColumnProperty, ref _column, column))
            SetCurrentValue(ContentProperty, column?.Header);
    }

    private void SetSortDirection(ListViewSortDirection direction)
    {
        if (!SetAndRaise(SortDirectionProperty, ref _sortDirection, direction))
            return;

        // DirectProperty-backed, so the pseudo-classes are set imperatively (cf. ComboBox's :open).
        PseudoClasses.Set(":sort-ascending", direction == ListViewSortDirection.Ascending);
        PseudoClasses.Set(":sort-descending", direction == ListViewSortDirection.Descending);
        PushSortIndicator();
    }

    // The indicator is a plain TextBlock rather than a GlyphPresenter because it carries no themed glyph
    // SET (there is no unchecked/checked pair) — just one of two strings, or nothing at all. Collapsing it
    // (rather than blanking the text) keeps an unsorted header's width free of a phantom indicator cell.
    private void PushSortIndicator()
    {
        if (_sortIndicator is not { } indicator)
            return;

        var glyph = _sortDirection switch
        {
            ListViewSortDirection.Ascending => AscendingGlyph,
            ListViewSortDirection.Descending => DescendingGlyph,
            _ => null
        };

        indicator.SetCurrentValue(TextBlock.TextProperty, glyph);
        indicator.SetCurrentValue(VisibilityProperty, glyph is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed);
    }

    private static void OnGlyphChanged(UIObject sender, string? oldValue, string? newValue)
        => (sender as ListViewColumnHeader)?.PushSortIndicator();
}
