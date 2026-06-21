using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A selectable list of items (design doc §12.6/§12.7). Containers are <see cref="ListBoxItem"/>s; selection rides
/// the <see cref="SelectingItemsControl"/> base over a <see cref="SelectionModel"/>. The list itself is not a tab stop — its
/// items host is a single tab stop (<see cref="Input.KeyboardNavigationMode.Once"/>) and the items are focusable
/// (keyboard navigation lands in P9.3b). Removing the selected item re-targets to the nearest survivor (CD-P9-9).
/// </summary>
public class ListBox : SelectingItemsControl
{
    private int _pendingFocusIndex = -1; // a keyboard-nav focus parked until its (virtualized) container materializes

    /// <summary>Creates a list box (not itself a tab stop; the items host is the single tab stop).</summary>
    public ListBox()
    {
        IsTabStop = false;
        // ItemsPanel = new FuncTemplateContent(static _ =>
        // {
        //     var panel = new StackPanel();
        //     KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once); // the group is one tab stop
        //     return panel;
        // });

        // Completes a parked keyboard-nav focus once the scrolled-to container materializes (virtualizing mode).
        ItemContainerGenerator.ContainersRealizedChanged += OnContainersRealizedForPendingFocus;
    }

    /// <inheritdoc/>
    protected internal override bool HandlesScrolling => true;
    
    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new ListBoxItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is ListBoxItem;

    /// <inheritdoc/>
    private protected override void OnTextSearchMatch(int containerIndex)
    {
        base.OnTextSearchMatch(containerIndex); // selection-follows
        ItemContainerGenerator.ContainerFromIndex(containerIndex)?.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible
    }

    /// <inheritdoc/>
    private protected override void OnSelectionEmptiedByRemoval(int removalIndex)
    {
        // CD-P9-9: a removal dropped the whole selection — re-select the nearest surviving item (the item that
        // slid into the removed slot, clamped to the end).
        var count = ItemContainerGenerator.ContainerCount;
        if (count > 0)
            Selection.Select(Math.Min(removalIndex, count - 1));
    }

    // ── keyboard navigation (P9.3b; design doc §12.7) ─────────────────────────────────────────────────
    //
    // The "current" item is the keyboard cursor — resolved fresh each key from the LIVE focused container
    // (e.OriginalSource), never a cached index a collection edit could leave stale (audit CD-P9-16). Arrows/Home/End
    // move it (focus = Directional ⇒ :focus-visible, the reverse-video focus-row cue); selection follows per mode +
    // modifiers; Space toggles/selects the current; Enter activates it. (Page paging + Ctrl+A select-all are deferred.)
    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var count = ItemContainerGenerator.ContainerCount;
        if (count == 0)
            return;

        var current = ResolveCurrent(e, count); // −1 ⇒ no anchored current (focus is outside the items)

        switch (e.Key)
        {
            case Key.UpArrow:
                MoveCurrent(current < 0 ? 0 : Math.Max(0, current - 1), e.Modifiers); // first key enters at the top
                break;
            case Key.DownArrow:
                MoveCurrent(current < 0 ? 0 : Math.Min(count - 1, current + 1), e.Modifiers);
                break;
            case Key.Home:
                MoveCurrent(0, e.Modifiers);
                break;
            case Key.End:
                MoveCurrent(count - 1, e.Modifiers);
                break;
            case Key.PageUp:
                MoveCurrent(current < 0 ? 0 : Math.Max(0, current - ItemsPerPage()), e.Modifiers);
                break;
            case Key.PageDown:
                MoveCurrent(current < 0 ? 0 : Math.Min(count - 1, current + ItemsPerPage()), e.Modifiers);
                break;
            case Key.Enter:
                if (current < 0)
                    return; // nothing anchored — no phantom activation
                if (RaiseItemActivated(current) is false)
                    return; // leave key event unhandled (Enter / Escape bubble for IsDefault / IsCancel, spec §13)
                break;
            default:
                if (!IsSpace(e) || current < 0)
                    return; // not a nav key, or no anchored current — leave unhandled
                if (SelectionMode == SelectionMode.Single)
                    Selection.Select(current);
                else
                    Selection.Toggle(current); // Space / Ctrl+Space toggle in multi-select
                break;
        }

        e.Handled = true;
    }

    // The current item is the container that owns the focused element the key bubbled from — authoritative after any
    // structural change (the container moved with its data). Falls back to the selection only when focus is outside
    // the items (e.g. the list root itself is focused), and to −1 when there is genuinely no anchor.
    private int ResolveCurrent(KeyEventArgs e, int count)
    {
        for (var node = e.OriginalSource; node is not null; node = node.VisualParent)
        {
            var index = ItemContainerGenerator.IndexFromContainer(node);
            if (index >= 0)
                return index;
        }

        return SelectedIndex >= 0 && SelectedIndex < count ? SelectedIndex : -1;
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); Key.Space is only NUL→Ctrl+Space. Ctrl+Space
    // is allowed (toggle), so the only excluded combos are other modifiers — but for a list, treat any Space-ish as toggle.
    private static bool IsSpace(KeyEventArgs e)
        => e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ');

    private void MoveCurrent(int target, KeyModifiers modifiers)
    {
        BringTargetIntoFocus(target); // ⇒ :focus-visible (realized now, or scrolled-then-focused when virtualized)

        // ReSharper disable once RedundantJumpStatement
        if (SelectionMode == SelectionMode.Single)
            Selection.Select(target); // selection-follows-focus
        else if ((modifiers & KeyModifiers.Control) != 0)
            return; // Ctrl+arrow: move focus only, leave the selection alone
        else if ((modifiers & KeyModifiers.Shift) != 0)
            Selection.SelectRangeFromAnchor(target);
        else
            Selection.Select(target);
    }

    // Realized ⇒ focus immediately (its GotFocus brings it into view through the ScrollViewer). Virtualized +
    // off-band (the container is not materialized) ⇒ scroll its ESTIMATED position into the realization window,
    // then focus it the moment it materializes (OnContainersRealizedForPendingFocus) — keyboard nav reaches an
    // item that does not exist yet.
    private void BringTargetIntoFocus(int target)
    {
        if (ItemContainerGenerator.ContainerFromIndex(target) is { } container)
        {
            _pendingFocusIndex = -1; // a realized target supersedes any parked focus
            container.Focus(FocusNavigationMethod.Directional);
            return;
        }

        _pendingFocusIndex = target;
        if (ItemsHost is ILogicalScrollHost logical && FindItemsScrollViewer() is { } scroll)
            scroll.EnsureVisible(logical.BringItemIntoView(target)); // scroll the estimate in ⇒ the panel realizes it
    }

    // Completes a parked keyboard-nav focus: when the scrolled-to container materializes, focus it. Deferred via
    // the dispatcher because the realize channel fires DURING the panel's measure pass — focusing synchronously
    // there would re-enter layout (focus raises routed events + restyles). Re-resolves the container by index at
    // post time so a recycle between realization and the post can't focus a stale container.
    private void OnContainersRealizedForPendingFocus(object? sender, ContainersChangedEventArgs e)
    {
        if (_pendingFocusIndex < 0 || e.Action != ContainersChangedAction.Realized)
            return;

        if (ItemContainerGenerator.ContainerFromIndex(_pendingFocusIndex) is not { } container)
            return; // the target is still outside the realized window — stay parked for the next realize pass

        var index = _pendingFocusIndex;
        _pendingFocusIndex = -1;

        if (UIApplication.Current?.Dispatcher is { } dispatcher)
            dispatcher.Post(() =>
            {
                if (ItemContainerGenerator.ContainerFromIndex(index) is { IsAttachedToTree: true } c)
                    c.Focus(FocusNavigationMethod.Directional);
            });
        else if (container.IsAttachedToTree)
            container.Focus(FocusNavigationMethod.Directional); // no dispatcher (BYO host) — best-effort synchronous
    }
}
