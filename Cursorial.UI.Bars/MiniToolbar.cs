using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A floating strip of high-frequency bar controls shown on right-click over an element (the ribbon guide's Mini
/// Toolbar). Attach one to any element via the <see cref="BarProperty"/> attached property; a right-click over that
/// element opens the strip at the pointer on a light-dismiss <see cref="Popup"/>. It hosts bar controls horizontally
/// (its <c>Items</c> — the same <see cref="BarButton"/>/<see cref="BarToggleButton"/>/… a Toolbar or Ribbon hosts),
/// so one command set drives every surface. Parallels <see cref="ContextMenu"/>, but the strip is a horizontal bar
/// rather than a vertical menu, and it wins the right-click over a co-present ContextMenu (it marks the event handled).
/// </summary>
public sealed class MiniToolbar : ItemsControl
{
    /// <summary>The mini toolbar shown on a right-click over the element (an attached-property trigger).</summary>
    public static readonly AttachedProperty<MiniToolbar?> BarProperty =
        UIProperty.RegisterAttached<MiniToolbar, UIElement, MiniToolbar?>("Bar", changed: OnBarChanged);

    private Popup? _popup;
    private UIElement? _watchedTarget;
    private UIElement? _pendingTarget; // the anchor to watch once the popup's surface actually opens (Popup.Opened)
    private bool _isOpen;              // the REAL surface-open state (Popup.IsOpen reflects only the IsOpenProperty DP)

    internal UIElement? WatchedTargetForTests => _watchedTarget;

    /// <summary>Creates a mini toolbar (its items lay out horizontally; the host itself is not a focus stop).</summary>
    public MiniToolbar()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ => new StackPanel { Orientation = Orientation.Horizontal });
        Focusable = false;
        Ribbon.SetButtonSize(this, RibbonButtonSize.Small);
    }

    /// <summary>Reads <see cref="BarProperty"/> — the mini toolbar attached to <paramref name="element"/>.</summary>
    public static MiniToolbar? GetBar(UIElement element) => element.GetValue(BarProperty);

    /// <summary>Sets <see cref="BarProperty"/>.</summary>
    public static void SetBar(UIElement element, MiniToolbar? value) => element.SetValue(BarProperty, value);

    /// <summary>Whether the strip is currently shown (the real surface-open state, not just the requested flag).</summary>
    public bool IsOpen => _isOpen;

    /// <summary>Opens the strip at the pointer over <paramref name="target"/> (the right-click anchor). Re-opening
    /// relocates an open strip.</summary>
    public void Open(UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        _popup ??= CreatePopup();
        if (_isOpen)
            _popup.SetCurrentValue(Popup.IsOpenProperty, false); // a Popup re-places only on the closed→open edge

        // The anchor is watched from Popup.Opened (real surface open), NOT here: if OpenCore bails (no live
        // WindowManager) Opened never fires, so a failed open cannot strand the DetachedFromLogicalTree subscription.
        _pendingTarget = target;
        _popup.PlacementTarget = target;
        _popup.Placement = PlacementMode.Pointer; // land at the right-click cell
        _popup.VerticalOffset = 1;
        _popup.SetCurrentValue(Popup.IsOpenProperty, true);
    }

    /// <summary>Closes the strip (a no-op when already closed).</summary>
    public void Close()
    {
        _popup?.SetCurrentValue(Popup.IsOpenProperty, false);
        UnwatchTarget(); // belt-and-suspenders: release the watch even if the popup never truly opened (no Closed fires)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        Close(); // release the popup surface (the strip is rooted only on its own WM-owned surface)
        base.OnDetachedFromTree(in e);
    }

    private Popup CreatePopup()
    {
        var popup = new Popup { Child = this, StaysOpen = false }; // StaysOpen=false ⇒ light-dismiss participant
        popup.Opened += OnPopupOpened;
        popup.Closed += OnPopupClosed;
        return popup;
    }

    // The surface actually opened — now arm the anchor watch (a bailed OpenCore never reaches here, so no leak).
    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _isOpen = true;
        if (_pendingTarget is { } target)
            WatchTarget(target);
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        _isOpen = false;
        UnwatchTarget();
    }

    // The anchor lives in the host tree; if it detaches while the strip is open, nothing else would close the
    // separate popup surface — watch its logical-tree detach and close on it (mirrors ContextMenu).
    private void WatchTarget(UIElement target)
    {
        if (ReferenceEquals(_watchedTarget, target))
            return;

        UnwatchTarget();
        _watchedTarget = target;
        target.DetachedFromLogicalTree += OnTargetDetached;
    }

    private void UnwatchTarget()
    {
        if (_watchedTarget is { } target)
            target.DetachedFromLogicalTree -= OnTargetDetached;
        _watchedTarget = null;
    }

    private void OnTargetDetached(object? sender, LogicalTreeAttachmentEventArgs e) => Close();

    // The attached-property trigger: subscribe the target's bubbling right-click to open its strip. Marking the event
    // handled makes the strip win over the dispatcher's post-routing ContextMenu router (which skips a handled event).
    private static void OnBarChanged(UIObject sender, MiniToolbar? oldValue, MiniToolbar? newValue)
    {
        if (sender is not UIElement target)
            return;

        if (oldValue is not null)
            target.RemoveHandler(UIElement.MouseUpEvent, OnTargetMouseUp);
        if (newValue is not null)
            target.AddHandler(UIElement.MouseUpEvent, OnTargetMouseUp);
    }

    private static void OnTargetMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.Button != MouseButton.Right || sender is not UIElement target || GetBar(target) is not { } bar)
            return;

        bar.Open(target);
        e.Handled = true; // consume so the ContextMenu router (post-routing) does not also fire
    }
}
