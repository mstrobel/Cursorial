using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;

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
    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> BackgroundProperty =
        UIProperty.Register<DataGridAutoFilterRow, Cursorial.Drawing.Media.IBrush?>(nameof(Background));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> TextBrushProperty =
        UIProperty.Register<DataGridAutoFilterRow, Cursorial.Drawing.Media.IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> PlaceholderBrushProperty =
        UIProperty.Register<DataGridAutoFilterRow, Cursorial.Drawing.Media.IBrush?>(nameof(PlaceholderBrush));

    public static readonly StyledProperty<Cursorial.Drawing.Media.IBrush?> WellBackgroundProperty =
        UIProperty.Register<DataGridAutoFilterRow, Cursorial.Drawing.Media.IBrush?>(nameof(WellBackground));

    /// <summary>The shared horizontal offset (the template binds it to the ScrollViewer's — §3.1).</summary>
    public static readonly StyledProperty<int> HorizontalOffsetProperty =
        UIProperty.Register<DataGridAutoFilterRow, int>(nameof(HorizontalOffset));

    static DataGridAutoFilterRow()
    {
        AffectsRender<DataGridAutoFilterRow>(
            BackgroundProperty, TextBrushProperty, PlaceholderBrushProperty, WellBackgroundProperty);
        // The offset re-arranges the hosted editor, not just the ink.
        AffectsMeasure<DataGridAutoFilterRow>(HorizontalOffsetProperty);
    }

    public Cursorial.Drawing.Media.IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Cursorial.Drawing.Media.IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? PlaceholderBrush { get => GetValue(PlaceholderBrushProperty); set => SetValue(PlaceholderBrushProperty, value); }
    public Cursorial.Drawing.Media.IBrush? WellBackground { get => GetValue(WellBackgroundProperty); set => SetValue(WellBackgroundProperty, value); }
    public int HorizontalOffset { get => GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }

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

        if (_editor is not null && EditEntry() is { } entry)
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

        int shift = -HorizontalOffset;
        var entries = layout.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            int x = entry.X + shift;
            int cellWidth = entry.Width + 2 * DataGridColumnLayout.CellPadding;
            if (x + cellWidth <= 0 || x >= Bounds.Columns)
                continue;
            if (i == _editColumnIndex && _editor is not null)
                continue; // the hosted editor paints this cell

            var column = entry.Column;
            if (!column.AllowFilter || column.FilterCellKind == FilterCellKind.Disabled)
                continue;

            string? summary = owner.GetColumnFilterSummary(column);
            int contentX = x + DataGridColumnLayout.CellPadding;

            if (column.FilterCellKind == FilterCellKind.DistinctPicker)
            {
                // "(All) ▾" idle / the active summary in a well-fill (the mockup's picker cells).
                if (summary is not null && WellBackground is not null)
                    context.FillOpaque(new Rect(contentX, 0, entry.Width, 1), WellBackground);
                string text = summary ?? "(All)";
                DrawClipped(context, contentX, text, Math.Max(1, entry.Width - 2),
                            summary is not null ? TextBrush : PlaceholderBrush);
                context.DrawText(x + cellWidth - DataGridColumnLayout.CellPadding - 1, 0, "▾", PlaceholderBrush);
            }
            else if (summary is not null)
            {
                if (WellBackground is not null)
                    context.FillOpaque(new Rect(contentX, 0, entry.Width, 1), WellBackground);
                DrawClipped(context, contentX, summary, entry.Width, TextBrush);
            }
            else
            {
                context.DrawText(contentX, 0, "⌕", PlaceholderBrush); // the idle affordance
            }
        }
    }

    private static void DrawClipped(RenderContext context, int x, string text, int maxWidth,
                                    Cursorial.Drawing.Media.IBrush? brush)
    {
        if (brush is null || text.Length == 0)
            return;
        if (GraphemeWidth.StringWidth(text) <= maxWidth)
        {
            context.DrawText(x, 0, text, brush);
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
        context.DrawText(x, 0, text.AsSpan(0, end), brush);
        context.DrawText(x + width, 0, "…", brush);
    }

    // ── The roving editor (the §3.2 element-hosting idiom, one cell at a time — panel Q4) ────────

    private Controls.TextBox? _editor;
    private int _editColumnIndex = -1;

    /// <summary>Whether the roving editor is hosted (tests + the grid's key routing).</summary>
    internal bool IsEditing => _editor is not null;

    /// <summary>The hosted editor (tests type through it).</summary>
    internal Controls.TextBox? Editor => _editor;

    private DataGridColumnLayout.Entry? EditEntry()
        => Layout is { } layout && _editColumnIndex >= 0 && _editColumnIndex < layout.Entries.Count
            ? layout.Entries[_editColumnIndex]
            : null;

    /// <summary>Hosts the editor at a Text-kind filter cell, seeded with the active condition text.</summary>
    internal void BeginEdit(int columnIndex)
    {
        var owner = _owner;
        var layout = Layout;
        if (owner is null || layout is null || columnIndex < 0 || columnIndex >= layout.Entries.Count)
            return;

        EndEdit();

        var column = layout.Entries[columnIndex].Column;
        var editor = new Controls.TextBox { Text = owner.GetColumnFilterSummary(column) ?? string.Empty };
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
        if (_editor is not null && EditEntry() is { } entry)
        {
            _editor.Arrange(new Rect(entry.X - HorizontalOffset + DataGridColumnLayout.CellPadding, 0,
                                     Math.Max(1, entry.Width), 1));
        }
        return finalSize;
    }

    // ── The operator grammar (§1: "> >= < <= = <>"; bare = contains/equals; empty = clear) ───────

    /// <summary>Parses the condition text: a null Op means bare text (the per-column default op).</summary>
    internal static (FilterOperator? Op, string Literal) ParseCondition(string text)
    {
        text = text.Trim();
        return text switch
        {
            ['>', '=', .. var rest] => (FilterOperator.GreaterThanOrEqual, rest.Trim()),
            ['<', '=', .. var rest] => (FilterOperator.LessThanOrEqual, rest.Trim()),
            ['<', '>', .. var rest] => (FilterOperator.NotEquals, rest.Trim()),
            ['>', .. var rest] => (FilterOperator.GreaterThan, rest.Trim()),
            ['<', .. var rest] => (FilterOperator.LessThan, rest.Trim()),
            ['=', .. var rest] => (FilterOperator.Equals, rest.Trim()),
            _ => (null, text),
        };
    }

    /// <summary>
    /// Parses + validates + writes the editor's condition; false (editor stays open) when the
    /// literal doesn't convert to the column's key type. Empty text clears the column's fragment.
    /// </summary>
    internal bool CommitEdit()
    {
        var owner = _owner;
        if (owner is null || _editor is null || EditEntry() is not { } entry)
            return false;

        var column = entry.Column;
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
        if (e.Handled || e.Button != MouseButton.Left || _owner is null || Layout is not { } layout)
            return;

        var position = e.GetPosition(this);
        int index = layout.EntryAt(position.Column + HorizontalOffset);
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
