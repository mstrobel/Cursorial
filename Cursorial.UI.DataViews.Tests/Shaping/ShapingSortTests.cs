using Cursorial.UI.DataViews.Shaping;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.DataViews.Shaping;

/// <summary>
/// Differential tests for <see cref="ShapingSort"/> against a known-stable reference
/// (<see cref="Enumerable.OrderBy{TSource,TKey}(IEnumerable{TSource}, Func{TSource,TKey})"/>) across
/// the adversarial input families TimSort implementations get wrong: run boundaries, merge tails,
/// gallop exits, the corrected stack invariants, and stability under non-total comparisons.
/// </summary>
public class ShapingSortTests
{
    private static int[] Identity(int n) => Enumerable.Range(0, n).ToArray();

    /// <summary>
    /// Sorts a permutation of [0, n) whose element order is defined by <paramref name="values"/>
    /// (permutation entries index into it), then asserts equivalence with the stable reference.
    /// The comparison is VALUE-only (not total) so stability is genuinely exercised: equal values
    /// must keep ascending-index order.
    /// </summary>
    private static void AssertSortsLikeStableReference(int[] values)
    {
        int n = values.Length;
        int[] permutation = Identity(n);
        var scratch = new SortScratch();

        ShapingSort.Sort(permutation, n, (a, b) => values[a].CompareTo(values[b]), scratch);

        int[] expected = Identity(n).OrderBy(i => values[i]).ToArray(); // OrderBy is documented stable
        Assert.Equal(expected, permutation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(31)]  // below MinMerge — the pure binary-insertion path
    [InlineData(32)]  // exactly MinMerge — first run-stack path
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public void Random_input_sorts_stably(int n)
    {
        var rng = new Random(42 + n);
        AssertSortsLikeStableReference(Enumerable.Range(0, n).Select(_ => rng.Next(0, Math.Max(1, n / 2))).ToArray());
    }

    [Theory]
    [InlineData(100)]
    [InlineData(4097)]
    public void Already_sorted_input(int n)
        => AssertSortsLikeStableReference(Enumerable.Range(0, n).ToArray());

    [Theory]
    [InlineData(100)]
    [InlineData(4097)]
    public void Reverse_sorted_input(int n)
        => AssertSortsLikeStableReference(Enumerable.Range(0, n).Reverse().ToArray());

    [Fact]
    public void All_equal_input()
        => AssertSortsLikeStableReference(Enumerable.Repeat(7, 500).ToArray());

    [Theory]
    [InlineData(1)]    // single perturbation
    [InlineData(50)]   // 5%
    [InlineData(200)]  // 20%
    public void Mostly_sorted_input(int perturbations)
    {
        var rng = new Random(perturbations);
        int[] values = Enumerable.Range(0, 1000).ToArray();
        for (int i = 0; i < perturbations; i++)
        {
            int a = rng.Next(values.Length), b = rng.Next(values.Length);
            (values[a], values[b]) = (values[b], values[a]);
        }
        AssertSortsLikeStableReference(values);
    }

    [Fact]
    public void Organ_pipe_input()
    {
        // Ascend then descend — exactly two natural runs, exercising one big MergeHi/Lo.
        int[] values = Enumerable.Range(0, 500).Concat(Enumerable.Range(0, 500).Reverse()).ToArray();
        AssertSortsLikeStableReference(values);
    }

    [Fact]
    public void Sawtooth_input()
    {
        // Many short ascending runs — stresses the run stack + collapse invariants.
        int[] values = new int[1024];
        for (int i = 0; i < values.Length; i++)
            values[i] = i % 8;
        AssertSortsLikeStableReference(values);
    }

    [Fact]
    public void Few_uniques_block_input()
    {
        // Long streaks of equal keys — the galloping mode's bulk-copy paths + stability tails.
        var rng = new Random(9);
        int[] values = new int[2048];
        int i = 0;
        while (i < values.Length)
        {
            int v = rng.Next(4);
            int run = Math.Min(values.Length - i, rng.Next(1, 200));
            for (int k = 0; k < run; k++)
                values[i++] = v;
        }
        AssertSortsLikeStableReference(values);
    }

    [Fact]
    public void Alternating_input_defeats_galloping_gracefully()
    {
        // Strict alternation forces constant gallop-mode entry/exit (the minGallop penalty path).
        int[] values = new int[1500];
        for (int i = 0; i < values.Length; i++)
            values[i] = i % 2 == 0 ? i : i - 1;
        AssertSortsLikeStableReference(values);
    }

    [Fact]
    public void Large_random_differential()
    {
        var rng = new Random(1234);
        AssertSortsLikeStableReference(Enumerable.Range(0, 100_000).Select(_ => rng.Next()).ToArray());
    }

    [Fact]
    public void Large_mostly_sorted_differential()
    {
        var rng = new Random(4321);
        int[] values = Enumerable.Range(0, 100_000).ToArray();
        for (int i = 0; i < 1000; i++)
            values[rng.Next(values.Length)] = rng.Next();
        AssertSortsLikeStableReference(values);
    }

    [Fact]
    public void Randomized_pattern_fuzz()
    {
        // 200 random shapes across sizes/duplication factors — the catch-all differential net.
        var rng = new Random(77);
        for (int round = 0; round < 200; round++)
        {
            int n = rng.Next(0, 700);
            int distinct = Math.Max(1, rng.Next(1, Math.Max(2, n)));
            int[] values = new int[n];
            for (int i = 0; i < n; i++)
                values[i] = rng.Next(distinct);

            // Occasionally pre-sort segments to fabricate natural runs.
            if (n > 10 && rng.Next(3) == 0)
            {
                int segments = rng.Next(1, 6);
                int start = 0;
                for (int s = 0; s < segments && start < n; s++)
                {
                    int length = rng.Next(1, n - start + 1);
                    Array.Sort(values, start, length);
                    if (rng.Next(2) == 0)
                        Array.Reverse(values, start, length);
                    start += length;
                }
            }

            AssertSortsLikeStableReference(values);
        }
    }

    [Fact]
    public void Scratch_reuse_across_sorts_is_clean()
    {
        // One scratch across many differently-sized sorts (the steady-state reuse contract).
        var scratch = new SortScratch();
        var rng = new Random(5);
        for (int round = 0; round < 30; round++)
        {
            int n = rng.Next(0, 5000);
            int[] values = Enumerable.Range(0, n).Select(_ => rng.Next(100)).ToArray();
            int[] permutation = Identity(n);
            ShapingSort.Sort(permutation, n, (a, b) => values[a].CompareTo(values[b]), scratch);
            Assert.Equal(Identity(n).OrderBy(i => values[i]).ToArray(), permutation);
        }
    }

    [Fact]
    public void Length_bounds_are_validated()
    {
        var scratch = new SortScratch();
        Assert.Throws<ArgumentOutOfRangeException>(() => ShapingSort.Sort(new int[4], 5, (a, b) => a - b, scratch));
        Assert.Throws<ArgumentNullException>(() => ShapingSort.Sort(null!, 0, (a, b) => a - b, scratch));
        Assert.Throws<ArgumentNullException>(() => ShapingSort.Sort(new int[4], 4, null!, scratch));
    }

    [Fact]
    public void Partial_length_sorts_only_the_prefix()
    {
        int[] values = [5, 3, 4, 1, 2, 99, 98, 97];
        int[] permutation = Identity(8);
        ShapingSort.Sort(permutation, 5, (a, b) => values[a].CompareTo(values[b]), new SortScratch());
        Assert.Equal(new[] { 3, 4, 1, 2, 0 }, permutation.Take(5).ToArray());
        Assert.Equal(new[] { 5, 6, 7 }, permutation.Skip(5).ToArray()); // untouched tail
    }
}
