using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// Surface A (bars guide §4): a single horizontal row of bar controls (<see cref="BarButton"/>,
/// <see cref="BarToggleButton"/>, <see cref="BarSeparator"/>, …) with <b>discrete overflow</b> — when the row is too
/// narrow, the trailing items fold into a chevron (<c>»</c>) popup. The fold is the Actipro live-control re-parent
/// model: the overflowed items are the <b>same</b> control instances, moved by the <see cref="ToolbarOverflowPanel"/>
/// (the toolbar's items panel) from the row band into the popup's overflow band, and back when the bar widens. The
/// panel owns the distribution (an <see cref="IItemsHostPanel"/>, so the <see cref="ItemsPresenter"/> steps back);
/// the toolbar mediates the chevron + popup chrome.
/// <para>
/// Per-item folding policy is the attached <see cref="OverflowModeProperty"/> (<see cref="ToolbarOverflowMode"/>).
/// </para>
/// </summary>
[TemplatePart(PartOverflowToggle, typeof(Button))]
[TemplatePart(PartOverflowPopup, typeof(Popup))]
[TemplatePart(PartOverflowHost, typeof(Panel))]
public class Toolbar : ItemsControl
{
    /// <summary>Whether the overflow popup is open (the chevron toggles it; the popup writes back on light-dismiss/Escape).</summary>
    public static readonly StyledProperty<bool> IsOverflowOpenProperty =
        UIProperty.Register<Toolbar, bool>(nameof(IsOverflowOpen), changed: OnIsOverflowOpenChanged);

    /// <summary>Whether any item is currently overflowed (read-only; the panel sets it). Drives chevron visibility.</summary>
    public static readonly DirectProperty<Toolbar, bool> HasOverflowProperty =
        UIProperty.RegisterDirect<Toolbar, bool>(nameof(HasOverflow), static c => c._hasOverflow);

    /// <summary>The number of (non-separator) items in the overflow popup (read-only; the panel sets it). Drives the
    /// chevron's "{N} more" tooltip.</summary>
    public static readonly DirectProperty<Toolbar, int> OverflowCountProperty =
        UIProperty.RegisterDirect<Toolbar, int>(nameof(OverflowCount), static c => c._overflowCount);

    /// <summary>The per-item overflow policy (attached; default <see cref="ToolbarOverflowMode.AsNeeded"/>).</summary>
    public static readonly AttachedProperty<ToolbarOverflowMode> OverflowModeProperty =
        UIProperty.RegisterAttached<Toolbar, UIElement, ToolbarOverflowMode>(
            "OverflowMode", defaultValue: ToolbarOverflowMode.AsNeeded, changed: OnOverflowModeChanged);

    private const string PartOverflowToggle = "PART_OverflowToggle";
    private const string PartOverflowPopup = "PART_OverflowPopup";
    private const string PartOverflowHost = "PART_OverflowHost";

    private bool _hasOverflow;
    private int _overflowCount;

    private ToolbarOverflowPanel? _panel;
    private Button? _chevron;
    private Popup? _popup;
    private Panel? _overflowHost;

    // The popup surface may not be built the instant IsOverflowOpen flips (a topology-locked open defers); when an
    // open-and-enter can't focus the first item yet, this parks the focus-into for Popup.Opened to complete.
    private bool _pendingEnterOverflow;

    internal Button? OverflowToggleForTests => _chevron;
    internal Panel? OverflowHostForTests => _overflowHost;

    static Toolbar()
    {
        Control.ThemeProperty.OverrideDefaultValue<Toolbar>(CursorialBarsTheme.ToolbarStyle());

        // The toolbar is a single Tab stop with arrow navigation WITHIN (the discoverable entry — Tab lands on the
        // bar, arrows move between its controls and wrap; no obscure focus-the-toolbar shortcut). It does NOT retain
        // focus (RetainsFocus = false): tabbing in remembers where focus came from, a pointer/access-key invoke
        // returns there immediately, and Escape returns there from keyboard navigation (see OnKeyDown).
        KeyboardNavigation.TabNavigationProperty.OverrideDefaultValue<Toolbar>(KeyboardNavigationMode.Once);
        KeyboardNavigation.DirectionalNavigationProperty.OverrideDefaultValue<Toolbar>(DirectionalNavigationMode.Cycle);
        FocusManager.RetainsFocusProperty.OverrideDefaultValue<Toolbar>(false);
        // Its own focus scope, so entering the bar leaves the OUTER scope's logical focus (the editor) untouched —
        // that preserved element is what a return restores to (see FocusManager.RetainsFocus).
        FocusManager.IsFocusScopeProperty.OverrideDefaultValue<Toolbar>(true);
    }

    /// <inheritdoc cref="IsOverflowOpenProperty"/>
    public bool IsOverflowOpen { get => GetValue(IsOverflowOpenProperty); set => SetValue(IsOverflowOpenProperty, value); }

    /// <inheritdoc cref="HasOverflowProperty"/>
    public bool HasOverflow => _hasOverflow;

    /// <inheritdoc cref="OverflowCountProperty"/>
    public int OverflowCount => _overflowCount;

    /// <summary>Reads the per-item <see cref="OverflowModeProperty"/>.</summary>
    public static ToolbarOverflowMode GetOverflowMode(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(OverflowModeProperty);
    }

    /// <summary>Sets the per-item <see cref="OverflowModeProperty"/>.</summary>
    public static void SetOverflowMode(UIElement element, ToolbarOverflowMode value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(OverflowModeProperty, value);
    }

    /// <summary>The panel reports the fold result here (read-only state + chevron visibility + auto-close on un-overflow).</summary>
    internal void SetOverflowState(bool hasOverflow, int overflowCount)
    {
        SetAndRaise(HasOverflowProperty, ref _hasOverflow, hasOverflow);
        SetAndRaise(OverflowCountProperty, ref _overflowCount, overflowCount);

        // The chevron is ALWAYS measured (so the panel can reserve its width), so its idle state is Hidden (still
        // measured), never Collapsed (which would zero its measure and break the reserve). Visible↔Hidden is a
        // render-side flip — no measure invalidation, so this never re-enters the fold.
        if (_chevron is not null)
        {
            _chevron.Visibility = hasOverflow ? Visibility.Visible : Visibility.Hidden;
            _chevron.IsTabStop = hasOverflow;
        }

        // When nothing overflows any more, force the popup shut (a stale-open popup over an empty band). This only
        // fires on a bounded fold pass — the panel skips the fold on a measure-to-content (unbounded) pass, so a
        // sizing pass can't transiently report "no overflow" and tear the open popup down.
        if (!hasOverflow && IsOverflowOpen)
            SetCurrentValue(IsOverflowOpenProperty, false);
    }

    /// <summary>The panel registers itself when it becomes the items host (it may connect before or after the
    /// template's other parts are found — whichever is second triggers the wiring).</summary>
    internal void RegisterOverflowPanel(ToolbarOverflowPanel panel)
    {
        _panel = panel;
        WireOverflow();
    }

    /// <summary>The panel deregisters on detach / ItemsPanel swap.</summary>
    internal void UnregisterOverflowPanel(ToolbarOverflowPanel panel)
    {
        if (ReferenceEquals(_panel, panel))
            _panel = null;
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_chevron is not null)
            _chevron.Click -= OnChevronClick;
        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup.Opened -= OnPopupOpened;
        }

        _chevron = GetTemplatePart<Button>(PartOverflowToggle);
        _popup = GetTemplatePart<Popup>(PartOverflowPopup);
        _overflowHost = GetTemplatePart<Panel>(PartOverflowHost);

        WireOverflow();
    }

    // Idempotent — runs the wiring once both the panel (via RegisterOverflowPanel) and the template parts
    // (via OnApplyTemplate) are available; safe to re-run on a re-template.
    private void WireOverflow()
    {
        if (_panel is not null)
        {
            _panel.OverflowHost = _overflowHost;
            _panel.OverflowToggle = _chevron;
        }

        if (_chevron is not null)
        {
            _chevron.Click -= OnChevronClick;
            _chevron.Click += OnChevronClick;
            _chevron.Visibility = _hasOverflow ? Visibility.Visible : Visibility.Hidden;
            _chevron.IsTabStop = _hasOverflow;
        }

        if (_popup is not null)
        {
            _popup.PlacementTarget = _chevron ?? (UIElement) this;
            _popup.Placement = PlacementMode.Bottom;
            _popup.KeepOpenOnAnchorPress = true; // a chevron press closes via its Click, not dismiss-then-reopen
            _popup.Closed -= OnPopupClosed;
            _popup.Closed += OnPopupClosed;
            _popup.Opened -= OnPopupOpened;
            _popup.Opened += OnPopupOpened;
            _popup.SetCurrentValue(Popup.IsOpenProperty, IsOverflowOpen);
        }
    }

    private void OnChevronClick(object? sender, ClickEventArgs e)
    {
        if (IsOverflowOpen)
            SetCurrentValue(IsOverflowOpenProperty, false); // a second activation toggles it shut
        else
            OpenAndEnterOverflow();
    }

    // Open the popup (if closed) and move keyboard focus into it — the chevron is a drop-opener, so activating it
    // (Space/Enter via Click, or Down via OnKeyDown) enters the band rather than parking focus on the chevron. The
    // focus move also keeps a pointer-click from tripping the toolbar's auto-return (focus changed ⇒ no yank).
    private void OpenAndEnterOverflow()
    {
        if (!IsOverflowOpen)
            SetCurrentValue(IsOverflowOpenProperty, true);

        if (!FocusFirstOverflowItem())
            _pendingEnterOverflow = true; // surface not built yet (deferred/locked open) — complete on Popup.Opened
    }

    private bool FocusFirstOverflowItem()
        => FirstOverflowFocusable() is { } first && first.Focus(FocusNavigationMethod.Directional);

    private UIElement? FirstOverflowFocusable()
    {
        if (_overflowHost is not { } host)
            return null;

        for (var i = 0; i < host.VisualChildrenCount; i++)
            if (host.GetVisualChild(i) is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true } item)
                return item;

        return null;
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (!_pendingEnterOverflow)
            return;

        _pendingEnterOverflow = false;
        FocusFirstOverflowItem();
    }

    // The overflow popup stays open while keyboard focus is on the chevron OR inside the band; it closes once focus
    // has left BOTH. Subscribed to FocusManager.FocusedElementChanged only while open (OnIsOverflowOpenChanged).
    private void OnFocusChanged(object? sender, FocusChangedEventArgs e)
    {
        if (IsOverflowOpen
            && _chevron?.IsKeyboardFocusWithin != true
            && _overflowHost?.IsKeyboardFocusWithin != true)
            SetCurrentValue(IsOverflowOpenProperty, false);
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        if (IsOverflowOpen)
            SetCurrentValue(IsOverflowOpenProperty, false);
    }

    private static void OnIsOverflowOpenChanged(UIObject sender, bool oldValue, bool newValue)
    {
        var toolbar = (Toolbar) sender;
        toolbar._popup?.SetCurrentValue(Popup.IsOpenProperty, newValue);

        // The focus-leaves-both closer watches focus only while the popup is open (subscribe on open, drop on close).
        if (UIApplication.Current?.FocusManager is { } focus)
        {
            focus.FocusedElementChanged -= toolbar.OnFocusChanged;
            if (newValue)
                focus.FocusedElementChanged += toolbar.OnFocusChanged;
        }

        if (!newValue)
            toolbar._pendingEnterOverflow = false;
    }

    private static void OnOverflowModeChanged(UIObject sender, ToolbarOverflowMode oldValue, ToolbarOverflowMode newValue)
    {
        // Re-fold the row. The item's VisualParent is the WRONG thing to invalidate: an overflowed item lives in the
        // popup band, so its VisualParent is PART_OverflowHost on the popup surface — invalidating it never reaches the
        // ToolbarOverflowPanel (the fold owner). In BOTH bands the container stays a LOGICAL child of the Toolbar, so
        // walk the logical chain to the owning Toolbar and invalidate its panel's fold directly.
        for (UIElement? node = sender as UIElement; node is not null; node = node.LogicalParent)
            if (node is Toolbar toolbar)
            {
                toolbar._panel?.InvalidateFold();
                return;
            }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || UIApplication.Current?.FocusManager is not {} focus)
            return;

        // Down on the focused chevron opens the overflow popup AND enters it. (Space/Enter enter via the chevron's
        // Click → OnChevronClick; Down is not a button activation, so the toolbar drives it.) An overflow item's
        // KeyDown reaches us because the overflowed controls stay LOGICAL children of the toolbar — the popup's
        // surface route crosses back through the logical parent.
        if (e.Key == Key.DownArrow && _chevron is not null && ReferenceEquals(focus.FocusedElement, _chevron))
        {
            OpenAndEnterOverflow();
            e.Handled = true;
            return;
        }

        // Up from the FIRST overflowed item steps back to the chevron (the popup stays open — the focus-leaves-both
        // closer only fires once focus leaves the chevron too). Intra-band Up/Down is the band's own Cycle nav.
        if (e.Key == Key.UpArrow && _chevron is not null && _overflowHost is { IsKeyboardFocusWithin: true }
            && ReferenceEquals(focus.FocusedElement, FirstOverflowFocusable()))
        {
            _chevron.Focus(FocusNavigationMethod.Directional);
            e.Handled = true;
            return;
        }

        // Escape returns focus to where it came from (the RetainsFocus return). Resolve the return through the
        // TOOLBAR, not the focused element: the focused element may be the chevron, itself a retaining focus scope
        // (a FindReturningScope barrier so opening the popup doesn't yank focus) — walking from it would stop there
        // and never return. The toolbar IS the non-retaining scope, so this reaches the outer scope's logical focus
        // from any bar element, the chevron included. Only when UNHANDLED (an open dropdown consumes Escape first).
        if (e.Key == Key.Escape && focus.RestoreRetainedFocus(this))
            e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        // Close the overflow popup BEFORE the old template root detaches — otherwise the old PART_OverflowPopup's
        // TopLevelSurface + pooled scenes leak (a re-template never re-wires/closes the old popup). Mirrors the
        // OnDetachedFromTree leak guard. WireOverflow re-acquires the new template's popup afterwards.
        if (_popup is not null)
        {
            _popup.SetCurrentValue(Popup.IsOpenProperty, false);
            _popup.Closed -= OnPopupClosed;
            _popup.Opened -= OnPopupOpened;
        }
        if (_chevron is not null)
            _chevron.Click -= OnChevronClick;

        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // Close the overflow popup so its TopLevelSurface + pooled scenes are released (the ContextMenu W7 leak guard;
        // the popup's anchor is our own template part, so we close it directly).
        if (_popup is not null)
            _popup.SetCurrentValue(Popup.IsOpenProperty, false);

        base.OnDetachedFromTree(in e);
    }
}
