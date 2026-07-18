namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The engine's row mirror with <b>stable slots</b> (design doc §2.1): row id = slot index, assigned
/// at insert and never shifted by other rows' inserts/removes — so per-column key vectors stay
/// aligned without mass re-extraction, and permutations/flag-sets refer to rows by a fixed id.
/// Freed slots recycle through a free list; each occupancy is stamped with a monotonic
/// <b>insertion sequence</b> (the sort tiebreak — slot indices are reuse-scrambled, sequences keep
/// equal-key rows in insertion order, the DevExpress-stable tie semantics; panel finding 2026-07-18).
/// The store also maintains the source-order sequence (the INCC index space) so collection events
/// map positions → slots.
/// </summary>
internal sealed class RowStore<TRow>
{
    private TRow[] _rows = [];
    private int[] _sourceOrder = [];       // source position → slot
    private int _count;                    // live rows (== source length)
    private int _slotHighWater;            // slots ever allocated (key-vector capacity requirement)
    private long _nextSequence;
    private readonly Stack<int> _freeSlots = new();
    private readonly List<int> _deferredFrees = [];
    private bool _deferReclamation;

    /// <summary>Slot → insertion sequence (monotonic per occupancy). Read by the compiled comparer's
    /// tiebreak via <see cref="ShapingCodegen.BuildSlotComparison"/> — through the field, so growth
    /// re-allocation is observed (the key-vector rule).</summary>
    internal long[] Sequences = [];

    /// <summary>Reference-typed rows map back to their slot for INPC dispatch (doc §2.6). Null for value-type rows.</summary>
#pragma warning disable CS8714 // TRow is unconstrained since §9.6 (struct rows); the map exists ONLY for reference rows and stores no nulls
    private readonly Dictionary<TRow, int>? _slotByRow =
        typeof(TRow).IsValueType ? null : new Dictionary<TRow, int>(ReferenceEqualityComparer<TRow>.Instance);
#pragma warning restore CS8714

    /// <summary>Live row count (the source's length).</summary>
    public int Count => _count;

    /// <summary>The slot capacity key vectors must cover (high-water; freed slots stay within it).</summary>
    public int SlotCapacity => _slotHighWater;

    /// <summary>The row occupying <paramref name="slot"/> (undefined for freed slots — callers track liveness).</summary>
    public TRow GetRow(int slot) => _rows[slot];

    /// <summary>The slot at source position <paramref name="index"/>.</summary>
    public int SlotAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
        return _sourceOrder[index];
    }

    /// <summary>Enumerates the live slots in source order (the reset/rebuild walk).</summary>
    public ReadOnlySpan<int> SourceOrder => _sourceOrder.AsSpan(0, _count);

    /// <summary>Resolves a reference row instance to its slot; −1 when unknown (or value-typed rows).</summary>
    public int SlotOf(TRow row)
        => _slotByRow is not null && _slotByRow.TryGetValue(row, out int slot) ? slot : -1;

    /// <summary>Inserts <paramref name="row"/> at source position <paramref name="index"/>; returns its slot.</summary>
    public int Insert(int index, TRow row)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_count, nameof(index));

        int slot;
        if (_freeSlots.Count > 0)
        {
            slot = _freeSlots.Pop();
        }
        else
        {
            slot = _slotHighWater++;
            if (slot >= _rows.Length)
                Array.Resize(ref _rows, Math.Max(16, _rows.Length * 2));
        }

        _rows[slot] = row;
        if (slot >= Sequences.Length)
            Array.Resize(ref Sequences, Math.Max(16, Math.Max(slot + 1, Sequences.Length * 2)));
        Sequences[slot] = _nextSequence++;

        if (_count == _sourceOrder.Length)
            Array.Resize(ref _sourceOrder, Math.Max(16, _sourceOrder.Length * 2));
        if (index < _count)
            Array.Copy(_sourceOrder, index, _sourceOrder, index + 1, _count - index);
        _sourceOrder[index] = slot;
        _count++;

        // Duplicate reference instances: the LAST insert wins the INPC mapping (documented — doc §2.6).
        if (_slotByRow is not null && row is not null)
            _slotByRow[row] = slot;

        return slot;
    }

    /// <summary>Removes the row at source position <paramref name="index"/>; returns its freed slot.</summary>
    public int RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));

        int slot = _sourceOrder[index];
        var row = _rows[slot];

        Array.Copy(_sourceOrder, index + 1, _sourceOrder, index, _count - index - 1);
        _count--;

        if (_slotByRow is not null && row is not null && _slotByRow.TryGetValue(row, out int mapped) && mapped == slot)
            _slotByRow.Remove(row);

        _rows[slot] = default!;   // release the reference
        // §2.6 invariant 2 (both halves — final-audit fix): a freed slot ALWAYS parks and recycles
        // only via ReleaseDeferredFrees, which the controller calls after a publish that excludes
        // the slot (and never while a shape is in flight) — no published permutation can watch its
        // slot's row swapped under it, and no id-keyed consumer sees a recycled id before pruning.
        _deferredFrees.Add(slot);
        return slot;
    }

    /// <summary>Releases parked freed slots to the free list (no-op while <see cref="DeferReclamation"/> is up).</summary>
    public void ReleaseDeferredFrees()
    {
        if (_deferReclamation || _deferredFrees.Count == 0)
            return;
        foreach (int slot in _deferredFrees)
            _freeSlots.Push(slot);
        _deferredFrees.Clear();
    }

    /// <summary>
    /// Gates slot reclamation while a background shape references the current slot space (design doc
    /// §2.6 invariant 2): with the gate up, freed slots park in a deferred list — an insert can never
    /// reuse a slot an in-flight or published snapshot still references. Lowering the gate releases
    /// the parked slots to the free list.
    /// </summary>
    public bool DeferReclamation
    {
        get => _deferReclamation;
        set => _deferReclamation = value; // release rides ReleaseDeferredFrees (publish-gated)
    }

    /// <summary>
    /// Replaces the row stored at <paramref name="slot"/> (row id) — the §9.6 value-type edit
    /// write-back: the compiled mutator produces a modified COPY of the struct row; storing it back
    /// here is what makes the edit real (a setter through a box mutates a throwaway copy). Also
    /// re-points the INPC identity map for reference rows. The caller owns dirty-marking/reshaping;
    /// undefined for freed slots (callers track liveness, the <see cref="GetRow"/> contract).
    /// </summary>
    public void SetRow(int slot, in TRow row)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)_slotHighWater, nameof(slot));

        if (_slotByRow is not null)
        {
            var old = _rows[slot];
            if (old is not null && _slotByRow.TryGetValue(old, out int mapped) && mapped == slot)
                _slotByRow.Remove(old);
            if (row is not null)
                _slotByRow[row] = slot;
        }

        _rows[slot] = row;
    }

    /// <summary>Replaces the row at source position <paramref name="index"/> in place; returns its slot (keys must re-extract).</summary>
    public int Replace(int index, TRow row)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));

        int slot = _sourceOrder[index];
        var old = _rows[slot];
        if (_slotByRow is not null)
        {
            if (old is not null && _slotByRow.TryGetValue(old, out int mapped) && mapped == slot)
                _slotByRow.Remove(old);
            if (row is not null)
                _slotByRow[row] = slot;
        }

        _rows[slot] = row;
        return slot;
    }

    /// <summary>Moves the row at source position <paramref name="oldIndex"/> to <paramref name="newIndex"/> (slot unchanged — source order is not a shaping input beyond the id tiebreak's insert order).</summary>
    public void Move(int oldIndex, int newIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)oldIndex, (uint)_count, nameof(oldIndex));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)newIndex, (uint)_count, nameof(newIndex));

        int slot = _sourceOrder[oldIndex];
        if (oldIndex < newIndex)
            Array.Copy(_sourceOrder, oldIndex + 1, _sourceOrder, oldIndex, newIndex - oldIndex);
        else
            Array.Copy(_sourceOrder, newIndex, _sourceOrder, newIndex + 1, oldIndex - newIndex);
        _sourceOrder[newIndex] = slot;
    }

    /// <summary>Clears everything (the INCC Reset path — the controller rebuilds from the source).
    /// The insertion-sequence counter keeps counting: sequences are unique per occupancy forever.</summary>
    public void Clear()
    {
        if (_slotByRow is not null)
            _slotByRow.Clear();
        Array.Clear(_rows, 0, _slotHighWater);
        _count = 0;
        _slotHighWater = 0;
        _freeSlots.Clear();
        _deferredFrees.Clear(); // parked slots are meaningless after a reset
    }

    /// <summary>Reference-equality comparer so INPC row mapping never runs user Equals overrides.</summary>
    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
