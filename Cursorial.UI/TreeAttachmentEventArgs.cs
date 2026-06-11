namespace Cursorial.UI;

/// <summary>
/// The args for the visual-tree attachment virtuals (<c>UIElement.OnAttachedToTree</c> /
/// <c>OnDetachedFromTree</c>), passed by <c>in</c>. Carries the visual root the subtree
/// attached to (or is detaching from) and the element's visual parent at that moment.
/// </summary>
public readonly struct TreeAttachmentEventArgs
{
    /// <summary>Creates the args (public so override behavior is unit-testable without an attach walk).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public TreeAttachmentEventArgs(UIElement root, UIElement? parent)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        Parent = parent;
    }

    /// <summary>The visual root of the tree being attached to / detached from.</summary>
    public UIElement Root { get; }

    /// <summary>The element's visual parent (<see langword="null"/> for the root element itself).</summary>
    public UIElement? Parent { get; }
}

/// <summary>
/// The args for <see cref="UIElement.AttachedToLogicalTree"/> /
/// <see cref="UIElement.DetachedFromLogicalTree"/> — the S2 seam (binding engine, P4): DataContext
/// inheritance and namescope registration ride these notifications. Raised when the element's
/// logical parent is assigned or cleared; the direction is explicit
/// (<see cref="OldParent"/> / <see cref="NewParent"/>), so one handler can serve both events.
/// </summary>
public sealed class LogicalTreeAttachmentEventArgs : EventArgs
{
    /// <summary>Creates the args (public so handlers are unit-testable without a tree mutation).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
    public LogicalTreeAttachmentEventArgs(UIElement element, UIElement? oldParent, UIElement? newParent)
    {
        ArgumentNullException.ThrowIfNull(element);
        Element = element;
        OldParent = oldParent;
        NewParent = newParent;
    }

    /// <summary>The element whose logical attachment changed.</summary>
    public UIElement Element { get; }

    /// <summary>The logical parent before the change — <see langword="null"/> on attach.</summary>
    public UIElement? OldParent { get; }

    /// <summary>The logical parent after the change — <see langword="null"/> on detach.</summary>
    public UIElement? NewParent { get; }
}
