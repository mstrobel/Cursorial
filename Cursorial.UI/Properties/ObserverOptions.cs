namespace Cursorial.UI;

/// <summary>
/// Options for <see cref="UIObject.AddObserver{T}(StyledProperty{T}, IValueObserver{T}, ObserverOptions)"/>.
/// </summary>
public readonly record struct ObserverOptions
{
    /// <summary>
    /// Subscribes the winning-<em>base</em> channel (ledger A20): the observer's
    /// <see cref="IValueObserver{T}.OnBaseValueChanged"/> fires <b>only</b> when the effective base —
    /// the winner among sub-Animation priorities — changes, delivering
    /// <c>(oldBase, newBase, isAnimated)</c>. Base changes are detected even under an active
    /// Animation entry (the sanctioned exception to "no notification on a base write under
    /// animation"). A subscription with this option receives no ordinary change deliveries —
    /// subscribe plainly in addition if both channels are needed; the two subscriptions dispose
    /// independently.
    /// </summary>
    public bool IncludeBaseChanges { get; init; }
}
