using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// The shared base for the bar drop-openers — a <see cref="ButtonBase"/> that hosts a dropdown <see cref="Popup"/>
/// (<c>PART_Popup</c>) over its <see cref="DropDownContent"/> (a menu, a gallery, …). <see cref="BarPopupButton"/>
/// opens the dropdown from the whole control; <see cref="BarSplitButton"/> opens it from a separate <c>▾</c> zone
/// while the label runs the primary action.
/// <para>
/// Opening moves focus INTO the dropdown content (a drop-opener enters rather than parking on the face); the dropdown
/// closes back to the face on Escape / light-dismiss / a content invoke (the <see cref="Popup"/>'s on-close focus
/// restore returns focus to the opener, since the opener was focused when the popup opened). The <b>opener</b> element
/// is a retaining focus scope (a <c>FindReturningScope</c> barrier) so a pointer-click-open never trips an enclosing
/// non-retaining <see cref="Toolbar"/>'s auto-return and yanks focus back out of the dropdown — which opener is the
/// barrier differs per control (the whole <see cref="BarPopupButton"/>; only the <c>▾</c> part of a
/// <see cref="BarSplitButton"/>, so its primary label action still auto-returns like a <see cref="BarButton"/>).
/// </para>
/// </summary>
[TemplatePart(PartPopup, typeof(Popup))]
public abstract class BarDropDownButton : ButtonBase
{
    private protected const string PartPopup = "PART_Popup";
    private protected const string PartDropDownContent = "PART_DropDownContent";

    /// <summary>The content shown in the dropdown Popup (a menu of items, a gallery grid, …). Hosted through the
    /// code-set <c>PART_DropDownContent</c> presenter — a <c>TemplateBinding</c> inside <c>Popup.Child</c> does not
    /// resolve (the popup-child subtree carries no <c>TemplatedParent</c> stamp).</summary>
    public static readonly StyledProperty<object?> DropDownContentProperty =
        UIProperty.Register<BarDropDownButton, object?>(nameof(DropDownContent), changed: OnDropDownContentChanged);

    /// <inheritdoc cref="BarButton.IconProperty"/>
    public static readonly StyledProperty<object?> IconProperty =
        BarButton.IconProperty.AddOwner<BarDropDownButton>(); // same identity as BarButton's — one template binds both

    /// <inheritdoc cref="BarButton.InputGestureTextProperty"/>
    public static readonly StyledProperty<string?> InputGestureTextProperty =
        BarButton.InputGestureTextProperty.AddOwner<BarDropDownButton>();

    /// <summary>Whether the dropdown is open (<c>:open</c>; two-way with the Popup).</summary>
    public static readonly DirectProperty<BarDropDownButton, bool> IsDropDownOpenProperty =
        UIProperty.RegisterDirect<BarDropDownButton, bool>(
            nameof(IsDropDownOpen), static b => b._isDropDownOpen, static (b, v) => b.SetDropDownOpen(v));

    /// <summary>Where the dropdown opens relative to the opener (default <see cref="PlacementMode.Bottom"/> — a downward
    /// <c>▾</c> caret opening below the face). A <see cref="Toolbar"/> flips this to <see cref="PlacementMode.Left"/>
    /// when the opener is re-parented into its (right-anchored) overflow menu, so the dropdown flies out to the SIDE
    /// like a submenu rather than opening downward over the menu rows (which would overlap them and hijack the menu's
    /// up/down navigation). The <see cref="CaretGlyph"/> and the open/back arrow keys follow the placement — a side
    /// placement adopts the MenuItem submenu model (the open arrow opens AND enters; the opposite arrow backs out) while
    /// <see cref="PlacementMode.Bottom"/> keeps the ComboBox model (open parks on the face, a second Down enters).</summary>
    public static readonly StyledProperty<PlacementMode> DropDownPlacementProperty =
        UIProperty.Register<BarDropDownButton, PlacementMode>(
            nameof(DropDownPlacement), PlacementMode.Bottom, changed: OnDropDownPlacementChanged);

    /// <summary>The direction glyph the face shows for the current <see cref="DropDownPlacement"/> (▾ down / ▴ up /
    /// ▸ right / ◂ left) — the theme templates bind their caret to it via <c>TemplateBinding</c>, so a placement flip
    /// re-glyphs the caret live.</summary>
    public static readonly DirectProperty<BarDropDownButton, string> CaretGlyphProperty =
        UIProperty.RegisterDirect<BarDropDownButton, string>(nameof(CaretGlyph), static b => b._caretGlyph);

    private bool _isDropDownOpen;
    private string _caretGlyph = CaretFor(PlacementMode.Bottom);
    private bool _pendingEnter; // a side-placement open requested focus-into; completed on Popup.Opened (content laid out)
    private Popup? _popup;
    private ContentPresenter? _contentSite; // PART_DropDownContent — hosts DropDownContent (code-set, see the property)

    /// <summary>The shared <see cref="BarCommand"/> auto-fill state for the concrete split/popup buttons.</summary>
    private protected readonly BarCommandSync CommandSync = new();

    /// <inheritdoc cref="DropDownContentProperty"/>
    public object? DropDownContent { get => GetValue(DropDownContentProperty); set => SetValue(DropDownContentProperty, value); }

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="InputGestureTextProperty"/>
    public string? InputGestureText { get => GetValue(InputGestureTextProperty); set => SetValue(InputGestureTextProperty, value); }

    /// <inheritdoc cref="IsDropDownOpenProperty"/>
    public bool IsDropDownOpen { get => _isDropDownOpen; set => SetDropDownOpen(value); }

    /// <inheritdoc cref="DropDownPlacementProperty"/>
    public PlacementMode DropDownPlacement
    {
        get => GetValue(DropDownPlacementProperty);
        set => SetValue(DropDownPlacementProperty, value);
    }

    /// <inheritdoc cref="CaretGlyphProperty"/>
    public string CaretGlyph => _caretGlyph;

    /// <summary>The dropdown Popup template part (available after <see cref="OnApplyTemplate"/>) — the concrete
    /// controls consult it for hit-test / focus-containment decisions.</summary>
    private protected Popup? DropDownPopup => _popup;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;

        _popup = GetTemplatePart<Popup>(PartPopup);

        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = DropDownPlacement; // Bottom by default; a Toolbar flips it to Left inside its overflow menu
            _popup.KeepOpenOnAnchorPress = true; // the opener owns the toggle (no dismiss-then-reopen race)
            _popup.Closed -= OnPopupClosed;
            _popup.Closed += OnPopupClosed;
            _popup.Opened -= OnPopupOpened;
            _popup.Opened += OnPopupOpened; // a side-placement open enters the content once the surface is laid out
            _popup.SetCurrentValue(Popup.IsOpenProperty, _isDropDownOpen); // sync the part to current state
        }

        if (_contentSite is not null)
            _contentSite.RemoveHandler(ButtonBase.ClickEvent, OnDropDownItemClick);

        _contentSite = GetTemplatePart<ContentPresenter>(PartDropDownContent);
        if (_contentSite is not null)
        {
            _contentSite.Content = DropDownContent; // code-set (a TemplateBinding in Popup.Child would not resolve)
            // The dropdown is a self-contained arrow-nav scope so Up/Down move among its items (Contained, NOT Cycle:
            // Up from the first item returns focus to the opener face — see OnKeyDown — like a menu). Invoking any
            // item closes the dropdown (menu-like).
            KeyboardNavigation.SetDirectionalNavigation(_contentSite, DirectionalNavigationMode.Contained);
            // The dropdown content is also a RETAINING focus-scope barrier: it is an ANCESTOR of every realized dropdown
            // item (unlike the opener, which is only an ancestor of the popup for the whole-control BarPopupButton — a
            // BarSplitButton's retaining ▾ zone is a SIBLING of the popup, never on the item's returning-scope walk). So
            // invoking a dropdown item does NOT trip an enclosing non-retaining Toolbar's auto-return (which would yank
            // focus back to the pre-entry document instead of the Popup W4 restore-to-face). This unifies the close-focus
            // behavior across BarPopupButton and BarSplitButton; the split button's PRIMARY label action does not cross
            // the content presenter, so it still auto-returns like a BarButton.
            FocusManager.SetIsFocusScope(_contentSite, true);
            _contentSite.AddHandler(ButtonBase.ClickEvent, OnDropDownItemClick, handledEventsToo: true);
        }
    }

    // A drop-down item was invoked (a Click bubbling out of the content) — close the drop-down (the Popup's W4 restore
    // returns focus to the face). Menu-like: the command still runs, we just dismiss.
    //
    // The close is DEFERRED to the next dispatcher turn, and that is load-bearing. Closing synchronously here — during
    // the item's Click bubble, BEFORE ButtonBase.OnClick reads the item's Command (read-after-raise) — tears down the
    // popup surface and detaches the content, which the ContentPresenter releases on detach (recycling), clearing the
    // item's inherited DataContext and its bound Command before OnClick can execute it (the command silently no-ops).
    // Deferring lets the item's OnClick finish (running the command) first; the detach still happens next turn, so a
    // later re-template releases the content cleanly. (Framework-level binding/hosting fixes — keep-value-on-detach,
    // ContentPresenter non-recycling — each regressed retention or re-templating; the deferral is the correct, targeted
    // fix.) Idempotent: the deferred CloseDropDown is a no-op if something already closed the dropdown.
    private void OnDropDownItemClick(object? sender, ClickEventArgs e)
    {
        if (UIApplication.Current?.Dispatcher is { } dispatcher)
            dispatcher.Post(CloseDropDown);
        else
            CloseDropDown();
    }

    private static void OnDropDownContentChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is BarDropDownButton { _contentSite: { } site })
            site.Content = newValue;
    }

    private static void OnDropDownPlacementChanged(UIObject sender, PlacementMode oldValue, PlacementMode newValue)
    {
        if (sender is not BarDropDownButton self)
            return;

        if (self._popup is not null)
            self._popup.Placement = newValue;
        self.SetAndRaise(CaretGlyphProperty, ref self._caretGlyph, CaretFor(newValue)); // re-glyphs the bound caret live
    }

    // The direction glyph for a placement — the caret points the way the dropdown flies out. Center/Pointer fall back to
    // the downward caret (there is no meaningful direction to indicate).
    private static string CaretFor(PlacementMode placement) => placement switch
    {
        PlacementMode.Top => "▴",
        PlacementMode.Right => "▸",
        PlacementMode.Left => "◂",
        _ => "▾",
    };

    // A side-placement open requested focus-into (the MenuItem submenu model): the content is not laid out synchronously
    // on open, so the enter is parked and completed here once the popup surface exists. The flag is consumed
    // UNCONDITIONALLY (so an aborted side open can never strand it and hijack a later open) and honored ONLY while the
    // placement is still a side one — a Bottom (ComboBox-model) open must always park focus on the face.
    private void OnPopupOpened(object? sender, EventArgs e)
    {
        var enter = _pendingEnter && DropDownPlacement is PlacementMode.Left or PlacementMode.Right;
        _pendingEnter = false;
        if (enter)
            FocusFirstDropDownItem();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        // Unhook the old parts (mirrors ComboBox) so a re-template can't strand the OnPopupClosed handler on a
        // torn-down Popup. Deliberately do NOT touch the popup's open state here: neither closing it (a WM-surface
        // popup closed mid-detach re-enters the style engine and double-retracts its content's frames) nor flipping
        // the :open pseudo-class (a restyle mid-detach hits the same crash). A re-template-while-open therefore
        // re-opens on the fresh template — the ComboBox-consistent behavior; the transient old-surface orphan rides
        // the same framework-wide Popup-teardown gap ComboBox has (tracked separately as a Popup-layer fix).
        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup.Opened -= OnPopupOpened;
        }
        if (_contentSite is not null)
            _contentSite.RemoveHandler(ButtonBase.ClickEvent, OnDropDownItemClick);
        _pendingEnter = false;
        _popup = null;
        _contentSite = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        SetDropDownOpen(false); // release the popup surface so it doesn't leak on detach
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        // Focus left the opener AND its dropdown (IsKeyboardFocusWithin covers the popup, whose Child is a logical
        // descendant): close the dropdown so it never lingers open with focus elsewhere — the ComboBox precedent.
        // Light dismiss only handles an outside PRESS; a keyboard Tab away to a sibling generates no press, so without
        // this the dropdown would stay open with focus gone. Moving focus INTO the dropdown keeps IsKeyboardFocusWithin
        // true, so an open-then-enter stays open.
        if (!IsKeyboardFocusWithin && _isDropDownOpen)
            SetDropDownOpen(false);
    }

    /// <summary>Opens the dropdown (if closed). Focus stays on the opener face (the retaining focus-scope barrier keeps
    /// an enclosing Toolbar from yanking it); <see cref="EnterDropDown"/> moves focus into the content once it is laid
    /// out (Down when open, matching ComboBox — fresh content is not laid out synchronously on open).</summary>
    protected void OpenDropDown()
    {
        if (!_isDropDownOpen)
            SetDropDownOpen(true);
    }

    /// <summary>Closes the dropdown (the Popup's on-close restore returns focus to the face).</summary>
    protected void CloseDropDown() => SetDropDownOpen(false);

    /// <summary>Opens when closed, closes when open — the whole-control / ▾-zone toggle.</summary>
    protected void ToggleDropDown()
    {
        if (_isDropDownOpen)
            SetDropDownOpen(false);
        else
            SetDropDownOpen(true);
    }

    /// <summary>Moves focus into the open dropdown's first focusable item (the content must already be laid out).</summary>
    protected bool EnterDropDown() => FocusFirstDropDownItem();

    /// <summary>Whether keyboard focus is already inside the dropdown content (vs on the opener face).</summary>
    private bool IsDropDownContentFocused => _contentSite?.IsKeyboardFocusWithin ?? false;

    /// <summary>Whether the FIRST focusable dropdown item currently has keyboard focus (so Up returns to the opener).</summary>
    private bool IsFirstDropDownItemFocused
        => UIApplication.Current?.FocusManager is { } focus && _contentSite?.Child is { } content
        && ReferenceEquals(focus.FocusedElement, FirstFocusable(content));

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var placement = DropDownPlacement;
        var sideways = placement is PlacementMode.Left or PlacementMode.Right;

        // Escape always closes an open dropdown (the Popup W4 restore returns focus to the face).
        if (e.Key == Key.Escape && _isDropDownOpen)
        {
            CloseDropDown();
            e.Handled = true;
            return;
        }

        // The OPEN arrow points the way the dropdown flies out (Down for Bottom, Right/Left for a side placement, Up for
        // Top). For a side placement, opening it also ENTERS the content in one press (the MenuItem submenu model); for
        // Bottom the open parks focus on the face and a second Down enters (the ComboBox model). This also frees Up/Down
        // for the PARENT menu's navigation when the opener is a row in a vertical overflow menu (a side placement) — the
        // open arrow is horizontal there, so Up/Down fall through unhandled and the menu moves between rows.
        if (e.Key == OpenKeyFor(placement))
        {
            if (_isDropDownOpen && !IsDropDownContentFocused)
            {
                EnterDropDown(); // open with focus still on the FACE → move focus into the content's first item
            }
            else if (_isDropDownOpen)
            {
                return; // focus already in the content → let its own nav advance (leave unhandled, DO NOT re-enter)
            }
            else if (sideways)
            {
                _pendingEnter = true; // submenu model: one press opens AND enters (completed on Popup.Opened)
                OpenDropDown();
            }
            else
            {
                OpenDropDown(); // ComboBox model: opens with focus on the face; a subsequent Down enters
            }
            e.Handled = true;
            return;
        }

        // The BACK arrow (the opposite of the open arrow — Up for Bottom, the opposite horizontal for a side placement)
        // returns focus from the FIRST dropdown item to the opener face (menu-like); deeper items let it bubble to the
        // content's own Contained nav (moves to the previous item). Restricted to the SUPPORTED placements: the content
        // navigates vertically (Up/Down), so a Top placement's back arrow (Down) would collide with "advance to the next
        // item" and trap the first item — Top/Center/Pointer therefore get no first-item back-out (Escape still closes).
        if (placement is PlacementMode.Bottom or PlacementMode.Left or PlacementMode.Right
            && e.Key == BackKeyFor(placement) && _isDropDownOpen && IsFirstDropDownItemFocused)
        {
            Focus(FocusNavigationMethod.Directional);
            e.Handled = true;
        }
    }

    // The arrow that opens/enters the dropdown, pointing the way it flies out.
    private static Key OpenKeyFor(PlacementMode placement) => placement switch
    {
        PlacementMode.Right => Key.RightArrow,
        PlacementMode.Left => Key.LeftArrow,
        PlacementMode.Top => Key.UpArrow,
        _ => Key.DownArrow,
    };

    // The arrow that backs out of the dropdown — the opposite of the open arrow.
    private static Key BackKeyFor(PlacementMode placement) => placement switch
    {
        PlacementMode.Right => Key.LeftArrow,
        PlacementMode.Left => Key.RightArrow,
        PlacementMode.Top => Key.DownArrow,
        _ => Key.UpArrow,
    };

    // Walk the REALIZED presenter child, not the raw DropDownContent object: the PART_DropDownContent presenter
    // realizes every content kind (a UIElement, a string, or a DataTemplate over a view-model) into its visual Child,
    // whereas `DropDownContent is UIElement` only sees element content — a DataTemplated dropdown would open but never
    // be enterable by keyboard.
    private bool FocusFirstDropDownItem()
        => _isDropDownOpen && _contentSite?.Child is { } content
        && FirstFocusable(content) is { } first && first.Focus(FocusNavigationMethod.Directional);

    private static UIElement? FirstFocusable(UIElement element)
    {
        if (element is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
            return element;

        for (var i = 0; i < element.VisualChildrenCount; i++)
            if (FirstFocusable(element.GetVisualChild(i)) is { } found)
                return found;

        return null;
    }

    private void SetDropDownOpen(bool value)
    {
        if (!SetAndRaise(IsDropDownOpenProperty, ref _isDropDownOpen, value))
            return;

        PseudoClasses.Set(":open", value);
        _popup?.SetCurrentValue(Popup.IsOpenProperty, value); // light-dismiss/Escape write back via OnPopupClosed
    }

    // Light-dismiss / Escape / a content invoke closed the popup — sync state. Focus return to the face is the
    // Popup's own W4 on-close restore (the opener was the focused element captured when the popup opened).
    private void OnPopupClosed(object? sender, PopupClosedEventArgs e) => SetDropDownOpen(false);
}
