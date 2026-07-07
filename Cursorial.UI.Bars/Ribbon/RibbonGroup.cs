using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// A labeled group inside a <see cref="RibbonTab"/> (the guide's <c>.rg</c>): a horizontal row of bar controls over a
/// muted group name, with an optional <c>⋰</c> dialog-launcher. It derives from <see cref="HeaderedItemsControl"/> —
/// <see cref="HeaderedItemsControl.Header"/> is the bottom group name; the group's items are the SAME bar controls a
/// <see cref="Toolbar"/> hosts, used directly (no wrapper). Set <see cref="Ribbon.ButtonSizeProperty"/> on a control
/// (or on the group as a default) to pick its Large/Medium/Small face.
/// </summary>
[TemplatePart(PartLauncher, typeof(ButtonBase))]
[TemplatePart(PartSeparator, typeof(UIElement))]
[TemplatePart(PartItemsHost, typeof(UIElement))]
[TemplatePart(PartInlineColumn, typeof(Panel))]
[TemplatePart(PartCollapsedButton, typeof(BarPopupButton))]
[TemplatePart(PartCollapsedPopupHost, typeof(RibbonGroupPanel))]
public class RibbonGroup : HeaderedItemsControl
{
    private const string PartLauncher = "PART_Launcher";
    private const string PartSeparator = "PART_GroupSeparator";
    private const string PartItemsHost = "PART_ItemsHost";
    private const string PartInlineColumn = "PART_InlineColumn";
    private const string PartCollapsedButton = "PART_CollapsedButton";
    private const string PartCollapsedPopupHost = "PART_CollapsedPopupHost";

    /// <summary>Raised (bubbling) when the <c>⋰</c> dialog-launcher is invoked — the app opens the group's full dialog.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> DialogLauncherRequestedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(DialogLauncherRequested), RoutingStrategy.Bubble, typeof(RibbonGroup));

    /// <summary>Whether the group shows a <c>⋰</c> dialog-launcher (the guide's optional <c>.launch</c> corner cell).</summary>
    public static readonly StyledProperty<bool> HasDialogLauncherProperty =
        UIProperty.Register<RibbonGroup, bool>(nameof(HasDialogLauncher), defaultValue: false, changed: OnHasDialogLauncherChanged);

    private ButtonBase? _launcher;
    private UIElement? _separator;
    private bool _isLastInBand;

    private RibbonGroupDensity _density;
    private int _naturalWidthNormal;    // frozen last-known Normal width (measured while at Normal)
    private int _estimatedCompactWidth; // analytic Compact width, frozen alongside Normal (see ComputeCompactEstimate)

    private RibbonGroupPanel? _groupPanel;    // the inline items-host panel (adoption owner; set by RegisterGroupPanel)
    private BarPopupButton? _collapsedButton;
    private Panel? _collapsedPopupHost;       // PART_CollapsedPopupHost — the flyout band the LIVE controls move into

    internal ButtonBase? DialogLauncherForTests => _launcher;
    internal BarPopupButton? CollapsedButtonForTests => _collapsedButton;
    internal Panel? CollapsedPopupHostForTests => _collapsedPopupHost;

    /// <summary>The group's DIRECT containers in document order (a <see cref="RibbonControlGroup"/> appears as
    /// itself) — the band's authored-height walk reads this un-flattened view. Valid whether the controls sit inline
    /// or in the collapsed flyout (the panel owns the authoritative adopted list).</summary>
    internal IReadOnlyList<UIElement> Containers => _groupPanel?.Containers ?? [];

    /// <summary>The group's hosted bar controls, in document order — the KeyTip L2 targets (keytips-design §6). A
    /// <see cref="RibbonControlGroup"/> container FLATTENS to its children: the grouping is pure layout, so every
    /// command inside a stack keeps its own KeyTip. Valid inline or in the collapsed flyout.</summary>
    internal IReadOnlyList<UIElement> KeyTipContainers
    {
        get
        {
            var containers = Containers;
            List<UIElement>? flat = null;

            for (var i = 0; i < containers.Count; i++)
            {
                if (containers[i] is RibbonControlGroup controlGroup)
                {
                    if (flat is null)
                    {
                        flat = new List<UIElement>(containers.Count + 4);
                        for (var j = 0; j < i; j++)
                            flat.Add(containers[j]);
                    }

                    var generator = controlGroup.ItemContainerGenerator;
                    for (var j = 0; j < generator.ContainerCount; j++)
                        if (generator.ContainerFromIndex(j) is { } child)
                            flat.Add(child);
                }
                else
                {
                    flat?.Add(containers[i]);
                }
            }

            return flat ?? containers;
        }
    }

    /// <summary>The group's ⋰ dialog launcher (a KeyTip L2 Activate target), or null when the group has none.</summary>
    internal ButtonBase? KeyTipDialogLauncher => _launcher;

    /// <summary>The collapsed-group dropdown opener (a KeyTip L2 DrillPopup when <see cref="Density"/> is
    /// <see cref="RibbonGroupDensity.Collapsed"/>), or null.</summary>
    internal BarPopupButton? KeyTipCollapsedButton => _collapsedButton;

    /// <summary>The band-assigned density tier (never author-set — the <see cref="RibbonBand"/> owns the width budget).</summary>
    internal RibbonGroupDensity Density => _density;
    internal RibbonGroupDensity DensityForTests => _density;

    /// <summary>The group's width at a given density tier, for the band's single-pass fold. All three are STABLE
    /// (independent of the deferred <c>:density-compact</c> restyle): Normal is the frozen measured width, Compact is
    /// an analytic estimate frozen alongside it, Collapsed is the analytic [name ▾] dropdown width. Reading stable
    /// widths — never the live post-restyle DesiredSize — is what lets the fold assign all tiers in one pass without
    /// waiting a frame for a compacted face to materialize.</summary>
    internal int TierWidth(RibbonGroupDensity tier) => tier switch
    {
        RibbonGroupDensity.Compact => _estimatedCompactWidth,
        RibbonGroupDensity.Collapsed => EstimateCollapsedWidth(),
        _ => _naturalWidthNormal,
    };

    // The Collapsed dropdown is [group-name ▾]: the header's rendered width plus the caret + button padding. Analytic
    // (never measured — the flyout is a closed popup), independent of item count.
    private int EstimateCollapsedWidth() =>
        GraphemeWidth.StringWidth(Header?.ToString() ?? string.Empty) + 5; // " ▾" caret + face padding, erring high

    // The Compact width = the Normal width minus the label savings from each ICON-bearing button dropping to icon-only
    // (a label-only button keeps its label — no saving). Analytic per-child (icon grapheme width + face padding),
    // computed from the children's NORMAL measurements while the group is at Normal, so it never depends on the
    // deferred restyle. Errs high (never over-promises savings) so the band under-collapses rather than clipping.
    // A RibbonControlGroup container recurses: its saving is packed(normal) − packed(icon-only) through the SAME
    // packer the live layout uses (both rows narrow together — a per-child sum would double-count the stack).
    private int ComputeCompactEstimate()
    {
        if (_groupPanel is null)
            return _naturalWidthNormal;

        var savings = 0;
        foreach (var container in _groupPanel.Containers)
        {
            if (container is RibbonControlGroup controlGroup)
                savings += Math.Max(0, container.DesiredSize.Columns - controlGroup.EstimateCompactWidth());
            else
                savings += Math.Max(0, container.DesiredSize.Columns - CompactWidthOf(container));
        }

        return Math.Max(1, _naturalWidthNormal - savings);
    }

    // A single control's analytic :density-compact width: icon-bearing → the icon-only face (icon width + border
    // padding (1,0)); label-only → unchanged (Compact never blanks a label-only button). Shared with
    // RibbonControlGroup's stack-aware estimate so the two rules can't drift. An Icon CONTROL (the tiered
    // Glyph/Image/Emoji/Text element — the common case) reports its measured width, falling back to the typical
    // 2-cell face when unmeasured; a string icon measures its graphemes. Errs high (never over-promises savings).
    internal static int CompactWidthOf(UIElement container)
    {
        // Only the BarItemTemplate carries the :density-compact label-drop rules — BarButton and BarToggleButton.
        // Split/popup buttons, combos, and galleries keep their full faces under Compact, so claiming icon-only
        // savings for them over-promises and the fold would keep a Compact tier that actually clips (audit F2).
        if (container is not (BarButton or BarToggleButton) ||
            container.GetValue(BarButton.IconProperty) is not { } icon)
        {
            return container.DesiredSize.Columns;
        }

        var iconWidth = icon switch
        {
            UIElement { DesiredSize.Columns: > 0 } measured => measured.DesiredSize.Columns,
            UIElement => 2, // an unmeasured Icon control: the typical 2-cell face
            _ => Math.Max(1, GraphemeWidth.StringWidth(icon.ToString() ?? string.Empty)),
        };

        return iconWidth + 2; // border padding (1,0)
    }

    // Called by RibbonBand's fold to assign the group's density tier. Guarded no-op on a stable value (SetIsLastInBand
    // precedent). Compact fans the inherited :density-compact signal to every hosted control (icon-only faces);
    // Collapsed instead self-stamps :density-collapsed (swap the inline column for the [name ▾] dropdown) and moves the
    // live items presenter into the flyout — where the controls render at AUTHORED size, so :density-compact is FALSE.
    internal void SetDensity(RibbonGroupDensity value)
    {
        if (_density == value)
            return;
        var wasCollapsed = _density == RibbonGroupDensity.Collapsed;
        _density = value;
        // Compact writes the inherited signal locally; un-compact CLEARS it (never a local false, which would
        // shadow a ribbon-root pin — RibbonLayoutMode.Compact stamps true at the root and it must flow through).
        if (value == RibbonGroupDensity.Compact)
            Ribbon.SetIsDensityCompact(this, true);
        else
            ClearValue(Ribbon.IsDensityCompactProperty);
        PseudoClasses.Set(":density-collapsed", value == RibbonGroupDensity.Collapsed);

        var isCollapsed = value == RibbonGroupDensity.Collapsed;
        if (wasCollapsed != isCollapsed)
        {
            // Capture whether focus was inside BEFORE the controls leave the inline surface for the (closed) flyout.
            var hadFocusWithin = isCollapsed && IsKeyboardFocusWithin;
            ReconcileCollapsedHosting();
            // Focus repair: the opener's :density-collapsed Visibility flip is a DEFERRED restyle, so it isn't
            // focusable yet this frame (a Focus() here would silently no-op). Defer + retry until it materializes.
            if (hadFocusWithin)
                ScheduleCollapsedFocusRepair(4);
        }
        InvalidateMeasure();
    }

    // Hand focus to the collapsed opener once its deferred :density-collapsed visibility flip has landed (retried
    // across dispatcher turns — the QAT float ScheduleEnterFloatedBody pattern). Only repairs if the group is still
    // collapsed and nothing else already claimed focus, so a light-dismiss / competing handler wins cleanly.
    private void ScheduleCollapsedFocusRepair(int attemptsLeft)
    {
        if (UIApplication.Current is not { } app)
            return;

        app.Dispatcher.Post(() =>
        {
            if (_density != RibbonGroupDensity.Collapsed)
                return; // widened again before the repair ran — nothing to do

            if (_collapsedButton is { IsEffectivelyVisible: true } opener)
            {
                if (!opener.IsKeyboardFocusWithin)
                    opener.Focus(FocusNavigationMethod.Programmatic);
                return;
            }

            if (attemptsLeft > 0)
                ScheduleCollapsedFocusRepair(attemptsLeft - 1); // opener not visible yet (restyle pending) — retry
        });
    }

    // Called by the inline RibbonGroupPanel (the adoption owner) when it connects/disconnects as the items host. The
    // group holds the reference so it can drive the Collapsed control-move; on connect it catches up to the current
    // tier (a group already Collapsed when its panel connects re-hosts into the flyout at once).
    internal void RegisterGroupPanel(RibbonGroupPanel? panel)
    {
        _groupPanel = panel;
        ReconcileCollapsedHosting();
    }

    // Point the inline panel at the flyout band (Collapsed) or back inline — the LIVE controls move all-or-nothing, no
    // presenter re-parent (moving the presenter unrealizes its containers). No-op until BOTH the panel and the flyout
    // part are known (they connect in either order — panel via RegisterGroupPanel, part via OnApplyTemplate).
    private void ReconcileCollapsedHosting()
    {
        if (_groupPanel is null || _collapsedPopupHost is null)
            return;
        _groupPanel.SetPopupHost(_density == RibbonGroupDensity.Collapsed ? _collapsedPopupHost : null);
    }

    /// <summary>Creates a ribbon group hosting bar controls.</summary>
    public RibbonGroup()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ => new RibbonGroupPanel());
        // A group is TRANSPARENT to keyboard navigation — neither a directional-nav container, a focus scope, nor a
        // single Tab stop. The selected tab's whole content is one seamless nav plane: arrow (directional) nav flows
        // across group boundaries through the owning RibbonBand (the single directional container), and Tab flows
        // control-by-control through the default (Continue) tab order — so there is no "arrows within a group, Tab to
        // cross" mode switch. The owning Ribbon is the single returning focus scope, so Escape returns focus to before
        // the ribbon was entered — from any group control OR the tab strip (see Ribbon.OnKeyDown). A group being Once
        // (a single Tab stop) would also stop the band's directional collection at the group edge — the coupling that
        // made arrows unable to cross groups; leaving it Continue lets directional descend into every control.
    }

    /// <inheritdoc cref="DialogLauncherRequestedEvent"/>
    public event EventHandler<RoutedEventArgs>? DialogLauncherRequested
    {
        add => AddHandler(DialogLauncherRequestedEvent, value!);
        remove => RemoveHandler(DialogLauncherRequestedEvent, value!);
    }

    /// <inheritdoc cref="HasDialogLauncherProperty"/>
    public bool HasDialogLauncher { get => GetValue(HasDialogLauncherProperty); set => SetValue(HasDialogLauncherProperty, value); }

    // The band panel calls this on its last group so the trailing │ separator doesn't dangle at the band edge. It only
    // STORES the flag + invalidates self; the actual separator Visibility write happens in this group's own
    // MeasureOverride, so a Visibility flip's invalidation stays self-contained rather than walking back up into the
    // band mid-measure. Guarded so a stable flag is a no-op (no per-measure churn).
    internal void SetIsLastInBand(bool last)
    {
        if (_isLastInBand == last)
            return;
        _isLastInBand = last;
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        ApplySeparatorVisibility(); // idempotent; the separator is this group's own template part
        var size = base.MeasureOverride(availableSize);
        // Freeze the Normal width AND the analytic Compact estimate from a NORMAL pass (the children are at their
        // authored faces, so their DesiredSize is the trustworthy full width the estimate subtracts label savings from).
        // A demoted group reports a shrunk width, so only the Normal pass is a valid sample.
        if (_density == RibbonGroupDensity.Normal)
        {
            _naturalWidthNormal = size.Columns;
            _estimatedCompactWidth = ComputeCompactEstimate();
        }
        return size;
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_launcher is not null)
            _launcher.Click -= OnLauncherClick;
        _launcher = GetTemplatePart<ButtonBase>(PartLauncher);
        if (_launcher is not null)
            _launcher.Click += OnLauncherClick;

        _separator = GetTemplatePart<UIElement>(PartSeparator);
        ApplySeparatorVisibility();

        _collapsedButton = GetTemplatePart<BarPopupButton>(PartCollapsedButton);
        if (_collapsedButton is not null)
            _collapsedButton.FocusContentOnOpen = true; // Down opens the flyout AND enters the hosted group (one press)
        _collapsedPopupHost = GetTemplatePart<RibbonGroupPanel>(PartCollapsedPopupHost);
        // Reconcile the current tier against the fresh flyout part: a group re-templated while Collapsed re-hosts the
        // live controls into the new flyout band (the Ribbon.OnApplyTemplate reconcile precedent).
        ReconcileCollapsedHosting();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_launcher is not null)
            _launcher.Click -= OnLauncherClick;
        if (_collapsedButton is not null)
            _collapsedButton.IsDropDownOpen = false; // release the flyout surface
        _launcher = null;
        _separator = null;
        _collapsedButton = null;
        _collapsedPopupHost = null;
        base.OnTemplateDetaching(old);
    }

    private void ApplySeparatorVisibility()
    {
        if (_separator is not null)
            _separator.Visibility = _isLastInBand ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLauncherClick(object? sender, ClickEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(DialogLauncherRequestedEvent, this));
        e.Handled = true;
    }

    private static void OnHasDialogLauncherChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is RibbonGroup group)
            group.PseudoClasses.Set(":has-launcher", newValue);
    }
}
