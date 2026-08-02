namespace Cursorial.UI;

/// <summary>
/// The args for <see cref="UIElement.TemplatedParentChanged"/>. Raised when the element's templated
/// parent is assigned or cleared; the direction is explicit (<see cref="OldTemplatedParent"/> /
/// <see cref="NewTemplatedParent"/>), so one handler can serve both events.
/// </summary>
public sealed class TemplatedParentChangedEventArgs : EventArgs
{
    /// <summary>Creates the args (public so handlers are unit-testable without a mutation).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public TemplatedParentChangedEventArgs(UIElement element, UIElement? oldTemplatedParent, UIElement? newTemplatedParent)
    {
        ArgumentNullException.ThrowIfNull(element);
        Element = element;
        OldTemplatedParent = oldTemplatedParent;
        NewTemplatedParent = newTemplatedParent;
    }

    /// <summary>The element whose templated parent changed.</summary>
    public UIElement Element { get; }

    /// <summary>The templated parent before the change — <see langword="null"/> on first assignment</summary>
    public UIElement? OldTemplatedParent { get; }

    /// <summary>The templaeted parent after the change — <see langword="null"/> on teardown.</summary>
    public UIElement? NewTemplatedParent { get; }
}