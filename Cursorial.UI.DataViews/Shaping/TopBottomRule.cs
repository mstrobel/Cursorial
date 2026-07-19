namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// The top/bottom-N rule (design doc §2.7's deferred TopBottom entry; the mockup's "Top / Bottom —
/// Amount" pane): cells whose value ranks in the column's top (or bottom) <see cref="Count"/> items
/// (or percent of the filtered view) wear <see cref="Format"/>. Evaluation rides the engine's TopK
/// stats seam (§9.5): the per-publish threshold from
/// <see cref="DataViewController.TryGetTopKThreshold"/> feeds a once-compiled rank predicate over
/// the column's own sort comparison (ties include; null keys never qualify).
/// </summary>
public sealed class TopBottomRule : FormatRule
{
    /// <summary>The engine-seam gate — the §9.5 TopK stats block landed, so the lane is live
    /// (kept as the UI's single capability probe).</summary>
    internal static bool EngineSupportsTopK => true;

    /// <summary>Top (largest values) vs bottom (smallest).</summary>
    public required bool Top { get; init; }

    /// <summary>How many items (or, with <see cref="Percent"/>, what percentage of the view) qualify.</summary>
    public required int Count { get; init; }

    /// <summary>Whether <see cref="Count"/> is a percentage of the filtered view rather than an item count.</summary>
    public bool Percent { get; init; }

    /// <summary>The format qualifying cells wear.</summary>
    public required CellFormat Format { get; init; }
}
