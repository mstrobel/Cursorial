namespace Cursorial.UI.DataViews.Shaping;

/// <summary>One row of the flattened view (the snapshot's public read surface — design doc §2.5).</summary>
public readonly struct ViewRowInfo
{
    /// <summary>Whether this view row is a group header (else a data row).</summary>
    public required bool IsGroup { get; init; }

    /// <summary>The data row's id (slot) — data rows only (−1 for group rows).</summary>
    public required int RowId { get; init; }

    /// <summary>The group node index — group rows only (−1 for data rows).</summary>
    public required int GroupNodeIndex { get; init; }

    /// <summary>The nesting level (0 = top; a data row's level is its innermost group's level + 1; 0 when ungrouped).</summary>
    public required int Level { get; init; }
}

/// <summary>One group in the derived structure (§2.5): a contiguous run of the sorted view.</summary>
public sealed class GroupNode
{
    /// <summary>The grouping level (0 = outermost).</summary>
    public required int Level { get; init; }

    /// <summary>The parent node index (−1 at level 0).</summary>
    public required int Parent { get; init; }

    /// <summary>The group's formatted key (the display caption; also the collapse-state key component).</summary>
    public required string FormattedKey { get; init; }

    /// <summary>First index of the group's data rows in the SORTED view permutation.</summary>
    public required int SortedStart { get; init; }

    /// <summary>The group's data-row count (all levels — contiguous by construction).</summary>
    public required int RowCount { get; init; }

    /// <summary>The collapse-state path key ("level0Key¦level1Key¦…").</summary>
    public required string PathKey { get; init; }

    /// <summary>Whether the group renders collapsed (its rows + subgroups hidden).</summary>
    public bool IsCollapsed { get; internal set; }

    /// <summary>Formatted per-group summary cells, aligned with the summary descriptions (filled by the controller).</summary>
    public string[] Summaries { get; internal set; } = [];
}

/// <summary>
/// The published artifact the grid binds (design doc §2.6): the sorted permutation, the derived
/// groups, and the collapse-aware flattened view. Immutable-by-contract after publish; versioned so
/// stale publishes drop. Group entries in the flat view encode as <c>~nodeIndex</c> (sign-
/// discriminated — row ids keep the full non-negative range).
/// </summary>
public sealed class DataViewSnapshot
{
    internal DataViewSnapshot(int version, int[] sortedView, int sortedLength, GroupNode[] groups, int[] flatView, int flatLength)
    {
        Version = version;
        SortedView = sortedView;
        SortedLength = sortedLength;
        Groups_ = groups;
        FlatView = flatView;
        FlatLength = flatLength;
    }

    /// <summary>The shape version (monotonic; the staleness gate).</summary>
    public int Version { get; }

    /// <summary>The sorted, filtered permutation of row ids (internal backing — length <see cref="SortedLength"/>).</summary>
    internal int[] SortedView { get; }
    internal int SortedLength { get; }

    /// <summary>The derived group nodes (empty when ungrouped), indexable by <see cref="ViewRowInfo.GroupNodeIndex"/>.</summary>
    public IReadOnlyList<GroupNode> Groups => Groups_;
    internal GroupNode[] Groups_ { get; }

    /// <summary>The flattened view (internal backing — group rows as ~nodeIndex).</summary>
    internal int[] FlatView { get; }
    internal int FlatLength { get; }

    /// <summary>The number of view rows (group headers + visible data rows).</summary>
    public int Count => FlatLength;

    /// <summary>Total visible data rows (post-filter, pre-collapse).</summary>
    public int DataRowCount => SortedLength;

    /// <summary>Reads one view row.</summary>
    public ViewRowInfo GetRow(int viewIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)viewIndex, (uint)FlatLength, nameof(viewIndex));

        int entry = FlatView[viewIndex];
        if (entry >= 0)
        {
            return new ViewRowInfo { IsGroup = false, RowId = entry, GroupNodeIndex = -1, Level = DataRowLevel };
        }

        int nodeIndex = ~entry;
        return new ViewRowInfo { IsGroup = true, RowId = -1, GroupNodeIndex = nodeIndex, Level = Groups_[nodeIndex].Level };
    }

    /// <summary>The view index of a data row id, or −1 when not visible (collapsed/filtered). O(flat) — cold callers only; the grid maintains its own inverse map.</summary>
    public int IndexOfRow(int rowId)
    {
        for (int i = 0; i < FlatLength; i++)
        {
            if (FlatView[i] == rowId)
                return i;
        }
        return -1;
    }

    /// <summary>Data rows' level = grouping depth (uniform — every leaf sits under the full group chain).</summary>
    internal int DataRowLevel { get; init; }

    internal static DataViewSnapshot Empty { get; } = new(0, [], 0, [], [], 0);
}
