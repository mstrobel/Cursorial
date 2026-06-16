namespace Cursorial.UI.Controls;

/// <summary>
/// The membership delta of a <see cref="SelectionModel.SelectionChanged"/> notification (design doc §12.6 / ledger
/// CD-P9-11). The event fires only when the set of selected <i>items</i> changes; for the non-structural operations
/// the lists are a clean set-diff in one index space. A structural removal that drops selected indices reports those
/// dropped indices in <see cref="RemovedIndexes"/> (their pre-removal positions); a pure shift reports nothing (the
/// same items stay selected).
/// </summary>
public sealed class SelectionChangedEventArgs(IReadOnlyList<int> addedIndexes, IReadOnlyList<int> removedIndexes) : EventArgs
{
    /// <summary>The indices newly added to the selection.</summary>
    public IReadOnlyList<int> AddedIndexes { get; } = addedIndexes;

    /// <summary>The indices removed from the selection (pre-removal positions for a structural drop).</summary>
    public IReadOnlyList<int> RemovedIndexes { get; } = removedIndexes;
}
