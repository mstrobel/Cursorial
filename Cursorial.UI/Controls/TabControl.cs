using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A tabbed control (design doc §12.7): a single-selection <see cref="SelectingItemsControl"/> whose containers are
/// <see cref="TabItem"/>s. The themed template hosts the tab strip (<c>PART_TabStrip</c>, an
/// <see cref="ItemsPresenter"/> of the tabs' headers) and a content host (<c>PART_ContentHost</c>, a
/// <see cref="ContentPresenter"/> showing only the selected tab's <see cref="SelectedContent"/>). Selection follows
/// a click or keyboard (Left/Right/Home/End move + select; Ctrl+PageUp/PageDown cycle); the first tab auto-selects.
/// </summary>
public class TabControl : SelectingItemsControl
{
    private static readonly UIPropertyKey<object?> SelectedContentPropertyKey =
        UIProperty.RegisterReadOnly<TabControl, object?>(nameof(SelectedContent));

    /// <summary>The selected tab's content — what <c>PART_ContentHost</c> shows. Read-only (driven by selection).</summary>
    public static readonly StyledProperty<object?> SelectedContentProperty = SelectedContentPropertyKey.Property;

    /// <summary>The template applied to a non-<see cref="TabItem"/> item's content in the content host.</summary>
    public static readonly StyledProperty<DataTemplate?> ContentTemplateProperty =
        UIProperty.Register<TabControl, DataTemplate?>(nameof(ContentTemplate));

    /// <summary>Creates a tab control (not itself a tab stop; the tab strip is the single tab stop).</summary>
    public TabControl()
    {
        IsTabStop = false;
        ItemsPanel = new ItemsPanelTemplate(static _ =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once); // the strip is one tab stop
            return panel;
        });

        SelectionChanged += (_, _) => UpdateSelectedContent();
        ItemContainerGenerator.ContainersChanged += (_, _) =>
        {
            EnsureSelection();      // a tab control always shows a tab (WPF parity)
            UpdateSelectedContent(); // re-resolve after a structural change (the selected container may be new)
        };
    }

    /// <inheritdoc cref="SelectedContentProperty"/>
    public object? SelectedContent => GetValue(SelectedContentProperty);

    /// <inheritdoc cref="ContentTemplateProperty"/>
    public DataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }

    // No type-ahead: a TabControl's content lives in its visual subtree, so unhandled typing in the content would
    // bubble up and switch tabs. (WPF scopes tab type-ahead to the header strip; that header-scoped form is a
    // possible refinement — for now type-ahead is off so content typing is never hijacked.)
    private protected override bool TextSearchNavigates => false;

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new TabItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is TabItem;

    // Auto-select the first tab when nothing is selected (a tab control with content but no selection is useless).
    private void EnsureSelection()
    {
        if (SelectedIndex < 0 && ItemContainerGenerator.ContainerCount > 0)
            Selection.Select(0);
    }

    // The content host shows the selected tab's Content (the strip shows only the headers). A TabItem's Content is
    // presented ONLY here — its own template renders just the Header — so there is no double-hosting.
    private void UpdateSelectedContent()
    {
        object? content = null;
        if (SelectedIndex >= 0 && ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is TabItem tab)
            content = tab.Content;
        SetValue(SelectedContentPropertyKey, content);
    }

    // ── keyboard navigation (design doc §12.7) ────────────────────────────────────────────────────────
    //
    // The strip is horizontal: Left/Right move + select (selection-follows-focus, single mode); Home/End jump to the
    // ends; Ctrl+PageUp/PageDown cycle (the universal chord — Ctrl+Tab is wire-ambiguous and deferred). The "current"
    // tab is the selected one (single selection == focus for a tab strip).
    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        // Strip navigation (Left/Right/Home/End/Ctrl+PgUp/PgDn) applies ONLY when a tab HEADER has focus — not when
        // focus is inside the selected tab's CONTENT (e.g. a Ribbon group's buttons), where arrows are the content's
        // own directional navigation. Without this guard, arrowing within a tab body hijacks tab switching.
        var focusedIndex = FocusedTabIndex(e.OriginalSource);
        if (focusedIndex < 0)
            return;

        var count = ItemContainerGenerator.ContainerCount;
        if (count == 0)
            return;

        // Anchor navigation to the FOCUSED tab, not the selected one — a command tab (IsSelectable=false, the Ribbon's
        // File tab) can be FOCUSED without being selected, so focus and selection diverge; nav must follow focus so a
        // focused-but-unselected tab is not a one-way trap (arrow onto File, then arrow back off it).
        var current = focusedIndex;
        var ctrl = (e.Modifiers & KeyModifiers.Control) != 0;

        int target;
        switch (e.Key)
        {
            // Skip Collapsed tabs and continue in the pressed direction — a hidden tab (e.g. a Ribbon contextual tab)
            // is a realized, counted container but is non-navigable; landing on it and letting a SelectionChanged
            // handler redirect warps selection to the wrong place (traps interior traversal / jumps backward past a
            // trailing hidden tab). At a hidden edge there is nothing to move to, so stay put (target < 0 below).
            case Key.LeftArrow when !ctrl:  target = NextVisibleIndex(current - 1, -1, count); break;
            case Key.RightArrow when !ctrl: target = NextVisibleIndex(current + 1, +1, count); break;
            case Key.Home when !ctrl:       target = NextVisibleIndex(0, +1, count); break;
            case Key.End when !ctrl:        target = NextVisibleIndex(count - 1, -1, count); break;
            case Key.PageUp when ctrl:      target = CycleVisibleIndex(current, -1, count); break; // cycle (wrap)
            case Key.PageDown when ctrl:    target = CycleVisibleIndex(current, +1, count); break;
            default: return; // not a tab-nav key — leave unhandled
        }

        if (target < 0 || target == current)
        {
            e.Handled = true; // a tab-nav key with nowhere visible to go (a hidden edge) is consumed, not passed on
            return;
        }

        if (ItemContainerGenerator.ContainerFromIndex(target) is TabItem { IsSelectable: false } commandTab)
        {
            // A command tab (File): FOCUS it — reachable, highlights — but do NOT select it, so the shown body stays
            // on the current content tab. Its own activation (Enter/click) opens Backstage rather than a band.
            commandTab.Focus(FocusNavigationMethod.Directional);
            e.Handled = true;
            return;
        }

        Selection.Select(target);
        // Focus the tab that ENDED UP selected, not the pre-select target: a SelectionChanged handler may have
        // redirected the selection (e.g. the Ribbon skips its non-selectable File tab), and focus must track it.
        var focusIndex = SelectedIndex >= 0 ? SelectedIndex : target;
        ItemContainerGenerator.ContainerFromIndex(focusIndex)?.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible
        e.Handled = true;
    }

    // The first NON-Collapsed container at or beyond `start`, stepping by `step` (±1); -1 if none in that direction.
    // A Collapsed tab (a hidden Ribbon contextual tab) is realized + counted but non-navigable, so it is skipped.
    private int NextVisibleIndex(int start, int step, int count)
    {
        for (var i = start; i >= 0 && i < count; i += step)
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Visibility: not Visibility.Collapsed })
                return i;
        return -1;
    }

    // The next NON-Collapsed container from `from` with wraparound (Ctrl+PageUp/PageDown cycle over visible tabs only);
    // `from` itself if none other is visible.
    private int CycleVisibleIndex(int from, int step, int count)
    {
        for (var n = 1; n <= count; n++)
        {
            var i = (((from + step * n) % count) + count) % count;
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Visibility: not Visibility.Collapsed })
                return i;
        }
        return from;
    }

    // The index of the tab header the key event originated from (a container of THIS control), or -1 when focus is in
    // the selected tab's BODY (content) rather than on a header. Containers are logical children of the TabControl and
    // visual children of the strip panel; a walk up the visual chain from the focused element reaches the container
    // when a header is focused. Returns the index so navigation can anchor to the focused tab (which may differ from
    // the selected tab when a command tab holds focus).
    private int FocusedTabIndex(UIElement? source)
    {
        for (var node = source; node is not null; node = node.VisualParent)
            if (node is TabItem tab && ItemContainerGenerator.IndexFromContainer(tab) is >= 0 and var i)
                return i;
        return -1;
    }
}
