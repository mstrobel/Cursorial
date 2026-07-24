using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering.Imaging;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Controls;

/// <summary>
/// An item in a <see cref="Menu"/> / submenu / <c>ContextMenu</c> (design doc §12.7). A <b>leaf</b> (no sub-items)
/// invokes on click — raising <see cref="Click"/>, executing <see cref="Command"/>, toggling <see cref="IsChecked"/>
/// when <see cref="IsCheckable"/> — and dismisses the menu; a <b>submenu header</b> (has sub-items) toggles its
/// submenu <see cref="Popup"/> instead. Derives from <see cref="HeaderedItemsControl"/> (the header is the label,
/// the items are the submenu); it cannot also extend <see cref="ButtonBase"/> (single inheritance), so it carries
/// its own click/command surface. <c>:highlighted</c>/<c>:open</c> are <c>DirectProperty</c>-backed (flipped via
/// <see cref="UIElement.PseudoClasses"/>); <c>:checked</c> mirrors <see cref="IsChecked"/> via <see cref="PseudoClassMapping"/>.
/// </summary>
[TemplatePart(PartPopup, typeof(Popup))] // optional: a leaf item's template may omit the submenu surface
[TemplatePart(PartIcon, typeof(ContentPresenter))]  // optional: a leaf item's template may omit the submenu surface
[TemplatePart(PartGestureText, typeof(TextBlock))]  // optional: a leaf item's template may omit the submenu surface
public class MenuItem : HeaderedItemsControl, IAccessKeyTarget
{
    private const string PartPopup = "PART_Popup";
    private const string PartIcon = "PART_Icon";
    private const string PartGestureText = "PART_GestureText";

    /// <inheritdoc cref="IsWithinMenuProperty"/>
    internal static readonly UIPropertyKey<bool> IsWithinMenuPropertyKey =
        UIProperty.RegisterAttachedReadOnly<MenuItem, UIElement, bool>("IsWithinMenu", defaultValue: false, inherits: true);

    /// <summary>Indicates whether the element is within a <see cref="Menu"/> popup.</summary>
    public static readonly AttachedProperty<bool> IsWithinMenuProperty = (AttachedProperty<bool>) IsWithinMenuPropertyKey.Property;
    
    /// <summary>The icon or glyph to display in the left gutter.</summary>
    public static readonly StyledProperty<object?> IconProperty =
        UIProperty.Register<MenuItem, object?>(nameof(Icon), changed: OnIconChanged);

    /// <summary>The command invoked when a leaf item is clicked/activated.</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        UIProperty.Register<MenuItem, ICommand?>(nameof(Command), changed: OnCommandChanged);

    /// <summary>The parameter passed to <see cref="Command"/>.</summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        UIProperty.Register<MenuItem, object?>(nameof(CommandParameter), changed: OnCommandParameterChanged);

    /// <summary>The display-only gesture hint (e.g. "Ctrl+S") shown right-aligned, faint. Not a live binding.</summary>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        UIProperty.Register<MenuItem, string?>(nameof(InputGestureText));

    /// <summary>Whether the item shows a check column and toggles <see cref="IsChecked"/> on click.</summary>
    public static readonly StyledProperty<bool> IsCheckableProperty =
        UIProperty.Register<MenuItem, bool>(nameof(IsCheckable), changed: OnIsCheckableChanged);

    /// <summary>The checked state of a <see cref="IsCheckable"/> item (<c>:checked</c> mirrors it).</summary>
    public static readonly StyledProperty<bool> IsCheckedProperty =
        UIProperty.Register<MenuItem, bool>(nameof(IsChecked), changed: OnIsCheckedChanged);

    /// <summary>Whether this item's submenu is open (<c>:open</c>; two-way with the submenu <see cref="Popup"/>).</summary>
    public static readonly DirectProperty<MenuItem, bool> IsSubmenuOpenProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(IsSubmenuOpen), static m => m._isSubmenuOpen, static (m, v) => m.SetSubmenuOpen(v));

    /// <summary>Whether this item is the highlighted (current) item — hover or keyboard cursor (<c>:highlighted</c>).</summary>
    public static readonly DirectProperty<MenuItem, bool> IsHighlightedProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(IsHighlighted), static m => m._isHighlighted, static (m, v) => m.SetHighlighted(v));

    /// <summary>Whether this item has sub-items (a submenu header) rather than being an invoke-on-click leaf.</summary>
    public static readonly DirectProperty<MenuItem, bool> HasItemsProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(HasItems), getter: o => o.HasItems);

    /// <summary>Indicates whether the menu item is a top-level item within a <see cref="Menu"/> control.</summary>
    public static readonly DirectProperty<MenuItem, bool> IsTopLevelProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(IsTopLevel), getter: o => o.IsTopLevel);

    /// <summary>
    /// Whether this item's template should reserve the icon-tray gutter: <see langword="true"/> for a
    /// non-top-level item that has an <see cref="Icon"/> or any sibling in the same menu popup that
    /// does — the whole popup answers identically, so labels stay column-aligned whether or not each
    /// individual row carries an icon. Top-level bar items never show a tray. A read-only binding
    /// source (the tray visibility in a control template binds it); re-evaluated when any sibling's
    /// icon changes or items enter/leave the popup.
    /// </summary>
    public static readonly DirectProperty<MenuItem, bool> IsIconTrayVisibleProperty =
        UIProperty.RegisterDirect<MenuItem, bool>(nameof(IsIconTrayVisible), getter: o => o.IsIconTrayVisible);

    /// <summary>The bubbling event raised when a leaf item is invoked.</summary>
    public static readonly RoutedEvent<ClickEventArgs> ClickEvent =
        RoutedEvent<ClickEventArgs>.Register(nameof(Click), RoutingStrategy.Bubble, typeof(MenuItem));

    /// <summary>The bubbling event raised whenever the menu item is checked (<see cref="IsChecked"/> ⇒ true).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CheckedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Checked), RoutingStrategy.Bubble, typeof(MenuItem));

    /// <summary>The bubbling event raised whenever the menu item is unchecked (<see cref="IsChecked"/> ⇒ false).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> UncheckedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(Unchecked), RoutingStrategy.Bubble, typeof(MenuItem));

    /// <summary>The direct event raised whenever the value of <see cref="IsChecked"/> changes.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> IsCheckedChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(IsCheckedChanged), RoutingStrategy.Bubble, typeof(MenuItem));

    /// <summary>The bubbling event raised when this item's submenu opens (<see cref="IsSubmenuOpen"/> ⇒ true).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SubmenuOpenedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(SubmenuOpened), RoutingStrategy.Bubble, typeof(MenuItem));

    /// <summary>The bubbling event raised when this item's submenu closes (<see cref="IsSubmenuOpen"/> ⇒ false).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SubmenuClosedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(SubmenuClosed), RoutingStrategy.Bubble, typeof(MenuItem));

    private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(250);

    protected static readonly IconCarrier CheckmarkIcon = BuildCheckmarkIcon();

    private bool _isSubmenuOpen;
    private bool _isHighlighted;
    // private bool _isPointerOver;
    private bool _hasItemsCached;
    private bool _isTopLevelCached;
    private bool? _isIconTrayVisibleCached;
    private ItemsControl? _iconTrayOwner; // the owner at attach — detach must refresh the group it LEFT
    private char _registeredAccessKey;
    private Popup? _popup;
    private ContentPresenter? _icon;
    private UITimer? _hoverTimer;

    static MenuItem()
    {
        AffectsRender<MenuItem>(IconProperty);
        AffectsParentMeasure<MenuItem>(IconProperty);

        PseudoClassMapping.Register<MenuItem>(IsCheckedProperty, ":checked");
        PseudoClassMapping.Register<MenuItem>(IsCheckableProperty, ":checkable");
        PseudoClassMapping.Register<UIElement>(IsWithinMenuProperty, ":within-menu");
        PseudoClassMapping.Register<UIElement>(IsTopLevelProperty, ":top-level");

        // MenuItem.Header folds access-key literals ("_File" → mnemonic 'F') — the per-type flag (doc §12.5 ②).
        HeaderProperty.OverrideMetadata<MenuItem>(new PropertyMetadata<object?>(Changed: OnHeaderChanged) { ParsesAccessKeyLiterals = true });
    }

    /// <summary>Creates a menu item (focusable — keyboard navigation lands focus on the highlighted item).</summary>
    public MenuItem()
    {
        Focusable = true;
    }

    /// <summary>CLR sugar over <see cref="CheckedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Checked
    {
        add => AddHandler(CheckedEvent, value!);
        remove => RemoveHandler(CheckedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="UncheckedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? Unchecked
    {
        add => AddHandler(UncheckedEvent, value!);
        remove => RemoveHandler(UncheckedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="IsCheckedChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? IsCheckedChanged
    {
        add => AddHandler(IsCheckedChangedEvent, value!);
        remove => RemoveHandler(IsCheckedChangedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="SubmenuOpenedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? SubmenuOpened
    {
        add => AddHandler(SubmenuOpenedEvent, value!);
        remove => RemoveHandler(SubmenuOpenedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="SubmenuClosedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? SubmenuClosed
    {
        add => AddHandler(SubmenuClosedEvent, value!);
        remove => RemoveHandler(SubmenuClosedEvent, value!);
    }

    /// <inheritdoc/>
    protected internal override bool HandlesScrolling => true;

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="CommandProperty"/>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <inheritdoc cref="CommandParameterProperty"/>
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc cref="IsCheckableProperty"/>
    public bool IsCheckable { get => GetValue(IsCheckableProperty); set => SetValue(IsCheckableProperty, value); }

    /// <inheritdoc cref="IsCheckedProperty"/>
    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    /// <inheritdoc cref="IsSubmenuOpenProperty"/>
    public bool IsSubmenuOpen { get => _isSubmenuOpen; set => SetSubmenuOpen(value); }

    /// <inheritdoc cref="IsHighlightedProperty"/>
    public bool IsHighlighted { get => _isHighlighted; set => SetHighlighted(value); }

    /// <inheritdoc cref="HasItemsProperty"/>
    public bool HasItems => ItemContainerGenerator.ContainerCount > 0;

    /// <inheritdoc cref="IsTopLevelProperty"/>
    public bool IsTopLevel => OwnerItemsControl is Menu;

    /// <inheritdoc cref="IsIconTrayVisibleProperty"/>
    public bool IsIconTrayVisible => _isIconTrayVisibleCached ??= ComputeIsIconTrayVisible();

    private bool ComputeIsIconTrayVisible()
    {
        if (OwnerItemsControl is not {} owner)
            return ShouldDisplayIconTray(this); // standalone (no popup): its own icon decides

        return owner is not Menu && AnyContainerHasIcon(owner);
    }

    // 'Valid' is simply non-null for now — one place to tighten if that ever changes.
    private static bool HasValidIcon(MenuItem? item) => item?.Icon is not null;

    private static bool AnyContainerHasIcon(ItemsControl owner)
    {
        var generator = owner.ItemContainerGenerator;

        for (var i = 0; i < generator.ContainerCount; i++)
        {
            if (generator.ContainerFromIndex(i) is MenuItem sibling && ShouldDisplayIconTray(sibling))
                return true;
        }

        return false;
    }

    private static bool ShouldDisplayIconTray(MenuItem item) => item.IsCheckable || HasValidIcon(item);

    /// <summary>CLR sugar over <see cref="ClickEvent"/>.</summary>
    public event EventHandler<ClickEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value!);
        remove => RemoveHandler(ClickEvent, value!);
    }

    /// <summary>The command-aware enabled gate (CD25): enabled unless a non-null <see cref="Command"/> reports it can't execute.</summary>
    protected override bool IsEnabledCore => Command is not { } command || command.CanExecute(CommandParameter);

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new MenuItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is MenuItem or Separator;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        SubscribeContainersChanged();
        SubscribeCanExecute(); // CD25: a live CanExecuteChanged must re-gate IsEnabledCore (matches ButtonBase)
        RegisterAccessKey();   // register the Header mnemonic with the AccessKeyManager (doc §12.5)
        UpdateHasItems();
        UpdateIsTopLevel();

        // This item may bring the popup's first icon (or join a group of items w/ at least one icon group) —
        // refresh the group, and remember the owner: detach must refresh the group this item LEAVES.
        _iconTrayOwner = OwnerItemsControl;
        RefreshIconTrayGroup(_iconTrayOwner);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        StopHoverTimer();       // owners stop timers on detach (§12.2)
        SetSubmenuOpen(false);  // close the submenu so its Popup surface doesn't leak on detach
        UnsubscribeContainersChanged();
        UnsubscribeCanExecute();
        UnregisterAccessKey();
        UpdateHasItems();

        // Refresh the group this item is LEAVING (it may have carried the popup's last icon) via
        // the owner captured at attach — the logical link may already be severed here.
        var leftGroup = _iconTrayOwner;
        _iconTrayOwner = null;
        RefreshIconTrayGroup(leftGroup);

        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup.ClearValue(IsWithinMenuPropertyKey);
        }

        if (_icon is not null)
            _icon.ClearValue(ContentPresenter.ContentProperty);

        _popup = GetTemplatePart<Popup>(PartPopup);
        _icon = GetTemplatePart<ContentPresenter>(PartIcon);

        if (_popup is not null)
        {
            _popup.SetValue(IsWithinMenuPropertyKey, true);
            _popup.PlacementTarget = this;
            _popup.Placement = OwnerItemsControl is Menu ? PlacementMode.Bottom : PlacementMode.Right; // bar → down, nested → right
            _popup.Closed += OnPopupClosed;
            _popup.SetCurrentValue(Popup.IsOpenProperty, _isSubmenuOpen); // sync the part to current state
        }

        if (_icon is not null)
            UpdateIconSite();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        if (HasItems)
        {
            // A click is a deliberate activation (unlike a hover, which must not steal focus — OnMouseEnter): take
            // keyboard focus so the menu enters keyboard mode (arrows then navigate — #134) and, via the Pointer
            // method, arm the auto-return so a later leaf-invoke / Escape returns focus to the pre-menu origin (the
            // Menu is a non-retaining scope). Focus first so OnGotFocus's CloseSiblings runs before the open. Pointer
            // focus never sets :focus-visible (doc §7.7), so a mouse-opened header shows the highlight, not the ring.
            Focus(FocusNavigationMethod.Pointer);

            if (_isSubmenuOpen)
                SetSubmenuOpen(false); // toggle closed
            else
                OpenSubmenu();         // open (closing any sibling submenu)
        }
        else
        {
            Invoke(InvokeMethod.Pointer); // a leaf invokes + dismisses
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);

        // _isPointerOver = true;

        RefreshHighlight();

        if (HasItems && !_isSubmenuOpen)
        {
            if (MenuIsActive())
                OpenSubmenu(); // a sibling submenu is already open ⇒ switch immediately (no delay)
            else if (IsTopLevel is false)
                StartHoverTimer(); // otherwise open after the hover delay
        }

        // Take keyboard focus on hover only when the menu is ALREADY keyboard-driven (the top-level menu holds
        // keyboard focus). A pure-mouse hover still highlights (above) and arms hover-open / sibling-switch, but must
        // not STEAL focus when the user isn't navigating by keyboard. Programmatic method — mouse entry never captures
        // a return scope, so a mouse-only menu interaction leaves keyboard focus on the pre-menu origin throughout.
        if (TopLevelMenu is { IsKeyboardFocusWithin: true })
            Focus();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        // _isPointerOver = false;
        RefreshHighlight();
        StopHoverTimer(); // cancel a pending hover-open (an already-open submenu stays open)
    }

    /// <inheritdoc/>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        CloseSiblings();
        RefreshHighlight(); // the focused item is the highlighted (current) item
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        RefreshHighlight();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsFocused)
            return; // OnKeyDown is a class handler on the whole bubble route — only the FOCUSED item navigates,
                    // else a key a focused sub-item leaves unhandled bubbles to an ancestor header that re-interprets it

        var topLevel = OwnerItemsControl is Menu; // bar items run horizontally; submenu items run vertically

        switch (e.Key)
        {
            case Key.DownArrow when topLevel && HasItems:
                OpenSubmenuWithFocus(); // Down on the bar opens the submenu and enters it
                break;
            case Key.DownArrow when !topLevel:
                MoveToSibling(+1);
                break;
            case Key.UpArrow when !topLevel:
                MoveToSibling(-1);
                break;
            case Key.PageDown when !topLevel:
                MoveToEdgeSibling(last: true); // a menu isn't paged — PageDown lands on the last item
                break;
            case Key.PageUp when !topLevel:
                MoveToEdgeSibling(last: false); // …PageUp on the first
                break;
            case Key.RightArrow when topLevel:
                MoveToSibling(+1); // next bar header
                break;
            case Key.RightArrow when HasItems:
                OpenSubmenuWithFocus(); // descend into a nested submenu
                break;
            case Key.LeftArrow when topLevel:
                MoveToSibling(-1); // previous bar header
                break;
            case Key.LeftArrow:
                CloseAndFocusParent(); // ascend a level
                break;
            case Key.Enter:
                if (HasItems)
                    OpenSubmenuWithFocus();
                else
                    Invoke(InvokeMethod.KeyboardEnter);
                break;
            case Key.Space or Key.Character when e.Text.Span is " ":
                if (IsCheckable)
                    Invoke(InvokeMethod.KeyboardSpace);
                else if (HasItems)
                    OpenSubmenuWithFocus();
                break;
            default:
                return; // not a menu-nav key — leave unhandled. A focused submenu item's Escape is the submenu
                        // Popup's (→ OnPopupClosed, which whole-chain-collapses + returns focus on EscapeKey); a
                        // top-level header's Escape with no submenu open is Menu.OnKeyDown's.
        }

        e.Handled = true;
    }

    /// <summary>Invokes a leaf item: raise <see cref="Click"/>, toggle <see cref="IsChecked"/> (if checkable),
    /// execute <see cref="Command"/>, then dismiss the whole menu.</summary>
    protected virtual void Invoke(InvokeMethod method = InvokeMethod.Programmatic)
    {
        RaiseEvent(RentEvent(ClickEvent));

        if (IsCheckable)
            SetCurrentValue(IsCheckedProperty, !IsChecked); // SetCurrentValue preserves a two-way IsChecked binding

        if (Command is {} command && command.CanExecute(CommandParameter))
            command.Execute(CommandParameter);

        if (method is InvokeMethod.KeyboardEnter || IsCheckable is false)
            CloseMenuChain();
    }

    private void RefreshHighlight() => SetHighlighted(IsFocused/* || _isPointerOver*/); // highlighted = focused or hovered

    // Opens this submenu, first closing any sibling submenu (only one branch of a level is open at a time).
    private void OpenSubmenu()
    {
        StopHoverTimer();
        CloseSiblings();
        SetSubmenuOpen(true);
    }

    // Keyboard open: open the submenu and move focus to its first item (so arrows work inside it).
    private void OpenSubmenuWithFocus()
    {
        OpenSubmenu();
        FocusFirstItem();
    }

    private void FocusFirstItem()
    {
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Focusable: true } first)
            {
                first.Focus(FocusNavigationMethod.Directional);
                return;
            }
    }

    // Moves focus to the next/previous focusable sibling at this level, wrapping and skipping Separators.
    // Focuses the first (last:false) or last (last:true) focusable sibling — the menu "page" jump.
    private void MoveToEdgeSibling(bool last)
    {
        if (OwnerItemsControl is not { } owner)
            return;

        var generator = owner.ItemContainerGenerator;
        var count = generator.ContainerCount;
        for (var i = last ? count - 1 : 0; i >= 0 && i < count; i += last ? -1 : 1)
            if (generator.ContainerFromIndex(i) is { Focusable: true } target)
            {
                target.Focus(FocusNavigationMethod.Directional);
                return;
            }
    }

    private void MoveToSibling(int delta)
    {
        if (OwnerItemsControl is not { } owner)
            return;

        var generator = owner.ItemContainerGenerator;
        var count = generator.ContainerCount;
        var index = generator.IndexFromContainer(this);
        if (count == 0 || index < 0)
            return;

        for (var step = 1; step <= count; step++)
        {
            var i = (((index + delta * step) % count) + count) % count; // wrap
            if (generator.ContainerFromIndex(i) is { Focusable: true } target)
            {
                target.Focus(FocusNavigationMethod.Directional);
                return;
            }
        }
    }

    // Ascends one level: closes the parent header's submenu and focuses the header (Left / back-out).
    private void CloseAndFocusParent()
    {
        if (OwnerItemsControl is MenuItem parent)
        {
            parent.SetSubmenuOpen(false);
            parent.Focus(FocusNavigationMethod.Directional);
        }
    }

    private void StartHoverTimer()
    {
        StopHoverTimer();
        _hoverTimer = UITimer.Start(HoverOpenDelay, OpenSubmenu); // leave cancels it before it fires
    }

    private void StopHoverTimer()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
    }

    // Whether any sibling at this level already has its submenu open (the menu is "active").
    private bool MenuIsActive()
    {
        if (OwnerItemsControl is not { } owner)
            return false;

        for (var i = 0; i < owner.ItemContainerGenerator.ContainerCount; i++)
            if (owner.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { _isSubmenuOpen: true })
                return true;
        return false;
    }

    private void CloseSiblings()
    {
        if (OwnerItemsControl is not { } owner)
            return;

        for (var i = 0; i < owner.ItemContainerGenerator.ContainerCount; i++)
        {
            if (owner.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { _isSubmenuOpen: true } sibling && !ReferenceEquals(sibling, this))
                sibling.SetSubmenuOpen(false);
        }
    }

    private void SetSubmenuOpen(bool value)
    {
        if (!SetAndRaise(IsSubmenuOpenProperty, ref _isSubmenuOpen, value))
            return;

        PseudoClasses.Set(":open", value);
        _popup?.SetCurrentValue(Popup.IsOpenProperty, value); // drive the part (light-dismiss writes back via OnPopupClosed)

        // Bubble Submenu{Opened,Closed} so a Menu bar / ContextMenu host can watch every descendant item from one
        // handler. This is the single chokepoint every open/close path funnels through (keyboard collapse,
        // sibling-switch, chain-close, light-dismiss via OnPopupClosed → SetSubmenuOpen(false)).
        var routedEvent = value ? SubmenuOpenedEvent : SubmenuClosedEvent;
        var args = RentEvent(routedEvent);
        RaiseEvent(args);
    }

    private void SetHighlighted(bool value)
    {
        if (SetAndRaise(IsHighlightedProperty, ref _isHighlighted, value))
            PseudoClasses.Set(":highlighted", value);
    }

    // A genuine user dismiss — Escape on a focused submenu item, or a click-away — collapses the WHOLE menu chain and
    // returns focus (decision ③). A Programmatic close (the one CloseMenuChain itself issues via SetSubmenuOpen(false),
    // plus sibling-switch / left-arrow ascent / detach) takes the idempotent one-level path — the reentrancy fence.
    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        if (e.Reason is PopupCloseReason.EscapeKey or PopupCloseReason.LightDismiss)
            CloseMenuChain();
        else
            SetSubmenuOpen(false);
    }

    // Collapses the WHOLE menu (a leaf invoke, Escape, or a click-away dismisses everything — decision ③): return
    // focus to the pre-menu origin, THEN close every open submenu up the ownership chain. The Menu is a non-retaining
    // focus scope, so RestoreRetainedFocus resolves the entry-captured origin; doing it FIRST — before any Popup
    // closes — means each closing submenu observes no keyboard focus within and SUPPRESSES its own per-level
    // trigger-restore (Popup W4). On the paths that enter here with focus still on a live menu element (a leaf invoke,
    // a light-dismiss, a top-level-header Escape) that is a single focus move. The deep-submenu Escape path enters via
    // the innermost Popup's OWN teardown (CloseCore → ClosePopup detaches the focused item → detach-repair moves focus
    // to the parent header, Programmatic) BEFORE firing Closed → OnPopupClosed → here, so it shows one extra transition
    // — but both hops are Restore/Programmatic (skipped by MarkReturnableEntry) and the final destination is the
    // origin. A ContextMenu RETAINS focus (RestoreRetainedFocus no-ops for it) and its Popup's W4 restore returns focus
    // to the right-clicked trigger — so it is simply closed (its surface tears down the rest of the chain).
    private void CloseMenuChain()
    {
        // Resolve the top-level owner (a Menu bar, or a ContextMenu) up the ownership chain.
        ItemsControl? owner = null;
        for (UIElement? node = this; node is not null; node = (node as MenuItem)?.OwnerItemsControl)
            if (node is Menu || node is ContextMenu)
            {
                owner = (ItemsControl) node;
                break;
            }

        if (owner is Menu && UIApplication.Current?.FocusManager is { } focus)
            focus.RestoreRetainedFocus(owner); // focus leaves to the origin first — one move, no W4 cascade

        for (UIElement? node = this; node is not null; node = (node as MenuItem)?.OwnerItemsControl)
        {
            if (node is MenuItem { _isSubmenuOpen: true } item)
                item.SetSubmenuOpen(false);
            else if (node is ContextMenu context)
                context.Close();
        }
    }

    // The owning top-level menu surface (a Menu bar or a ContextMenu), walked up the LOGICAL chain so it crosses the
    // submenu Popup boundaries (an item nested in a submenu popup still reaches its root menu). Used by the hover-gate.
    private ItemsControl? TopLevelMenu
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is Menu || node is ContextMenu)
                    return (ItemsControl) node;
            return null;
        }
    }

    // The ItemsControl (Menu or parent MenuItem) that generated this container — its logical parent (punch 43).
    private ItemsControl? OwnerItemsControl
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is ItemsControl owner)
                    return owner;
            return null;
        }
    }

    private void SubscribeCanExecute()
    {
        if (Command is { } command)
            command.CanExecuteChanged += OnCanExecuteChanged;
    }

    private void UnsubscribeCanExecute()
    {
        if (Command is { } command)
            command.CanExecuteChanged -= OnCanExecuteChanged;
    }

    private void SubscribeContainersChanged()
    {
        ItemContainerGenerator.ContainersChanged += OnContainersChanged;
    }

    private void UnsubscribeContainersChanged()
    {
        ItemContainerGenerator.ContainersChanged -= OnContainersChanged;
    }

    private void OnContainersChanged(object? sender, ContainersChangedEventArgs e)
    {
        UpdateHasItems();
    }

    private void UpdateHasItems()
    {
        var hadItems = _hasItemsCached;
        var hasItems = HasItems;
        
        if (hadItems != hasItems)
        {
            _hasItemsCached = hasItems;
            DispatchPropertyChanged(HasItemsProperty, null, hadItems, hasItems, BindingPriority.LocalValue);
        }
    }

    private void UpdateIsTopLevel()
    {
        var wasTopLevel = _isTopLevelCached;
        var isTopLevel = IsTopLevel;

        if (wasTopLevel != isTopLevel)
        {
            _isTopLevelCached = isTopLevel;
            DispatchPropertyChanged(IsTopLevelProperty, null, wasTopLevel, IsTopLevel, BindingPriority.LocalValue);
        }
    }

    private void UpdateIsIconTrayVisible()
    {
        var was = _isIconTrayVisibleCached;

        _isIconTrayVisibleCached = null;

        var now = IsIconTrayVisible;

        if (was != now)
            DispatchPropertyChanged(IsIconTrayVisibleProperty, null, was, now, BindingPriority.LocalValue);
    }

    /// <summary>
    /// Re-evaluates <see cref="IsIconTrayVisible"/> for this item AND every sibling in the same
    /// popup — the tray is a per-popup fact, so one item's icon (or arrival/departure) flips the
    /// whole group. <paramref name="owner"/> overrides the live owner walk for the detach path,
    /// where the logical link is already severed and only the captured owner knows the group.
    /// </summary>
    private void RefreshIconTrayGroup(ItemsControl? owner = null)
    {
        owner ??= OwnerItemsControl;
        UpdateIsIconTrayVisible();

        if (owner is null)
            return;

        var generator = owner.ItemContainerGenerator;

        for (var i = 0; i < generator.ContainerCount; i++)
        {
            if (generator.ContainerFromIndex(i) is MenuItem sibling && !ReferenceEquals(sibling, this))
                sibling.UpdateIsIconTrayVisible();
        }
    }

    private void UpdateIconSite()
    {
        if (_icon is null)
            return;

        if (Icon is Icon icon)
        {
            _icon.Content = icon;
            _icon.Visibility = Visibility.Visible;
        } 
        else if (Icon is IconCarrier carrier)
        {
            _icon.Content = carrier;
            _icon.Visibility = Visibility.Visible;
        }
        else if (Icon is ImageData image)
        {
            _icon.Content = new ImagePresenter { Source = image };
            _icon.Visibility = Visibility.Visible;
        }
        else if (Icon is {} other)
        {
            _icon.Content = other.ToString();
            _icon.Visibility = Visibility.Visible;
        }
        else if (IsChecked)
        {
            _icon.Content = CheckmarkIcon;
            _icon.Visibility = Visibility.Visible;
        }
        else
        {
            _icon.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => InvalidateIsEnabledCore();

    // ── access keys (doc §12.5; mnemonic source is Header, not Content) ────────────────────────────────

    bool IAccessKeyTarget.IsAccessKeyEligible => IsEffectivelyEnabled && IsEffectivelyVisible;

    void IAccessKeyTarget.OnAccessKey(AccessKeyEventArgs e) => OnAccessKey(e);

    /// <summary>Access-key reaction: a multi-match only focuses (the manager already did); a unique match opens the
    /// submenu (header) or invokes (leaf).</summary>
    protected virtual void OnAccessKey(AccessKeyEventArgs e)
    {
        if (e.IsMultiMatch)
            return; // the manager focused us; cycling among matches never invokes

        if (HasItems)
            OpenSubmenuWithFocus();
        else
            Invoke(InvokeMethod.AccessKey);
    }

    private AccessText GetAccessText()
        => Header is string s && HeaderProperty.GetMetadata(GetType()).ParsesAccessKeyLiterals == true ? AccessText.Parse(s) : default;

    private void RegisterAccessKey()
    {
        if (UIApplication.Current?.AccessKeys is not { } manager)
            return;

        var access = GetAccessText();
        if (!access.HasKey)
            return;

        _registeredAccessKey = access.Key;
        manager.Register(access.Key, this);
    }

    private void UnregisterAccessKey()
    {
        if (_registeredAccessKey != '\0' && UIApplication.Current?.AccessKeys is { } manager)
            manager.Unregister(_registeredAccessKey, this);
        _registeredAccessKey = '\0';
    }

    private static void OnHeaderChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is MenuItem { IsAttachedToTree: true } item) // re-fold + re-register the mnemonic
        {
            item.UnregisterAccessKey();
            item.RegisterAccessKey();
        }
    }

    private static void OnCommandChanged(UIObject sender, ICommand? oldValue, ICommand? newValue)
    {
        if (sender is not MenuItem item)
            return;

        // Re-point the CanExecuteChanged subscription (on Command change AND, via attach/detach, lifetime) — CD25.
        if (oldValue is { } old && item.IsAttachedToTree)
            old.CanExecuteChanged -= item.OnCanExecuteChanged;
        if (newValue is { } @new && item.IsAttachedToTree)
            @new.CanExecuteChanged += item.OnCanExecuteChanged;

        item.InvalidateIsEnabledCore();
    }

    private static void OnIconChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is not MenuItem item)
            return;

        item.UpdateIconSite();

        // An icon appearing on (or leaving) ANY item flips the whole popup's tray — refresh the group.
        item.RefreshIconTrayGroup();
    }

    private static void OnIsCheckableChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not MenuItem item)
            return;

        item.UpdateIconSite();

        // An icon appearing on (or leaving) ANY item flips the whole popup's tray — refresh the group.
        item.RefreshIconTrayGroup();
    }

    private static void OnIsCheckedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not MenuItem item)
            return;

        item.UpdateIconSite();

        // An icon appearing on (or leaving) ANY item flips the whole popup's tray — refresh the group.
        item.RefreshIconTrayGroup();

        // Control author gets notified before the event(s) go out.
        item.OnIsCheckedChangedCore(oldValue, newValue);

        var routedEvent = newValue ? CheckedEvent : UncheckedEvent;
        var args = item.RentEvent(routedEvent);

        item.RaiseEvent(args);
        args = item.RentEvent(IsCheckedChangedEvent);
        item.RaiseEvent(args);
    }

    /// <summary>The control-author hook called after <see cref="IsChecked"/> changes, before the routed event.</summary>
    private protected virtual void OnIsCheckedChangedCore(bool? oldValue, bool? newValue)
    {
    }

    private static void OnCommandParameterChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is MenuItem item)
            item.InvalidateIsEnabledCore(); // the gate reads CanExecute(CommandParameter)
    }
    
    /// <inheritdoc cref="IsWithinMenuProperty"/>
    public static bool GetIsWithinMenu(UIElement element) => element.GetValue(IsWithinMenuProperty);
    
    private static IconCarrier BuildCheckmarkIcon()
    {
        return new IconCarrier
               {
                   Glyph = "",
                   GlyphWidth = 2,
                   Emoji = "✅",
                   Text = "✓"
               };
    }
}
