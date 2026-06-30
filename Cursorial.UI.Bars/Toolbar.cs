using Cursorial.UI.Controls;

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

    static Toolbar()
    {
        Control.ThemeProperty.OverrideDefaultValue<Toolbar>(CursorialBarsTheme.ToolbarStyle());
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
            _popup.Closed -= OnPopupClosed;

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
            _popup.SetCurrentValue(Popup.IsOpenProperty, IsOverflowOpen);
        }
    }

    private void OnChevronClick(object? sender, ClickEventArgs e)
        => SetCurrentValue(IsOverflowOpenProperty, !IsOverflowOpen);

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        if (IsOverflowOpen)
            SetCurrentValue(IsOverflowOpenProperty, false);
    }

    private static void OnIsOverflowOpenChanged(UIObject sender, bool oldValue, bool newValue)
        => ((Toolbar) sender)._popup?.SetCurrentValue(Popup.IsOpenProperty, newValue);

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
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        // Close the overflow popup BEFORE the old template root detaches — otherwise the old PART_OverflowPopup's
        // TopLevelSurface + pooled scenes leak (a re-template never re-wires/closes the old popup). Mirrors the
        // OnDetachedFromTree leak guard. WireOverflow re-acquires the new template's popup afterwards.
        if (_popup is not null)
        {
            _popup.SetCurrentValue(Popup.IsOpenProperty, false);
            _popup.Closed -= OnPopupClosed;
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
