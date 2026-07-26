using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// Raised by <see cref="ListView.SortingEvent"/> when a header click is about to change the sort
/// (WPF's <c>DataGrid.Sorting</c> shape). The control has NOT sorted anything yet — that is the point:
/// the collection belongs to the host, so the host sorts it (or swaps in a sorted view) and marks the
/// args <see cref="RoutedEventArgs.Handled"/>. See <see cref="ListView.IsBuiltInSortEnabled"/> for the
/// opt-in convenience path.
/// </summary>
/// <remarks>
/// <b>Vetoable, so the args are a real allocation</b> — never a pooled rental: <see cref="Cancel"/> and
/// <see cref="RoutedEventArgs.Handled"/> must survive the raise for the caller to read them.
/// </remarks>
public sealed class ListViewSortingEventArgs : RoutedEventArgs
{
    /// <summary>Creates the args for a pending sort.</summary>
    /// <param name="routedEvent">The event being raised (<see cref="ListView.SortingEvent"/>).</param>
    /// <param name="source">The raising <see cref="ListView"/>.</param>
    /// <param name="column">The column whose header was clicked.</param>
    /// <param name="direction">The direction the click resolved to.</param>
    public ListViewSortingEventArgs(RoutedEvent routedEvent, UIElement source, ListViewColumn column, ListViewSortDirection direction)
        : base(routedEvent, source)
    {
        Column = column;
        Direction = direction;
    }

    /// <summary>The column whose header was clicked.</summary>
    public ListViewColumn Column { get; }

    /// <summary>The direction the click resolved to (the cycle is ascending → descending → ascending).</summary>
    public ListViewSortDirection Direction { get; }

    /// <summary>The sort key path (<see cref="ListViewColumn.SortMemberPath"/>, else the column's
    /// <see cref="ListViewColumn.DisplayMemberPath"/>) — the string a view-model usually needs.</summary>
    public string? SortMemberPath => Column.EffectiveSortMemberPath;

    /// <summary>
    /// Set to <see langword="true"/> to abandon the sort entirely: neither
    /// <see cref="ListView.SortColumn"/> nor <see cref="ListView.SortDirection"/> is updated and no
    /// indicator moves (distinct from <see cref="RoutedEventArgs.Handled"/>, which means "I sorted it —
    /// update the indicator but don't touch my collection").
    /// </summary>
    public bool Cancel { get; set; }
}
