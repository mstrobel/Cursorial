using Cursorial.Media;

namespace Cursorial.UI.Input;

/// <summary>
/// Args for the bubbling <c>QueryCursor</c> event (design doc §7.6; WPF parity). The event bubbles from
/// the element directly under the pointer up to the root; each element's <see cref="UIElement.OnQueryCursor"/>
/// fills in <see cref="Cursor"/> from its own <see cref="UIElement.Cursor"/> when it has one and either the
/// cursor is still unclaimed (a nearer element hasn't set it) or the element forces it
/// (<see cref="UIElement.ForceCursor"/>). The cursor the route settles on is the effective pointer shape —
/// <see langword="null"/> means "no preference", i.e. the terminal default.
/// </summary>
/// <remarks>
/// A handler may set <see cref="Cursor"/> directly (and <see cref="RoutedEventArgs.Handled"/> to stop nearer
/// defaults from being overridden by farther ones — the route is leaf→root, so the deepest handler wins
/// unless an ancestor force-overrides). The args is pooled on the framework resolution path (per-frame hover
/// re-resolution), so do not retain it past the dispatch.
/// </remarks>
public sealed class QueryCursorEventArgs : RoutedEventArgs
{
    private MouseCursorShape? _cursor;

    /// <summary>Creates an empty caller-owned args (also the pooled-construction path).</summary>
    public QueryCursorEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to raise.</summary>
    /// <param name="routedEvent">The <see cref="UIElement.QueryCursorEvent"/>.</param>
    /// <param name="source">The element under the pointer.</param>
    public QueryCursorEventArgs(RoutedEvent<QueryCursorEventArgs> routedEvent, UIElement source)
        : base(routedEvent, source)
    {
    }

    /// <summary>The cursor the route has settled on; <see langword="null"/> = no preference (terminal default).</summary>
    public MouseCursorShape? Cursor
    {
        get
        {
            ThrowIfStale();
            return _cursor;
        }
        set
        {
            ThrowIfStale();
            _cursor = value;
        }
    }

    /// <inheritdoc/>
    internal override void ResetPooledState() => _cursor = null;
}
