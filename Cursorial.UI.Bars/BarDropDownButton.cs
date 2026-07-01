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

    private bool _isDropDownOpen;
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
            _popup.Placement = PlacementMode.Bottom;
            _popup.KeepOpenOnAnchorPress = true; // the opener owns the toggle (no dismiss-then-reopen race)
            _popup.Closed -= OnPopupClosed;
            _popup.Closed += OnPopupClosed;
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
            _contentSite.AddHandler(ButtonBase.ClickEvent, OnDropDownItemClick, handledEventsToo: true);
        }
    }

    // A drop-down item was invoked (a Click bubbling out of the content) — close the drop-down (the Popup's W4 restore
    // returns focus to the face). Menu-like: the command still runs (the Click already fired), we just dismiss.
    private void OnDropDownItemClick(object? sender, ClickEventArgs e) => CloseDropDown();

    private static void OnDropDownContentChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is BarDropDownButton { _contentSite: { } site })
            site.Content = newValue;
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
            _popup.Closed -= OnPopupClosed;
        if (_contentSite is not null)
            _contentSite.RemoveHandler(ButtonBase.ClickEvent, OnDropDownItemClick);
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

        switch (e.Key)
        {
            case Key.DownArrow when _isDropDownOpen && !IsDropDownContentFocused:
                EnterDropDown(); // open with focus still on the FACE → move focus into the content's first item
                e.Handled = true;
                break;
            // When focus is ALREADY in the dropdown content, Down is directional navigation among the items (handled
            // by the content's own nav scope) — do NOT re-enter at the first item, so Down actually advances.
            case Key.DownArrow when _isDropDownOpen:
                break;
            // Up from the FIRST dropdown item returns focus to the opener face (menu-like) instead of stalling or
            // cycling; deeper items let Up bubble to the content's own Contained nav (moves to the previous item).
            case Key.UpArrow when _isDropDownOpen && IsFirstDropDownItemFocused:
                Focus(FocusNavigationMethod.Directional);
                e.Handled = true;
                break;
            case Key.DownArrow:
                OpenDropDown(); // Down opens; a subsequent Down enters
                e.Handled = true;
                break;
            case Key.Escape when _isDropDownOpen:
                CloseDropDown(); // Escape closes; the Popup W4 restore returns focus to the face
                e.Handled = true;
                break;
        }
    }

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
