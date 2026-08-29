using System.Collections.ObjectModel;

using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// The Ribbon command surface (the bars guide's Surface B): a single-selection host of <see cref="RibbonTab"/> tabs —
/// a tab strip over the selected tab's band of <see cref="RibbonGroup"/>s. It IS a <see cref="TabControl"/>, so the
/// strip selection, keyboard navigation (Left/Right/Home/End, Ctrl+PgUp/PgDn), single-tab-stop focus, and the
/// selected-tab content host are all inherited verbatim; the Ribbon only substitutes the container type and its
/// theme. Every hosted control is the SAME <see cref="BarButton"/>/<see cref="BarToggleButton"/>/… a
/// <see cref="Toolbar"/> hosts, bound to the SAME <see cref="BarCommand"/>s — "one control set, three surfaces".
/// </summary>
public class Ribbon : TabControl
{
    /// <summary>The size tier a bar control renders at inside a ribbon group (inherited, so a group can set a default
    /// its controls pick up; a per-control set wins). Stamps <c>:size-large</c>/<c>:size-small</c> (Medium = none) and
    /// re-measures the control so its face re-lays-out.</summary>
    public static readonly AttachedProperty<RibbonButtonSize> ButtonSizeProperty =
        UIProperty.RegisterAttached<Ribbon, UIElement, RibbonButtonSize>(
            "ButtonSize", defaultValue: RibbonButtonSize.Medium, inherits: true, targetsChildren: true);

    /// <summary>The inherited COMPACT-density signal a group fans to its hosted controls under the band's fold. A group
    /// at <see cref="RibbonGroupDensity.Compact"/> writes <c>true</c>; it inherits to every child bar control and stamps
    /// <c>:density-compact</c> — a per-control pseudo-class ORTHOGONAL to <c>:size-large</c> (it never writes
    /// <see cref="ButtonSizeProperty"/>, so an authored <c>Large</c> face restores byte-identically when the band
    /// widens). Framework-set only (the band drives it); not for app authors.</summary>
    public static readonly AttachedProperty<bool> IsDensityCompactProperty =
        UIProperty.RegisterAttached<Ribbon, UIElement, bool>("IsDensityCompact", defaultValue: false, inherits: true, targetsChildren: true);

    /// <summary>The DEEPEST density tier a group may be demoted to under the band's fold (a per-group author cap — the
    /// ribbon analog of <c>ToolbarOverflowMode.Never</c>). Default <see cref="RibbonGroupDensity.Collapsed"/> (fully
    /// collapsible); set <see cref="RibbonGroupDensity.Compact"/> to forbid the dropdown, or
    /// <see cref="RibbonGroupDensity.Normal"/> to pin a signature group at full size (never demotes). NOT inherited.</summary>
    public static readonly AttachedProperty<RibbonGroupDensity> MinDensityProperty =
        UIProperty.RegisterAttached<Ribbon, RibbonGroup, RibbonGroupDensity>(
            "MinDensity", defaultValue: RibbonGroupDensity.Collapsed, targetsChildren: true);


    /// <inheritdoc cref="BandContentRowsProperty"/>
    protected internal static readonly UIPropertyKey<int> BandContentRowsPropertyKey =
        UIProperty.RegisterAttachedReadOnly<Ribbon, UIElement, int>("BandContentRows", defaultValue: 1, inherits: true, targetsChildren: true);

    /// <summary>The band's AUTHORED content height in rows (1 or 2), stamped by <see cref="RibbonBand"/> from authored
    /// facts only — a control authoring a <see cref="RibbonButtonSize.Large"/> face, a <see cref="RibbonControlGroup"/>
    /// pinning <see cref="RibbonControlGroupRowBehavior.TwoRow"/> — never from density-demoted faces, so the width
    /// fold re-inks faces without re-flowing rows. Inherited so an <c>Auto</c> control group reads it wherever it
    /// sits (inline or relocated into the collapsed flyout). Framework-set only.</summary>
    public static readonly StyledProperty<int> BandContentRowsProperty = BandContentRowsPropertyKey.Property;

    /// <summary>The ribbon's overall layout density — the USER-directED axis (bind to a command or an options key):
    /// <see cref="RibbonLayoutMode.Classic"/> full presentation, <see cref="RibbonLayoutMode.Simplified"/> one labeled
    /// row (no group footers), <see cref="RibbonLayoutMode.Compact"/> one icon-only row. Authored faces are never
    /// touched — flipping back to Classic restores them exactly. The width-driven density fold stays active WITHIN
    /// whatever mode is selected.</summary>
    public static readonly StyledProperty<RibbonLayoutMode> LayoutModeProperty =
        UIProperty.Register<Ribbon, RibbonLayoutMode>(
            nameof(LayoutMode), defaultValue: RibbonLayoutMode.Classic, changed: OnLayoutModeChanged);

    /// <summary>The inherited SIMPLIFIED-layout signal (<see cref="LayoutModeProperty"/> != Classic): stamps
    /// <c>:layout-simplified</c> ribbon-wide — Large faces demote to their <c>[icon][label]</c> row, group footers
    /// collapse, and <see cref="RibbonBand"/> pins its authored height to 1 (so control groups lay flat). The exact
    /// <c>IsDensityCompact</c> mechanism, one axis over. Framework-set only.</summary>
    internal static readonly AttachedProperty<bool> IsLayoutSimplifiedProperty =
        UIProperty.RegisterAttached<Ribbon, UIElement, bool>("IsLayoutSimplified", defaultValue: false, inherits: true, targetsChildren: true);

    /// <summary>Raised (bubbling) when the special File tab is invoked — the app opens its Backstage/File view. In P2
    /// the ribbon leaves the caption row and Backstage to the app; this is the hook.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> BackstageRequestedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(BackstageRequested), RoutingStrategy.Bubble, typeof(Ribbon));

    /// <summary>Where the Quick Access Toolbar sits — the caption row above the strip (default) or a band below the
    /// ribbon body. Stamps <c>:qat-below</c> so the template flips which QAT host is shown.</summary>
    public static readonly StyledProperty<RibbonQuickAccessPlacement> QuickAccessPlacementProperty =
        UIProperty.Register<Ribbon, RibbonQuickAccessPlacement>(
            nameof(QuickAccessPlacement), RibbonQuickAccessPlacement.AboveRibbon, changed: OnQuickAccessPlacementChanged);

    /// <summary>Raised (bubbling) when the QAT customize dropdown's "More Commands…" row is invoked — the app opens its
    /// full options dialog (the ribbon supplies none itself). Mirrors <see cref="BackstageRequestedEvent"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> QuickAccessMoreCommandsRequestedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(QuickAccessMoreCommandsRequested), RoutingStrategy.Bubble, typeof(Ribbon));

    /// <summary>Whether the ribbon is minimized to a tabs-only strip (the body band hidden, reclaiming rows). Stamps
    /// <c>:minimized</c> so the template collapses the body; the pin (⌃/⌄) and a double-click on a tab toggle it, and a
    /// click on any content tab while minimized restores it (and shows that tab's band). The Office "collapse the
    /// ribbon" behavior.</summary>
    public static readonly StyledProperty<bool> IsMinimizedProperty =
        UIProperty.Register<Ribbon, bool>(nameof(IsMinimized), defaultValue: false, changed: OnIsMinimizedChanged);

    private const string PartQuickAccessAbove = "PART_QuickAccessAbove";
    private const string PartQuickAccessBelow = "PART_QuickAccessBelow";
    private const string PartQuickAccessCollapsed = "PART_QuickAccessCollapsed";
    private const string PartQatCollapsed = "PART_QatCollapsed";
    private const string PartQatCustomize = "PART_QatCustomize";
    private const string PartQatPopup = "PART_QatPopup";
    private const string PartQatChecklistHost = "PART_QatChecklistHost";
    private const string PartStrip = "PART_Strip";
    private const string PartPinButton = "PART_PinButton";
    private const string PartContentHost = "PART_ContentHost";

    private ButtonBase? _pinButton;
    private UIElement? _bodyContentHost; // the selected tab's band host — the ↕ Down target / Up origin

    private readonly ObservableCollection<BarCommand> _quickAccessCommands = [];
    private readonly ObservableCollection<BarCommand> _quickAccessCandidates = [];
    private readonly QuickAccessGenerator _qatGenerator;

    private Toolbar? _qatAbove;
    private Toolbar? _qatBelow;
    private Toolbar? _qatCollapsedBar;      // the ⋯▾ popup's QAT mini-bar (the collapse-first degraded tier's command host)
    private ButtonBase? _qatCollapsedButton; // the ⋯▾ opener itself (PART_QatCollapsed)
    private ButtonBase? _qatCustomize;
    private Popup? _qatPopup;
    private Panel? _qatChecklistHost;
    private RibbonStripPanel? _strip; // reports the collapse-first verdict via CollapseChanged
    private CheckBox? _qatBelowToggle; // the checklist's "Show Below the Ribbon" row (tracked for check refresh)

    private bool _qatCollapsed; // effective collapse (Above placement + has-qat + the strip's tight-width verdict)
    private bool _redirectingSelection;

    // Test hooks (Cursorial.UI.Bars.Tests has InternalsVisibleTo): the active QAT toolbar host, the customize opener,
    // its checklist popup + host, and the collapse-first state.
    internal Toolbar? ActiveQuickAccessToolbarForTests => ActiveQuickAccessHost;
    internal ButtonBase? QatCustomizeForTests => _qatCustomize;
    internal Popup? QatPopupForTests => _qatPopup;
    internal Panel? QatChecklistForTests => _qatChecklistHost;
    internal ButtonBase? PinButtonForTests => _pinButton;
    internal ButtonBase? QatCollapsedButtonForTests => _qatCollapsedButton;
    internal Toolbar? QatCollapsedBarForTests => _qatCollapsedBar;
    internal bool IsQuickAccessCollapsedForTests => _qatCollapsed;
    internal Toolbar? QatAboveForTests => _qatAbove;

    static Ribbon()
    {
        // The WHOLE ribbon (tab strip + the selected tab's content) is ONE returning focus scope, exactly like a
        // Toolbar: entering it (Tab onto the strip, or into a group control) captures the pre-entry element, and Escape
        // from anywhere in the ribbon returns focus there (see OnKeyDown). A pointer/access-key invoke of a ribbon
        // command auto-returns to the document (the bar "click Bold, keep typing" model). This is the sole returning
        // scope for the surface — a RibbonGroup is deliberately NOT its own scope, so Escape returns to before the
        // ribbon was entered (not merely to the strip).
        FocusManager.IsFocusScopeProperty.OverrideDefaultValue<Ribbon>(true);
        FocusManager.RetainsFocusProperty.OverrideDefaultValue<Ribbon>(false);

        // The band's authored-height stamp reaches Auto control-group panels as an INHERITED value change; their row
        // count reads it in measure, so the change must re-measure them. Registered HERE (the property's declaring
        // type, before any instance can touch it) — the global effects lane freezes on first resolution, so a
        // consumer-type static ctor is too late when another test/app path touched the property first.
        // Attached ⇒ this ALSO writes the property's GLOBAL effects lane (UIObject.AddEffects, ledger A1), so the
        // stamp re-measures EVERY inheriting host — the control-group panels named here AND BarSeparator's
        // band-height measure ride the same registration.
        AffectsMeasure<RibbonControlGroupPanel>(BandContentRowsProperty);

        // The size context can't be TemplateBound into a bar control's template (TemplateBinding resolves a CLR
        // property name and an attached property reports SourceMissing), so the inherited value CHANGE stamps a
        // pseudo-class the size-aware BarItemTemplate keys off (Medium ⇒ no class). The stamp re-matches the
        // control's style, which flips the size-aware face's Visibility (AffectsMeasure ⇒ the face re-lays-out).
        // Inherited, so a change on a group fans out to every hosted control; a local per-control set stamps the same.
        PseudoClassMapping.Register<UIElement, RibbonButtonSize>(
            ButtonSizeProperty, ClassifySize, ":size-large", ":size-small");

        // :density-compact fans the band's Compact demotion out to every hosted control (inherited signal → per-control
        // pseudo-class). Orthogonal to :size-large — the BarItemTemplate demotes the large face under it by document
        // order WITHOUT touching ButtonSize, so an authored Large face restores exactly when the band widens (false ⇒ no
        // class ⇒ no change-only seeding needed, the Medium precedent).
        PseudoClassMapping.Register<UIElement, bool>(
            IsDensityCompactProperty, static c => c ? ":density-compact" : null);
        // (:has-icon — consumed by the Compact cascade — is registered in BarButton's static ctor so it is live before
        //  any Icon is set, since the mapping is change-only and buttons are often built before a Ribbon exists.)

        // :layout-simplified fans the user's LayoutMode out to every hosted control AND the group/band chrome
        // (inherited signal → pseudo-class): the BarItemTemplate demotes Large faces to the [icon][label] row by
        // document order (after the :size-large rules, before the :density-compact ones, all equal specificity),
        // the group template hides its name footer, and the band re-derives its authored height (the AffectsMeasure
        // below — the band READS the inherited value rather than walking to the Ribbon, so the change re-measures it
        // even when no face visibly flips, e.g. a stacks-only tab flattening).
        PseudoClassMapping.Register<UIElement, bool>(
            IsLayoutSimplifiedProperty, static v => v ? ":layout-simplified" : null);
        AffectsMeasure<RibbonBand>(IsLayoutSimplifiedProperty);
        AffectsMeasure<RibbonControlGroupPanel>(IsLayoutSimplifiedProperty); // TwoRow pins flatten under the mode directly

        // :qat-below flips which QAT host the template shows (AboveRibbon ⇒ no class); the generator re-points to the
        // now-visible toolbar in OnQuickAccessPlacementChanged.
        PseudoClassMapping.Register<Ribbon, RibbonQuickAccessPlacement>(
            QuickAccessPlacementProperty,
            static p => p == RibbonQuickAccessPlacement.BelowRibbon ? ":qat-below" : null);

        // :minimized collapses PART_Body (the default false ⇒ no class ⇒ body shown, so a non-minimized ribbon is
        // unaffected — no change-only seeding needed).
        PseudoClassMapping.Register<Ribbon, bool>(
            IsMinimizedProperty, static m => m ? ":minimized" : null);
    }

    /// <inheritdoc cref="LayoutModeProperty"/>
    public RibbonLayoutMode LayoutMode { get => GetValue(LayoutModeProperty); set => SetValue(LayoutModeProperty, value); }

    private static void OnLayoutModeChanged(UIObject sender, RibbonLayoutMode oldValue, RibbonLayoutMode newValue)
    {
        if (sender is not Ribbon ribbon)
            return;

        ribbon.SetValue(IsLayoutSimplifiedProperty, newValue != RibbonLayoutMode.Classic);

        // The Compact pin: icon-only ribbon-wide, stamped at the ROOT so it inherits everywhere. The fold's per-group
        // un-compact CLEARS its local value (never writes false), so this pin is never shadowed by a Normal group.
        if (newValue == RibbonLayoutMode.Compact)
            ribbon.SetValue(IsDensityCompactProperty, true);
        else
            ribbon.ClearValue(IsDensityCompactProperty);
    }

    private static string? ClassifySize(RibbonButtonSize size) => size switch
    {
        RibbonButtonSize.Large => ":size-large",
        RibbonButtonSize.Small => ":size-small",
        _ => null,
    };

    /// <summary>Creates a ribbon.</summary>
    public Ribbon()
    {
        SelectionChanged += OnRibbonSelectionChanged;
        _qatGenerator = new QuickAccessGenerator(_quickAccessCommands);

        // :has-qat gates the caption row's visibility — a ribbon that never populates the QAT renders exactly as before
        // (no caption row). Stamped from EITHER collection being non-empty (a candidate-only ribbon still shows the
        // customize ▾). The generator owns the commands→toolbar sync; this only drives the caption's presence.
        _quickAccessCommands.CollectionChanged += (_, _) => { UpdateHasQat(); RefreshChecklistChecks(); };
        _quickAccessCandidates.CollectionChanged += (_, _) => { UpdateHasQat(); RebuildChecklist(); };
    }

    /// <inheritdoc cref="BackstageRequestedEvent"/>
    public event EventHandler<RoutedEventArgs>? BackstageRequested
    {
        add => AddHandler(BackstageRequestedEvent, value!);
        remove => RemoveHandler(BackstageRequestedEvent, value!);
    }

    /// <inheritdoc cref="QuickAccessMoreCommandsRequestedEvent"/>
    public event EventHandler<RoutedEventArgs>? QuickAccessMoreCommandsRequested
    {
        add => AddHandler(QuickAccessMoreCommandsRequestedEvent, value!);
        remove => RemoveHandler(QuickAccessMoreCommandsRequestedEvent, value!);
    }

    /// <summary>The ordered set of commands ON the Quick Access Toolbar — the SAME <see cref="BarCommand"/>s the ribbon
    /// groups bind (define-once). Add/remove reflects live into the QAT (via the generator).</summary>
    public ObservableCollection<BarCommand> QuickAccessCommands => _quickAccessCommands;

    /// <summary>The full candidate list the customize checklist enumerates (a superset of <see cref="QuickAccessCommands"/>
    /// — a candidate that is off the QAT still appears, unchecked). Populate it to drive the "customize" dropdown.</summary>
    public ObservableCollection<BarCommand> QuickAccessCandidates => _quickAccessCandidates;

    /// <inheritdoc cref="QuickAccessPlacementProperty"/>
    public RibbonQuickAccessPlacement QuickAccessPlacement
    {
        get => GetValue(QuickAccessPlacementProperty);
        set => SetValue(QuickAccessPlacementProperty, value);
    }

    /// <inheritdoc cref="IsMinimizedProperty"/>
    public bool IsMinimized
    {
        get => GetValue(IsMinimizedProperty);
        set => SetValue(IsMinimizedProperty, value);
    }

    /// <summary>Reads the ribbon size tier attached to <paramref name="element"/>.</summary>
    public static RibbonButtonSize GetButtonSize(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ButtonSizeProperty);
    }

    /// <summary>Sets the ribbon size tier on <paramref name="element"/> (inherits to its descendants).</summary>
    public static void SetButtonSize(UIElement element, RibbonButtonSize value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ButtonSizeProperty, value);
    }

    /// <summary>Reads the inherited compact-density signal on <paramref name="element"/> (<see cref="IsDensityCompactProperty"/>).</summary>
    public static bool GetIsDensityCompact(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsDensityCompactProperty);
    }

    /// <summary>Sets the inherited compact-density signal on <paramref name="element"/> (framework-set by the band's fold).</summary>
    public static void SetIsDensityCompact(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsDensityCompactProperty, value);
    }

    /// <summary>Reads the per-group density floor (<see cref="MinDensityProperty"/>) — the deepest tier the band may
    /// demote the group to.</summary>
    public static RibbonGroupDensity GetMinDensity(RibbonGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.GetValue(MinDensityProperty);
    }

    /// <summary>Sets the per-group density floor: <see cref="RibbonGroupDensity.Normal"/> pins it full-size,
    /// <see cref="RibbonGroupDensity.Compact"/> forbids the dropdown, <see cref="RibbonGroupDensity.Collapsed"/>
    /// (default) allows full collapse.</summary>
    public static void SetMinDensity(RibbonGroup group, RibbonGroupDensity value)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.SetValue(MinDensityProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); // TabControl handles strip Left/Right/Home/End/Ctrl+PgUp/PgDn; RibbonTab handles File activate

        // Ctrl+F1 toggles minimize from anywhere in the ribbon (the Office keyboard accelerator). The pin chevron is a
        // mouse affordance and deliberately not a tab stop, so this is the keyboard route to collapse/expand the band;
        // when minimized, activating a content tab FLOATS the band (see below). An app wanting it global binds it above.
        if (!e.Handled && e.Key == Key.F1 && (e.Modifiers & KeyModifiers.Control) != 0)
        {
            IsMinimized = !IsMinimized;
            e.Handled = true;
            return;
        }

        // A FLOATED (temporarily-revealed) band: Escape first re-collapses the float and returns to the strip; a
        // second Escape (no longer floating) exits the ribbon via the RetainsFocus return below.
        if (!e.Handled && e.Key == Key.Escape && _floating)
        {
            CollapseFloatingBand(refocusSelectedTab: true);
            e.Handled = true;
            return;
        }

        // Escape returns focus to where it came from before entering the ribbon (the RetainsFocus return) — resolved
        // through the RIBBON (the returning scope), so it reaches the outer focus from the tab strip AND from any group
        // control. Only when UNHANDLED: an open dropdown / a deeper handler consumes Escape first.
        if (!e.Handled && e.Key == Key.Escape && UIApplication.Current?.FocusManager is { } escapeFocus && escapeFocus.RestoreRetainedFocus(this))
        {
            e.Handled = true;
            return;
        }

        // ↕ crossing between the tab strip and the ribbon body (Office parity). Down off a focused tab HEADER drops
        // into the body (its remembered / first control); Up from the body's TOP row — no directional target above it
        // within the Contained band — climbs back to the selected tab header (rather than moving within the body).
        // Unmodified arrows only, and only when nothing deeper already consumed the key (a group's own directional nav,
        // an open dropdown). Left/Right/other arrows fall to the base TabControl / the band's directional nav.
        if (!e.Handled && e.Modifiers == KeyModifiers.None && UIApplication.Current?.FocusManager is { } focus)
        {
            if (e.Key == Key.DownArrow && FocusedTabContainer(e.OriginalSource) is not null)
                e.Handled = EnterBody(focus);
            else if (e.Key == Key.UpArrow && AtBodyTop(focus, e.OriginalSource))
                e.Handled = FocusSelectedTab();
        }
    }

    // The tab header the key came from (a realized container of THIS ribbon), or null when focus is in the body. The
    // File tab counts too: while it is merely FOCUSED (not activated — activation opens Backstage on Enter/Space/click)
    // the previously-selected content band is still shown, so Down should drop into that VISIBLE band like any other
    // tab (EnterBody targets the shown _bodyContentHost, not File's own — File has no band).
    private RibbonTab? FocusedTabContainer(UIElement? source)
    {
        for (var node = source; node is not null; node = node.VisualParent)
        {
            if (node is RibbonTab tab && ItemContainerGenerator.IndexFromContainer(tab) >= 0)
                return tab;
        }

        return null;
    }

    // True when focus sits inside the ribbon body with no directional target above it within the Contained band —
    // the top row, from which Up climbs back to the tab strip instead of moving within the body. A COLLAPSED group's
    // opener always qualifies: its group's controls live in a (closed) flyout, so nothing sits above it in its own
    // column — but the opener is a single bottom-aligned button, so directional Up would otherwise score a DIAGONAL
    // neighbor (the first control of the taller uncollapsed group beside it) instead of climbing to the tab strip.
    private bool AtBodyTop(FocusManager focus, UIElement? source)
        => source is not null
           && _bodyContentHost is { } host && host.IsAncestorOf(source)
           && (IsCollapsedGroupOpener(source) || focus.FindNext(source, FocusNavigationDirection.Up) is null);

    // Whether focus is on a collapsed group's [name ▾] opener (a collapsed group has no other focusable in the body —
    // its controls are in the closed flyout — so walking up to a Collapsed RibbonGroup identifies the opener).
    private static bool IsCollapsedGroupOpener(UIElement? source)
    {
        for (var node = source; node is not null; node = node.VisualParent)
        {
            if (node is RibbonGroup { Density: RibbonGroupDensity.Collapsed })
                return true;
        }

        return false;
    }

    // Whether the minimized band is temporarily revealed (floated). Not a persistent property — it re-collapses on
    // dismiss (Esc / focus leaving the ribbon), leaving IsMinimized untouched.
    private bool _floating;

    // Bumped every time the band (re)floats. A ScheduleEnterFloatedBody retry chain captures the generation it was
    // armed under; a dismiss-then-re-float within the same interaction bumps this, so the stale chain's queued
    // callbacks orphan themselves (they see a newer generation) and exactly one chain is ever live per float.
    private int _floatGeneration;

    // Drop focus into the ribbon body (the selected tab's band). When MINIMIZED, first FLOAT the band (a transient
    // reveal — the Office "click/arrow a tab to peek the band" model) and enter it once it lays out; otherwise enter
    // now. The float auto-dismisses (see the Esc branch + the focus-leaves-ribbon override below).
    private bool EnterBody(FocusManager focus)
    {
        if (IsMinimized)
        {
            if (!FloatBand())
                return false;
            ScheduleEnterFloatedBody(4); // the revealed body realizes over the next layout pass — enter once it does
            return true;
        }

        return EnterBodyNow(focus);
    }

    private bool EnterBodyNow(FocusManager focus)
        => _bodyContentHost is { IsEffectivelyVisible: true } host
           && focus.ResolveFocusEntry(host) is { } target
           && target.Focus(FocusNavigationMethod.Directional);

    // Floats the minimized band: reveal it via :floating (which overrides the :minimized body collapse — a 2-pseudo
    // rule beating the 1-pseudo collapse) WITHOUT clearing IsMinimized. Returns false when not minimized / already floating.
    internal bool FloatBand()
    {
        if (!IsMinimized || _floating)
            return false;
        _floating = true;
        _floatGeneration++; // arm a new retry generation; any prior chain's queued callbacks now orphan themselves
        PseudoClasses.Set(":floating", true);
        return true;
    }

    // Re-collapses a floated band (dismiss); optionally returns focus to the strip. A no-op when not floating.
    private void CollapseFloatingBand(bool refocusSelectedTab)
    {
        if (!_floating)
            return;
        _floating = false;
        PseudoClasses.Set(":floating", false);
        if (refocusSelectedTab)
            FocusSelectedTab();
    }

    // Retry entering the just-floated body across dispatcher turns until its content host realizes (revealing a
    // collapsed subtree needs a layout pass before its controls are focusable). Bounded so a never-focusable body stops;
    // aborts if the float was dismissed first.
    private void ScheduleEnterFloatedBody(int attemptsLeft)
    {
        if (UIApplication.Current is not { } app)
            return;

        var generation = _floatGeneration; // this chain belongs to the float armed now
        app.Dispatcher.Post(() =>
        {
            if (_floatGeneration != generation) // a dismiss+re-float replaced us — a fresh chain owns entry
                return;
            if (!_floating || EnterBodyNow(app.FocusManager))
                return;
            if (attemptsLeft > 0)
                ScheduleEnterFloatedBody(attemptsLeft - 1);
        });
    }

    // Climb back to the tab strip: focus the selected tab header (the tab whose band the body shows), or the first
    // content tab if selection is somehow clear.
    private bool FocusSelectedTab()
    {
        var index = SelectedIndex >= 0 ? SelectedIndex : FirstContentTabIndex();
        return index >= 0
               && ItemContainerGenerator.ContainerFromIndex(index) is { } tab
               && tab.Focus(FocusNavigationMethod.Directional);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _bodyContentHost = GetTemplatePart<UIElement>(PartContentHost);
        _qatAbove = GetTemplatePart<Toolbar>(PartQuickAccessAbove);
        _qatBelow = GetTemplatePart<Toolbar>(PartQuickAccessBelow);
        _qatCollapsedBar = GetTemplatePart<Toolbar>(PartQuickAccessCollapsed);
        _qatCollapsedButton = GetTemplatePart<ButtonBase>(PartQatCollapsed);

        if (_strip is not null)
            _strip.CollapseChanged -= OnStripCollapseChanged;
        _strip = GetTemplatePart<RibbonStripPanel>(PartStrip);
        if (_strip is not null)
            _strip.CollapseChanged += OnStripCollapseChanged;

        if (_qatCustomize is not null)
            _qatCustomize.Click -= OnQatCustomizeClick;
        _qatCustomize = GetTemplatePart<ButtonBase>(PartQatCustomize);
        if (_qatCustomize is not null)
            _qatCustomize.Click += OnQatCustomizeClick;

        if (_qatPopup is not null)
            _qatPopup.Opened -= OnQatPopupOpened;
        _qatPopup = GetTemplatePart<Popup>(PartQatPopup);
        if (_qatPopup is not null)
            _qatPopup.Opened += OnQatPopupOpened;

        _qatChecklistHost = GetTemplatePart<Panel>(PartQatChecklistHost);
        RebuildChecklist(); // build the checklist rows NOW (before any open) so the popup surface sizes to real content

        if (_pinButton is not null)
            _pinButton.Click -= OnPinClick;
        _pinButton = GetTemplatePart<ButtonBase>(PartPinButton);
        if (_pinButton is not null)
            _pinButton.Click += OnPinClick;
        UpdatePinState(); // glyph + :pinned from the current IsMinimized

        UpdateQatCollapseState(); // reconcile :qat-collapsed against the fresh strip (defaults uncollapsed pre-layout)
        UpdateQatHost();          // point the generator at the active QAT toolbar (catch-up + re-template safe)
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_qatCustomize is not null)
            _qatCustomize.Click -= OnQatCustomizeClick;
        if (_qatPopup is not null)
        {
            _qatPopup.Opened -= OnQatPopupOpened;
            _qatPopup.SetCurrentValue(Popup.IsOpenProperty, false); // release the WM surface + pooled scenes (Toolbar precedent)
        }
        if (_pinButton is not null)
            _pinButton.Click -= OnPinClick;
        if (_strip is not null)
            _strip.CollapseChanged -= OnStripCollapseChanged;
        _qatGenerator.SetHost(null); // release the generated controls from the torn-down toolbar
        _qatAbove = null;
        _qatBelow = null;
        _qatCollapsedBar = null;
        _qatCollapsedButton = null;
        _strip = null;
        _qatCustomize = null;
        _qatPopup = null;
        _qatChecklistHost = null;
        _qatBelowToggle = null;
        _pinButton = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // A plain tree-detach (navigating away from the view) doesn't fire OnTemplateDetaching — close the QAT
        // customize popup so its WM surface + pooled scenes don't leak (the Toolbar/ContextMenu leak-guard precedent).
        _qatPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        base.OnDetachedFromTree(in e);
    }

    // The QAT toolbar the generated command controls currently live in: the below-band under :qat-below, the ⋯▾
    // popup's mini-bar when collapsed (the tight-width degraded tier), else the inline above cluster. One live control
    // set, re-hosted between them (the generator's move-not-rebuild — placement/collapse preserve per-control state).
    private Toolbar? ActiveQuickAccessHost
        => QuickAccessPlacement == RibbonQuickAccessPlacement.BelowRibbon ? _qatBelow
           : _qatCollapsed ? _qatCollapsedBar
           : _qatAbove;

    // Points the generator at the active host (the :qat-below / :qat-collapsed template rules flip which is visible;
    // the generator fills the visible one).
    private void UpdateQatHost() => _qatGenerator.SetHost(ActiveQuickAccessHost);

    private bool HasQat => _quickAccessCommands.Count > 0 || _quickAccessCandidates.Count > 0;

    // Reconcile the effective collapse from the strip's tight-width verdict. Collapse applies ONLY to the inline
    // (Above) placement and only when there are COMMANDS to tuck into the ⋯▾ popup — gated on the commands count, NOT
    // HasQat: a CANDIDATES-only ribbon (the ▾ present but nothing pinned) would otherwise collapse into an EMPTY ⋯▾
    // popup (the generator hosts commands only). Below has its own full-width row; a QAT-less ribbon never shows the ⋯▾.
    // Stamps :qat-collapsed (theme swaps the inline cluster for ⋯▾) and re-hosts the commands.
    private void UpdateQatCollapseState()
    {
        var effective = QuickAccessPlacement == RibbonQuickAccessPlacement.AboveRibbon
                        && _quickAccessCommands.Count > 0
                        && _strip is { IsCollapsed: true };
        if (_qatCollapsed == effective)
            return;
        _qatCollapsed = effective;
        PseudoClasses.Set(":qat-collapsed", effective);
        UpdateQatHost(); // move the commands into / out of the ⋯▾ popup mini-bar
    }

    private void OnStripCollapseChanged(object? sender, EventArgs e) => UpdateQatCollapseState();

    private void OnQatCustomizeClick(object? sender, ClickEventArgs e)
    {
        if (_qatPopup is not null)
            _qatPopup.IsOpen = !_qatPopup.IsOpen; // the customize ▾ toggles its own checklist popup
        e.Handled = true;
    }

    private void UpdateHasQat()
    {
        PseudoClasses.Set(":has-qat", HasQat);
        UpdateQatCollapseState(); // a QAT gained/lost changes whether the ⋯▾ collapse is applicable
    }

    // On open, only REFRESH the checked states — the rows are already built (eagerly, in OnApplyTemplate / on a
    // candidates change), so the popup surface was sized to real content at placement. Building rows here on Opened
    // would add them AFTER the surface is placed/sized from an empty host → the checklist renders as a clipped sliver
    // on first open. (A raw Popup stays open across inside toggles — light-dismiss closes only on an outside press —
    // so a stay-open checklist fits, unlike a BarDropDownButton whose item-click closes the dropdown.)
    private void OnQatPopupOpened(object? sender, EventArgs e)
    {
        RefreshChecklistChecks();
        // Move keyboard focus INTO the checklist so a KEYBOARD-opened customize menu is navigable at once (a Popup,
        // unlike an activated Window, gets no auto-focus — the Backstage-menu parity). The popup lays out synchronously
        // on open, so the rows are realized now. Gated on the open being keyboard-driven: a MOUSE open must NOT yank
        // focus off the pointer's target into the checklist (the click leaves focus where the user put it).
        if (UIApplication.Current?.InputDispatcher.LastModality == InputModality.Keyboard)
            FocusFirstChecklistRow();
    }

    // The first focusable checklist row (the leading candidate CheckBox), so an opened customize menu is keyboard-
    // navigable without a pointer (Up/Down move between rows — the host is a Contained directional container). With no
    // candidate rows the only focusables are the trailing "More Commands…" (a DISMISS action) and "Show Below" toggle —
    // auto-focusing there would make the first Enter dismiss the menu, so skip the auto-focus entirely (the menu stays
    // open; the user can still arrow/Tab to those actions deliberately).
    private bool FocusFirstChecklistRow()
    {
        if (_qatChecklistHost is null || _quickAccessCandidates.Count == 0)
            return false;

        foreach (var child in _qatChecklistHost.Children)
        {
            if (child is { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true } row
                && row.Focus(FocusNavigationMethod.Restore))
                return true;
        }

        return false;
    }

    // Builds the checklist rows (candidate CheckBoxes + separator + "More Commands…" + "Show Below the Ribbon"). Called
    // EAGERLY so the content exists before the popup surface is sized; a candidates change rebuilds it.
    private void RebuildChecklist()
    {
        if (_qatChecklistHost is null)
            return;

        _qatChecklistHost.Children.Clear();
        _qatBelowToggle = null;
        foreach (var candidate in _quickAccessCandidates)
        {
            var command = candidate; // capture per row
            var row = new CheckBox { Content = command.Text ?? string.Empty, IsChecked = _quickAccessCommands.Contains(command) };
            row.Click += (_, _) => ToggleMembership(command, row.IsChecked == true);
            _qatChecklistHost.Children.Add(row);
        }

        // A HORIZONTAL rule in the vertical checklist (BarSeparator defaults to Vertical for a horizontal bar; only the
        // ToolbarOverflowPanel flips it by band, and this checklist is a plain vertical StackPanel).
        _qatChecklistHost.Children.Add(new BarSeparator { Orientation = Orientation.Horizontal });

        var more = new BarButton { Content = "More Commands…" };
        more.Click += (_, _) =>
        {
            _qatPopup?.Close();
            RaiseEvent(new RoutedEventArgs(QuickAccessMoreCommandsRequestedEvent, this));
        };
        _qatChecklistHost.Children.Add(more);

        _qatBelowToggle = new CheckBox
        {
            Content = "Show Below the Ribbon",
            IsChecked = QuickAccessPlacement == RibbonQuickAccessPlacement.BelowRibbon,
        };
        _qatBelowToggle.Click += (_, _) => QuickAccessPlacement =
            _qatBelowToggle.IsChecked == true ? RibbonQuickAccessPlacement.BelowRibbon : RibbonQuickAccessPlacement.AboveRibbon;
        _qatChecklistHost.Children.Add(_qatBelowToggle);
    }

    // Refreshes the built rows' checked states from current membership + placement (on open, and on a membership change
    // while open — keeping the checklist in sync without a rebuild). Candidate rows are the leading children, one per
    // candidate in order. Programmatic IsChecked writes do NOT raise Click, so this never re-enters ToggleMembership.
    private void RefreshChecklistChecks()
    {
        if (_qatChecklistHost is null)
            return;

        for (var i = 0; i < _quickAccessCandidates.Count && i < _qatChecklistHost.Children.Count; i++)
            if (_qatChecklistHost.Children[i] is CheckBox row)
                row.IsChecked = _quickAccessCommands.Contains(_quickAccessCandidates[i]);

        if (_qatBelowToggle is not null)
            _qatBelowToggle.IsChecked = QuickAccessPlacement == RibbonQuickAccessPlacement.BelowRibbon;
    }

    // Add/remove a candidate from the QAT (the generator reflects it live). Gated on the actual membership state so a
    // programmatic IsChecked sync (which does not raise Click) never double-inserts.
    private void ToggleMembership(BarCommand command, bool wanted)
    {
        var has = _quickAccessCommands.Contains(command);
        if (wanted && !has)
            _quickAccessCommands.Add(command);
        else if (!wanted && has)
            _quickAccessCommands.Remove(command);
    }

    private static void OnQuickAccessPlacementChanged(UIObject sender, RibbonQuickAccessPlacement oldValue, RibbonQuickAccessPlacement newValue)
    {
        if (sender is Ribbon ribbon)
        {
            ribbon.UpdateQatCollapseState(); // collapse is Above-only — switching to Below clears any stale :qat-collapsed
            ribbon.UpdateQatHost();          // move the generated controls to the now-visible host (even if collapse was unchanged)
        }
    }

    private bool _justRestoredByClick; // set when a click-to-restore fired, so the SAME double-click's 2nd press doesn't re-minimize

    // A real double-click arrives as TWO ButtonDowns (ClickCount 1 then 2). If the ClickCount==1 press restored a
    // minimized ribbon, the ClickCount>=2 press of the same gesture must NOT toggle it back — RibbonTab consumes this
    // flag to decide. (A single synthetic ClickCount==2 event, as in a headless SendClick(clickCount:2), never set the
    // flag, so it still toggles.)
    internal bool ConsumeJustRestoredByClick()
    {
        var value = _justRestoredByClick;
        _justRestoredByClick = false;
        return value;
    }

    internal void SetJustRestoredByClick(bool value) => _justRestoredByClick = value;

    // The pin/chevron toggles the minimized state (⌃ collapse ↔ ⌄ expand). Focusable=false, so it never steals Tab.
    private void OnPinClick(object? sender, ClickEventArgs e)
    {
        IsMinimized = !IsMinimized;
        e.Handled = true;
    }

    private void UpdatePinState()
    {
        // The chevron glyph indicates state: ⌃ = "collapse" (expanded), ⌄ = "expand" (minimized). (A :pinned pseudo-
        // class would need self-stamping on the pin — PseudoClasses is protected — so the glyph carries the cue.)
        if (_pinButton is not null)
            _pinButton.Content = IsMinimized ? "⌄" : "⌃";
    }

    private static void OnIsMinimizedChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is Ribbon ribbon)
        {
            ribbon.CollapseFloatingBand(refocusSelectedTab: false); // a persistent minimize/expand ends any transient float
            ribbon.UpdatePinState(); // the :minimized pseudo-class (body collapse) is driven by the PseudoClassMapping
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        // A floated (temporarily-revealed) band auto-collapses when keyboard focus LEAVES the ribbon — Tab away, or a
        // command that returns focus to the document (the ribbon is a non-retaining scope). The Office peek-then-dismiss.
        if (_floating && ReferenceEquals(args.Property, IsKeyboardFocusWithinProperty) && !args.GetNewValue<bool>())
            CollapseFloatingBand(refocusSelectedTab: false);
    }

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new RibbonTab();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is RibbonTab;

    // The File tab is a command, not a selectable band. TabControl.EnsureSelection auto-selects index 0 (which may be
    // File) and Left/Right can land on it — redirect any selection that lands on a File tab (or a hidden contextual
    // tab) to the nearest content tab so its empty/blank band is never shown. Silent (no Backstage): a File CLICK
    // opens Backstage via RibbonTab.
    private void OnRibbonSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_redirectingSelection || SelectedIndex < 0)
            return;
        if (ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not RibbonTab selected || IsContentTab(selected))
            return;

        RedirectToFirstContentTab();
    }

    // A contextual tab that hides while it is the selected tab strands selection on a Collapsed tab (no band, no
    // self-raised SelectionChanged) — redirect to the first content tab so the band never blanks. Called by RibbonTab.
    internal void OnContextualTabHidden(RibbonTab tab)
    {
        if (_redirectingSelection || !ReferenceEquals(SelectedItemContainer(), tab))
            return;

        RedirectToFirstContentTab();
    }

    // A contextual tab re-shown while the ribbon has NO valid selection (every content tab was hidden, so a prior
    // redirect settled at -1) recovers selection onto the first content tab — otherwise a re-shown sole content tab
    // would stay blank (no structural change re-runs EnsureSelection). Called by RibbonTab on Visibility→Visible.
    internal void OnContextualTabShown(RibbonTab tab)
    {
        if (_redirectingSelection || SelectedIndex >= 0)
            return;

        RedirectToFirstContentTab();
    }

    private void RedirectToFirstContentTab()
    {
        _redirectingSelection = true;
        try
        {
            // -1 (nothing selected) if the ribbon has no visible content tab right now — a transient state at startup
            // (containers not yet realized) that EnsureSelection recovers, and the recoverable all-hidden state that
            // OnContextualTabShown recovers when a content tab re-appears.
            SelectedIndex = FirstContentTabIndex();
        }
        finally
        {
            _redirectingSelection = false;
        }
    }

    // A content tab is an ordinary selectable band tab: not the File command tab, and (if contextual) currently shown.
    private static bool IsContentTab(RibbonTab tab) => !tab.IsFileTab && tab.Visibility != Visibility.Collapsed;

    private RibbonTab? SelectedItemContainer()
        => SelectedIndex >= 0 ? ItemContainerGenerator.ContainerFromIndex(SelectedIndex) as RibbonTab : null;

    private int FirstContentTabIndex()
    {
        var count = ItemContainerGenerator.ContainerCount;
        for (var i = 0; i < count; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is RibbonTab tab && IsContentTab(tab))
                return i;
        return -1;
    }
}
