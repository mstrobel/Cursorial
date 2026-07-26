using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The payload of <see cref="BreadcrumbBar.ItemActivated"/> — the segment the user asked to navigate to
/// (a chip click, <c>Enter</c>/<c>Space</c> on the active chip, or a pick from the leading ellipsis
/// drop-down). It carries the data <see cref="Item"/> and its <see cref="Index"/> in the bar's items, so a
/// host can truncate its own path model at that depth without reading the container.
/// </summary>
/// <param name="routedEvent">The event being raised.</param>
/// <param name="source">The dispatch target (the <see cref="BreadcrumbBar"/>).</param>
/// <param name="item">The activated item.</param>
/// <param name="index">The activated item's index in the bar's items.</param>
/// <param name="container">The chip container the generator holds for <paramref name="item"/>, if any. An elided
/// (folded-away) segment still has one — it is <see cref="Visibility.Collapsed"/>, not unrealized — so a pick out
/// of the ellipsis drop-down carries its container too.</param>
public sealed class BreadcrumbBarItemEventArgs(
    RoutedEvent routedEvent,
    UIElement source,
    object? item,
    int index,
    BreadcrumbBarItem? container = null)
    : RoutedEventArgs(routedEvent, source)
{
    /// <summary>The activated item (the data, not the container).</summary>
    public object? Item { get; } = item;

    /// <summary>The activated item's index in the bar's items; <c>−1</c> when the item is not in the collection.</summary>
    public int Index { get; } = index;

    /// <summary>The chip container for <see cref="Item"/>, when one is realized.</summary>
    public BreadcrumbBarItem? Container { get; } = container;
}

/// <summary>
/// The payload of the <see cref="BreadcrumbBar"/> edit-mode boundary events
/// (<see cref="BreadcrumbBar.EditingStarted"/>, <see cref="BreadcrumbBar.EditCommitted"/>,
/// <see cref="BreadcrumbBar.EditCanceled"/>). The bar owns the chips ↔ edit state machine but knows nothing
/// about paths: it hands the host a <see cref="Text"/> string at each boundary and lets the host supply the
/// initial text (on <see cref="BreadcrumbBar.EditingStarted"/>) or validate it (on
/// <see cref="BreadcrumbBar.EditCommitted"/>).
/// </summary>
/// <param name="routedEvent">The event being raised.</param>
/// <param name="source">The dispatch target (the <see cref="BreadcrumbBar"/>).</param>
/// <param name="text">The initial value of <see cref="Text"/>.</param>
public sealed class BreadcrumbBarEditEventArgs(RoutedEvent routedEvent, UIElement source, string text)
    : RoutedEventArgs(routedEvent, source)
{
    /// <summary>
    /// The edit text, mutable at both boundaries.
    /// <list type="bullet">
    /// <item><see cref="BreadcrumbBar.EditingStarted"/>: seeded from <see cref="BreadcrumbBar.Text"/>; a handler
    /// <b>replaces</b> it with the host's own rendering of the path, and the bar pushes that into the edit box
    /// pre-selected.</item>
    /// <item><see cref="BreadcrumbBar.EditCommitted"/>: what the user typed; a handler may <b>normalize</b> it
    /// (expand <c>~</c>, resolve <c>..</c>, add a trailing separator) and the bar adopts the normalized value into
    /// <see cref="BreadcrumbBar.Text"/>.</item>
    /// <item><see cref="BreadcrumbBar.EditCanceled"/>: what the user abandoned, for information only — nothing is
    /// adopted.</item>
    /// </list>
    /// </summary>
    public string Text { get; set; } = text ?? "";

    /// <summary>
    /// Commit only: set by a handler that rejected <see cref="Text"/> (a path that does not resolve) to keep the
    /// bar in edit mode with the text intact, instead of falling back to the chips. Ignored by the other events.
    /// </summary>
    public bool KeepEditing { get; set; }
}

/// <summary>
/// The payload of the separator / ellipsis drop-down events
/// (<see cref="BreadcrumbBar.DropDownOpening"/>, <see cref="BreadcrumbBar.ChildActivated"/>) — the
/// Explorer-style "▸ drops the sibling list" affordance. The bar never enumerates a hierarchy itself: on
/// <see cref="BreadcrumbBar.DropDownOpening"/> it hands the host the segment the separator follows and an empty
/// <see cref="Children"/> list to fill; if the host fills it, the bar shows those entries and reports the pick
/// through <see cref="BreadcrumbBar.ChildActivated"/> with <see cref="SelectedChild"/> set.
/// </summary>
/// <param name="routedEvent">The event being raised.</param>
/// <param name="source">The dispatch target (the <see cref="BreadcrumbBar"/>).</param>
/// <param name="item">The item whose separator was pressed.</param>
/// <param name="index">That item's index in the bar's items.</param>
/// <param name="children">The child list (the opening event passes a fresh empty list for the host to fill).</param>
/// <param name="selectedChild">The picked child (<see cref="BreadcrumbBar.ChildActivated"/> only).</param>
public sealed class BreadcrumbBarDropDownEventArgs(
    RoutedEvent routedEvent,
    UIElement source,
    object? item,
    int index,
    IList<object?> children,
    object? selectedChild = null)
    : RoutedEventArgs(routedEvent, source)
{
    /// <summary>The item whose trailing separator was pressed (the parent of <see cref="Children"/>).</summary>
    public object? Item { get; } = item;

    /// <summary>The parent item's index in the bar's items.</summary>
    public int Index { get; } = index;

    /// <summary>
    /// The children to offer. On <see cref="BreadcrumbBar.DropDownOpening"/> this arrives empty and a handler
    /// fills it (leaving it empty means "no drop-down" — the press is inert). The entries are shown through the
    /// usual content/template chain, so view-models with a <c>DataTemplate</c> render as authored.
    /// </summary>
    public IList<object?> Children { get; } = children;

    /// <summary>The picked child — set only on <see cref="BreadcrumbBar.ChildActivated"/>.</summary>
    public object? SelectedChild { get; } = selectedChild;
}
