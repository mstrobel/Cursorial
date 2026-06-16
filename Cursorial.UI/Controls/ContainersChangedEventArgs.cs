namespace Cursorial.UI.Controls;

/// <summary>What an <see cref="ItemContainerGenerator"/> did to a contiguous range of containers (design doc §12.6 — the visualization seam).</summary>
public enum ContainersChangedAction
{
    /// <summary>Containers were realized (created + prepared) for the range.</summary>
    Realized,

    /// <summary>Containers were unrealized (cleared + detached) for the range.</summary>
    Unrealized,

    /// <summary>A container moved to <see cref="ContainersChangedEventArgs.StartIndex"/> (reorder only — no realize/unrealize).</summary>
    Moved,

    /// <summary>Every container was unrealized — the host should drop all and re-adopt from scratch.</summary>
    Reset
}

/// <summary>
/// A range-based notification from <see cref="ItemContainerGenerator"/> (design doc §12.6): the
/// <see cref="ItemsPresenter"/> consumes it to adopt/release containers into its panel. Range-shaped so a
/// future virtualizing host can react to spans rather than per-item churn.
/// </summary>
public sealed class ContainersChangedEventArgs(
    ContainersChangedAction action,
    int startIndex,
    int count,
    int oldStartIndex = -1,
    IReadOnlyList<UIElement>? removedContainers = null) : EventArgs
{
    /// <summary>What happened to the range.</summary>
    public ContainersChangedAction Action { get; } = action;

    /// <summary>The first affected item index — the destination index for <see cref="ContainersChangedAction.Moved"/>
    /// (meaningless for <see cref="ContainersChangedAction.Reset"/>).</summary>
    public int StartIndex { get; } = startIndex;

    /// <summary>The number of containers in the range.</summary>
    public int Count { get; } = count;

    /// <summary>For <see cref="ContainersChangedAction.Moved"/>, the source index the range moved from; otherwise −1.</summary>
    public int OldStartIndex { get; } = oldStartIndex;

    /// <summary>For <see cref="ContainersChangedAction.Unrealized"/>, the actual container instances being removed
    /// (the generator has already dropped them from its index list when this fires, so the host removes these
    /// directly rather than by a now-stale index). Null for the other actions.</summary>
    public IReadOnlyList<UIElement>? RemovedContainers { get; } = removedContainers;
}
