namespace Cursorial.UI.Input;

/// <summary>
/// Routed-event args carrying a settable <see cref="Cancel"/> veto (the Avalonia
/// <c>CancelRoutedEventArgs</c>): a <em>pre-commit</em> event — e.g.
/// <see cref="Cursorial.UI.Controls.Expander.Expanding"/> /
/// <see cref="Cursorial.UI.Controls.Expander.Collapsing"/> — raises this <b>before</b> the pending
/// state change is applied and abandons the change when a handler sets <see cref="Cancel"/> to
/// <see langword="true"/>.
/// </summary>
/// <remarks>
/// Because it carries a mutable payload, an instance is <b>caller-owned</b> (constructed with
/// <see langword="new"/>, never rented/pooled): the raiser reads <see cref="Cancel"/> after
/// <see cref="UIElement.RaiseEvent"/> returns.
/// </remarks>
public class CancelRoutedEventArgs : RoutedEventArgs
{
    /// <summary>Creates an empty caller-owned args.</summary>
    public CancelRoutedEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to raise.</summary>
    /// <param name="routedEvent">The event to raise.</param>
    /// <param name="source">The dispatch target (becomes <see cref="RoutedEventArgs.Source"/> and <see cref="RoutedEventArgs.OriginalSource"/>).</param>
    public CancelRoutedEventArgs(RoutedEvent routedEvent, UIElement source)
        : base(routedEvent, source)
    {
    }

    /// <summary>
    /// Whether a handler has vetoed the pending change. The raiser reads this after the raise and
    /// abandons the operation when it is <see langword="true"/>; defaults to <see langword="false"/>.
    /// </summary>
    public bool Cancel { get; set; }
}
