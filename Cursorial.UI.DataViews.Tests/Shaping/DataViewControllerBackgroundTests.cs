using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The background lane (§2.6) under a manually-pumped runner — deterministic interleavings for the
/// integrity invariants: in-flight deferral, tick replay after publish, supersede-and-rerun, and
/// the slot-reclamation gate.
/// </summary>
public class DataViewControllerBackgroundTests
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

    /// <summary>Captures background work for manual, on-test-thread pumping (real threads would race the assertions).</summary>
    private sealed class ManualRunner
    {
        public readonly Queue<Action> Pending = new();
        public void Run(Action work) => Pending.Enqueue(work);
        public void PumpOne() => Pending.Dequeue()();
        public bool HasWork => Pending.Count > 0;
    }

    private static (DataViewController<Item> Controller, ManualRunner Runner, ObservableCollection<Item> Source)
        Setup(int rows, int threshold)
    {
        var source = new ObservableCollection<Item>();
        for (int i = 0; i < rows; i++)
            source.Add(new Item($"I{i:D3}", i * 10));

        var runner = new ManualRunner();
        var controller = new DataViewController<Item>
        {
            BackgroundThreshold = threshold,
            BackgroundRunner = runner.Run,
        };
        controller.SetColumns([
            new ShapingColumnDescription { Key = "Id", FieldName = nameof(Item.Id) },
            new ShapingColumnDescription { Key = "Value", FieldName = nameof(Item.Value) },
        ]);
        controller.AttachSource(source);

        // The attach itself routes to background past the threshold — settle so each test starts
        // from a quiescent published state.
        while (runner.HasWork)
            runner.PumpOne();

        return (controller, runner, source);
    }

    private static string[] Ids(DataViewController<Item> controller)
    {
        var ids = new List<string>();
        for (int i = 0; i < controller.Snapshot.Count; i++)
        {
            var row = controller.Snapshot.GetRow(i);
            if (!row.IsGroup)
                ids.Add(controller.RowAccessor(row.RowId).Id);
        }
        return ids.ToArray();
    }

    [Fact]
    public void Big_reshape_routes_to_background_and_publishes_on_pump()
    {
        var (controller, runner, _) = Setup(rows: 10, threshold: 5);
        using var _ = controller;
        int before = controller.Snapshot.Version;

        controller.SetShape([SortDescription.Descending("Value")], [], [], null);
        Assert.True(runner.HasWork);                       // routed off-thread
        Assert.Equal(before, controller.Snapshot.Version); // nothing published yet

        runner.PumpOne();                                  // the shape runs + posts inline (InlineScheduler)
        Assert.True(controller.Snapshot.Version > before);
        Assert.Equal("I009", Ids(controller)[0]);          // descending by value
    }

    [Fact]
    public void Small_sets_stay_synchronous()
    {
        var (controller, runner, _) = Setup(rows: 4, threshold: 100);
        using var _ = controller;
        controller.SetShape([SortDescription.Ascending("Value")], [], [], null);
        Assert.False(runner.HasWork);
        Assert.Equal(4, controller.Snapshot.DataRowCount);
    }

    [Fact]
    public void Tick_during_in_flight_defers_and_replays_after_publish()
    {
        var (controller, runner, source) = Setup(rows: 10, threshold: 5);
        using var _ = controller;
        controller.SetShape([SortDescription.Ascending("Value")], [], [], null);
        Assert.True(runner.HasWork);

        // A value change arrives mid-shape: it must neither extract nor repair now.
        int before = controller.Snapshot.Version;
        source[0].Value = 999;
        controller.Flush();                                // in-flight ⇒ no-op
        Assert.Equal(before, controller.Snapshot.Version); // still nothing published

        runner.PumpOne();                                  // publish + the deferred tick replays (posted inline)
        Assert.Equal("I000", Ids(controller)[^1]);         // the ticked row repositioned to the end
    }

    [Fact]
    public void Superseding_shape_drops_the_stale_result_and_reruns()
    {
        var (controller, runner, _) = Setup(rows: 10, threshold: 5);
        using var _ = controller;

        controller.SetShape([SortDescription.Ascending("Value")], [], [], null);   // shape A (in flight)
        controller.SetShape([SortDescription.Descending("Value")], [], [], null);  // shape B supersedes

        runner.PumpOne();                                  // A completes → stale → B re-runs (background again)
        Assert.True(runner.HasWork);
        runner.PumpOne();                                  // B publishes
        Assert.Equal("I009", Ids(controller)[0]);
        Assert.Equal("I000", Ids(controller)[^1]);
    }

    [Fact]
    public void Slot_reclamation_defers_while_in_flight()
    {
        var (controller, runner, source) = Setup(rows: 10, threshold: 5);
        using var _ = controller;
        controller.SetShape([SortDescription.Ascending("Value")], [], [], null);
        Assert.True(runner.HasWork);                       // in flight; the gate is up

        // Remove a row mid-shape, then add one: the add must NOT reuse the freed slot (the in-flight
        // permutation references it). New slots extend the high-water instead.
        var removed = source[3];
        source.RemoveAt(3);
        source.Add(new Item("NEW", 5));

        runner.PumpOne();                                  // stale-vs-tick replay resolves everything
        var ids = Ids(controller);
        Assert.DoesNotContain(removed.Id, ids);
        Assert.Contains("NEW", ids);
        // NEW sorts by value 5 — right after I000 (0) before I001 (10).
        Assert.Equal(new[] { "I000", "NEW" }, ids.Take(2).ToArray());
    }

    [Fact]
    public void Randomized_interleaving_matches_oracle()
    {
        var (controller, runner, source) = Setup(rows: 40, threshold: 10);
        using var _ = controller;
        controller.SetShape([SortDescription.Ascending("Value"), SortDescription.Ascending("Id")], [], [], null);
        var rng = new Random(7);

        for (int round = 0; round < 120; round++)
        {
            switch (rng.Next(5))
            {
                case 0:
                    source.Add(new Item($"N{round:D3}", rng.Next(1000)));
                    break;
                case 1 when source.Count > 5:
                    source.RemoveAt(rng.Next(source.Count));
                    break;
                case 2:
                    controller.SetShape(
                        [rng.Next(2) == 0 ? SortDescription.Ascending("Value") : SortDescription.Descending("Value"),
                         SortDescription.Ascending("Id")],
                        [], [], null);
                    break;
                default:
                    source[rng.Next(source.Count)].Value = rng.Next(1000);
                    break;
            }

            if (rng.Next(3) == 0 && runner.HasWork)
                runner.PumpOne();
            if (rng.Next(4) == 0)
                controller.Flush();
        }

        // Settle: pump everything out, flush the tail.
        while (runner.HasWork)
            runner.PumpOne();
        controller.Flush();
        while (runner.HasWork)
            runner.PumpOne();

        // The final snapshot must match the LINQ oracle for whatever shape won last. Read the shape
        // back from behavior: sort ascending to compare canonically.
        controller.SetShape([SortDescription.Ascending("Value"), SortDescription.Ascending("Id")], [], [], null);
        while (runner.HasWork)
            runner.PumpOne();
        controller.Flush();
        while (runner.HasWork)
            runner.PumpOne();

        var expected = source.OrderBy(i => i.Value).ThenBy(i => i.Id, StringComparer.CurrentCulture)
                             .Select(i => i.Id).ToArray();
        Assert.Equal(expected, Ids(controller));
    }
}
