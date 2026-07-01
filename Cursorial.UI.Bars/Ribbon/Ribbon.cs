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

    private bool _redirectingSelection;

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
    }

    private static string? ClassifySize(RibbonButtonSize size) => size switch
    {
        RibbonButtonSize.Large => ":size-large",
        RibbonButtonSize.Small => ":size-small",
        _ => null,
    };

    /// <summary>Creates a ribbon.</summary>
    public Ribbon() => SelectionChanged += OnRibbonSelectionChanged;

    /// <inheritdoc cref="BackstageRequestedEvent"/>
    public event EventHandler<RoutedEventArgs>? BackstageRequested
    {
        add => AddHandler(BackstageRequestedEvent, value!);
        remove => RemoveHandler(BackstageRequestedEvent, value!);
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
