namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// Derives the group structure from a sorted view and flattens it with collapse state (design doc
/// §2.5): one O(V) walk per reshape using the per-level formatted keys (grouping prepended the group
/// columns to the sort, so groups are contiguous runs). Collapse state keys on the formatted-key
/// path and survives reshapes. Deliberately not incremental — the walk is memory-bandwidth cheap
/// relative to sort; big-V cycles route to the background lane (§2.6).
/// </summary>
internal static class ShapingGroups
{
    /// <summary>The reusable, pooled output of a derive+flatten pass.</summary>
    public sealed class Buffers
    {
        internal readonly List<GroupNode> Nodes = [];
        internal int[] Flat = [];
        internal int FlatLength;

        internal int[] RentFlat(int minimum)
        {
            if (Flat.Length < minimum)
                Flat = new int[Math.Max(minimum, Math.Max(64, Flat.Length * 2))];
            return Flat;
        }
    }

    /// <summary>
    /// Walks <paramref name="sortedView"/>[0..<paramref name="sortedLength"/>) and emits group nodes
    /// (per <paramref name="groupColumns"/> levels, boundaries by formatted-key change — the group
    /// key IS a sort level, so equal formatted keys are contiguous) plus the collapse-aware
    /// flattened view (group rows as <c>~nodeIndex</c>; rows/subgroups of a collapsed group are
    /// skipped). Ungrouped: the flat view is the sorted view verbatim (no copy — the caller aliases).
    /// </summary>
    /// <returns>The group nodes + flat view inside <paramref name="buffers"/>.</returns>
    public static void DeriveAndFlatten(
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
            return;
        }

        // Worst case flat length: every row + one group row per level per row (all groups distinct).
        var flat = buffers.RentFlat(sortedLength * (levels + 1));
        int write = 0;

        // Per-level open-group tracking.
        Span<int> openNode = stackalloc int[levels];
        openNode.Fill(-1);
        var openKeys = new string[levels];
        var openPaths = new string[levels];
        bool suppressed = false;   // inside a collapsed ancestor?
        int suppressLevel = 0;     // the level that collapsed

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

            if (changed >= 0)
            {
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

                    // Re-evaluate suppression from the outermost change.
                    if (level == changed)
                    {
                        suppressed = false;
                        for (int check = 0; check < changed; check++)
                        {
                            if (buffers.Nodes[openNode[check]].IsCollapsed)
                            {
                                suppressed = true;
                                suppressLevel = check;
                                break;
                            }
                        }
                    }

                    if (!suppressed)
                        flat[write++] = ~openNode[level];   // emit the group row

                    if (!suppressed && node.IsCollapsed)
                    {
                        suppressed = true;
                        suppressLevel = level;
                    }
                }
            }

            if (!suppressed)
                flat[write++] = slot;
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

        buffers.FlatLength = write;
    }
}
