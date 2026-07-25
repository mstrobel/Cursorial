using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// Adaptive value entry (§10): an enum (or bool) column offers a prepopulated dropdown of its valid
/// names in the rule editor and Filter Builder, instead of a free-text box the user must type into.
/// </summary>
public class DataGridValueEntryTests
{
    private enum ShipState { Pending, Shipped, Cancelled }

    private sealed class Shipment
    {
        public string Id { get; set; } = string.Empty;
        public ShipState Status { get; set; }
    }

    private static (UIHeadlessHost Host, DataGrid Grid) Show()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(70, 18) });
        var grid = new DataGrid
        {
            ItemsSource = new ObservableCollection<Shipment>
            {
                new() { Id = "S-1", Status = ShipState.Pending },
                new() { Id = "S-2", Status = ShipState.Shipped },
                new() { Id = "S-3", Status = ShipState.Cancelled },
                new() { Id = "S-4", Status = ShipState.Shipped },
            },
        };
        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid);
    }

    private static int OpIndex(IEnumerable? items, string label)
        => ((IEnumerable)items!).Cast<string>().ToList().IndexOf(label);

    // ── Enum member [Display(Name)] ───────────────────────────────────────────────────────────────

    private enum Priority
    {
        [Display(Name = "Low priority")] Low,
        [Display(Name = "High priority")] High,
        Normal, // no [Display] → the raw member name
    }

    private sealed class Ticket
    {
        public string Id { get; set; } = string.Empty;
        public Priority Priority { get; set; }
    }

    [Fact]
    public void Enum_member_display_name_renders_in_cells()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(70, 12) });
        using var _ = host;
        var grid = new DataGrid
        {
            ItemsSource = new ObservableCollection<Ticket>
            {
                new() { Id = "T-1", Priority = Priority.Low },
                new() { Id = "T-2", Priority = Priority.High },
                new() { Id = "T-3", Priority = Priority.Normal },
            },
        };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        var all = string.Join("\n", Enumerable.Range(0, 12).Select(host.GetRowText));
        Assert.Contains("Low priority", all);   // the [Display] text, not "Low"
        Assert.Contains("High priority", all);
        Assert.Contains("Normal", all);          // no [Display] ⇒ the raw member name
    }

    [Fact]
    public void Enum_dropdown_shows_display_names_but_stores_member_names()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(70, 14) });
        using var disposeHost = host;
        var grid = new DataGrid { ItemsSource = new ObservableCollection<Ticket> { new() { Id = "T-1", Priority = Priority.Low } } };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        _ = manager.NewRuleAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;
        editor.SetKind(RuleEditorKind.Highlight);
        editor.ColumnCombo!.SelectedIndex = 1; // Priority
        host.RunUntilIdle();

        var combo = Assert.IsType<ComboBox>(editor.HighlightRows[0].Value.Element);
        Assert.Equal(new[] { "Low priority", "High priority", "Normal" }, combo.ItemsSource!.Cast<string>());

        // Picking the "High priority" label yields the raw member name "High" (what parses/filters).
        combo.SelectedIndex = 1;
        Assert.Equal("High", editor.HighlightRows[0].Value.Value);
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public void Rule_editor_offers_an_enum_dropdown_and_lands_the_picked_value()
    {
        var (host, grid) = Show();
        using var _ = host;

        var managerTask = grid.OpenRulesManagerAsync();
        host.RunUntilIdle();
        var manager = grid.ActiveRulesManager!;
        var create = manager.NewRuleAsync();
        host.RunUntilIdle();
        var editor = manager.ActiveRuleEditor!;

        editor.SetKind(RuleEditorKind.Highlight);
        editor.ColumnCombo!.SelectedIndex = 1; // Status (the enum column) — rebuilds the value inputs
        host.RunUntilIdle();

        // The value input is now a dropdown prepopulated with the enum's names.
        var row = editor.HighlightRows[0];
        Assert.True(row.Value.IsChoice);
        var combo = Assert.IsType<ComboBox>(row.Value.Element);
        Assert.Equal(new[] { "Pending", "Shipped", "Cancelled" }, combo.ItemsSource!.Cast<string>());

        // Pick a value + Equals; OK lands the parsed enum value on the rule.
        row.Operator.SelectedIndex = OpIndex(row.Operator.ItemsSource, "Equals");
        row.Value.Value = "Shipped";
        editor.Ok();
        host.RunUntilIdle();

        Assert.True(create.IsCompletedSuccessfully);
        var rule = Assert.IsType<ThresholdRule>(Assert.Single(grid.Columns[1].FormatRules));
        Assert.Equal(ShipState.Shipped, Assert.Single(rule.Entries).Value);
        manager.CloseWindow();
        host.RunUntilIdle();
        Assert.True(managerTask.IsCompleted);
    }

    [Fact]
    public void Filter_builder_offers_an_enum_dropdown_for_an_enum_field()
    {
        var (host, grid) = Show();
        using var _ = host;

        var task = grid.OpenFilterBuilderAsync();
        host.RunUntilIdle();
        var builder = grid.ActiveFilterBuilder!;

        // Point the condition's field at the enum column — the value input becomes a name dropdown.
        builder.ConditionRows[0].Field.SelectedIndex = OpIndex(builder.ConditionRows[0].Field.ItemsSource, "Status");
        host.RunUntilIdle();
        var row = builder.ConditionRows[0];
        Assert.True(row.Value.IsChoice);
        var combo = Assert.IsType<ComboBox>(row.Value.Element);
        Assert.Equal(new[] { "Pending", "Shipped", "Cancelled" }, combo.ItemsSource!.Cast<string>());

        // Pick "Shipped" with Equals and apply — only the two Shipped rows survive.
        row.Operator.SelectedIndex = OpIndex(row.Operator.ItemsSource, "=");
        row.Value.Value = "Shipped";
        builder.Ok();
        host.RunUntilIdle();
        Assert.True(task.IsCompleted);
        Assert.Equal(2, grid.Snapshot.Count);
    }
}
