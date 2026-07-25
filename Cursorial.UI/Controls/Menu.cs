using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A horizontal menu bar (design doc §12.7) — an <see cref="ItemsControl"/> whose items are top-level
/// <see cref="MenuItem"/>s laid out in a row; each opens its submenu downward. Registers as the window's main menu
/// with the <see cref="AccessKeyManager"/> (<see cref="IMainMenu"/>), so an Alt tap / F10 enters menu mode and
/// moves focus to the first item.
/// </summary>
public class Menu : ItemsControl, IMainMenu
{
    /// <summary>
    /// The bubbling event raised when the bar leaves menu mode — every open top-level submenu has shut and
    /// keyboard focus has returned to the pre-menu origin (an Escape, a leaf invoke, or a light-dismiss). The
    /// entry counterpart lives on the access-key engine (<see cref="AccessKeyManager.EnterMenuMode"/> /
    /// <see cref="IMainMenu.OnEnterMenuMode"/>). Mirrors Avalonia's <c>MenuBase.MenuClosed</c> (WPF's
    /// <c>Menu</c> has no such event).
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> MenuClosedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(MenuClosed), RoutingStrategy.Bubble, typeof(Menu));

    /// <summary>Creates a menu bar (items stack horizontally).</summary>
    public Menu()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ => new StackPanel { Orientation = Orientation.Horizontal });
        Classes.Add(".menu");
    }

    static Menu()
    {
        IsTabStopProperty.OverrideMetadata<Menu>(new PropertyMetadata<bool>(DefaultValue: false));
        KeyboardNavigation.TabNavigationProperty.OverrideMetadata<Menu>(new PropertyMetadata<KeyboardNavigationMode>(KeyboardNavigationMode.None));

        // The menu bar is a NON-RETAINING focus scope (mirrors the bars Toolbar): entered via Alt/F10/click, not Tab,
        // and RETURNS focus to where it came from when it closes (Escape / leaf invoke / light-dismiss). The
        // AccessKey entry in OnEnterMenuMode captures that pre-menu origin (MarkReturnableEntry), and item focus
        // memory records on the Menu itself so an excursion never clobbers the window-root scope's memory.
        FocusManager.IsFocusScopeProperty.OverrideDefaultValue<Menu>(true);
        FocusManager.RetainsFocusProperty.OverrideDefaultValue<Menu>(false);
    }

    /// <summary>CLR sugar over <see cref="MenuClosedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? MenuClosed
    {
        add => AddHandler(MenuClosedEvent, value!);
        remove => RemoveHandler(MenuClosedEvent, value!);
    }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new MenuItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is MenuItem or Separator;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        if (UIApplication.Current?.AccessKeys is {} manager)
            manager.MainMenu = this; // one per app, last wins (doc §7.8)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (UIApplication.Current?.AccessKeys is {} manager && ReferenceEquals(manager.MainMenu, this))
            manager.MainMenu = null;

        base.OnDetachedFromTree(in e);
    }

    /// <summary>Menu-mode entry (Alt tap / F10): focus the first top-level item so keyboard navigation can begin.</summary>
    void IMainMenu.OnEnterMenuMode()
    {
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Focusable: true } first)
            {
                first.Focus(FocusNavigationMethod.AccessKey);
                return;
            }
        }
    }

    /// <summary>
    /// Menu mode ends when keyboard focus leaves the bar. The Menu is a NON-RETAINING focus scope, so every
    /// exit path — an Escape, a leaf invoke, a light-dismiss — routes focus back to the pre-menu origin, which
    /// flips <see cref="UIElement.IsKeyboardFocusWithin"/> false here. Moving between the bar headers and their
    /// submenu popups keeps it true (the placement-target bridge counts a focused submenu item as within the
    /// bar, so a sibling-switch never trips it), making this true→false edge the single chokepoint every close
    /// funnels through — <see cref="MenuClosed"/> fires exactly once per menu session.
    /// </summary>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        if (ReferenceEquals(args.Property, IsKeyboardFocusWithinProperty) && !args.GetNewValue<bool>())
        {
            var closed = RentEvent(MenuClosedEvent);
            RaiseEvent(closed);
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Escape with a focused TOP-LEVEL header (the cue-down second Esc — the cue-up first Esc is consumed by the
        // AccessKeyManager, which only clears the cue and keeps focus). Escape from a focused submenu ITEM is the
        // submenu Popup's (→ MenuItem.OnPopupClosed, which whole-chain-collapses).
        if (e.Handled || !IsKeyboardFocusWithin || e.Key != Key.Escape || UIApplication.Current?.FocusManager is not { } focus)
            return;

        // A header can be focused with its OWN submenu open (a mouse-click-open on a keyboard-focused header, or a
        // hover sibling-switch while keyboard-active) — that submenu is on a separate surface, not on the header's
        // Escape route, so nothing else would close it. Sweep any open top-level submenu shut, then return focus to
        // the pre-menu origin (the Menu is a non-retaining scope). Mirrors Toolbar.OnKeyDown.
        var closedSubmenu = false;
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { IsSubmenuOpen: true } open)
            {
                open.IsSubmenuOpen = false;
                closedSubmenu = true;
            }

        if (focus.RestoreRetainedFocus(this) || closedSubmenu)
            e.Handled = true;
    }
}
