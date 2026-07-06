using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>How a <see cref="Backstage"/> is shown when the File tab is invoked (the framework-owner's choice —
/// full-screen is faithful to the desktop ribbon but can overwhelm a terminal).</summary>
public enum BackstageDisplayMode
{
    /// <summary>The Office 2010+ File screen: a maximized, chrome-less surface that takes over the whole window
    /// (the design guide's default).</summary>
    FullScreen,

    /// <summary>The Office 2007 dropdown-menu style: a compact panel anchored under the File tab, light-dismissed
    /// like a menu — less overwhelming on a terminal.</summary>
    Menu,
}

/// <summary>
/// The Backstage — the File tab's view (the bars guide §"Backstage"): a left <b>rail</b> of document-level
/// <i>destinations</i> (<see cref="BackstageItem"/>s — New, Open, Save, Export, Print, Account, Options) over a
/// <b>detail pane</b> that swaps per destination (the rail persists), with a <c>◂</c> back that returns to the
/// document. It IS a <see cref="TabControl"/> (so the single-selection model, the selected-item content host, and
/// <see cref="BackstageItem"/>'s <see cref="TabItem.IsSelectable"/>/access-key selection are inherited verbatim); the
/// Backstage only substitutes a VERTICAL rail (Up/Down navigation) for the horizontal tab strip and adds a
/// <see cref="BackRequestedEvent"/> + a <see cref="DisplayMode"/>. It is built from the same bar controls — the rail
/// rows are <see cref="BackstageItem"/>s, each detail pane an ordinary panel of controls.
/// <para>The Backstage does not host itself; the app opens it (in a full-window <c>Window</c> or a File-anchored
/// <c>Popup</c> per <see cref="DisplayMode"/>) in response to the Ribbon's
/// <see cref="Ribbon.BackstageRequestedEvent"/>, and closes it on <see cref="BackRequestedEvent"/> (the ◂ / Escape).</para>
/// </summary>
[Cursorial.Markup.ContentProperty(nameof(Items))]
public class Backstage : TabControl
{
    /// <summary>How the Backstage is shown (full-window vs a File-anchored menu). Consumed by the host that opens the
    /// Backstage (it picks the surface); also stamps <c>:backstage-menu</c> so the template compacts its chrome.</summary>
    public static readonly StyledProperty<BackstageDisplayMode> DisplayModeProperty =
        UIProperty.Register<Backstage, BackstageDisplayMode>(nameof(DisplayMode), BackstageDisplayMode.FullScreen);

    /// <summary>Raised (bubbling) when the user asks to leave the Backstage — the <c>◂</c> back button or Escape. The
    /// host handler closes the Backstage surface and returns to the document.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> BackRequestedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(BackRequested), RoutingStrategy.Bubble, typeof(Backstage));

    private const string PartBackButton = "PART_BackButton";
    private const string PartContentHost = "PART_ContentHost";

    private ButtonBase? _backButton;
    private UIElement? _contentHost;

    static Backstage()
    {
        // The Menu display mode re-skins the ONE template into the compact File-anchored panel (FullScreen ⇒ no class).
        PseudoClassMapping.Register<Backstage, BackstageDisplayMode>(
            DisplayModeProperty, static m => m == BackstageDisplayMode.Menu ? ":backstage-menu" : null);

        // The Backstage owns its focus memory (a scope); Escape/◂ raise BackRequested and the host closes + restores
        // focus (a modal Window hands activation back to the document; a Menu Popup restores focus to the File tab).
        FocusManager.IsFocusScopeProperty.OverrideDefaultValue<Backstage>(true);
    }

    /// <summary>Creates a Backstage with a VERTICAL rail (Up/Down navigates destinations; one Tab stop).</summary>
    public Backstage()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ =>
        {
            var rail = new StackPanel { Orientation = Orientation.Vertical };
            KeyboardNavigation.SetTabNavigation(rail, KeyboardNavigationMode.Once); // the rail is one tab stop
            return rail;
        });
    }

    /// <inheritdoc cref="DisplayModeProperty"/>
    public BackstageDisplayMode DisplayMode
    {
        get => GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    /// <inheritdoc cref="BackRequestedEvent"/>
    public event EventHandler<RoutedEventArgs>? BackRequested
    {
        add => AddHandler(BackRequestedEvent, value!);
        remove => RemoveHandler(BackRequestedEvent, value!);
    }

    /// <summary>Moves keyboard focus onto the selected rail destination (or the first selectable one) so a freshly-shown
    /// Backstage is keyboard-navigable without a pointer. A no-op (returns false) when no container is realized/focusable
    /// yet — the FullScreen host relies on window-activation auto-focus instead; the Menu host calls this after the
    /// popup lays out (synchronously on open).</summary>
    public bool FocusSelectedDestination()
    {
        var count = ItemContainerGenerator.ContainerCount;
        var index = SelectedIndex >= 0 ? SelectedIndex : NextSelectableIndex(0, +1, count);
        return index >= 0
            && ItemContainerGenerator.ContainerFromIndex(index) is { } container
            && container.Focus(FocusNavigationMethod.Restore);
    }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new BackstageItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is BackstageItem;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_backButton is not null)
            _backButton.Click -= OnBackButtonClick;
        _backButton = GetTemplatePart<ButtonBase>(PartBackButton);
        if (_backButton is not null)
            _backButton.Click += OnBackButtonClick;

        // Invoking a command button in the DETAIL PANE completes the Backstage interaction and returns to the document
        // (the Office model — Save/Print/Export/… act and close). Wired on the content host so any ButtonBase.Click in a
        // destination's pane closes the Backstage; the rail's own selection (choosing a destination) is unaffected.
        if (_contentHost is not null)
            _contentHost.RemoveHandler(ButtonBase.ClickEvent, OnDetailPaneClick);
        _contentHost = GetTemplatePart<UIElement>(PartContentHost);
        _contentHost?.AddHandler(ButtonBase.ClickEvent, OnDetailPaneClick, handledEventsToo: true);
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_backButton is not null)
            _backButton.Click -= OnBackButtonClick; // unhook so a re-template doesn't strand the handler
        _contentHost?.RemoveHandler(ButtonBase.ClickEvent, OnDetailPaneClick);
        _backButton = null;
        _contentHost = null;
        base.OnTemplateDetaching(old);
    }

    private void OnBackButtonClick(object? sender, ClickEventArgs e)
    {
        RaiseBackRequested();
        e.Handled = true;
    }

    // A detail-pane command button was invoked. Close the Backstage — but DEFER to the next dispatcher turn so the
    // button's own OnClick (its Command) runs first: closing synchronously here tears down the surface and detaches the
    // button before its command executes (the BarDropDownButton read-after-raise recycling gotcha). Idempotent.
    private void OnDetailPaneClick(object? sender, ClickEventArgs e)
    {
        if (UIApplication.Current?.Dispatcher is { } dispatcher)
            dispatcher.Post(RaiseBackRequested);
        else
            RaiseBackRequested();
    }

    private void RaiseBackRequested() => RaiseEvent(new RoutedEventArgs(BackRequestedEvent, this));

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Vertical-rail navigation (Up/Down/Home/End) + Escape=back are handled here; Left/Right and everything else
        // fall to the base TabControl (its index-based nav is a harmless alias on a vertical rail; the base class-
        // handler chain still runs). Anchoring nav to the FOCUSED rail row (not the selected one) keeps a
        // focused-but-unselected destination reachable, mirroring TabControl.
        if (!e.Handled && HandleRailKey(e))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool HandleRailKey(KeyEventArgs e)
    {
        // Rail navigation claims only the UNMODIFIED forms — a Ctrl/Shift/Alt chord falls through to the base handler,
        // an ancestor InputBinding, or the dispatcher's navigation tail (mirroring TabControl's `when !ctrl` discipline;
        // otherwise a modifier-agnostic match would silently swallow, e.g., a Ctrl+Home an app bound above the Backstage).
        if (e.Modifiers != KeyModifiers.None)
            return false;

        if (e.Key == Key.Escape)
        {
            RaiseEvent(new RoutedEventArgs(BackRequestedEvent, this)); // ◂ keyboard twin — the host closes the surface
            return true;
        }

        var focused = FocusedRailIndex(e.OriginalSource);
        if (focused < 0)
            return false; // focus is in the detail pane → arrows are the detail content's own navigation

        var count = ItemContainerGenerator.ContainerCount;
        if (count == 0)
            return false;

        int target;
        switch (e.Key)
        {
            case Key.UpArrow:   target = NextSelectableIndex(focused - 1, -1, count); break;
            case Key.DownArrow: target = NextSelectableIndex(focused + 1, +1, count); break;
            case Key.Home:      target = NextSelectableIndex(0, +1, count); break;
            case Key.End:       target = NextSelectableIndex(count - 1, -1, count); break;
            // Left/Right have no meaning on a VERTICAL rail — CONSUME them (return true) rather than let the base
            // TabControl's horizontal index nav run: that nav (NextVisibleIndex) skips only Collapsed containers, NOT
            // IsSelectable=false rows, so a Left/Right off a content row would strand focus on a separator/section head
            // (a dead row with no detail pane). The rail is Up/Down-navigable only.
            case Key.LeftArrow:
            case Key.RightArrow: return true;
            default:            return false;
        }

        if (target < 0 || target == focused)
            return true; // consumed even with nowhere to go (a rail-edge arrow must not bubble out to switch surfaces)

        SelectedIndex = target; // selection follows focus — the detail pane swaps to the new destination
        var focusIndex = SelectedIndex >= 0 ? SelectedIndex : target;
        ItemContainerGenerator.ContainerFromIndex(focusIndex)?.Focus(FocusNavigationMethod.Directional);
        return true;
    }

    // The first SELECTABLE, non-Collapsed rail row at or beyond `start`, stepping by `step` — a non-selectable
    // destination (a [separator] / section header, IsSelectable=false) or a hidden one is skipped; -1 if none.
    private int NextSelectableIndex(int start, int step, int count)
    {
        for (var i = start; i >= 0 && i < count; i += step)
            if (ItemContainerGenerator.ContainerFromIndex(i) is BackstageItem { IsSelectable: true, Visibility: not Visibility.Collapsed })
                return i;
        return -1;
    }

    // The index of the rail row the key came from (a container of THIS Backstage), or -1 when focus is in the detail
    // pane (a walk up the visual chain reaches the BackstageItem when a rail row is focused).
    private int FocusedRailIndex(UIElement? source)
    {
        for (var node = source; node is not null; node = node.VisualParent)
            if (node is BackstageItem item && ItemContainerGenerator.IndexFromContainer(item) is >= 0 and var i)
                return i;
        return -1;
    }
}
