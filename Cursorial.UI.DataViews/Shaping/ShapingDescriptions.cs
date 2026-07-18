using System.Linq.Expressions;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>The direction of one sort level.</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// One column the controller shapes over (the engine-facing description; the GRID's
/// <c>DataGridColumn</c> maps onto this — design doc §3.1). Identified by <see cref="Key"/>
/// (the grid column instance or a field-name string for headless use).
/// </summary>
public sealed class ShapingColumnDescription
{
    /// <summary>The column identity every other description references.</summary>
    public required object Key { get; init; }

    /// <summary>The dotted property path (mutually exclusive with <see cref="KeySelector"/>).</summary>
    public string? FieldName { get; init; }

    /// <summary>The typed row→key selector lambda (code-only authoring lane).</summary>
    public LambdaExpression? KeySelector { get; init; }

    /// <summary>Display format string (also the group-caption format for this column).</summary>
    public string? Format { get; init; }

    /// <summary>String sort/compare mode (CurrentCulture default; Ordinal is the perf opt-in).</summary>
    public StringComparison StringComparison { get; init; } = StringComparison.CurrentCulture;
}

/// <summary>One sort level: a column + direction.</summary>
public readonly record struct SortDescription(object ColumnKey, SortDirection Direction)
{
    public static SortDescription Ascending(object columnKey) => new(columnKey, SortDirection.Ascending);
    public static SortDescription Descending(object columnKey) => new(columnKey, SortDirection.Descending);
}

/// <summary>One grouping level: a column + the group-header sort direction (a group level IS a sort level in v1).</summary>
public readonly record struct GroupDescription(object ColumnKey, SortDirection Direction = SortDirection.Ascending);

/// <summary>One summary: a column + aggregate + optional format/display template ("{0}" = the value).</summary>
public readonly record struct SummaryDescription(object ColumnKey, AggregateKind Aggregate, string? Format = null, string? DisplayTemplate = null);

/// <summary>
/// The owner-thread marshaling seam (design doc invariant 1): the engine never references UI types;
/// the grid adapter bridges to the UIDispatcher, tests use an inline/recording stub.
/// </summary>
public interface IShapingScheduler
{
    /// <summary>Whether the caller is on the owner thread.</summary>
    bool CheckAccess();

    /// <summary>Posts <paramref name="action"/> to the owner thread (ordered, never inline).</summary>
    void Post(Action action);
}

/// <summary>The synchronous scheduler (headless single-threaded use; also the sync-lane default).</summary>
public sealed class InlineShapingScheduler : IShapingScheduler
{
    public static InlineShapingScheduler Instance { get; } = new();
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
}
