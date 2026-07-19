namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// Derives the group structure from a sorted view and flattens it with collapse state (design doc
/// §2.5): one O(V) walk per reshape using the per-level formatted keys (grouping prepended the group
/// columns to the sort, so groups are contiguous runs). Collapse state keys on the formatted-key
/// path and survives reshapes. Deliberately not incremental — the walk is memory-bandwidth cheap
/// relative to sort; big-V cycles route to the background lane (§2.6).
/// <para>
/// §9.5 (order groups by summary) splits the pass in two: <see cref="Derive"/> emits the nodes and
/// the sibling-order substrate (<see cref="Buffers.ChildOrder"/> in document order), the caller may
/// permute sibling segments (a PER-PUBLISH projection — the key-ordered view itself is never
/// touched: it stays the incremental-repair substrate), then <see cref="Flatten"/> walks the
/// (possibly permuted) sibling order. <see cref="DeriveAndFlatten"/> is the no-projection
/// convenience composing both.
/// </para>
/// </summary>
internal static class ShapingGroups
{
    /// <summary>The reusable, pooled output of a derive+flatten pass.</summary>
    public sealed class Buffers
    {
        internal readonly List<GroupNode> Nodes = [];
        internal int[] Flat = [];
        internal int FlatLength;

        // The §9.5 sibling-order substrate (filled by Derive in document order; segments may be
        // permuted between Derive and Flatten): ChildOrder holds node indices — the root segment
        // first ([0, RootCount)), then each node's children segment at [ChildStart[n],
        // ChildStart[n] + ChildCount[n]). ChildFill is the derive-time fill-cursor scratch.
        internal int[] ChildOrder = [];
        internal int[] ChildStart = [];
        internal int[] ChildCount = [];
        internal int[] ChildFill = [];
        internal int RootCount;

        internal int[] RentFlat(int minimum)
        {
            if (Flat.Length < minimum)
                Flat = new int[Math.Max(minimum, Math.Max(64, Flat.Length * 2))];
            return Flat;
        }

        internal static void EnsureLength(ref int[] array, int minimum)
        {
            if (array.Length < minimum)
                array = new int[Math.Max(minimum, Math.Max(16, array.Length * 2))];
        }
    }

    /// <summary>
    /// The original single-pass shape: <see cref="Derive"/> then <see cref="Flatten"/> with the
    /// document (key-derivation) sibling order — no summary projection. Ungrouped: the flat view is
    /// the sorted view verbatim (no copy — the caller aliases; <see cref="Buffers.FlatLength"/> is
    /// the −1 sentinel).
    /// </summary>
    public static void DeriveAndFlatten(
        int[] sortedView, int sortedLength,
        IReadOnlyList<ShapedColumn> groupColumns,
        IReadOnlySet<string> collapsedPaths,
        Buffers buffers)
    {
        Derive(sortedView, sortedLength, groupColumns, collapsedPaths, buffers);
        if (groupColumns.Count > 0)
            Flatten(sortedView, groupColumns.Count, buffers);
    }

    /// <summary>
    /// The derive pass: walks <paramref name="sortedView"/>[0..<paramref name="sortedLength"/>) and
    /// emits group nodes (per <paramref name="groupColumns"/> levels, boundaries by key change via
    /// the column COMPARISON — the group key IS a sort level, so equal keys are contiguous), with
    /// counts, parent links, collapse-path keys, and the sibling-order substrate
    /// (<see cref="Buffers.ChildOrder"/> et al., document order). Emits NO flat view — the caller
    /// (optionally) permutes sibling segments, then calls <see cref="Flatten"/>. Ungrouped
    /// (<paramref name="groupColumns"/> empty): clears the nodes and sets the −1 alias sentinel on
    /// <see cref="Buffers.FlatLength"/>.
    /// </summary>
    public static void Derive(
        int[] sortedView, int sortedLength,
        IReadOnlyList<ShapedColumn> groupColumns,
        IReadOnlySet<string> collapsedPaths,
        Buffers buffers)
    {
        ArgumentNullException.ThrowIfNull(sortedView);
        ArgumentNullException.ThrowIfNull(groupColumns);
        ArgumentNullException.ThrowIfNull(collapsedPaths);
        ArgumentNullException.ThrowIfNull(buffers);

        buffers.Nodes.Clear();
        int levels = groupColumns.Count;

        if (levels == 0)
        {
            // Ungrouped: flat = sorted (alias — the snapshot carries both references to one array).
            buffers.FlatLength = -1; // sentinel: alias the sorted view
            buffers.RootCount = 0;
            return;
        }

        // Per-level open-group tracking.
        Span<int> openNode = stackalloc int[levels];
        openNode.Fill(-1);
        var openKeys = new string[levels];
        var openPaths = new string[levels];

        int previousSlot = -1;
        for (int i = 0; i < sortedLength; i++)
        {
            int slot = sortedView[i];

            // Find the outermost level whose key changed — via the column COMPARISON (zero ⇒ same
            // group; the group key is a sort level, so equality is exactly key-run membership).
            // Formatting happens once per NEW group, never per row (§2.5 — the O(V) walk must not
            // allocate V×levels strings).
            int changed = -1;
            if (previousSlot < 0)
            {
                changed = 0;
            }
            else
            {
                for (int level = 0; level < levels; level++)
                {
                    if (groupColumns[level].CompareSlots(previousSlot, slot) != 0)
                    {
                        changed = level;
                        break;
                    }
                }
            }
            previousSlot = slot;

            if (changed < 0)
                continue;

            // Open new groups at the changed level and every inner level.
            for (int level = changed; level < levels; level++)
            {
                openKeys[level] = groupColumns[level].FormatSlot(slot);

                string parentPath = level == 0 ? string.Empty : openPaths[level - 1];
                string path = parentPath.Length == 0 ? openKeys[level] : parentPath + "¦" + openKeys[level];
                openPaths[level] = path;

                var node = new GroupNode
                {
                    Level = level,
                    Parent = level == 0 ? -1 : openNode[level - 1],
                    FormattedKey = openKeys[level],
                    SortedStart = i,
                    RowCount = 0, // patched below via the counting pass
                    PathKey = path,
                    IsCollapsed = collapsedPaths.Contains(path),
                };
                openNode[level] = buffers.Nodes.Count;
                buffers.Nodes.Add(node);
            }
        }

        // Counting pass: RowCount = run length per node (next sibling-or-end boundary). Nodes are in
        // document order; a node's run ends where the next node at its level-or-outer starts.
        var nodes = buffers.Nodes;
        for (int n = 0; n < nodes.Count; n++)
        {
            int end = sortedLength;
            for (int m = n + 1; m < nodes.Count; m++)
            {
                if (nodes[m].Level <= nodes[n].Level)
                {
                    end = nodes[m].SortedStart;
                    break;
                }
            }
            // GroupNode.RowCount is init-only for the public surface; patch through the backing list.
            nodes[n] = new GroupNode
            {
                Level = nodes[n].Level,
                Parent = nodes[n].Parent,
                FormattedKey = nodes[n].FormattedKey,
                SortedStart = nodes[n].SortedStart,
                RowCount = end - nodes[n].SortedStart,
                PathKey = nodes[n].PathKey,
                IsCollapsed = nodes[n].IsCollapsed,
                Summaries = nodes[n].Summaries,
            };
        }

        BuildChildOrder(buffers);
    }

    /// <summary>
    /// Builds the sibling-order substrate in document order: for each parent, its children's node
    /// indices in a contiguous <see cref="Buffers.ChildOrder"/> segment (roots first). Document
    /// order within a segment IS key order (the derive walk follows the key-ordered view), which is
    /// what makes the §9.5 tie rule ("ties keep key order") fall out of a total-order sort.
    /// </summary>
    private static void BuildChildOrder(Buffers buffers)
    {
        var nodes = buffers.Nodes;
        int count = nodes.Count;
        Buffers.EnsureLength(ref buffers.ChildOrder, count);
        Buffers.EnsureLength(ref buffers.ChildStart, count);
        Buffers.EnsureLength(ref buffers.ChildCount, count);
        Buffers.EnsureLength(ref buffers.ChildFill, count);

        int roots = 0;
        Array.Clear(buffers.ChildCount, 0, count);
        for (int n = 0; n < count; n++)
        {
            int parent = nodes[n].Parent;
            if (parent < 0)
                roots++;
            else
                buffers.ChildCount[parent]++;
        }
        buffers.RootCount = roots;

        int cursor = roots;
        for (int n = 0; n < count; n++)
        {
            buffers.ChildStart[n] = cursor;
            buffers.ChildFill[n] = cursor;
            cursor += buffers.ChildCount[n];
        }

        int rootFill = 0;
        for (int n = 0; n < count; n++)
        {
            int parent = nodes[n].Parent;
            if (parent < 0)
                buffers.ChildOrder[rootFill++] = n;
            else
                buffers.ChildOrder[buffers.ChildFill[parent]++] = n;
        }
    }

    /// <summary>
    /// The flatten pass: walks the sibling order (<see cref="Buffers.ChildOrder"/> — document order
    /// unless the caller permuted segments) depth-first and emits the collapse-aware flattened view
    /// into <see cref="Buffers.Flat"/> (group rows as <c>~nodeIndex</c>; rows/subgroups of a
    /// collapsed group are skipped; a group's data rows emit in the sorted view's KEY order — the
    /// §9.5 projection reorders siblings, never rows).
    /// </summary>
    public static void Flatten(int[] sortedView, int levels, Buffers buffers)
    {
        ArgumentNullException.ThrowIfNull(sortedView);
        ArgumentNullException.ThrowIfNull(buffers);

        if (levels == 0)
        {
            buffers.FlatLength = -1; // ungrouped alias sentinel (Derive parity)
            return;
        }

        int rowTotal = 0;
        for (int r = 0; r < buffers.RootCount; r++)
            rowTotal += buffers.Nodes[buffers.ChildOrder[r]].RowCount;

        var flat = buffers.RentFlat(buffers.Nodes.Count + rowTotal);
        int write = 0;
        for (int r = 0; r < buffers.RootCount; r++)
            write = EmitGroup(buffers.ChildOrder[r], sortedView, levels, buffers, flat, write);
        buffers.FlatLength = write;
    }

    private static int EmitGroup(int nodeIndex, int[] sortedView, int levels, Buffers buffers, int[] flat, int write)
    {
        flat[write++] = ~nodeIndex; // the group row
        var node = buffers.Nodes[nodeIndex];
        if (node.IsCollapsed)
            return write; // rows + subgroups suppressed

        if (node.Level == levels - 1)
        {
            // Innermost level: the group's data rows, in KEY order (the sorted-view run verbatim).
            for (int i = 0; i < node.RowCount; i++)
                flat[write++] = sortedView[node.SortedStart + i];
        }
        else
        {
            int start = buffers.ChildStart[nodeIndex];
            int count = buffers.ChildCount[nodeIndex];
            for (int c = 0; c < count; c++)
                write = EmitGroup(buffers.ChildOrder[start + c], sortedView, levels, buffers, flat, write);
        }
        return write;
    }
}
