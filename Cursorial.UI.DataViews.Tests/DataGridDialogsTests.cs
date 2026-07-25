using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

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
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    [SuppressMessage("ReSharper", "RedundantAssignment")]
    private sealed class Order(string id, string region, decimal amount) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Region { get; } = region;
        public decimal Amount { get; set => Set(ref field, value); } = amount;

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

    /// <summary>The index of an operator label in a rule-editor operator combo (label-based so the
    /// Between insertion — or any future reorder — never re-breaks index-hardcoded tests).</summary>
    private static int OperatorIndex(DataGridRuleEditor editor, string label)
    {
        var items = ((System.Collections.IEnumerable)editor.OperatorCombo!.ItemsSource!).Cast<string>().ToList();
        int i = items.IndexOf(label);
        Assert.True(i >= 0, $"operator '{label}' present");
        return i;
    }

    /// <summary>Opens the rules manager and a fresh ＋ New Rule editor (the §10.4/.5/.6 editor tests' setup).</summary>
    private static DataGridRuleEditor OpenNewRuleEditor(DataGrid grid, UIHeadlessHost host)
    {
        _ = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        _ = manager.NewRuleAsync();
        host.RunUntilIdle();
        return manager.ActiveRuleEditor!;
    }

    /// <summary>Closes the open rules manager (the editor tests' teardown).</summary>
    private static void CloseManager(DataGrid grid, UIHeadlessHost host)
    {
        grid.ActiveRulesManager?.CloseWindow();
        host.RunUntilIdle();
    }

    // ReSharper disable once UnusedTupleComponentInReturnValue
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
    public void Completion_popup_suggests_fields_and_accepts_the_pick()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        editor.TextBox.Focus(FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();

        // Typing a field partial inside a bracket opens the completion popup with matching fields.
        host.SendText("[Amo");
        host.RunUntilIdle();
        Assert.True(editor.IsCompletionOpen);
        Assert.Contains("Amount", editor.CompletionItems);
        Assert.DoesNotContain("Region", editor.CompletionItems); // prefix-filtered

        // Enter accepts the highlighted candidate (index 0), splicing the full [Amount] token.
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal("[Amount]", editor.TextBox.Text);
        Assert.False(editor.IsCompletionOpen);

        // A bare identifier offers function tokens; Escape dismisses without accepting.
        host.SendText(" > Con");
        host.RunUntilIdle();
        Assert.True(editor.IsCompletionOpen);
        Assert.Contains("Contains", editor.CompletionItems);
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(editor.IsCompletionOpen);
        Assert.Equal("[Amount] > Con", editor.TextBox.Text); // Esc left the text as typed

        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public void Completion_replaces_the_whole_token_when_the_caret_is_mid_token()
    {
        var (host, grid, _) = Show();
        using var _ = host;
        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        editor.TextBox.Focus(FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();

        // Build "[Amo]" with the caret BEFORE the ']' (mid-token) — the audit's corruption case.
        host.SendText("[]");
        host.RunUntilIdle();
        host.SendKey(Key.LeftArrow); // caret between '[' and ']'
        host.RunUntilIdle();
        host.SendText("Amo"); // "[Amo]", caret after "Amo"
        host.RunUntilIdle();
        Assert.True(editor.IsCompletionOpen);

        host.SendKey(Key.Enter); // accept "Amount"
        host.RunUntilIdle();
        Assert.Equal("[Amount]", editor.TextBox.Text); // whole token replaced — NOT "[Amount]]"
        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public void Icon_set_with_a_blanked_glyph_still_reopens_as_icon_set()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // An icon set with the middle bucket's glyph cleared (Icon = null on that entry only).
        var amount = grid.Columns[2];
        amount.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amount,
            Entries =
            [
                new ThresholdEntry(FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Foreground: Color.FromRgb(0x9E, 0xCE, 0x6A), Icon: "▲")),
                new ThresholdEntry(FilterOperator.GreaterThanOrEqual, 15000m, new CellFormat(Foreground: Color.FromRgb(0xE0, 0xAF, 0x68))), // no glyph
                new ThresholdEntry(FilterOperator.LessThan, 15000m, new CellFormat(Foreground: Color.FromRgb(0xF7, 0x76, 0x8E), Icon: "▼")),
            ],
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        manager.Select(0);
        var edit = manager.EditSelectedAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;

        // Audit fix: a blanked glyph used to reclassify the rule as Highlight (the `All(Icon)` test);
        // it now reopens as the Icon Set it is, glyphs (incl. the blank) preserved.
        Assert.Equal(RuleEditorKind.IconSet, editor.Kind);
        Assert.Equal("▲", editor.IconGlyphBoxes[0].Text);
        Assert.Equal(string.Empty, editor.IconGlyphBoxes[1].Text);
        Assert.Equal("▼", editor.IconGlyphBoxes[2].Text);
        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(edit.IsCompletedSuccessfully);
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public async Task Expression_editor_validates_live_and_apply_lands_the_filter()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
        Assert.True(await task);
        Assert.Null(grid.ActiveFilterEditor);
        Assert.Equal(2, grid.Snapshot.Count); // 31900 + 27300
        Assert.Equal("[Amount] > 20000", grid.FilterExpressionText);
        Assert.NotNull(grid.Filter);
    }

    [Fact]
    public async Task Expression_editor_apply_with_invalid_text_stays_open_and_writes_nothing()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
        Assert.False(await task);
    }

    [Fact]
    public async Task Expression_editor_inserters_splice_tokens_at_the_caret()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
    public async Task FilterExpressionText_round_trips_the_editor_and_applies_programmatically()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
        Assert.False(await task);
        Assert.Equal("[Region] = 'East'", grid.FilterExpressionText); // cancel wrote nothing
    }

    [Fact]
    public async Task TryApplyFilterExpression_reports_diagnostics_and_applies_nothing_on_failure()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
    public async Task Builder_seeds_from_a_structural_filter_and_edits_apply()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
        Assert.Equal("15000", builder.ConditionRows[0].Value.Value);
        Assert.Equal("East", builder.ConditionRows[1].Value.Value);

        // Edit 1 — flip the Region operator through the real combo: = ⇒ <>.
        var regionRow = builder.ConditionRows[1];
        regionRow.Operator.SelectedIndex = 1; // "<>"
        Assert.Equal(FilterOperator.NotEquals, regionRow.Model.Operator);

        // Edit 2 — add a condition (the structural buttons rebuild the rows; re-fetch). Picking a
        // field now also rebuilds (the value input is keyed to the column type), so re-fetch again.
        builder.AddCondition(builder.Root);
        builder.ConditionRows[^1].Field.SelectedIndex = 2; // Amount
        var added = builder.ConditionRows[^1];
        added.Operator.SelectedIndex = 2; // "<"
        added.Value.Value = "30000";
        Assert.Equal(FilterOperator.LessThan, added.Model.Operator);
        Assert.Equal("30000", added.Model.ValueText);

        // OK ⇒ Amount > 15000 AND Region <> East AND Amount < 30000 ⇒ SO-1046 + SO-1047.
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        var applied = Assert.IsType<FilterGroupNode>(grid.Filter);
        Assert.Equal(3, applied.Children.Count);
        Assert.Equal(2, grid.Snapshot.Count);
        Assert.NotNull(FindText(host, "SO-1046"));
        Assert.NotNull(FindText(host, "SO-1047"));
        Assert.Null(FindText(host, "SO-1044"));
    }

    [Fact]
    public async Task Builder_value_parse_failure_vetoes_ok_on_the_strip()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // The starter row targets Id (string) — retarget to Amount and type garbage.
        var row = builder.ConditionRows[0];
        row.Field.SelectedIndex = 2;    // Amount (decimal)
        row.Operator.SelectedIndex = 4; // ">"
        row.Value.Value = "not-a-number";

        builder.Ok();
        host.RunUntilIdle();
        Assert.False(task.IsCompleted); // vetoed — still open
        Assert.StartsWith("✕", builder.ValidationStrip.Text);
        Assert.Null(grid.Filter);

        builder.Cancel();
        host.RunUntilIdle();
        Assert.False(await task);
    }

    [Fact]
    public async Task Builder_edit_as_text_hop_carries_the_model_text_into_the_editor()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
        Assert.True(await task);
        Assert.Equal("[Amount] > 15000", grid.FilterExpressionText);
        Assert.Equal(3, grid.Snapshot.Count); // 31900 + 19800 + 27300
    }

    [Fact]
    public async Task Rules_manager_up_down_enable_only_when_a_same_column_move_exists()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        // Region: ONE rule (lists first — Columns order); Amount: TWO (reorderable pair).
        var regionColumn = grid.Columns[1];
        var amountColumn = grid.Columns[2];
        regionColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = regionColumn,
            Entries = [(FilterOperator.Equals, "West", new CellFormat(Bold: true))],
        });
        amountColumn.FormatRules.Add(new DataBarRule { ColumnKey = amountColumn });
        amountColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries = [(FilterOperator.GreaterThan, 20000m, new CellFormat(Bold: true))],
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        Assert.Equal(3, manager.Rows.Count);

        // The first row auto-selects on open (▲▼/✎/✕ always have a target) — and it is Region's
        // LONE rule, so both buttons gray: priority is per-COLUMN, and a silent no-op button was
        // the live-canary report ("they appear to do nothing"). The enablement is the Move Up /
        // Move Down BarCommands' CanExecute surfacing through the ButtonBase coupling — asserted
        // via IsEffectivelyEnabled (IsEnabled is the plain styled property; the command gate lives
        // in IsEnabledCore).
        Assert.Equal(0, manager.SelectedIndex);
        Assert.False(manager.UpButton!.IsEffectivelyEnabled);
        Assert.False(manager.DownButton!.IsEffectivelyEnabled);
        Assert.Null(manager.UpButton.Content); // the explicit glyph-only face beat the label auto-fill

        // Amount's first rule: ▼ live (a same-column successor), ▲ gray.
        manager.Select(1);
        Assert.False(manager.UpButton.IsEffectivelyEnabled);
        Assert.True(manager.DownButton.IsEffectivelyEnabled);

        // Amount's second: ▲ live, ▼ gray; the move works and the enablement follows the rule.
        manager.Select(2);
        Assert.True(manager.UpButton.IsEffectivelyEnabled);
        Assert.False(manager.DownButton.IsEffectivelyEnabled);
        manager.MoveSelected(-1);
        Assert.Same(manager.Rows[1].Rule, amountColumn.FormatRules[0]); // it moved up in ITS column
        Assert.False(manager.UpButton.IsEffectivelyEnabled);  // now first in its column
        Assert.True(manager.DownButton.IsEffectivelyEnabled);

        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public async Task Color_scale_previews_step_the_stops_across_the_swatch()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        // The editor's Red → Amber → Green preset colors (so the edit seed maps to a preset), plus
        // a 2-stop scale on Region — the live-canary report: every swatch cell wore the LAST stop.
        var red = Color.FromRgb(0xF7, 0x76, 0x8E);
        var amber = Color.FromRgb(0xE0, 0xAF, 0x68);
        var green = Color.FromRgb(0x9E, 0xCE, 0x6A);
        grid.Columns[1].FormatRules.Add(new ColorScaleRule { ColumnKey = grid.Columns[1], Stops = [red, green] });
        grid.Columns[2].FormatRules.Add(new ColorScaleRule { ColumnKey = grid.Columns[2], Stops = [red, amber, green] });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;

        // Manager mini-previews: 2 cells per stop for 3 stops, 3 per stop for 2 — in stop order.
        var threeStop = SwatchRuns(manager.Rows.First(r => r.Rule is ColorScaleRule { Stops.Count: 3 }).RowElement);
        Assert.Equal(["▒▒", "▒▒", "▒▒"], threeStop.Select(r => r.Text));
        Assert.Equal([red, amber, green], threeStop.Select(r => r.Color));
        var twoStop = SwatchRuns(manager.Rows.First(r => r.Rule is ColorScaleRule { Stops.Count: 2 }).RowElement);
        Assert.Equal(["▒▒▒", "▒▒▒"], twoStop.Select(r => r.Text));
        Assert.Equal([red, green], twoStop.Select(r => r.Color));

        // The editor's preview steps too.
        manager.Select(manager.Rows.ToList().FindIndex(r => r.Rule is ColorScaleRule { Stops.Count: 3 }));
        var edit = manager.EditSelectedAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;
        var editorSwatch = Assert.IsType<StackPanel>(editor.PreviewBorder.Child);
        Assert.Equal([red, amber, green],
                     editorSwatch.Children.OfType<TextBlock>()
                                 .Select(t => ((SolidColorBrush)t.Foreground!).Color));

        // Leaving the scale kind restores the plain preview text block.
        editor.SetKind(RuleEditorKind.Highlight);
        Assert.IsType<TextBlock>(editor.PreviewBorder.Child);
        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(edit.IsCompletedSuccessfully);

        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);

        static List<(string Text, Color Color)> SwatchRuns(Border rowElement)
        {
            var line = (StackPanel)rowElement.Child!;
            var preview = (Border)line.Children[2];
            var swatch = (StackPanel)((StackPanel)preview.Child!).Children[0];
            return swatch.Children.OfType<TextBlock>()
                         .Select(t => (t.Text ?? string.Empty, ((SolidColorBrush)t.Foreground!).Color))
                         .ToList();
        }
    }

    [Fact]
    public void Highlight_between_operator_lands_a_two_bound_entry()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var editor = OpenNewRuleEditor(grid, host);
        editor.SetKind(RuleEditorKind.Highlight);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount
        editor.OperatorCombo!.SelectedIndex = OperatorIndex(editor, "Between");
        editor.ValueBox!.Text = "15000";
        editor.SecondValueBox!.Text = "28000"; // revealed by Between (§10.5)
        editor.Ok();
        host.RunUntilIdle();

        var rule = Assert.IsType<ThresholdRule>(Assert.Single(grid.Columns[2].FormatRules));
        var entry = Assert.Single(rule.Entries);
        Assert.Equal(FilterOperator.Between, entry.Operator);
        Assert.Equal(15000m, entry.Value);
        Assert.Equal(28000m, entry.SecondValue); // the upper bound flowed through
        CloseManager(grid, host);
    }

    [Fact]
    public void Highlight_multi_entry_lands_every_condition_in_order()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var editor = OpenNewRuleEditor(grid, host);
        editor.SetKind(RuleEditorKind.Highlight);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount

        // First condition (row 0), then add a second via the ＋ hook (§10.4).
        editor.OperatorCombo!.SelectedIndex = OperatorIndex(editor, "Greater or equal");
        editor.ValueBox!.Text = "25000";
        editor.AddHighlightCondition();
        host.RunUntilIdle();
        Assert.Equal(2, editor.HighlightRows.Count);

        // The second row's controls.
        var second = editor.HighlightRows[1];
        second.Operator.SelectedIndex = OperatorIndex(editor, "Less than");
        second.Value.Value = "15000"; // Amount is a decimal column ⇒ a text-box ValueEditor
        editor.Ok();
        host.RunUntilIdle();

        var rule = Assert.IsType<ThresholdRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Equal(2, rule.Entries.Count);
        Assert.Equal((FilterOperator.GreaterThanOrEqual, (object)25000m), (rule.Entries[0].Operator, rule.Entries[0].Value));
        Assert.Equal((FilterOperator.LessThan, (object)15000m), (rule.Entries[1].Operator, rule.Entries[1].Value));
        CloseManager(grid, host);
    }

    [Fact]
    public void Editing_a_multi_entry_rule_seeds_a_row_per_entry()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var amount = grid.Columns[2];
        amount.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amount,
            Entries =
            [
                (FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Bold: true)),
                (FilterOperator.LessThan, 15000m, new CellFormat(Foreground: Color.FromRgb(0xF7, 0x76, 0x8E))),
            ],
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        manager.Select(0);
        var edit = manager.EditSelectedAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;

        // Both entries seeded a row (§10.4 — the editor used to seed only entry[0]).
        Assert.Equal(RuleEditorKind.Highlight, editor.Kind);
        Assert.Equal(2, editor.HighlightRows.Count);
        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(edit.IsCompletedSuccessfully);
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public void Custom_format_carries_arbitrary_colors()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var editor = OpenNewRuleEditor(grid, host);
        editor.SetKind(RuleEditorKind.Highlight);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount
        editor.OperatorCombo!.SelectedIndex = OperatorIndex(editor, "Greater or equal");
        editor.ValueBox!.Text = "25000";

        // Pick "Custom…" and enter a hex the presets don't offer (§10.6).
        var row = editor.HighlightRows[0];
        row.Format.SelectedIndex = DataGridRuleEditor.CustomFormatIndex;
        row.Custom.Foreground.Text = "#123456";
        editor.Ok();
        host.RunUntilIdle();

        var rule = Assert.IsType<ThresholdRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), Assert.Single(rule.Entries).Format.Foreground);
        CloseManager(grid, host);
    }

    [Fact]
    public void Custom_color_scale_lands_its_stops()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var editor = OpenNewRuleEditor(grid, host);
        editor.SetKind(RuleEditorKind.ColorScale);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount
        editor.ScaleCombo!.SelectedIndex = DataGridRuleEditor.CustomScaleIndex; // "Custom…"
        editor.ScaleStopBoxes[0].Text = "#111111";
        editor.ScaleStopBoxes[1].Text = "#222222";
        editor.ScaleStopBoxes[2].Text = string.Empty; // a 2-stop custom scale
        editor.Ok();
        host.RunUntilIdle();

        var rule = Assert.IsType<ColorScaleRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Equal(new[] { Color.FromRgb(0x11, 0x11, 0x11), Color.FromRgb(0x22, 0x22, 0x22) }, rule.Stops);
        CloseManager(grid, host);
    }

    [Fact]
    public void Custom_icon_glyphs_land_on_the_rule()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        var editor = OpenNewRuleEditor(grid, host);
        editor.SetKind(RuleEditorKind.IconSet);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount
        editor.IconGlyphBoxes[0].Text = "↑"; // ▲ glyph
        editor.IconGlyphBoxes[1].Text = "→"; // ● glyph
        editor.IconGlyphBoxes[2].Text = "↓"; // ▼ glyph
        editor.IconThresholdBoxes[0].Text = "25000"; // ▲ when >=
        editor.IconThresholdBoxes[1].Text = "15000"; // ● when >=
        editor.Ok();
        host.RunUntilIdle();

        var rule = Assert.IsType<ThresholdRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Equal(3, rule.Entries.Count);
        Assert.Equal("↑", rule.Entries[0].Format.Icon);
        Assert.Equal("→", rule.Entries[1].Format.Icon);
        Assert.Equal("↓", rule.Entries[2].Format.Icon);
        CloseManager(grid, host);
    }

    [Fact]
    public void Code_authored_predicate_rule_with_source_text_reseeds()
    {
        var (host, grid, _) = Show();
        using var _ = host;

        // §10.8: a lambda rule authored IN CODE that supplies SourceText re-seeds the field.
        var amount = grid.Columns[2];
        System.Linq.Expressions.Expression<Func<Order, bool>> predicate = o => o.Amount > 20000m;
        amount.FormatRules.Add(new PredicateRule
        {
            ColumnKey = amount,
            RowPredicate = predicate,
            Format = new CellFormat(Bold: true),
            SourceText = "[Amount] > 20000",
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        manager.Select(0);
        var edit = manager.EditSelectedAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;
        Assert.Equal(RuleEditorKind.Expression, editor.Kind);
        Assert.Equal("[Amount] > 20000", editor.ExpressionBox!.Text);
        editor.Cancel();
        host.RunUntilIdle();
        Assert.True(edit.IsCompletedSuccessfully);
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public async Task Editing_an_expression_rule_seeds_the_criteria_text()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;

        // Author an expression rule through the editor (the SourceText carrier's write side).
        var create = manager.NewRuleAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;
        editor.SetKind(RuleEditorKind.Expression);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount owns the rule
        editor.ExpressionBox!.Text = "[Amount] > 20000";
        editor.Ok();
        host.RunUntilIdle();
        Assert.True(create.IsCompletedSuccessfully);
        var rule = Assert.IsType<PredicateRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Equal("[Amount] > 20000", rule.SourceText);

        // Re-editing seeds the criteria field (live-canary fix — it used to start empty).
        var edit = manager.EditSelectedAsync();
        host.RunUntilIdle();
        var reopened = manager.ActiveRuleEditor!;
        Assert.Equal(RuleEditorKind.Expression, reopened.Kind);
        Assert.Equal("[Amount] > 20000", reopened.ExpressionBox!.Text);
        reopened.Cancel();
        host.RunUntilIdle();
        Assert.True(edit.IsCompletedSuccessfully);

        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public async Task Editor_designer_hop_carries_a_structural_draft_without_prompting()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        editor.TextBox.Text = "[Region] = 'East'";
        host.RunUntilIdle();

        // A fully structural draft hops straight through — no warning prompt, NOTHING applied yet.
        var hop = editor.RequestEditInBuilderAsync();
        host.RunUntilIdle();
        Assert.True(hop.IsCompleted);
        Assert.Null(FindText(host, "Open in designer?"));
        Assert.Null(grid.Filter); // side-effect-free until the designer's OK
        Assert.Equal(4, grid.Snapshot.Count);

        var builder = grid.ActiveFilterBuilder;
        Assert.NotNull(builder);
        var row = Assert.Single(builder.ConditionRows); // the draft seeded the tree
        Assert.Equal("East", row.Value.Value);

        // The designer's OK is the one write; the ORIGINAL editor entry-point task completes.
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        Assert.NotNull(grid.Filter);
        Assert.Equal(2, grid.Snapshot.Count); // the two East rows
    }

    [Fact]
    public async Task Editor_designer_hop_warns_on_a_compiled_draft_and_the_pair_survives_a_zero_edit_ok()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        editor.TextBox.Text = "Len([Id]) = 7";
        host.RunUntilIdle();
        Assert.True(editor.IsValid);

        // The compiled-only draft warns first (the locked-ƒ-row representability prompt).
        var hop = editor.RequestEditInBuilderAsync();
        host.RunUntilIdle();
        Assert.False(hop.IsCompleted);
        var proceed = FindText(host, "Open designer");
        Assert.NotNull(proceed);

        host.SendMouseMove(proceed.Value.X + 1, proceed.Value.Y); // hover first (release-clicks gate on it)
        host.RunUntilIdle();
        host.SendClick(proceed.Value.X + 1, proceed.Value.Y);
        host.RunUntilIdle();
        Assert.True(hop.IsCompletedSuccessfully); // a FAULTED hop must fail here, not hide (the cross-thread trap)
        Assert.True(editor.HopToBuilder, "hop flag not set");
        host.RunUntilIdle();

        // The designer shows the draft as ONE locked ƒ row labeled with the SOURCE TEXT.
        var builder = grid.ActiveFilterBuilder;
        Assert.NotNull(builder);
        Assert.Empty(builder.ConditionRows);
        if (FindText(host, "ƒ Len([Id]) = 7") is null)
        {
            var dump = string.Join("|", Enumerable.Range(0, 16).Select(r => host.GetRowText(r).TrimEnd()));
            Assert.Fail($"label missing; frame: {dump}");
        }

        // A zero-edit OK applies through the TEXT AUTHORITY: tree AND source text store together.
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        Assert.Equal("Len([Id]) = 7", grid.FilterExpressionText);
        Assert.IsType<FilterPredicateNode>(grid.Filter);
        Assert.Equal(4, grid.Snapshot.Count); // every Id is 7 chars — all rows match
    }

    [Fact]
    public async Task Editor_designer_hop_stay_here_keeps_the_editor_open_with_the_draft()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;
        editor.TextBox.Text = "Len([Id]) = 7";
        host.RunUntilIdle();

        var hop = editor.RequestEditInBuilderAsync();
        host.RunUntilIdle();
        var stay = FindText(host, "Stay here");
        Assert.NotNull(stay);

        host.SendMouseMove(stay.Value.X + 1, stay.Value.Y); // hover first (release-clicks gate on it)
        host.RunUntilIdle();
        host.SendClick(stay.Value.X + 1, stay.Value.Y);
        host.RunUntilIdle();
        Assert.True(hop.IsCompletedSuccessfully);

        // Declined: the editor is still open with the draft intact; nothing changed.
        Assert.NotNull(grid.ActiveFilterEditor);
        Assert.Null(grid.ActiveFilterBuilder);
        Assert.Equal("Len([Id]) = 7", grid.ActiveFilterEditor!.TextBox.Text);
        Assert.Null(grid.Filter);
        Assert.False(task.IsCompleted);

        grid.ActiveFilterEditor.Cancel();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.False(await task);
    }

    [Fact]
    public async Task Builder_empty_value_on_a_non_nullable_column_vetoes_ok()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // Retarget the starter row to Amount (decimal — non-nullable) and leave '=' + empty value.
        // Previously OK accepted Condition(Amount, Equals, null), the dialog closed reporting
        // success, and the POSTED shape push crashed the dispatcher (ConvertLiteral: a null
        // literal cannot match a non-nullable decimal key).
        var row = builder.ConditionRows[0];
        row.Field.SelectedIndex = 2; // Amount

        builder.Ok();
        host.RunUntilIdle();
        Assert.False(task.IsCompleted); // vetoed — the dialog stays open
        Assert.StartsWith("✕", builder.ValidationStrip.Text);
        Assert.Contains("(Blanks)", builder.ValidationStrip.Text);
        Assert.Null(grid.Filter); // no uncompilable tree reached the engine

        host.RunUntilIdle(); // no faulted dispatcher job pending
        Assert.Equal(4, grid.Snapshot.Count); // snapshot untouched

        builder.Cancel();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.False(await task);
    }

    [Fact]
    public async Task Builder_blanks_equals_on_a_string_column_still_applies()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // Region (string) '=' empty ⇒ the (Blanks) match — legal wherever the key can HOLD null,
        // and it must keep compiling end-to-end (the veto is only for non-nullable value keys).
        var row = builder.ConditionRows[0];
        row.Field.SelectedIndex = 1; // Region

        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        var condition = Assert.IsType<FilterConditionNode>(grid.Filter);
        Assert.Null(condition.Value);
        Assert.Equal(0, grid.Snapshot.Count); // no sample row has a blank Region — and no crash
    }

    [Fact]
    public async Task Builder_untouched_ok_applies_no_filter()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // The machine-seeded starter row (pre-selected first column, '=', empty value) must not
        // lower to an unintended '[Id] = (Blanks)': an untouched OK applies NOTHING.
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        Assert.Null(grid.Filter);
        Assert.Equal(4, grid.Snapshot.Count); // every row still visible
    }

    [Fact]
    public async Task Builder_clear_then_ok_clears_the_filter()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        grid.Filter = FilterNode.Condition(grid.Columns[1], FilterOperator.Equals, "East");
        host.RunUntilIdle();
        Assert.Equal(2, grid.Snapshot.Count);

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        builder.Clear(); // ⌫ Clear reseeds a pristine starter — OK must CLEAR, not land '[Id] = (Blanks)'
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(await task);
        Assert.Null(grid.Filter);
        Assert.Equal(4, grid.Snapshot.Count);
    }

    [Fact]
    public async Task Builder_zero_edit_ok_preserves_the_retained_expression_text()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        // A function call is non-structural ⇒ FilterNode.Custom with the §9.1-retained source text.
        grid.FilterExpressionText = "Len([Id]) = 7";
        host.RunUntilIdle();
        var before = Assert.IsType<FilterPredicateNode>(grid.Filter);
        Assert.Equal(4, grid.Snapshot.Count); // all sample ids are 7 chars

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // Zero edits: OK lowers back to the SAME seeded tree — the Filter write (which nulls
        // FilterExpressionText) must be skipped so the user's source text survives.
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        Assert.Same(before, grid.Filter);
        Assert.Equal("Len([Id]) = 7", grid.FilterExpressionText);

        // The next builder open still labels the opaque expression row from the retained text.
        var reopened = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        if (FindText(host, "ƒ Len([Id]) = 7") is null)
        {
            var dump = string.Join("|", Enumerable.Range(0, 16).Select(r => host.GetRowText(r).TrimEnd()));
            Assert.Fail($"label missing; frame: {dump}");
        }
        grid.ActiveFilterBuilder!.Cancel();
        host.RunUntilIdle();
        Assert.True(reopened.IsCompleted);
        Assert.Equal("Len([Id]) = 7", grid.FilterExpressionText); // cancel wrote nothing either
    }

    [Fact]
    public async Task Duplicate_filter_builder_open_rides_the_live_dialog()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        // A double-click posts BOTH opens before the first modal blocks its trigger — previously
        // two builder windows stacked with only the last one tracked (and closed at teardown).
        var first = grid.OpenFilterBuilderAsync();
        var second = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();

        // ONE dialog: the duplicate adopted the live window, and the tracked handle reaches it.
        var builder = grid.ActiveFilterBuilder;
        Assert.NotNull(builder);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        // Driving the tracked handle drives the one real dialog; both entry tasks complete with it.
        builder.Ok(); // untouched ⇒ applies no filter
        host.RunUntilIdle();
        Assert.True(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.True(await first);
        Assert.True(await second);
        Assert.Null(grid.Filter);
        Assert.Null(grid.ActiveFilterBuilder); // fully untracked after the one close
    }

    // ── The expression editor × the criteria bridge (null-literal exactness) ──────────────────────

    [Fact]
    public async Task Expression_editor_null_equals_on_a_non_nullable_column_matches_nothing_without_crashing()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var task = grid.OpenFilterEditorAsync();
        host.RunUntilIdle();
        var editor = grid.ActiveFilterEditor!;

        // '[Amount] = null' on a non-nullable decimal key: the strip is green (valid boolean
        // text), and the lowering must take the COMPILED lane — the structural
        // Condition(Equals, null) shape would throw in the engine's posted shape push while the
        // pinned §9.1 semantics say it simply matches nothing.
        editor.TextBox.Text = "[Amount] = null";
        host.RunUntilIdle();
        Assert.True(editor.IsValid);

        editor.Apply();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.True(await task);
        Assert.IsType<FilterPredicateNode>(grid.Filter); // compiled lane, not a structural null condition
        Assert.Equal("[Amount] = null", grid.FilterExpressionText);
        Assert.Equal(0, grid.Snapshot.Count); // constant-false — matches nothing, no faulted job

        // '<> null' is the constant-true mirror.
        Assert.True(grid.TryApplyFilterExpression("[Amount] <> null", out var notNullDiagnostics));
        Assert.Empty(notNullDiagnostics);
        host.RunUntilIdle();
        Assert.IsType<FilterPredicateNode>(grid.Filter);
        Assert.Equal(4, grid.Snapshot.Count);

        // Null-capable keys keep the structural (builder round-trippable) lane.
        Assert.True(grid.TryApplyFilterExpression("[Region] = null", out var regionDiagnostics));
        Assert.Empty(regionDiagnostics);
        host.RunUntilIdle();
        Assert.IsType<FilterConditionNode>(grid.Filter);
        Assert.Equal(0, grid.Snapshot.Count);
    }

    // ── The rules manager ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rules_manager_lists_rules_and_the_enabled_toggle_flips_rendering()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var red = Color.FromRgb(247, 118, 142);
        var amountColumn = grid.Columns[2];
        amountColumn.FormatRules.Add(new ThresholdRule
        {
            ColumnKey = amountColumn,
            Entries = [(FilterOperator.GreaterThanOrEqual, 25000m, new CellFormat(Foreground: red, Bold: true))],
        });
        grid.RefreshFormatRules();
        host.RunUntilIdle();

        var hit = FindText(host, "31,900");
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

        hit = FindText(host, "31,900");
        Assert.NotNull(hit);
        var cell = host.GetCell(hit.Value.X, hit.Value.Y);
        Assert.NotEqual(red, cell.Style.Foreground);
        Assert.True((cell.Style.Attributes & TextAttributes.Bold) == 0);

        // Back On (the rule instance survived — the one mutable knob): the format returns.
        row.Rule.Enabled = true;
        grid.RefreshFormatRules();
        host.RunUntilIdle();
        hit = FindText(host, "31,900");
        Assert.NotNull(hit);
        Assert.Equal(red, host.GetCell(hit.Value.X, hit.Value.Y).Style.Foreground);
    }

    [Fact]
    public async Task Add_rule_dialog_creates_a_data_bar_rule_end_to_end()
    {
        var (host, grid, _) = Show();
        await using var _ = host;
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
    public async Task Rule_editor_text_operator_on_a_non_string_column_vetoes_ok()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;

        var newRuleTask = manager.NewRuleAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;

        // Highlight pane, Amount (decimal), 'Text contains' + raw text: previously OK landed a
        // ThresholdRule the engine's CompileFormatRules THROWS on — swallowed once in the
        // fire-and-forget click, then re-thrown unhandled on every later rules mutation.
        editor.ColumnCombo!.SelectedIndex = 2;   // Amount
        editor.OperatorCombo!.SelectedIndex = OperatorIndex(editor, "Text contains"); // label-based (Between insertion shifts indices)
        editor.ValueBox!.Text = "3";

        editor.Ok();
        host.RunUntilIdle();
        Assert.False(newRuleTask.IsCompleted); // vetoed — the editor stays open
        Assert.True(editor.IsOpen);
        Assert.StartsWith("✕", editor.ValidationStrip.Text);
        Assert.Contains("text column", editor.ValidationStrip.Text);
        Assert.All(grid.Columns, c => Assert.Empty(c.FormatRules)); // no rule landed

        // The engine never saw an uncompilable rule: a follow-on recompile must not throw.
        grid.RefreshFormatRules();
        host.RunUntilIdle();
        Assert.Equal(4, grid.Snapshot.Count);

        editor.Cancel();
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(newRuleTask.IsCompleted);
        Assert.Null(newRuleTask.Exception);
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public async Task Double_new_rule_activation_opens_one_editor_and_lands_one_rule()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

        // The grid entry point double-activated too: the duplicate manager open adopts the live one.
        var managerTask = grid.OpenRulesManagerAsync();
        var duplicateManagerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;

        // A double-click on ＋ New Rule posts BOTH opens before either modal blocks the button —
        // previously two rule editors stacked with only the last tracked (orphaning the first
        // at teardown, and OK-ing both added duplicate rules).
        var first = manager.NewRuleAsync();
        var second = manager.NewRuleAsync();
        host.RunUntilIdle();

        var editor = manager.ActiveRuleEditor;
        Assert.NotNull(editor);
        Assert.True(editor.IsOpen);
        Assert.True(second.IsCompleted); // the duplicate was a guarded no-op
        Assert.Null(second.Exception);
        Assert.False(first.IsCompleted); // the one real editor is still open

        // OK on the ONE editor lands exactly one rule.
        editor.SetKind(RuleEditorKind.DataBar);
        editor.ColumnCombo!.SelectedIndex = 2; // Amount
        editor.Ok();
        host.RunUntilIdle();
        Assert.True(first.IsCompleted);
        Assert.Null(first.Exception);
        Assert.IsType<DataBarRule>(Assert.Single(grid.Columns[2].FormatRules));
        Assert.Single(manager.Rows);
        Assert.Null(manager.ActiveRuleEditor);

        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
        Assert.True(duplicateManagerTask.IsCompleted); // the rider completed with the live close
    }

    [Fact]
    public async Task Rules_manager_delete_and_reorder_operate_within_the_column_list()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
    public async Task TopBottom_rule_evaluates_through_the_TopK_seam_and_tracks_the_filtered_view()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
    public async Task Disabled_rules_never_reach_the_engine_push()
    {
        var (host, grid, _) = Show();
        await using var _ = host;

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
