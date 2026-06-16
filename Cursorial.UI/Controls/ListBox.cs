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
    /// <summary>Creates a list box (not itself a tab stop; the items host is the single tab stop).</summary>
    public ListBox()
    {
        IsTabStop = false;
        ItemsPanel = new FuncTemplateContent(static _ =>
        {
            var panel = new StackPanel();
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once); // the group is one tab stop
            return panel;
        });
    }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new ListBoxItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is ListBoxItem;

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
            case Key.Enter:
                if (current < 0)
                    return; // nothing anchored — no phantom activation
                RaiseItemActivated(current);
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
        ItemContainerGenerator.ContainerFromIndex(target)?.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible

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
}
