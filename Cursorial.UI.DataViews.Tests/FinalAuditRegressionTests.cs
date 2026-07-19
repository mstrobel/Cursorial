using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.Rendering;
using Cursorial.UI.DataViews;
using Cursorial.UI.DataViews.Shaping;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews;

/// <summary>
/// Regression pins for the final adversarial audit's confirmed findings (2026-07-18): the missing
/// dispatcher scheduler (cross-thread background publishes), the dirty-flag slot-reuse row loss,
/// the selection slot-reuse bleed, and the stale compiled kit after a column re-push.
/// </summary>
public class FinalAuditRegressionTests
{
    private sealed class Item(string id, int value) : INotifyPropertyChanged
    {
        private int _value = value;
        public string Id { get; } = id;
        public int Value { get => _value; set => Set(ref _value, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T v, [CallerMemberName] string? n = null)
        {
            field = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n!));
        }
    }

    private static readonly ShapingColumnDescription[] EngineColumns =
    [
        new() { Key = "Id", FieldName = nameof(Item.Id) },
        new() { Key = "Value", FieldName = nameof(Item.Value) },
    ];

    private static string[] Ids(DataViewController<Item> controller)
    {
        var ids = new List<string>();
        var snapshot = controller.Snapshot;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot.GetRow(i);
            if (!row.IsGroup)
                ids.Add(controller.RowAccessor(row.RowId).Id);
        }
        return ids.ToArray();
    }

    // ── Finding 1 (critical): the grid must publish background reshapes on the OWNER thread. ─────

    [Fact]
    public void Grid_background_reshape_publishes_on_the_owner_thread_and_repaints()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(50, 12) });
        using var _ = host;
        int ownerThread = Environment.CurrentManagedThreadId;

        var source = new ObservableCollection<Item>(
            Enumerable.Range(0, 40).Select(i => new Item($"R{i:D3}", i)));
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        // Route the next reshape through the REAL ThreadPool background lane.
        grid.Controller!.BackgroundThreshold = 8;

        int? publishThread = null;
        grid.Controller.SnapshotChanged += (_, _) => publishThread = Environment.CurrentManagedThreadId;

        int versionBefore = grid.Snapshot.Version;
        grid.CycleSort(grid.Columns[1]); // Value ascending — a full reshape past the threshold

        // The Task.Run leg is real asynchrony: pump frames until the publish lands (bounded).
        for (int i = 0; i < 200 && grid.Snapshot.Version == versionBefore; i++)
        {
            host.RunFrame();
            Thread.Sleep(5);
        }

        Assert.True(grid.Snapshot.Version > versionBefore, "the background reshape must publish");
        Assert.Equal(ownerThread, publishThread); // the audit's repro observed a pool thread here
        Assert.Contains("R000", host.GetRowText(1)); // and the grid repainted from the new snapshot
    }

    [Fact]
    public void Headless_controller_without_scheduler_degrades_to_sync_instead_of_cross_thread()
    {
        // No scheduler + the REAL Task.Run runner: the background lane must refuse (sync fallback),
        // never publish from the pool (the inline scheduler would run the publish right there).
        var source = new ObservableCollection<Item>(
            Enumerable.Range(0, 100).Select(i => new Item($"R{i:D3}", 99 - i)));
        using var controller = new DataViewController<Item> { BackgroundThreshold = 8 };
        controller.SetColumns(EngineColumns);
        controller.AttachSource(source);
        controller.SetShape([SortDescription.Ascending("Value")], [], [], null);

        // Published synchronously — the result is immediately visible, computed on this thread.
        Assert.Equal(100, controller.Snapshot.DataRowCount);
        Assert.Equal("R099", Ids(controller)[0]); // value 0 sorts first
    }

    // ── Finding 2 (critical): dirty → removed → slot-reused insert within one window. ───────────

    [Fact]
    public void Tick_remove_then_slot_reusing_insert_keeps_the_new_row()
    {
        var source = new ObservableCollection<Item> { new("A", 10), new("B", 20), new("C", 30) };
        using var controller = new DataViewController<Item>();
        controller.SetColumns(EngineColumns);
        controller.AttachSource(source);
        controller.SetShape([SortDescription.Ascending("Value")], [], [], null);

        // One coalescing window: A ticks dirty, A is removed (frees its slot), D inserts (may reuse it).
        source[0].Value = 15;      // dirty A
        source.RemoveAt(0);        // remove A
        source.Add(new Item("D", 25));
        controller.Flush();

        // The audit's repro lost D here (the stale dirty flag suppressed the reused slot).
        Assert.Equal(new[] { "B", "D", "C" }, Ids(controller));
    }

    // ── Finding 3 (major): selection must not bleed onto a slot's next occupant. ─────────────────

    [Fact]
    public void Selection_does_not_bleed_through_slot_reuse()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(50, 10) });
        using var _ = host;
        var source = new ObservableCollection<Item> { new("A", 1), new("B", 2), new("C", 3) };
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        host.SendClick(4, 1); // select A (the first data row)
        host.RunUntilIdle();
        Assert.Single(grid.RowSelection.MaterializeSelectedIds(grid.Snapshot));

        source.RemoveAt(0);                 // A dies; its slot parks, then frees after publish
        source.Add(new Item("Z", 9));       // Z may reuse A's slot
        host.RunUntilIdle();

        // The audit's repro rendered Z selected here (A's id aliased onto it).
        Assert.Empty(grid.RowSelection.MaterializeSelectedIds(grid.Snapshot));
    }

    [Fact]
    public void Inverted_selection_treats_recycled_and_fresh_slots_identically()
    {
        // The documented all-except semantics: new rows JOIN a select-all selection (Ctrl+A means
        // "everything") — and slot luck must not decide. A row arriving on A's RECYCLED slot and a
        // row arriving on a FRESH slot must both come in selected, even though A itself had been
        // individually un-selected (its stale exception must not transfer to the recycled id).
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(50, 10) });
        using var _ = host;
        var source = new ObservableCollection<Item> { new("A", 1), new("B", 2), new("C", 3) };
        var grid = new DataGrid { ItemsSource = source };
        host.ShowRoot(grid);
        host.RunUntilIdle();

        grid.RowSelection.SelectAll();                    // inverted mode
        int deadId = grid.Snapshot.GetRow(0).RowId;
        grid.RowSelection.Toggle(deadId);                 // un-select A (it joins the exception set)

        source.RemoveAt(0);                               // A dies
        host.RunUntilIdle();                              // publish frees A's slot
        source.Add(new Item("Z", 9));                     // Z recycles A's slot
        source.Add(new Item("Y", 8));                     // Y takes a fresh slot
        host.RunUntilIdle();

        var selected = grid.RowSelection.MaterializeSelectedIds(grid.Snapshot);
        var names = selected.Select(id => ((Item)grid.Controller!.GetRowObject(id)).Id)
                            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "B", "C", "Y", "Z" }, names); // consistent regardless of slot reuse
    }

    // ── Finding 4 (major): a column re-push must recompile the kit. ──────────────────────────────

    [Fact]
    public void SetColumns_after_SetShape_sorts_new_rows_with_fresh_keys()
    {
        var source = new ObservableCollection<Item> { new("A", 30), new("B", 20), new("C", 10) };
        using var controller = new DataViewController<Item>();
        controller.SetColumns(EngineColumns);
        controller.AttachSource(source);
        controller.SetShape([SortDescription.Descending("Value")], [], [], null);
        Assert.Equal(new[] { "A", "B", "C" }, Ids(controller));

        // Re-push the same column set (the grid does this on ANY column change), then insert.
        controller.SetColumns(EngineColumns);
        source.Add(new Item("D", 99));
        controller.Flush();

        // The audit's repro sorted D by a never-extracted default key (last instead of first).
        Assert.Equal(new[] { "D", "A", "B", "C" }, Ids(controller));
    }
}
