using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The direct-draw auto-filter band under the header (design doc §3.4; the mockup's
/// <c>filterrow</c>): per column a drawn condition cell keyed on
/// <see cref="DataGridColumn.FilterCellKind"/> — <c>Text</c> cells show the active condition text
/// in a well-fill (a muted <c>⌕</c> placeholder when idle) and host ONE roving TextBox editor on
/// click (the rows presenter's element-hosting idiom — panel Q4: lightweight cells, one focused
/// editor at a time); <c>DistinctPicker</c> cells draw <c>(All) ▾</c> (or the active summary) and
/// route to the checklist popup; <c>Disabled</c> draws nothing. Enter parses the operator grammar
/// (<c>&gt; &gt;= &lt; &lt;= = &lt;&gt;</c> prefixes; bare text = Contains on string columns /
/// Equals otherwise; empty clears) into a per-column <see cref="FilterNode.Condition"/> fragment —
/// the engine converts literals at compile, and the fragment is validated
/// (<see cref="DataViewController.CanCompileFilter"/>) BEFORE the write so a bad literal keeps the
/// editor open instead of throwing inside the posted shape push. Its own render boundary
/// (ClipToBounds, §3.1); mirrors the shared horizontal offset; collapses to zero rows while
/// <see cref="DataGrid.ShowAutoFilterRow"/> is off.
/// </summary>
public sealed class DataGridAutoFilterRow : UIElement
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridAutoFilterRow, IBrush?>(
            nameof(Background),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.SurfaceBrush });

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridAutoFilterRow, IBrush?>(
            nameof(TextBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.TextBrush });

    public static readonly StyledProperty<IBrush?> PlaceholderBrushProperty =
        UIProperty.Register<DataGridAutoFilterRow, IBrush?>(
            nameof(PlaceholderBrush),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.MutedBrush });

    public static readonly StyledProperty<IBrush?> WellBackgroundProperty =
        UIProperty.Register<DataGridAutoFilterRow, IBrush?>(
            nameof(WellBackground),
            new PropertyMetadata<IBrush?> { DefaultResourceKey = ThemeKeys.WellBrush });

    /// <summary>The shared horizontal offset (the template binds it to the ScrollViewer's — §3.1).</summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<DataGridAutoFilterRow, int>(nameof(HorizontalOffset));

    static DataGridAutoFilterRow()
    {
        // The offset is in BOTH effect sets (audit W2-0): AffectsRender re-inks the drawn filter
        // cells per H-tick (header/footer parity — AffectsMeasure alone re-ran measure into
        // unchanged bounds and never invalidated the band's cached raster, so the cells rendered
        // at stale x until an unrelated change), and AffectsMeasure re-arranges the hosted editor.
        AffectsRender<DataGridAutoFilterRow>(
            BackgroundProperty, TextBrushProperty, PlaceholderBrushProperty, WellBackgroundProperty,
            HorizontalOffsetProperty);

        AffectsMeasure<DataGridAutoFilterRow>(HorizontalOffsetProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? PlaceholderBrush
    {
        get => GetValue(PlaceholderBrushProperty);
        set => SetValue(PlaceholderBrushProperty, value);
    }

    public IBrush? WellBackground
    {
        get => GetValue(WellBackgroundProperty);
        set => SetValue(WellBackgroundProperty, value);
    }

    public int HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, value);
    }

    private DataGrid? _owner;

    /// <summary>The owning grid (stamped when the template applies).</summary>
    internal DataGrid? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
                return;

            if (_owner is not null)
                _owner.SnapshotChanged -= OnSnapshotChanged;

            _owner = value;

            if (_owner is not null)
                _owner.SnapshotChanged += OnSnapshotChanged;

            ClipToBounds = true; // own boundary — band-local re-ink (§3.1; safe: a 1-row band)
            InvalidateMeasure();
        }
    }

    // Column widths track the band's Auto sizing — every publish may re-resolve the layout.
    private void OnSnapshotChanged(object? sender, EventArgs e) => InvalidateVisual();

    private DataGridColumnLayout? Layout => _owner?.RowsPresenter?.ColumnLayout;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_owner is not { ShowAutoFilterRow: true })
            return new Size(availableSize.Columns, 0); // collapsed — no row spent

        if (_editor is not null && EditEntry() is {} entry)
            _editor.Measure(new Size(Math.Max(1, entry.Width), 1));

        return new Size(availableSize.Columns, 1);
    }

    protected override void Render(RenderContext context)
    {
        base.Render(context);
        var owner = _owner;
        var layout = Layout;

        if (owner is null || layout is null || Bounds.Rows < 1)
            return;

        if (Background is not null)
            context.FillOpaque(new Rect(0, 0, Bounds.Columns, 1), Background);

        // §9.2 paint order (the rows presenter's mirror): scrolling cells first (shifted), then the
        // frozen region re-fills its background and draws its cells unshifted on top.
        var entries = layout.Entries;

        for (int i = layout.FrozenCount; i < entries.Count; i++)
            DrawFilterCell(context, owner, layout, i);

        if (layout.FrozenWidth >
            0) // width, not count — the §9.3 gutter is pinned even with no Fixed column (audit W2-2)
        {
            if (Background is not null && HorizontalOffset > 0)
                context.FillOpaque(new Rect(0, 0, layout.FrozenWidth, 1), Background);

            for (int i = 0; i < layout.FrozenCount; i++)
                DrawFilterCell(context, owner, layout, i);
        }
    }

    /// <summary>One filter cell at its §9.2 draw position (picker / condition summary / idle ⌕).</summary>
    private void DrawFilterCell(RenderContext context, DataGrid owner, DataGridColumnLayout layout, int i)
    {
        var entry = layout.Entries[i];
        int x = DrawXOf(layout, i);
        int cellWidth = entry.Width + 2 * DataGridColumnLayout.CellPadding;
        int leftEdge = i < layout.FrozenCount ? 0 : layout.FrozenWidth;

        if (x + cellWidth <= leftEdge || x >= Bounds.Columns)
            return;

        if (i == _editColumnIndex && _editor is not null)
            return; // the hosted editor paints this cell

        // The §3.3 virtual band focus: the focused filter cell wears the well fill (drawn before
        // the kind branches so the walk stays visible even over disabled cells). NoColor tier: the
        // well brush resolves to Default (invisible) — a reverse-video bar carries the cue instead,
        // and the (now-invisible) well fills below are skipped so they don't clobber the bar (§4).
        bool noColor = context.Capabilities.Color.Depth == ColorDepth.NoColor;
        bool focusCue = noColor && i == owner.FilterCellFocusIndex;

        if (i == owner.FilterCellFocusIndex && WellBackground is not null && !noColor)
            context.FillOpaque(new Rect(x + DataGridColumnLayout.CellPadding, 0, entry.Width, 1), WellBackground);
        else if (focusCue)
            FillInverse(context, x + DataGridColumnLayout.CellPadding, entry.Width);

        var column = entry.Column;

        if (!column.AllowFilter || column.FilterCellKind == FilterCellKind.Disabled)
            return;

        string? summary = owner.GetColumnFilterSummary(column);
        int contentX = x + DataGridColumnLayout.CellPadding;

        var placeholderBrush = PlaceholderBrush ?? Brushes.Default;
        CellStyle contentStyle = focusCue ? default(CellStyle).WithAttributes(TextAttributes.Inverse) : default;

        if (column.FilterCellKind == FilterCellKind.DistinctPicker)
        {
            // "(All) ▾" idle / the active summary in a well-fill (the mockup's picker cells).
            if (summary is not null && WellBackground is not null && !noColor)
                context.FillOpaque(new Rect(contentX, 0, entry.Width, 1), WellBackground);

            string text = summary ?? "(All)";

            DrawClipped(context, contentX, text, Math.Max(1, entry.Width - 2),
                        summary is not null ? TextBrush : PlaceholderBrush, contentStyle);

            context.DrawText(x + cellWidth - DataGridColumnLayout.CellPadding - 1, 0, "▾", placeholderBrush, null, contentStyle);
        }
        else if (summary is not null)
        {
            if (WellBackground is not null && !noColor)
                context.FillOpaque(new Rect(contentX, 0, entry.Width, 1), WellBackground);

            DrawClipped(context, contentX, summary, entry.Width, TextBrush, contentStyle);
        }
        else
        {
            context.DrawText(contentX, 0, "⌕", placeholderBrush, null, contentStyle); // the idle affordance
        }
    }

    /// <summary>Reverse-video bar of <paramref name="width"/> space-bearing cells at row 0 — the
    /// NoColor filter-cell focus fill (SGR 7 swaps the terminal's real default fg/bg; §4).</summary>
    private void FillInverse(RenderContext context, int x, int width)
    {
        if (width <= 0)
            return;

        context.FillOpaque(new Rect(x, 0, width, 1), TextBrush ?? Brushes.Default, TextAttributes.Inverse);
    }

    /// <summary>A layout entry's painted x (§9.2 — frozen entries never shift).</summary>
    private int DrawXOf(DataGridColumnLayout layout, int index)
    {
        var entry = layout.Entries[index];
        return index < layout.FrozenCount ? entry.X : entry.X - HorizontalOffset;
    }

    private static void DrawClipped(RenderContext context, int x, string text, int maxWidth,
                                    IBrush? brush, CellStyle style = default)
    {
        if (brush is null || text.Length == 0)
            return;

        if (GraphemeWidth.StringWidth(text) <= maxWidth)
        {
            context.DrawText(x, 0, text, brush, null, style);
            return;
        }

        var enumerator = text.GetGraphemeEnumerator();
        int width = 0, end = 0;

        while (enumerator.MoveNext())
        {
            int next = width + GraphemeWidth.ClusterWidth(enumerator.Current);

            if (next > maxWidth - 1)
                break;

            width = next;
            end = enumerator.ElementIndex + enumerator.Current.Length;
        }

        context.DrawText(x, 0, text.AsSpan(0, end), brush, null, style);
        context.DrawText(x + width, 0, "…", brush, null, style);
    }

    // ── The roving editor (the §3.2 element-hosting idiom, one cell at a time — panel Q4) ────────

    private Controls.TextBox? _editor;
    private int _editColumnIndex = -1;
    private bool _editTextTouched;

    /// <summary>Whether the roving editor is hosted (tests + the grid's key routing).</summary>
    internal bool IsEditing => _editor is not null;

    /// <summary>The hosted editor (tests type through it).</summary>
    internal Controls.TextBox? Editor => _editor;

    /// <summary>The edited entry index while editing (the grid's §9.2 H-scroll policy reads it).</summary>
    internal int EditColumnIndex => _editColumnIndex;

    private DataGridColumnLayout.Entry? EditEntry()
        => Layout is {} layout && _editColumnIndex >= 0 && _editColumnIndex < layout.Entries.Count
               ? layout.Entries[_editColumnIndex]
               : null;

    /// <summary>
    /// Hosts the editor at a Text-kind filter cell, seeded with the active condition text when the
    /// stored fragment is a grammar <see cref="FilterConditionNode"/> (any other fragment — a
    /// checklist InSet, a predicate — stores a display digest, not grammar text, and seeds empty).
    /// Scrolls the cell clear of the frozen region first (§9.2 — the hosted-children policy).
    /// </summary>
    internal void BeginEdit(int columnIndex)
    {
        var owner = _owner;
        var layout = Layout;

        if (owner is null || layout is null || columnIndex < 0 || columnIndex >= layout.Entries.Count)
            return;

        EndEdit();

        // §9.2 (the hosted-children policy): the mouse path must land the editor clear of the
        // frozen region, exactly like the keyboard path (EnterBand/Left/Right) and the rows
        // editor (DataGrid.BeginEdit) already do. Scrolled BEFORE hosting so the offset tick's
        // own commit policy never sees a half-hosted editor.
        owner.ScrollColumnIntoView(columnIndex);

        var column = layout.Entries[columnIndex].Column;

        // Seed only text that round-trips the operator grammar: a Condition fragment's summary IS
        // its typed condition text, but a checklist InSet (or any other node) stores a display
        // digest like "(2)" — re-committing that as Contains("(2)") would destroy the filter.
        string seed = owner.GetColumnFilter(column) is FilterConditionNode
                          ? owner.GetColumnFilterSummary(column) ?? string.Empty
                          : string.Empty;

        var editor = new Controls.TextBox { Text = seed };
        // Untouched-commit tracking: only actually-typed text writes (CommitEdit's dismiss
        // contract below). Subscribed after the seed is set, so the seed itself never trips it.
        _editTextTouched = false;
        editor.TextChanged += (_, _) => _editTextTouched = true;
        // Pin the editor to its cell slot (the rows presenter's idiom): the TextBox theme's own
        // MinWidth would inflate DesiredSize past a narrow filter cell and arrange grows to
        // desired — the editor would paint over the neighboring cells. Min beats Max (LD1), so
        // both bounds stamp locally.
        editor.SetValue(MinWidthProperty, 1);
        editor.SetValue(MaxWidthProperty, Math.Max(1, layout.Entries[columnIndex].Width));
        _editor = editor;
        _editColumnIndex = columnIndex;
        AdoptChild(editor, index: -1);
        InvalidateMeasure();
        InvalidateVisual();

        // Focus after the editor materializes (measure/arrange run first) — the parked-work idiom.
        UIApplication.Current?.Dispatcher.Post(() =>
                                               {
                                                   if (_editor == editor)
                                                   {
                                                       editor.Focus(FocusNavigationMethod.Programmatic);
                                                       editor.SelectAll();
                                                   }
                                               });
    }

    /// <summary>Tears the editor down and returns focus to the grid.</summary>
    internal void EndEdit()
    {
        if (_editor is null)
            return;

        DisownChild(_editor);
        _editor = null;
        _editColumnIndex = -1;
        InvalidateMeasure();
        InvalidateVisual();
        _owner?.Focus(FocusNavigationMethod.Programmatic);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // No base.ArrangeOverride chain: the UIElement default re-arranges every visual child to
        // the full finalSize, which would stretch the roving editor across the whole band and
        // paint out the other filter cells (the rows presenter's latent v1 arrange bug, same fix).
        if (_editor is not null && Layout is {} layout && EditEntry() is {} entry)
        {
            _editor.Arrange(new Rect(DrawXOf(layout, _editColumnIndex) + DataGridColumnLayout.CellPadding, 0,
                                     Math.Max(1, entry.Width), 1));
        }

        return finalSize;
    }

    // ── The operator grammar (§1, live-canary-extended: "> >= < <= = <> != !" prefixes plus the
    //    %-wildcard forms; bare = contains/equals; empty = clear) ────────────────────────────────

    /// <summary>
    /// Parses the condition text: a null Op means bare text (the per-column default op). Prefix
    /// operators: <c>&gt; &gt;= &lt; &lt;= = &lt;&gt;</c> plus <c>!=</c>/<c>!</c> as not-equal
    /// aliases (the numeric-field muscle memory). Wildcard forms on otherwise-bare text:
    /// <c>xyz%</c> → starts-with, <c>%xyz</c> → ends-with, <c>%xy%</c> → contains (edge wildcards
    /// only — inner <c>%</c> stays literal; a lone <c>%</c> is bare text).
    /// </summary>
    internal static (FilterOperator? Op, string Literal) ParseCondition(string text)
    {
        text = text.Trim();

        return text switch
               {
                   ['>', '=', .. var rest] => (FilterOperator.GreaterThanOrEqual, rest.Trim()),
                   ['<', '=', .. var rest] => (FilterOperator.LessThanOrEqual, rest.Trim()),
                   ['<', '>', .. var rest] => (FilterOperator.NotEquals, rest.Trim()),
                   ['!', '=', .. var rest] => (FilterOperator.NotEquals, rest.Trim()),
                   ['!', .. var rest]      => (FilterOperator.NotEquals, rest.Trim()),
                   ['>', .. var rest]      => (FilterOperator.GreaterThan, rest.Trim()),
                   ['<', .. var rest]      => (FilterOperator.LessThan, rest.Trim()),
                   ['=', .. var rest]      => (FilterOperator.Equals, rest.Trim()),
                   _                       => ParseWildcards(text),
               };
    }

    private static (FilterOperator? Op, string Literal) ParseWildcards(string text)
    {
        if (text is ['%', _, _, ..] && text[^1] == '%')
            return (FilterOperator.Contains, text[1..^1]);

        if (text is [.., _, '%'])
            return (FilterOperator.StartsWith, text[..^1]);

        if (text is ['%', _, ..])
            return (FilterOperator.EndsWith, text[1..]);

        return (null, text);
    }

    /// <summary>
    /// Parses + validates + writes the editor's condition; false (editor stays open) when the
    /// literal doesn't convert to the column's key type. Empty text clears the column's fragment.
    /// Exception: when the STORED fragment is non-grammar (a checklist InSet, a predicate — its
    /// summary is a display digest, so <see cref="BeginEdit"/> seeded empty), an UNTOUCHED commit
    /// is a pure dismiss — close, keep the fragment — so Enter on a checklist-filtered cell never
    /// silently clears the checklist filter; only actually-typed text replaces or clears it.
    /// </summary>
    internal bool CommitEdit()
    {
        var owner = _owner;

        if (owner is null || _editor is null || EditEntry() is not {} entry)
            return false;

        var column = entry.Column;

        if (!_editTextTouched && owner.GetColumnFilter(column) is not (null or FilterConditionNode))
        {
            // The untouched-dismiss lane (finding [13]): the seed was empty because the fragment
            // doesn't round-trip the grammar — the empty-commit-clears contract below must not
            // fire from a seed the user never typed. A deliberate wipe DID change the text, so a
            // real clear still works; a grammar Condition (or no filter) keeps the classic
            // contract, its own condition text having been the seed.
            EndEdit();
            return true;
        }

        string text = _editor.Text.Trim();

        if (text.Length == 0)
        {
            owner.SetColumnFilter(column, null);
            EndEdit();
            return true;
        }

        var (op, literal) = ParseCondition(text);

        if (literal.Length == 0)
            return false; // a bare operator ("<", ">=") is incomplete — keep editing

        // Bare text: Contains on string columns, Equals otherwise (§1's grammar default).
        var effectiveOp = op ?? (owner.Controller?.GetColumnKeyType(column) == typeof(string)
                                     ? FilterOperator.Contains
                                     : FilterOperator.Equals);

        var fragment = FilterNode.Condition(column, effectiveOp, literal);

        if (owner.Controller?.CanCompileFilter(fragment) != true)
            return false; // unconvertible literal — the editor stays open for correction

        owner.SetColumnFilter(column, fragment, summary: text);
        EndEdit();
        return true;
    }

    // ── Input (the band is the hit leaf; the editor bubbles its keys through us) ──────────────────

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Handled || e.Button != MouseButton.Left || _owner is null || Layout is not {} layout)
            return;

        var position = e.GetPosition(this);

        int contentX = position.Column < layout.FrozenWidth
                           ? position.Column
                           : position.Column + HorizontalOffset; // the §9.2 split map

        int index = layout.EntryAt(contentX);

        if (index < 0)
            return;

        var column = layout.Entries[index].Column;

        if (!column.AllowFilter)
            return;

        switch (column.FilterCellKind)
        {
            case FilterCellKind.Text:
                BeginEdit(index);
                e.Handled = true;
                break;

            case FilterCellKind.DistinctPicker:
                _owner.OpenFilterPopup(column); // the checklist anchored to the column (panel Q4)
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || _editor is null)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                CommitEdit(); // an invalid literal keeps the editor open (the edit-commit contract)
                e.Handled = true;
                break;

            case Key.Escape:
                EndEdit();
                e.Handled = true;
                break;
        }
    }
}