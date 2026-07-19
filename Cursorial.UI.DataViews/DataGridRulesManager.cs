using System.Diagnostics.CodeAnalysis;

using Cursorial.Drawing.Media;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The conditional-formatting rules manager (design doc §2.7 UI; the mockup's <c>cfmgr</c>): a
/// modal window listing every <see cref="DataGridColumn.FormatRules"/> entry across the grid —
/// column · condition summary · a live mini style preview · the On checkbox — under a toolbar
/// (＋ New Rule / ✎ Edit / ✕ Delete / ▲▼ reorder-within-column). Edits apply LIVE (the mockup:
/// "this is the source of truth for the formatting seen in the grids above") — every mutation
/// funnels through <see cref="DataGrid.RefreshFormatRules"/>, and the On toggle flips
/// <see cref="FormatRule.Enabled"/> (the grid's collection funnel excludes disabled rules from the
/// engine push). ＋/✎ open the two-pane <see cref="DataGridRuleEditor"/> stacked modally above.
/// </summary>
[RequiresDynamicCode("The rule editor compiles expression rules against the grid's row type.")]
internal sealed class DataGridRulesManager
{
    /// <summary>One listed rule's live pieces (tests toggle/select through the real controls).</summary>
    internal sealed record RuleRow(DataGridColumn Column, FormatRule Rule, Border RowElement, CheckBox OnCheck);

    private readonly DataGrid _grid;
    private readonly Window _window;
    private readonly StackPanel _rowsHost = new();
    private readonly List<RuleRow> _rows = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Non-null when this instance is a duplicate-open rider adopting the LIVE dialog.</summary>
    private readonly DataGridRulesManager? _live;
    private DataGridRuleEditor? _activeRuleEditor;
    private int _selectedIndex = -1;

    public DataGridRulesManager(DataGrid grid)
    {
        _grid = grid;

        if (grid.ActiveRulesManager is { } open)
        {
            // Already-open guard (§2.7 dialogs): the fire-and-forget entry point can run twice in
            // one input batch (a double-click posts two opens before the first modal blocks its
            // trigger). The duplicate never builds a second window — it ADOPTS the live manager
            // (same window, same row list), so the grid's tracking handle (re-stamped onto the
            // duplicate) still reaches the ONE real dialog, and ShowAsync rides its close.
            var live = open._live ?? open;
            _live = live;
            _window = live._window;
            _rowsHost = live._rowsHost;
            _rows = live._rows;
            return;
        }

        var content = new StackPanel(); // vertical

        // The toolbar band (the mockup's ＋ ✎ ✕ ▲▼ strip).
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        var newRule = new Button { Content = "＋ New Rule" };
        newRule.Click += (_, _) => _ = NewRuleAsync();
        var edit = new Button { Content = "✎ Edit" };
        edit.Click += (_, _) => _ = EditSelectedAsync();
        var delete = new Button { Content = "✕ Delete" };
        delete.Click += (_, _) => DeleteSelected();
        var up = new Button { Content = "▲" };
        up.Click += (_, _) => MoveSelected(-1);
        var down = new Button { Content = "▼" };
        down.Click += (_, _) => MoveSelected(+1);
        toolbar.Children.Add(newRule);
        toolbar.Children.Add(edit);
        toolbar.Children.Add(delete);
        toolbar.Children.Add(up);
        toolbar.Children.Add(down);
        content.Children.Add(toolbar);

        // The header strip (fixed cell widths shared with the rows below).
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(HeaderCell("Column", ColumnWidth));
        header.Children.Add(HeaderCell("Condition", ConditionWidth));
        header.Children.Add(HeaderCell("Format", FormatWidth));
        header.Children.Add(HeaderCell("On", 4));
        content.Children.Add(header);

        content.Children.Add(new ScrollViewer
        {
            Content = _rowsHost,
            MaxHeight = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        _window = DataGridDialogHelpers.CreateDialogWindow("Conditional Formatting Rules Manager", content,
            ("Close", () => _window?.Close(true)));

        RefreshRows();
    }

    private const int ColumnWidth = 10;
    private const int ConditionWidth = 22;
    private const int FormatWidth = 14;

    /// <summary>Whether the manager is currently shown (the grid's test hook gates on it).</summary>
    internal bool IsOpen => _window.IsShown;

    /// <summary>The listed rules, display order (rebuilt on every mutation).</summary>
    internal IReadOnlyList<RuleRow> Rows => _rows;

    /// <summary>The selected list index (−1 none).</summary>
    internal int SelectedIndex => (_live ?? this)._selectedIndex;

    /// <summary>The live add/edit dialog (null while none is open; tests reach its panes).</summary>
    internal DataGridRuleEditor? ActiveRuleEditor => (_live ?? this)._activeRuleEditor;

    internal async Task ShowAsync()
    {
        if (_live is { } live)
        {
            // A duplicate open rides the live manager and completes when IT closes.
            await live._closed.Task;
            return;
        }

        try
        {
            _ = await _window.ShowDialogAsync();
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    // ── The rule list ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the rows from the columns' rule lists (the chooser's rebuild-per-mutation idiom).</summary>
    internal void RefreshRows()
    {
        if (_live is { } live)
        {
            live.RefreshRows();
            return;
        }

        _rows.Clear();
        _rowsHost.Children.Clear();

        foreach (var column in _grid.Columns)
        {
            foreach (var rule in column.FormatRules)
            {
                int index = _rows.Count;
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(Cell(column.EffectiveHeader, ColumnWidth));
                line.Children.Add(Cell(Summarize(rule), ConditionWidth));

                var previewBorder = BuildPreview(rule);
                previewBorder.Width = FormatWidth;
                line.Children.Add(previewBorder);

                var on = new CheckBox { IsChecked = rule.Enabled };
                var capturedRule = rule;
                // On IsCheckedChanged (not Click) deliberately: the one mutable knob
                // (FormatRule.Enabled) must follow the CHECK STATE however it flips — pointer,
                // Space, or a programmatic set — and re-collect; the grid's funnel drops disabled
                // rules from the engine push, so the band re-inks bare.
                on.AddHandler(ToggleButton.IsCheckedChangedEvent, (object? _, RoutedEventArgs _) =>
                {
                    capturedRule.Enabled = on.IsChecked == true;
                    _grid.RefreshFormatRules();
                });
                line.Children.Add(on);

                var row = new Border { Child = line };
                int capturedIndex = index;
                row.AddHandler(UIElement.MouseDownEvent, (object? _, MouseButtonEventArgs e) =>
                {
                    Select(capturedIndex);
                    e.Handled = true;
                });

                _rows.Add(new RuleRow(column, rule, row, on));
                _rowsHost.Children.Add(row);
            }
        }

        if (_selectedIndex >= _rows.Count)
            _selectedIndex = _rows.Count - 1;
        ApplySelectionLook();
    }

    /// <summary>Selects a listed rule (the ✎/✕/▲▼ target).</summary>
    internal void Select(int index)
    {
        if (_live is { } live)
        {
            live.Select(index);
            return;
        }

        _selectedIndex = index >= 0 && index < _rows.Count ? index : -1;
        ApplySelectionLook();
    }

    private void ApplySelectionLook()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i == _selectedIndex)
                _rows[i].RowElement.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SelectionBrush);
            else
                _rows[i].RowElement.ClearValue(Border.BackgroundProperty);
        }
    }

    private static TextBlock HeaderCell(string text, int width)
    {
        var block = new TextBlock { Text = text, Width = width };
        block.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        return block;
    }

    private static TextBlock Cell(string text, int width)
    {
        var block = new TextBlock { Text = text, Width = width };
        block.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
        return block;
    }

    /// <summary>The condition-summary column's text for a rule (the mockup's middle cell).</summary>
    internal static string Summarize(FormatRule rule) => rule switch
    {
        DataBarRule => "Data bar (min→max)",
        ColorScaleRule scale => $"Color scale ({scale.Stops.Count}-stop)",
        ThresholdRule { Entries.Count: > 0 } threshold =>
            $"{OperatorGlyph(threshold.Entries[0].Operator)} {DataGridDialogHelpers.FormatLiteral(threshold.Entries[0].Value)}" +
            (threshold.Entries.Count > 1 ? $" (+{threshold.Entries.Count - 1})" : string.Empty),
        PredicateRule => "ƒ expression (row)",
        TopBottomRule topBottom => $"{(topBottom.Top ? "Top" : "Bottom")} {topBottom.Count}{(topBottom.Percent ? "%" : string.Empty)}",
        _ => rule.GetType().Name,
    };

    private static string OperatorGlyph(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.NotEquals => "<>",
        FilterOperator.LessThan => "<",
        FilterOperator.LessThanOrEqual => "<=",
        FilterOperator.GreaterThan => ">",
        FilterOperator.GreaterThanOrEqual => ">=",
        FilterOperator.Contains => "contains",
        FilterOperator.StartsWith => "starts with",
        FilterOperator.EndsWith => "ends with",
        FilterOperator.Between => "between",
        _ => "?",
    };

    /// <summary>The live mini style preview (bar/scale glyphs, or a swatch wearing the rule's format).</summary>
    private static Border BuildPreview(FormatRule rule)
    {
        var text = new TextBlock();
        var host = new Border { Child = text };

        CellFormat format = default;
        switch (rule)
        {
            case DataBarRule:
                text.Text = "████░░ bar";
                text.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.CoolBrush);
                return host;
            case ColorScaleRule scale:
                text.Text = "▒▒▒ scale";
                text.Foreground = new SolidColorBrush(scale.Stops[^1]);
                return host;
            case ThresholdRule { Entries.Count: > 0 } threshold:
                format = threshold.Entries[0].Format;
                break;
            case PredicateRule predicate:
                format = predicate.Format;
                break;
            case TopBottomRule topBottom:
                format = topBottom.Format;
                break;
        }

        text.Text = "AaBb text";
        if (format.Foreground is { } fg)
            text.Foreground = new SolidColorBrush(fg);
        if (format.Background is { } bg)
            host.Background = new SolidColorBrush(bg);
        if (format.Bold)
            text.TextWeight = TextWeight.Bold;
        return host;
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ＋ New Rule: the two-pane editor; OK lands the rule on its target column's list. Routed
    /// through <c>Dispatcher.InvokeAsync</c> (the grid entry points' pattern) so the post-dialog
    /// mutation always resumes under the UI synchronization context — never on a pool thread.
    /// </summary>
    internal Task NewRuleAsync()
        => UIApplication.Current is { } application
            ? application.Dispatcher.InvokeAsync(NewRuleCoreAsync)
            : NewRuleCoreAsync();

    private async Task NewRuleCoreAsync()
    {
        if (_live is { } live)
        {
            await live.NewRuleCoreAsync();
            return;
        }
        if (ActiveRuleEditor is { IsOpen: true })
            return; // double-activation in one input batch — the first editor is already up

        var seedColumn = _selectedIndex >= 0 ? _rows[_selectedIndex].Column : _grid.Columns.FirstOrDefault();
        var editor = new DataGridRuleEditor(_grid, seed: null, seedColumn);
        _activeRuleEditor = editor;
        try
        {
            if (await editor.ShowAsync() && editor is { Result: { } rule, TargetColumn: { } column })
            {
                column.FormatRules.Add(rule);
                _grid.RefreshFormatRules();
                RefreshRows();
                Select(_rows.FindIndex(r => ReferenceEquals(r.Rule, rule)));
            }
        }
        finally
        {
            // Guarded (the grid entry points' pattern): never null out a LATER editor's tracking
            // from an earlier core's finally.
            if (ReferenceEquals(_activeRuleEditor, editor))
                _activeRuleEditor = null;
        }
    }

    /// <summary>✎ Edit: re-open the editor seeded from the selection; OK replaces in place.</summary>
    internal Task EditSelectedAsync()
        => UIApplication.Current is { } application
            ? application.Dispatcher.InvokeAsync(EditSelectedCoreAsync)
            : EditSelectedCoreAsync();

    private async Task EditSelectedCoreAsync()
    {
        if (_live is { } live)
        {
            await live.EditSelectedCoreAsync();
            return;
        }
        if (ActiveRuleEditor is { IsOpen: true })
            return; // double-activation in one input batch — the first editor is already up
        if (_selectedIndex < 0 || _selectedIndex >= _rows.Count)
            return;

        var (column, rule, _, _) = _rows[_selectedIndex];
        var editor = new DataGridRuleEditor(_grid, rule, column);
        _activeRuleEditor = editor;
        try
        {
            if (await editor.ShowAsync() && editor is { Result: { } replacement, TargetColumn: { } target })
            {
                replacement.Enabled = rule.Enabled; // the On state survives an edit
                int at = column.FormatRules.IndexOf(rule);
                if (ReferenceEquals(target, column) && at >= 0)
                {
                    column.FormatRules[at] = replacement; // same column: keep the priority slot
                }
                else
                {
                    if (at >= 0)
                        column.FormatRules.RemoveAt(at);
                    target.FormatRules.Add(replacement); // re-targeted: append on the new column
                }
                _grid.RefreshFormatRules();
                RefreshRows();
                Select(_rows.FindIndex(r => ReferenceEquals(r.Rule, replacement)));
            }
        }
        finally
        {
            if (ReferenceEquals(_activeRuleEditor, editor))
                _activeRuleEditor = null;
        }
    }

    /// <summary>✕ Delete: removes the selected rule.</summary>
    internal void DeleteSelected()
    {
        if (_live is { } live)
        {
            live.DeleteSelected();
            return;
        }
        if (_selectedIndex < 0 || _selectedIndex >= _rows.Count)
            return;

        var (column, rule, _, _) = _rows[_selectedIndex];
        column.FormatRules.Remove(rule);
        _grid.RefreshFormatRules();
        RefreshRows();
    }

    /// <summary>
    /// ▲/▼: reorders the selected rule WITHIN its column's list (priority is per-column — first
    /// match wins in the engine's threshold walk; cross-column order carries no meaning).
    /// </summary>
    internal void MoveSelected(int delta)
    {
        if (_live is { } live)
        {
            live.MoveSelected(delta);
            return;
        }
        if (_selectedIndex < 0 || _selectedIndex >= _rows.Count)
            return;

        var (column, rule, _, _) = _rows[_selectedIndex];
        int at = column.FormatRules.IndexOf(rule);
        int to = at + delta;
        if (at < 0 || to < 0 || to >= column.FormatRules.Count)
            return;

        (column.FormatRules[at], column.FormatRules[to]) = (column.FormatRules[to], column.FormatRules[at]);
        _grid.RefreshFormatRules();
        RefreshRows();
        Select(_rows.FindIndex(r => ReferenceEquals(r.Rule, rule)));
    }

    /// <summary>The teardown funnel.</summary>
    internal void CloseWindow()
    {
        ActiveRuleEditor?.CloseWindow();
        if (_window.IsShown)
            _window.Close(false);
    }
}
