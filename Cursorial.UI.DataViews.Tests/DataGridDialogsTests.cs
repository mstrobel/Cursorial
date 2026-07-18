using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// The filter/formatting dialog suite end-to-end (design doc §9.1 + the mockup's rules-manager /
/// add-edit-rule / Filter Builder / text-editor windows): the expression editor's live validation
/// strip and Apply→<c>Filter</c>+<c>FilterExpressionText</c> round-trip, the Builder's
/// seed-edit-apply loop and its Edit-as-Text hop, the rules manager's list + Enabled toggle
/// (rendering on/off through the real frame loop), and the add-rule dialog creating a DataBar rule
/// end-to-end. Dialogs open via <c>ShowDialogAsync</c> with the frame loop as the pump
/// (RunUntilIdle) — the windowing suite's async-dialog idiom.
/// </summary>
public class DataGridDialogsTests
{
    private sealed class Order(string id, string region, decimal amount) : INotifyPropertyChanged
    {
        private decimal _amount = amount;
        public string Id { get; } = id;
        public string Region { get; } = region;
        public decimal Amount { get => _amount; set => Set(ref _amount, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }

    private static ObservableCollection<Order> SampleOrders() =>
    [
        new("SO-1042", "East", 12450m),
        new("SO-1044", "East", 31900m),
        new("SO-1046", "South", 19800m),
        new("SO-1047", "West", 27300m),
    ];

    private static (UIHeadlessHost Host, DataGrid Grid, ObservableCollection<Order> Source) Show(
        int columns = 80, int rows = 24)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(columns, rows) });
        var source = SampleOrders();
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, source);
    }

    /// <summary>The first (column, row) of <paramref name="text"/> in the composited frame, or null.</summary>
    private static (int X, int Y)? FindText(UIHeadlessHost host, string text, int rows = 24)
    {
        for (int y = 0; y < rows; y++)
        {
            int x = host.GetRowText(y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
                return (x, y);
        }
        return null;
    }

    // ── The expression text editor ────────────────────────────────────────────────────────────────

    [Fact]
    public void Expression_editor_validates_live_and_apply_lands_the_filter()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor;
        Assert.NotNull(editor);

        // Invalid mid-typing text: the strip goes red with a positioned message (the mockup's vstrip).
        editor.TextBox.Text = "[Amount] >";
        host.RunUntilIdle();
        Assert.False(editor.IsValid);
        Assert.StartsWith("✕", editor.ValidationStrip.Text);

        // Completing the expression flips the strip green.
        editor.TextBox.Text = "[Amount] > 20000";
        host.RunUntilIdle();
        Assert.True(editor.IsValid);
        Assert.Equal("✓ Valid boolean expression", editor.ValidationStrip.Text);

        // Apply: the snapshot shrinks to the two matching rows; text + tree stored together (§9.1).
        editor.Apply();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(task.Result);
        Assert.Null(grid.ActiveFilterEditor);
        Assert.Equal(2, grid.Snapshot.Count); // 31900 + 27300
        Assert.Equal("[Amount] > 20000", grid.FilterExpressionText);
        Assert.NotNull(grid.Filter);
    }

    [Fact]
    public void Expression_editor_apply_with_invalid_text_stays_open_and_writes_nothing()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;

        editor.TextBox.Text = "[Nope] = 1"; // unknown field — a semantic (compile-band) diagnostic
        host.RunUntilIdle();
        Assert.False(editor.IsValid);

        editor.Apply(); // vetoed — the dialog must stay open, the grid untouched
        host.RunUntilIdle();
        Assert.False(task.IsCompleted);
        Assert.NotNull(grid.ActiveFilterEditor);
        Assert.Null(grid.Filter);
        Assert.Equal(4, grid.Snapshot.Count);

        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.False(task.Result);
    }

    [Fact]
    public void Expression_editor_inserters_splice_tokens_at_the_caret()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var openTask = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        Assert.Equal(string.Empty, editor.TextBox.Text);

        // Columns ▾ pick: the bracketed field token lands at the caret; the menu face resets.
        editor.ColumnsMenu.SelectedIndex = 1; // Region
        Assert.Equal("[Region]", editor.TextBox.Text);
        Assert.Equal(-1, editor.ColumnsMenu.SelectedIndex);

        // Functions ▾ pick: the call-shaped token continues from the moved caret.
        editor.FunctionsMenu.SelectedIndex = 0; // Contains(
        Assert.Equal("[Region]Contains(", editor.TextBox.Text);
        Assert.Equal(-1, editor.FunctionsMenu.SelectedIndex);

        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(openTask.IsCompleted);
    }

    // ── FilterExpressionText / TryApplyFilterExpression ───────────────────────────────────────────

    [Fact]
    public void FilterExpressionText_round_trips_the_editor_and_applies_programmatically()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.FilterExpressionText = "[Region] = 'East'";
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count); // SO-1042 + SO-1044

        // The editor re-opens seeded with the SOURCE text (the §9.1 retained-text amendment).
        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        Assert.Equal("[Region] = 'East'", editor.TextBox.Text);
        editor.Cancel();
        host.RunUntilIdle();
        Assert.False(task.Result);
        Assert.Equal("[Region] = 'East'", grid.FilterExpressionText); // cancel wrote nothing
    }

    [Fact]
    public void TryApplyFilterExpression_reports_diagnostics_and_applies_nothing_on_failure()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Invalid: diagnostics out, filter + text untouched.
        Assert.False(grid.TryApplyFilterExpression("[Amount] >", out var diagnostics));
        Assert.NotEmpty(diagnostics);
        Assert.Null(grid.Filter);
        Assert.Null(grid.FilterExpressionText);
        host.RunUntilIdle();
        Assert.Equal(4, grid.Snapshot.Count);

        // The property setter rides the same lane: an invalid assignment is a no-op.
        grid.FilterExpressionText = "[Amount] >=";
        Assert.Null(grid.FilterExpressionText);

        // Valid: both sides land together.
        Assert.True(grid.TryApplyFilterExpression("[Amount] >= 25000", out var clean));
        Assert.Empty(clean);
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count);
        Assert.Equal("[Amount] >= 25000", grid.FilterExpressionText);

        // Null clears filter AND text.
        Assert.True(grid.TryApplyFilterExpression(null, out var cleared));
        Assert.Empty(cleared);
        host.RunUntilIdle();
        Assert.Equal(4, grid.Snapshot.Count);
        Assert.Null(grid.FilterExpressionText);

        // A direct Filter write invalidates any stored text (a foreign tree — §9.1).
        Assert.True(grid.TryApplyFilterExpression("[Region] = 'East'", out var ignored));
        Assert.Empty(ignored);
        Assert.NotNull(grid.FilterExpressionText);
        grid.Filter = FilterNode.Condition(grid.Columns[2], FilterOperator.GreaterThan, 0m);
        Assert.Null(grid.FilterExpressionText);
    }

    // ── The Filter Builder ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Builder_seeds_from_a_structural_filter_and_edits_apply()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Amount > 15000 AND Region = East ⇒ SO-1044 only.
        grid.Filter = FilterNode.And(
            FilterNode.Condition(grid.Columns[2], FilterOperator.GreaterThan, 15000m),
            FilterNode.Condition(grid.Columns[1], FilterOperator.Equals, "East"));
        host.RunUntilIdle();
        Assert.Equal(1, grid.Snapshot.Count);

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder;
        Assert.NotNull(builder);

        // Seeded: one And root group over two editable condition rows.
        Assert.Single(builder.GroupRows);
        Assert.Equal(FilterGroupOperator.And, builder.Root.Operator);
        Assert.Equal(2, builder.ConditionRows.Count);
        Assert.Equal("15000", builder.ConditionRows[0].Value.Text);
        Assert.Equal("East", builder.ConditionRows[1].Value.Text);

        // Edit 1 — flip the Region operator through the real combo: = ⇒ <>.
        var regionRow = builder.ConditionRows[1];
        regionRow.Operator.SelectedIndex = 1; // "<>"
        Assert.Equal(FilterOperator.NotEquals, regionRow.Model.Operator);

        // Edit 2 — add a condition (the structural buttons rebuild the rows; re-fetch).
        builder.AddCondition(builder.Root);
        var added = builder.ConditionRows[^1];
        added.Field.SelectedIndex = 2;    // Amount
        added.Operator.SelectedIndex = 2; // "<"
        added.Value.Text = "30000";
        Assert.Equal(FilterOperator.LessThan, added.Model.Operator);
        Assert.Equal("30000", added.Model.ValueText);

        // OK ⇒ Amount > 15000 AND Region <> East AND Amount < 30000 ⇒ SO-1046 + SO-1047.
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(task.Result);
        var applied = Assert.IsType<FilterGroupNode>(grid.Filter);
        Assert.Equal(3, applied.Children.Count);
        Assert.Equal(2, grid.Snapshot.Count);
        Assert.NotNull(FindText(host, "SO-1046"));
        Assert.NotNull(FindText(host, "SO-1047"));
        Assert.Null(FindText(host, "SO-1044"));
    }

    [Fact]
    public void Builder_value_parse_failure_vetoes_ok_on_the_strip()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // The starter row targets Id (string) — retarget to Amount and type garbage.
        var row = builder.ConditionRows[0];
        row.Field.SelectedIndex = 2;    // Amount (decimal)
        row.Operator.SelectedIndex = 4; // ">"
        row.Value.Text = "not-a-number";

        builder.Ok();
        host.RunUntilIdle();
        Assert.False(task.IsCompleted); // vetoed — still open
        Assert.StartsWith("✕", builder.ValidationStrip.Text);
        Assert.Null(grid.Filter);

        builder.Cancel();
        host.RunUntilIdle();
        Assert.False(task.Result);
    }

    [Fact]
    public void Builder_edit_as_text_hop_carries_the_model_text_into_the_editor()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        grid.Filter = FilterNode.Condition(grid.Columns[2], FilterOperator.GreaterThan, 15000m);
        host.RunUntilIdle();

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        builder.RequestEditAsText();
        host.RunUntilIdle();

        // The chain re-opened the EXPRESSION editor seeded from the builder's model (ToText).
        Assert.Null(grid.ActiveFilterBuilder);
        var editor = grid.ActiveFilterEditor;
        Assert.NotNull(editor);
        Assert.Equal("[Amount] > 15000", editor.TextBox.Text);
        Assert.True(editor.IsValid);

        // Applying from the hopped-to editor completes the ORIGINAL builder entry-point task.
        editor.Apply();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(task.Result);
        Assert.Equal("[Amount] > 15000", grid.FilterExpressionText);
        Assert.Equal(3, grid.Snapshot.Count); // 31900 + 19800 + 27300
    }

    // ── The rules manager ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rules_manager_lists_rules_and_the_enabled_toggle_flips_rendering()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var red = Color.FromRgb(247, 118, 142);
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries = [(FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Foreground: red, Bold: true))],
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var hit = FindText(host, "31900");
        Assert.NotNull(hit);
        Assert.Equal(red, host.GetCell(hit.Value.X, hit.Value.Y).Style.Foreground);

        var task = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager;
        Assert.NotNull(manager);

        // The list: column · condition summary · On (checked — Enabled defaults true).
        var row = Assert.Single(manager.Rows);
        Assert.Same(amountColumn, row.Column);
        Assert.Equal(">= 25000", DataGridRulesManager.Summarize(row.Rule));
        Assert.True(row.OnCheck.IsChecked);

        // Toggle Off: Enabled flips and the re-collect drops the rule from the engine push.
        row.OnCheck.IsChecked = false;
        Assert.False(row.Rule.Enabled);
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);

        hit = FindText(host, "31900");
        Assert.NotNull(hit);
        var cell = host.GetCell(hit.Value.X, hit.Value.Y);
        Assert.NotEqual(red, cell.Style.Foreground);
        Assert.True((cell.Style.Attributes & TextAttributes.Bold) == 0);

        // Back On (the rule instance survived — the one mutable knob): the format returns.
        row.Rule.Enabled = true;
        grid.RefreshFormatRules();
        host.RunUntilIdle();
        hit = FindText(host, "31900");
        Assert.NotNull(hit);
        Assert.Equal(red, host.GetCell(hit.Value.X, hit.Value.Y).Style.Foreground);
    }

    [Fact]
    public void Add_rule_dialog_creates_a_data_bar_rule_end_to_end()
    {
        var (host, grid, _) = Show();
        using var _ = host;
        Assert.DoesNotContain("█", host.GetRowText(2));

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        Assert.Empty(manager.Rows);

        var newRuleTask = manager.NewRuleAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor;
        Assert.NotNull(editor);

        // The two-pane dialog: pick the Data Bar type and target the Amount column.
        editor.SetKind(RuleEditorKind.DataBar);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount
        editor.Ok();
        host.RunUntilIdle();
        Assert.True(newRuleTask.IsCompleted);
        Assert.Null(newRuleTask.Exception);

        // The rule landed on the column, the manager re-listed it, and the grid re-inked live.
        var rule = Assert.IsType<DataBarRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Same(grid.Columns[2], rule.ColumnKey);
        var row = Assert.Single(manager.Rows);
        Assert.Same(rule, row.Rule);

        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
        Assert.NotNull(FindText(host, "█")); // the max amount draws a full fill run
    }

    [Fact]
    public void Rules_manager_delete_and_reorder_operate_within_the_column_list()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var amountColumn = grid.Columns[2];
        var first = new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries = [(FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Bold: true))],
        };
        var second = new DataBarRule { ColumnKey = amountColumn };
        amountColumn.FormatRules.Add(first);
        amountColumn.FormatRules.Add(second);
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        Assert.Equal(2, manager.Rows.Count);

        // ▼ on the first rule swaps the pair within Amount's list (priority is per-column).
        manager.Select(0);
        manager.MoveSelected(+1);
        Assert.Same(second, amountColumn.FormatRules[0]);
        Assert.Same(first, amountColumn.FormatRules[1]);
        Assert.Same(second, manager.Rows[0].Rule);

        // ✕ deletes the selection (still the threshold rule — selection followed the move).
        manager.DeleteSelected();
        Assert.Same(second, Assert.Single(amountColumn.FormatRules));
        Assert.Single(manager.Rows);

        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    // ── TopBottom (the §9.5 TopK seam, live) ──────────────────────────────────────────────────────

    [Fact]
    public void TopBottom_rule_evaluates_through_the_TopK_seam_and_tracks_the_filtered_view()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // Top-2 of Amount over the 4 sample orders: 31900 and 27300 qualify (ties include; the
        // threshold is the K-th largest, re-derived per publish through TryGetTopKThreshold).
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new TopBottomRule
        {
            ColumnKey = amountColumn,
            Top = true,
            Count = 2,
            Format = new CellFormat(Bold: true),
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var controller = grid.Controller!;
        Assert.True(controller.HasFormatRules);
        Assert.True(controller.TryGetTopKThreshold(amountColumn, 2, percent: false, top: true, out var threshold));
        Assert.Equal(27300m, threshold);

        CellFormat FormatOf(string id)
        {
            var snapshot = grid.Snapshot;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var row = snapshot.GetRow(i);
                if (!row.IsGroup && controller.FormatCell(row.RowId, grid.Columns[0]) == id)
                    return controller.GetCellFormat(row.RowId, amountColumn);
            }
            throw new InvalidOperationException($"Row {id} not visible.");
        }

        Assert.True(FormatOf("SO-1044").Bold);   // 31900 — in the top 2
        Assert.True(FormatOf("SO-1047").Bold);   // 27300 — the threshold itself (ties include)
        Assert.True(FormatOf("SO-1042").IsEmpty); // 12450 — below
        Assert.True(FormatOf("SO-1046").IsEmpty); // 19800 — below

        // The population is the FILTERED view: excluding the current #1 promotes the next pair —
        // the threshold re-derives on the publish, no recompile (§9.5).
        grid.Filter = FilterNode.Condition(amountColumn, FilterOperator.LessThan, 30000m);
        host.RunUntilIdle();
        Assert.True(controller.TryGetTopKThreshold(amountColumn, 2, percent: false, top: true, out threshold));
        Assert.Equal(19800m, threshold);
        Assert.True(FormatOf("SO-1047").Bold);   // 27300 — still top
        Assert.True(FormatOf("SO-1046").Bold);   // 19800 — promoted into the top 2
        Assert.True(FormatOf("SO-1042").IsEmpty);

        // The rule type is offered live in the add-rule dialog (the seam gate is on).
        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        var listed = Assert.Single(manager.Rows);
        Assert.Equal("Top 2", DataGridRulesManager.Summarize(listed.Rule));

        var newRuleTask = manager.NewRuleAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;
        Assert.True(editor.TypeButtons.First(t => t.Kind == RuleEditorKind.TopBottom).Button.IsEnabled);

        editor.Cancel();
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(newRuleTask.IsCompleted);
        Assert.Null(newRuleTask.Exception);
        Assert.True(managerTask.IsCompleted);
    }

    // ── The disabled-rule collection funnel (engine-facing, no dialog) ────────────────────────────

    [Fact]
    public void Disabled_rules_never_reach_the_engine_push()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var bar = new DataBarRule { ColumnKey = grid.Columns[2], Enabled = false };
        grid.Columns[2].FormatRules.Add(bar);
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        Assert.False(grid.Controller!.HasFormatRules); // the funnel dropped it entirely
        Assert.DoesNotContain("█", host.GetRowText(2));

        bar.Enabled = true;
        grid.RefreshFormatRules();
        host.RunUntilIdle();
        Assert.True(grid.Controller!.HasFormatRules);
        Assert.NotNull(FindText(host, "█"));
    }
}
