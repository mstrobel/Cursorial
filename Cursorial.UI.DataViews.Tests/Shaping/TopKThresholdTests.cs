using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// The §9.5 TopK stats seam (the engine half of TopBottom conditional-format rules):
/// <see cref="DataViewController.TryGetTopKThreshold"/> returns the EXACT top/bottom-K threshold
/// over the visible (filtered) rows' non-null keys — ties include (every value equal to the
/// threshold qualifies), percent-K rounds up, nulls never qualify, results cache per published
/// snapshot and invalidate on every publish.
/// </summary>
public class TopKThresholdTests
{
    private sealed class Reading(string id, double? score, string label) : INotifyPropertyChanged
    {
        private double? _score = score;

        public string Id { get; } = id;
        public double? Score { get => _score; set => Set(ref _score, value); }
        public string Label { get; } = label;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }

    private static readonly ShapingColumnDescription[] Columns =
    [
        new() { Key = "Id", FieldName = nameof(Reading.Id) },
        new() { Key = "Score", FieldName = nameof(Reading.Score) },
        new() { Key = "Label", FieldName = nameof(Reading.Label) },
    ];

    private static DataViewController<Reading> NewController(IEnumerable<Reading> source)
    {
        var controller = new DataViewController<Reading>();
        controller.SetColumns(Columns);
        controller.AttachSource(source is ObservableCollection<Reading> oc ? oc : new List<Reading>(source));
        controller.SetShape([], [], [], null);
        return controller;
    }

    private static Reading[] Rows(params double?[] scores)
        => scores.Select((s, i) => new Reading($"R{i}", s, "L" + i)).ToArray();

    private static object? Threshold(DataViewController<Reading> controller, int count, bool percent, bool top)
    {
        Assert.True(controller.TryGetTopKThreshold("Score", count, percent, top, out object? threshold));
        return threshold;
    }

    [Fact]
    public void Top_k_returns_the_kth_largest()
    {
        using var controller = NewController(Rows(10, 20, 30, 40, 50));
        Assert.Equal(50.0, Threshold(controller, 1, percent: false, top: true));
        Assert.Equal(40.0, Threshold(controller, 2, percent: false, top: true));
        Assert.Equal(10.0, Threshold(controller, 5, percent: false, top: true));
    }

    [Fact]
    public void Bottom_k_returns_the_kth_smallest()
    {
        using var controller = NewController(Rows(10, 20, 30, 40, 50));
        Assert.Equal(10.0, Threshold(controller, 1, percent: false, top: false));
        Assert.Equal(20.0, Threshold(controller, 2, percent: false, top: false));
        Assert.Equal(50.0, Threshold(controller, 5, percent: false, top: false));
    }

    [Fact]
    public void Ties_include_all_values_at_the_threshold()
    {
        // Top-2 of [10, 20, 30, 40, 40, 50] → the 2nd largest is 40; BOTH 40s qualify under
        // >= threshold (the DevExpress ties-include semantics — 3 cells highlight, not 2).
        using var controller = NewController(Rows(10, 20, 30, 40, 40, 50));
        object? threshold = Threshold(controller, 2, percent: false, top: true);
        Assert.Equal(40.0, threshold);

        int qualifying = controller.Snapshot.DataRowCount == 6
            ? Enumerable.Range(0, 6).Count(i =>
                controller.RowAccessor(controller.Snapshot.GetRow(i).RowId).Score >= (double)threshold!)
            : -1;
        Assert.Equal(3, qualifying);

        // Bottom-2 of the same set: the 2nd smallest is 20 → exactly two cells <= 20.
        Assert.Equal(20.0, Threshold(controller, 2, percent: false, top: false));
    }

    [Fact]
    public void Percent_k_rounds_up()
    {
        // 7 non-null values; 25% → ceil(1.75) = 2 → the 2nd largest.
        using var controller = NewController(Rows(10, 20, 30, 40, 50, 60, 70));
        Assert.Equal(60.0, Threshold(controller, 25, percent: true, top: true));

        // 30% of 10 values → exactly 3.
        using var ten = NewController(Rows(1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
        Assert.Equal(8.0, Threshold(ten, 30, percent: true, top: true));

        // 100% → everything qualifies (threshold = the minimum).
        Assert.Equal(1.0, Threshold(ten, 100, percent: true, top: true));

        // A sliver of a percent still rounds up to K = 1.
        Assert.Equal(10.0, Threshold(ten, 1, percent: true, top: true));
    }

    [Fact]
    public void Nulls_are_excluded_from_population_and_thresholds()
    {
        // 3 nulls + [10, 20, 30, 40]: top-2 → 30 (nulls neither qualify nor count).
        using var controller = NewController(Rows(null, 10, null, 20, 30, null, 40));
        Assert.Equal(4, controller.VisibleNonNullCount("Score"));
        Assert.Equal(30.0, Threshold(controller, 2, percent: false, top: true));

        // Percent math runs over the NON-NULL population: 50% of 4 → 2 → 30 again.
        Assert.Equal(30.0, Threshold(controller, 50, percent: true, top: true));

        // Bottom-1 is the smallest non-null, never a null.
        Assert.Equal(10.0, Threshold(controller, 1, percent: false, top: false));
    }

    [Fact]
    public void K_larger_than_population_clamps_to_everything()
    {
        using var controller = NewController(Rows(10, 20, 30));
        Assert.Equal(10.0, Threshold(controller, 99, percent: false, top: true));
        Assert.Equal(30.0, Threshold(controller, 99, percent: false, top: false));
    }

    [Fact]
    public void Invalid_requests_return_false()
    {
        using var controller = NewController(Rows(10, 20, 30));

        Assert.False(controller.TryGetTopKThreshold("Score", 0, percent: false, top: true, out _));
        Assert.False(controller.TryGetTopKThreshold("Score", -3, percent: false, top: true, out _));
        Assert.False(controller.TryGetTopKThreshold("Missing", 1, percent: false, top: true, out _));
        Assert.Equal(0, controller.VisibleNonNullCount("Missing"));

        // All-null population: no threshold exists.
        using var empty = NewController(Rows(null, null));
        Assert.False(empty.TryGetTopKThreshold("Score", 1, percent: false, top: true, out object? threshold));
        Assert.Null(threshold);
        Assert.Equal(0, empty.VisibleNonNullCount("Score"));
    }

    [Fact]
    public void Thresholds_compute_over_the_filtered_view_only()
    {
        using var controller = NewController(Rows(10, 20, 30, 40, 50));
        controller.SetShape([], [], [], FilterNode.Condition("Score", FilterOperator.LessThan, 45.0));

        // Visible: [10, 20, 30, 40] — the 50 is filtered out of both the population and the answer.
        Assert.Equal(4, controller.VisibleNonNullCount("Score"));
        Assert.Equal(40.0, Threshold(controller, 1, percent: false, top: true));
        Assert.Equal(30.0, Threshold(controller, 2, percent: false, top: true));
    }

    [Fact]
    public void Cache_invalidates_when_a_publish_changes_values()
    {
        var rows = new ObservableCollection<Reading>(Rows(10, 20, 30, 40, 50));
        using var controller = NewController(rows);

        Assert.Equal(40.0, Threshold(controller, 2, percent: false, top: true));
        Assert.Equal(5, controller.VisibleNonNullCount("Score"));

        // A live INPC delta publishes a new snapshot — the cached threshold must not survive it.
        rows.Single(r => r.Id == "R4").Score = 5; // 50 → 5
        controller.Flush();

        Assert.Equal(30.0, Threshold(controller, 2, percent: false, top: true));

        // A structural change (add) re-invalidates again — including the population count.
        rows.Add(new Reading("R5", 100, "L5"));
        controller.Flush();
        Assert.Equal(6, controller.VisibleNonNullCount("Score"));
        Assert.Equal(40.0, Threshold(controller, 2, percent: false, top: true)); // [5,10,20,30,40,100] → 2nd largest = 40
    }

    [Fact]
    public void Repeated_requests_on_one_snapshot_are_consistent()
    {
        using var controller = NewController(Rows(10, 20, 30, 40, 50));
        Assert.Equal(40.0, Threshold(controller, 2, percent: false, top: true));
        Assert.Equal(40.0, Threshold(controller, 2, percent: false, top: true)); // the cache hit
        Assert.Equal(20.0, Threshold(controller, 2, percent: false, top: false)); // distinct cache key
        Assert.Equal(40.0, Threshold(controller, 2, percent: false, top: true));
    }

    [Fact]
    public void Non_numeric_columns_use_the_sort_comparison()
    {
        // TopK is comparison-generic — a string column's "largest" is its sort-last value.
        using var controller = NewController(Rows(1, 2, 3));
        Assert.True(controller.TryGetTopKThreshold("Label", 1, percent: false, top: true, out object? threshold));
        Assert.Equal("L2", threshold);
        Assert.True(controller.TryGetTopKThreshold("Label", 2, percent: false, top: false, out threshold));
        Assert.Equal("L1", threshold);
    }

    [Fact]
    public void Large_population_matches_the_full_sort_oracle()
    {
        var random = new Random(7);
        var scores = Enumerable.Range(0, 500)
                               .Select(_ => (double?)random.Next(0, 100))
                               .ToArray();
        using var controller = NewController(Rows(scores));

        var sorted = scores.Select(s => s!.Value).OrderByDescending(v => v).ToArray();
        foreach (int k in new[] { 1, 7, 100, 499, 500 })
        {
            Assert.Equal(sorted[k - 1], Threshold(controller, k, percent: false, top: true));
            Assert.Equal(sorted[^k], Threshold(controller, k, percent: false, top: false));
        }
    }
}
