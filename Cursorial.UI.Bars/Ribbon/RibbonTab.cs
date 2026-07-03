using Cursorial.Input;
using Cursorial.Markup;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Bars;

/// <summary>
/// One tab of a <see cref="Ribbon"/> (the guide's <c>.rib-tabs .t</c>). Its <see cref="HeaderedContentControl.Header"/>
/// is the strip label; its children are <see cref="RibbonGroup"/>s that make up the band shown when the tab is
/// selected. It derives from <see cref="TabItem"/>, inheriting selection (<c>:selected</c> two-way), the strip-label
/// rendering, and access-key folding; the band is hosted by the parent <see cref="Ribbon"/>'s content host exactly as
/// a <see cref="TabItem"/>'s content is (single-hosting — the tab's own template renders only the header).
/// <para>Set <see cref="IsFileTab"/> for the special accent File tab: it raises
/// <see cref="Ribbon.BackstageRequestedEvent"/> on click instead of selecting a band.</para>
/// <para>Set <see cref="IsContextual"/> for a conditional, purple-tinted tab (a "Table Tools"-style tab shown only
/// when relevant, guide §5). It is an ordinary selectable band tab — only its skin (purple ink, tinted well, purple
/// underline, <c>│</c> divider + <c>▾</c> caret) differs. Hide it (<see cref="UIElement.Visibility"/> =
/// <see cref="Visibility.Collapsed"/>) when it stops being relevant; if it was the selected tab the owning
/// <see cref="Ribbon"/> redirects selection to the first content tab so the band never blanks.</para>
/// </summary>
[ContentProperty(nameof(Groups))]
public class RibbonTab : TabItem
{
    /// <summary>Whether this is the special File tab (accent-filled; opens Backstage rather than selecting a band).</summary>
    public static readonly StyledProperty<bool> IsFileTabProperty =
        UIProperty.Register<RibbonTab, bool>(nameof(IsFileTab), defaultValue: false, changed: OnIsFileTabChanged);

    /// <summary>Whether this is a contextual tab — a conditional, purple-tinted band tab (guide §5). Stamps
    /// <c>:ribbon-contextual</c> (the skin) and swaps the active underline to the purple pen; hiding it while it is the
    /// selected tab makes the owning <see cref="Ribbon"/> redirect selection so the band never blanks.</summary>
    public static readonly StyledProperty<bool> IsContextualProperty =
        UIProperty.Register<RibbonTab, bool>(nameof(IsContextual), defaultValue: false, changed: OnIsContextualChanged);

    private const string PartUnderline = "PART_Underline";

    private readonly RibbonBand _band = new();
    private Separator? _underline;

    static RibbonTab()
    {
        Control.ThemeProperty.OverrideDefaultValue<RibbonTab>(CursorialBarsTheme.RibbonTabStyle());
    }

    /// <summary>Creates a ribbon tab; its content is the internally-owned band that hosts <see cref="Groups"/>.</summary>
    public RibbonTab() => Content = _band;

    /// <inheritdoc cref="IsFileTabProperty"/>
    public bool IsFileTab { get => GetValue(IsFileTabProperty); set => SetValue(IsFileTabProperty, value); }

    /// <inheritdoc cref="IsContextualProperty"/>
    public bool IsContextual { get => GetValue(IsContextualProperty); set => SetValue(IsContextualProperty, value); }

    /// <summary>The groups shown in this tab's band (the XAML content: <c>&lt;RibbonTab&gt;&lt;RibbonGroup/&gt;…</c>).</summary>
    public UIElementCollection Groups => _band.Children;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); // TabItem grabs PART_Underline, pins TabUnderlinePen + shows/hides it by selection.
        _underline = GetTemplatePart<Separator>(PartUnderline);
        UpdateContextualUnderline();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        // The File tab is a command, not a selectable band: a click FOCUSES it (it is reachable/highlights) and opens
        // Backstage, but does NOT change selection (the shown band stays put; File has no band).
        if (IsFileTab && !e.Handled && e.Button == MouseButton.Left)
        {
            Focus();
            RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, this));
            e.Handled = true;
            return;
        }

        base.OnMouseDown(e); // selects this content tab (SelectedContent → its band)

        // Minimize interactions (content tabs, left button). A real double-click arrives as TWO ButtonDowns
        // (ClickCount 1 then 2); to avoid the count==1 restore and the count>=2 toggle cancelling out, the count==1
        // restore records a flag the count>=2 press consumes:
        //   • count==1, minimized  → restore (expand) + flag, so a following count>=2 (same double-click) stays expanded;
        //   • count==1, expanded   → plain select (clear the flag);
        //   • count>=2             → toggle, UNLESS this gesture's count==1 already restored (then leave expanded).
        // Net: single-click-a-minimized-tab restores; double-click-expanded minimizes; double-click-minimized expands.
        if (!IsFileTab && e.Button == MouseButton.Left && OwnerRibbon is { } ribbon)
        {
            if (e.ClickCount >= 2)
            {
                if (!ribbon.ConsumeJustRestoredByClick())
                    ribbon.IsMinimized = !ribbon.IsMinimized;
            }
            else if (ribbon.IsMinimized)
            {
                ribbon.IsMinimized = false;
                ribbon.SetJustRestoredByClick(true);
            }
            else
            {
                ribbon.SetJustRestoredByClick(false);
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Enter / Space on the FOCUSED File tab activates it (opens Backstage) — the keyboard twin of a click, so a
        // File tab reached by arrow keys is activatable. A content tab's Enter/Space is the base TabItem's (no-op).
        if (IsFileTab && !e.Handled && IsFocused && IsActivationKey(e))
        {
            RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, this));
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnAccessKey(AccessKeyEventArgs e)
    {
        if (IsFileTab)
        {
            if (!e.IsMultiMatch)
                RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, this));
            return;
        }

        base.OnAccessKey(e);
    }

    // Enter, or a modifier-free spacebar. The spacebar's identity is (Key.Character, " ") on every wire (ND10);
    // Key.Space is only NUL→Ctrl+Space, which carries Control — excluded by the modifier-free gate.
    private static bool IsActivationKey(KeyEventArgs e)
        => e.Key == Key.Enter
           || (e.Modifiers == KeyModifiers.None
               && (e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ')));

    /// <inheritdoc/>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        if (ReferenceEquals(args.Property, VisibilityProperty) && IsContextual)
        {
            switch (args.GetNewValue<Visibility>())
            {
                // Hiding the SELECTED contextual tab strands selection on a Collapsed tab (which renders no band and
                // does not self-raise SelectionChanged) — the owner redirects to the first content tab (band never blanks).
                case Visibility.Collapsed when IsSelected:
                    OwnerRibbon?.OnContextualTabHidden(this);
                    break;
                // Re-showing a contextual tab recovers selection if every content tab had been hidden (SelectedIndex < 0).
                case Visibility.Visible:
                    OwnerRibbon?.OnContextualTabShown(this);
                    break;
            }
        }
    }

    // The owning Ribbon is this container's nearest logical-parent SelectingItemsControl (a generated container is a
    // logical child of the Ribbon — punch 43); a walk covers an own-container nested under a wrapper.
    private Ribbon? OwnerRibbon
    {
        get
        {
            for (UIElement? node = LogicalParent; node is not null; node = node.LogicalParent)
                if (node is Ribbon ribbon)
                    return ribbon;
            return null;
        }
    }

    // The active-tab underline is TabUnderlinePen for a normal tab and the purple RibbonContextualUnderlinePen for a
    // contextual one. TabItem pins the pen (SetResourceReference) in OnApplyTemplate, so re-point AFTER base — a Style
    // rule can't win over that pin (the template lane), and this mirrors how TabItem itself owns the underline.
    private void UpdateContextualUnderline()
        => _underline?.SetResourceReference(Control.BorderPenProperty,
            IsContextual ? ThemeKeys.RibbonContextualUnderlinePen : ThemeKeys.TabUnderlinePen);

    private static void OnIsFileTabChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not RibbonTab tab)
            return;
        tab.PseudoClasses.Set(":ribbon-file", newValue);
        // The File tab is a command, not a selectable band: reachable/focusable, but never the selected tab (a click
        // or arrow focuses it without selecting; its band would be empty). Enter/click opens Backstage instead.
        tab.IsSelectable = !newValue;
    }

    private static void OnIsContextualChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is not RibbonTab tab)
            return;
        tab.PseudoClasses.Set(":ribbon-contextual", newValue); // the skin (fill + purple ink + divider/caret)
        tab.UpdateContextualUnderline();                       // the purple ↔ accent underline pen swap
    }
}
