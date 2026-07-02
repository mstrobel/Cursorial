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
            "ButtonSize", defaultValue: RibbonButtonSize.Medium, inherits: true);

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
    private const string PartQatCustomize = "PART_QatCustomize";
    private const string PartQatPopup = "PART_QatPopup";
    private const string PartQatChecklistHost = "PART_QatChecklistHost";
    private const string PartPinButton = "PART_PinButton";

    private ButtonBase? _pinButton;

    private readonly ObservableCollection<BarCommand> _quickAccessCommands = [];
    private readonly ObservableCollection<BarCommand> _quickAccessCandidates = [];
    private readonly QuickAccessGenerator _qatGenerator;

    private Toolbar? _qatAbove;
    private Toolbar? _qatBelow;
    private ButtonBase? _qatCustomize;
    private Popup? _qatPopup;
    private Panel? _qatChecklistHost;
    private CheckBox? _qatBelowToggle; // the checklist's "Show Below the Ribbon" row (tracked for check refresh)

    private bool _redirectingSelection;

    // Test hooks (Cursorial.UI.Bars.Tests has InternalsVisibleTo): the active QAT toolbar host, the customize opener,
    // its checklist popup + host.
    internal Toolbar? ActiveQuickAccessToolbarForTests
        => QuickAccessPlacement == RibbonQuickAccessPlacement.BelowRibbon ? _qatBelow : _qatAbove;
    internal ButtonBase? QatCustomizeForTests => _qatCustomize;
    internal Popup? QatPopupForTests => _qatPopup;
    internal Panel? QatChecklistForTests => _qatChecklistHost;
    internal ButtonBase? PinButtonForTests => _pinButton;

    static Ribbon()
    {
        Control.ThemeProperty.OverrideDefaultValue<Ribbon>(CursorialBarsTheme.RibbonStyle());

        // The WHOLE ribbon (tab strip + the selected tab's content) is ONE returning focus scope, exactly like a
        // Toolbar: entering it (Tab onto the strip, or into a group control) captures the pre-entry element, and Escape
        // from anywhere in the ribbon returns focus there (see OnKeyDown). A pointer/access-key invoke of a ribbon
        // command auto-returns to the document (the bar "click Bold, keep typing" model). This is the sole returning
        // scope for the surface — a RibbonGroup is deliberately NOT its own scope, so Escape returns to before the
        // ribbon was entered (not merely to the strip).
        FocusManager.IsFocusScopeProperty.OverrideDefaultValue<Ribbon>(true);
        FocusManager.RetainsFocusProperty.OverrideDefaultValue<Ribbon>(false);

        // The size context can't be TemplateBound into a bar control's template (TemplateBinding resolves a CLR
        // property name and an attached property reports SourceMissing), so the inherited value CHANGE stamps a
        // pseudo-class the size-aware BarItemTemplate keys off (Medium ⇒ no class). The stamp re-matches the
        // control's style, which flips the size-aware face's Visibility (AffectsMeasure ⇒ the face re-lays-out).
        // Inherited, so a change on a group fans out to every hosted control; a local per-control set stamps the same.
        PseudoClassMapping.Register<UIElement, RibbonButtonSize>(
            ButtonSizeProperty, ClassifySize, ":size-large", ":size-small");

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

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); // TabControl handles strip Left/Right/Home/End/Ctrl+PgUp/PgDn; RibbonTab handles File activate

        // Escape returns focus to where it came from before entering the ribbon (the RetainsFocus return) — resolved
        // through the RIBBON (the returning scope), so it reaches the outer focus from the tab strip AND from any group
        // control. Only when UNHANDLED: an open dropdown / a deeper handler consumes Escape first.
        if (!e.Handled && e.Key == Key.Escape && UIApplication.Current?.FocusManager is { } focus && focus.RestoreRetainedFocus(this))
            e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _qatAbove = GetTemplatePart<Toolbar>(PartQuickAccessAbove);
        _qatBelow = GetTemplatePart<Toolbar>(PartQuickAccessBelow);

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

        UpdateQatHost(); // point the generator at the placement-appropriate QAT toolbar (catch-up + re-template safe)
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
        _qatGenerator.SetHost(null); // release the generated controls from the torn-down toolbar
        _qatAbove = null;
        _qatBelow = null;
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

    // Points the generator at whichever QAT toolbar the current placement shows (the :qat-below template rule flips
    // their Visibility; the generator fills the visible one).
    private void UpdateQatHost()
        => _qatGenerator.SetHost(QuickAccessPlacement == RibbonQuickAccessPlacement.BelowRibbon ? _qatBelow : _qatAbove);

    private void OnQatCustomizeClick(object? sender, ClickEventArgs e)
    {
        if (_qatPopup is not null)
            _qatPopup.IsOpen = !_qatPopup.IsOpen; // the customize ▾ toggles its own checklist popup
        e.Handled = true;
    }

    private void UpdateHasQat()
        => PseudoClasses.Set(":has-qat", _quickAccessCommands.Count > 0 || _quickAccessCandidates.Count > 0);

    // On open, only REFRESH the checked states — the rows are already built (eagerly, in OnApplyTemplate / on a
    // candidates change), so the popup surface was sized to real content at placement. Building rows here on Opened
    // would add them AFTER the surface is placed/sized from an empty host → the checklist renders as a clipped sliver
    // on first open. (A raw Popup stays open across inside toggles — light-dismiss closes only on an outside press —
    // so a stay-open checklist fits, unlike a BarDropDownButton whose item-click closes the dropdown.)
    private void OnQatPopupOpened(object? sender, EventArgs e) => RefreshChecklistChecks();

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

        _qatChecklistHost.Children.Add(new BarSeparator());

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
            ribbon.UpdateQatHost(); // move the generated controls to the now-visible host
    }

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
            ribbon.UpdatePinState(); // the :minimized pseudo-class (body collapse) is driven by the PseudoClassMapping
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
