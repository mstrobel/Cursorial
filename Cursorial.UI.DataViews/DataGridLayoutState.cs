namespace Cursorial.UI.DataViews;

/// <summary>
/// The serializable column-layout description <see cref="DataGrid.GetColumnLayout"/> emits and
/// <see cref="DataGrid.ApplyColumnLayout"/> restores (the DevExpress save/restore-layout
/// expectation, kept minimal by design): plain records of strings/bools with no serializer
/// affinity — System.Text.Json, XML, or a hand-rolled INI all round-trip it. Columns are keyed by
/// <see cref="DataGridColumn.FieldName"/> (identity across sessions is the field, never the
/// instance); widths ride <see cref="DataGridLength"/>'s canonical string form
/// (<c>"12"</c>/<c>"Auto"</c>/<c>"2*"</c> — its own Parse/ToString pair is the round-trip
/// contract); display order is the list order.
/// </summary>
public sealed record DataGridLayoutState
{
    /// <summary>Per-column state in display order.</summary>
    public required IReadOnlyList<DataGridColumnLayoutEntry> Columns { get; init; }

    /// <summary>The sort levels, outermost first (group levels are NOT repeated here).</summary>
    public required IReadOnlyList<DataGridSortLayoutEntry> Sorts { get; init; }

    /// <summary>The grouping levels, outermost first.</summary>
    public required IReadOnlyList<DataGridGroupLayoutEntry> Groups { get; init; }
}

/// <summary>One column's persisted layout facts (a null <see cref="FieldName"/> marks a
/// KeySelector-only column — recorded for completeness, skipped on restore).</summary>
public sealed record DataGridColumnLayoutEntry(string? FieldName, string Width, bool Visible);

/// <summary>One persisted sort level.</summary>
public sealed record DataGridSortLayoutEntry(string? FieldName, bool Descending);

/// <summary>One persisted grouping level.</summary>
public sealed record DataGridGroupLayoutEntry(string? FieldName, bool Descending);
