using Cursorial.Input;
using Cursorial.Rendering.Media;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A hierarchical trail of activatable "chips" with a leading-overflow fold and a chips ↔ raw-text edit hand-off —
/// the Actipro WPF <c>BreadcrumbBar</c> / Windows Explorer address bar shape, generalized: the bar knows nothing
/// about file systems, paths, or separators in strings. It is an <see cref="ItemsControl"/> over <b>any</b>
/// hierarchy (a directory trail, an object graph, a navigation history), so <see cref="ItemsControl.ItemsSource"/>,
/// <see cref="ItemsControl.ItemTemplate"/> and per-item <see cref="ButtonBase.Command"/> bindings all work the way
/// they do on every other items control. Containers are <see cref="BreadcrumbBarItem"/>s.
/// <code>
///   [▣ Home] ▸ [Projects] ▸ [assets]          ← the trail; the deepest chip is :current
///   [ … ] ▸ [Projects] ▸ [assets]             ← too narrow: the ANCESTORS fold behind the ellipsis chip
/// </code>
/// <para>
/// <b>Why an <see cref="ItemsControl"/> and not a <see cref="Control"/> with a bespoke collection.</b> Everything a
/// breadcrumb needs already exists on the items pipeline: a data-driven segment list, the by-type
/// <see cref="DataTemplate"/> chain for rendering a view-model segment, container recycling, and
/// <see cref="ItemsControl.ItemContainerStyle"/>. A <see cref="SelectingItemsControl"/> would be wrong — a trail has
/// no selection; the trailing segment is <c>:current</c> by POSITION, and activating an ancestor is a navigation
/// request the host answers by replacing the item collection, not a selection change.
/// </para>
/// <para>
/// <b>Keyboard (one tab stop, ND16).</b> <see cref="KeyboardNavigation.TabNavigationProperty"/> defaults to
/// <see cref="KeyboardNavigationMode.Once"/> on this type, so Tab lands on the bar once and the next Tab leaves the
/// whole trail. Inside, <c>←</c>/<c>→</c> move the active chip (focus ONLY — moving the highlight never navigates,
/// which is what makes arrowing along a trail safe), <c>Home</c>/<c>End</c> jump to the first/last chip, and
/// <c>Enter</c>/<c>Space</c> activate the active chip through the ordinary <see cref="ButtonBase"/> path (the
/// routed <see cref="ItemActivated"/> plus the chip's <see cref="ButtonBase.Command"/>). <c>F2</c> enters edit mode.
/// </para>
/// <para>
/// <b>Escape is deliberately NOT handled in chip mode.</b> In edit mode <c>Escape</c> reverts to the chips and is
/// claimed; in chip mode the bar leaves it completely alone — no <c>e.Handled</c>, no focus move — so it bubbles to
/// whatever hosts the bar (a dialog closes, a search field clears). A breadcrumb has no "cancel" of its own to
/// spend the gesture on, and swallowing it would strand the user in a dialog they can't dismiss.
/// </para>
/// <para>
/// <b>The drop-downs are pickers, not menus.</b> A chip's <c>▸</c> (and <c>↓</c> on the active chip) drops that
/// segment's children, and the leading <c>…</c> drops the folded-away ancestors — both as a
/// <see cref="CompletionPopup"/> in picker mode rather than a <see cref="ContextMenu"/>. A menu was the wrong
/// container for what those lists actually are: it does not SCROLL, so a directory with hundreds of folders
/// produced something taller than the terminal with no way to reach the bottom of it, and its type-ahead is a
/// prefix jump next to the picker's fuzzy filtering. The entries are sorted (by <c>Display</c>, ordinal
/// ignore-case, behind <see cref="CompletionItem.SortGroup"/> so a host can still group), and typing filters
/// them with match highlighting. The bar still enumerates nothing itself — see
/// <see cref="DropDownOpening"/>.
/// </para>
/// <para>
/// <b>Edit mode is a hand-off, not a parser.</b> The bar owns only the state machine (chips ↔
/// <c>PART_EditBox</c>, text pre-selected on entry, <c>Escape</c> discards, <c>Enter</c> commits, focus-out
/// commits) and raises <see cref="EditingStarted"/> / <see cref="EditCommitted"/> / <see cref="EditCanceled"/> at
/// the boundaries. Path rendering, parsing, validation and completion belong to the host — it seeds the text on
/// <see cref="EditingStarted"/> and validates on <see cref="EditCommitted"/> (setting
/// <see cref="BreadcrumbBarEditEventArgs.KeepEditing"/> to stay in the box on a bad path).
/// </para>
/// </summary>
[TemplatePart(PartChipsHost, typeof(UIElement))]
[TemplatePart(PartOverflowChip, typeof(BreadcrumbBarItem))]
[TemplatePart(PartEditBox, typeof(TextBox))]
[TemplatePart(PartDropDown, typeof(CompletionPopup))]
public class BreadcrumbBar : ItemsControl
{
    private const string PartChipsHost = "PART_ChipsHost";
    private const string PartOverflowChip = "PART_OverflowChip";
    private const string PartEditBox = "PART_EditBox";
    private const string PartDropDown = "PART_DropDown";

    private const string ExpandedChipPseudoClass = ":expanded";

    /// <summary>Whether the bar offers the raw-text edit mode at all (<c>F2</c> and <see cref="BeginEdit"/> are
    /// inert when false — the default; a host opts in when it can render and parse its own path text).</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        UIProperty.Register<BreadcrumbBar, bool>(nameof(IsEditable), defaultValue: false, changed: OnIsEditableChanged);

    /// <summary>
    /// The committed path text — what edit mode is seeded with and what a commit writes back (<c>:editing</c> is
    /// the mode, this is the value). It is plain data the host owns: the bar never parses it and never derives the
    /// chips from it.
    /// </summary>
    public static readonly DirectProperty<BreadcrumbBar, string> TextProperty =
        UIProperty.RegisterDirect<BreadcrumbBar, string>(nameof(Text), static b => b._text, static (b, v) => b.SetText(v), unsetValue: "");

    /// <summary>Whether the bar is currently showing its edit box instead of the chips (<c>:editing</c>; read-only —
    /// drive it with <see cref="BeginEdit"/> / <see cref="CommitEdit()"/> / <see cref="CancelEdit"/>).</summary>
    public static readonly DirectProperty<BreadcrumbBar, bool> IsEditingProperty =
        UIProperty.RegisterDirect<BreadcrumbBar, bool>(nameof(IsEditing), static b => b._isEditing);

    /// <summary>The index of the chip that currently holds focus (the "active" chip), or <c>−1</c> when focus is
    /// outside the trail or on the leading ellipsis chip. Read-only; <c>←</c>/<c>→</c>/<c>Home</c>/<c>End</c> and
    /// pointer focus drive it.</summary>
    public static readonly DirectProperty<BreadcrumbBar, int> ActiveIndexProperty =
        UIProperty.RegisterDirect<BreadcrumbBar, int>(nameof(ActiveIndex), static b => b._activeIndex);

    /// <summary>Whether any leading segment is folded behind the ellipsis chip (read-only; the panel's fold sets it).</summary>
    public static readonly DirectProperty<BreadcrumbBar, bool> HasOverflowProperty =
        UIProperty.RegisterDirect<BreadcrumbBar, bool>(nameof(HasOverflow), static b => b._hasOverflow);

    /// <summary>How many leading segments are folded behind the ellipsis chip (read-only; the panel's fold sets it).</summary>
    public static readonly DirectProperty<BreadcrumbBar, int> OverflowCountProperty =
        UIProperty.RegisterDirect<BreadcrumbBar, int>(nameof(OverflowCount), static b => b._overflowCount);

    /// <summary>The bubbling request to navigate to a segment — a chip click, <c>Enter</c>/<c>Space</c> on the
    /// active chip, or a pick from the ellipsis drop-down. The bar does NOT mutate itself in response; the host
    /// answers by replacing the items (that is what makes the same control work for a file path and an object graph).</summary>
    public static readonly RoutedEvent<BreadcrumbBarItemEventArgs> ItemActivatedEvent =
        RoutedEvent<BreadcrumbBarItemEventArgs>.Register(nameof(ItemActivated), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    /// <summary>Raised as the bar flips to edit mode; a handler seeds <see cref="BreadcrumbBarEditEventArgs.Text"/>
    /// with its own rendering of the path (the bar's own <see cref="Text"/> is the default).</summary>
    public static readonly RoutedEvent<BreadcrumbBarEditEventArgs> EditingStartedEvent =
        RoutedEvent<BreadcrumbBarEditEventArgs>.Register(nameof(EditingStarted), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    /// <summary>Raised when the user commits the edited text (<c>Enter</c>, or focus leaving the bar). A handler
    /// validates/navigates, and may set <see cref="BreadcrumbBarEditEventArgs.KeepEditing"/> to keep the box open.</summary>
    public static readonly RoutedEvent<BreadcrumbBarEditEventArgs> EditCommittedEvent =
        RoutedEvent<BreadcrumbBarEditEventArgs>.Register(nameof(EditCommitted), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    /// <summary>Raised when the user abandons the edit (<c>Escape</c>) — the typed text is DISCARDED and reported
    /// for information only; <see cref="Text"/> is untouched.</summary>
    public static readonly RoutedEvent<BreadcrumbBarEditEventArgs> EditCanceledEvent =
        RoutedEvent<BreadcrumbBarEditEventArgs>.Register(nameof(EditCanceled), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    /// <summary>Raised when a chip's <c>▸</c> separator is pressed (or <c>↓</c> struck on it): fill
    /// <see cref="BreadcrumbBarDropDownEventArgs.Children"/> to offer the Explorer-style sibling list (leave it
    /// empty and the press is inert). Add plain objects and each row is its <c>ToString()</c>; add
    /// <see cref="CompletionItem"/>s to carry an icon, a kind label and a sort group. Raised ONCE per opening —
    /// the filtering that follows is local, so a slow enumeration is never repeated per keystroke.</summary>
    public static readonly RoutedEvent<BreadcrumbBarDropDownEventArgs> DropDownOpeningEvent =
        RoutedEvent<BreadcrumbBarDropDownEventArgs>.Register(nameof(DropDownOpening), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    /// <summary>Raised when the user picks an entry out of a separator's sibling drop-down
    /// (<see cref="BreadcrumbBarDropDownEventArgs.SelectedChild"/> is the pick, under
    /// <see cref="BreadcrumbBarDropDownEventArgs.Item"/>).</summary>
    public static readonly RoutedEvent<BreadcrumbBarDropDownEventArgs> ChildActivatedEvent =
        RoutedEvent<BreadcrumbBarDropDownEventArgs>.Register(nameof(ChildActivated), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    private readonly List<UIElement> _ring = []; // reused arrow-navigation ring (no per-keystroke allocation)

    // The open drop-down's contents. Two lists rather than one because they are read by two different
    // parties: _dropDownItems is what the picker's provider hands back verbatim on every keystroke (so it
    // has to be a materialized IReadOnlyList<CompletionItem>, not a projection that re-allocates per
    // filter), and _dropDownEntries is what maps a pick back onto the thing to DO about it.
    private readonly List<DropDownEntry> _dropDownEntries = [];
    private readonly List<CompletionItem> _dropDownItems = [];
    private readonly ICompletionProvider _dropDownProvider;

    private string _text = "";
    private bool _isEditing;
    private int _activeIndex = -1;
    private bool _hasOverflow;
    private int _overflowCount;
    private int _firstVisibleIndex;   // the panel's fold result: elided = [0, _firstVisibleIndex)
    private bool _switchingMode;      // mid chips↔edit hand-off: a transient focus gap is not "the user left"

    private UIElement? _chipsHost;
    private BreadcrumbBarItem? _overflowChip;
    private TextBox? _editBox;
    private CompletionPopup? _dropDown;
    private bool _dropDownIsOverflow; // the open drop-down is the ellipsis list (vs. a separator's sibling list)

    // Where focus should land once a drop-down pick has been applied. Captured at pick time, consumed on Closed.
    private object? _pendingFocusChild;
    private int _pendingFocusAnchor = -1;
    private bool _hasPendingFocus;

    static BreadcrumbBar()
    {
        // ND16: the whole trail is ONE tab stop with arrow navigation inside — the Toolbar shape. Tab in, arrow
        // along, Tab out; there is no per-chip tab stop to wade through (a deep path would otherwise cost a dozen
        // Tabs to cross). Directional navigation stays None: the bar drives ←/→ itself in OnKeyDown so the ring can
        // skip the ELIDED chips, which are still focusable elements the generic navigator would happily land on.
        KeyboardNavigation.TabNavigationProperty.OverrideDefaultValue<BreadcrumbBar>(KeyboardNavigationMode.Once);
    }

    /// <summary>Creates a breadcrumb bar.</summary>
    public BreadcrumbBar()
    {
        // The bar is never a focus TARGET (the chips are) — but it must stay a tab STOP: the Once collector adds the
        // container itself as the single stop and only then resolves a target inside it, so IsTabStop = false here
        // would drop the whole trail out of the tab order (FocusNavigator.CollectInto).
        Focusable = false;

        // The horizontal, left-overflowing items panel is control identity, not chrome — a vertical StackPanel would
        // not be a breadcrumb — so it is set here rather than in the theme (Menu does the same for its horizontal
        // row). A consumer can still replace it; the fold simply stops.
        ItemsPanel = new ItemsPanelTemplate(
            static _ =>
            {
                var panel = new BreadcrumbBarPanel { Occludes = true };
                panel.SetBinding(Panel.BackgroundProperty,
                                 TemplateBinding.From<BreadcrumbBar, IBrush?>(BackgroundProperty));
                return panel;
            });

        // :current follows POSITION, so it is re-stamped whenever the container set changes (add/remove/reset).
        ItemContainerGenerator.ContainersChanged += OnContainersChanged;

        // One provider for the bar's whole life, reading the list the open drop-down refills. The picker
        // re-queries it on every keystroke, so the host is asked for children exactly ONCE (on the opening
        // event) and every later filter is local — a directory of five hundred entries is never re-enumerated
        // between keystrokes.
        _dropDownProvider = new DropDownProvider(_dropDownItems);
    }

    /// <inheritdoc cref="IsEditableProperty"/>
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }

    /// <inheritdoc cref="TextProperty"/>
    public string Text { get => _text; set => SetText(value); }

    /// <inheritdoc cref="IsEditingProperty"/>
    public bool IsEditing => _isEditing;

    /// <inheritdoc cref="ActiveIndexProperty"/>
    public int ActiveIndex => _activeIndex;

    /// <inheritdoc cref="HasOverflowProperty"/>
    public bool HasOverflow => _hasOverflow;

    /// <inheritdoc cref="OverflowCountProperty"/>
    public int OverflowCount => _overflowCount;

    /// <summary>
    /// The live contents of the edit box while <see cref="IsEditing"/> — what a completion provider reads on every
    /// keystroke. Outside edit mode it is <see cref="Text"/>. Deliberately a plain CLR property: it is a view of the
    /// <c>PART_EditBox</c>, not a value the bar owns (<see cref="Text"/> is that value).
    /// </summary>
    public string EditText => _isEditing ? _editBox?.Text ?? "" : _text;

    /// <summary>CLR sugar over <see cref="ItemActivatedEvent"/>.</summary>
    public event EventHandler<BreadcrumbBarItemEventArgs>? ItemActivated
    {
        add => AddHandler(ItemActivatedEvent, value!);
        remove => RemoveHandler(ItemActivatedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="EditingStartedEvent"/>.</summary>
    public event EventHandler<BreadcrumbBarEditEventArgs>? EditingStarted
    {
        add => AddHandler(EditingStartedEvent, value!);
        remove => RemoveHandler(EditingStartedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="EditCommittedEvent"/>.</summary>
    public event EventHandler<BreadcrumbBarEditEventArgs>? EditCommitted
    {
        add => AddHandler(EditCommittedEvent, value!);
        remove => RemoveHandler(EditCommittedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="EditCanceledEvent"/>.</summary>
    public event EventHandler<BreadcrumbBarEditEventArgs>? EditCanceled
    {
        add => AddHandler(EditCanceledEvent, value!);
        remove => RemoveHandler(EditCanceledEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="DropDownOpeningEvent"/>.</summary>
    public event EventHandler<BreadcrumbBarDropDownEventArgs>? DropDownOpening
    {
        add => AddHandler(DropDownOpeningEvent, value!);
        remove => RemoveHandler(DropDownOpeningEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="ChildActivatedEvent"/>.</summary>
    public event EventHandler<BreadcrumbBarDropDownEventArgs>? ChildActivated
    {
        add => AddHandler(ChildActivatedEvent, value!);
        remove => RemoveHandler(ChildActivatedEvent, value!);
    }

    // Test / inspection seams (template-private parts).
    internal BreadcrumbBarItem? OverflowChipPart => _overflowChip;
    internal TextBox? EditBoxPart => _editBox;
    internal CompletionPopup? DropDownPart => _dropDown;

    // ───────────────────────────── container policy ─────────────────────────────

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new BreadcrumbBarItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is BreadcrumbBarItem;

    // ───────────────────────────── template wiring ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_editBox is not null)
            _editBox.KeyDown -= OnEditBoxKeyDown;

        UnhookDropDown();

        _chipsHost = GetTemplatePart<UIElement>(PartChipsHost);
        _overflowChip = GetTemplatePart<BreadcrumbBarItem>(PartOverflowChip);
        _editBox = GetTemplatePart<TextBox>(PartEditBox);
        _dropDown = GetTemplatePart<CompletionPopup>(PartDropDown);

        if (_dropDown is not null)
        {
            _dropDown.MaxWidth = 48;
            _dropDown.Provider = _dropDownProvider;
            _dropDown.Picked += OnDropDownPicked;
            _dropDown.Closed += OnDropDownClosed;
        }

        if (_overflowChip is not null)
        {
            // Hidden until the fold reports elided ancestors — and out of the tab ring while hidden, so a trail that
            // fits never has a phantom stop in front of it.
            _overflowChip.SetCurrentValue(VisibilityProperty, _hasOverflow ? Visibility.Visible : Visibility.Collapsed);
            _overflowChip.IsTabStop = _hasOverflow;
        }

        if (_editBox is not null)
        {
            _editBox.SetCurrentValue(TextBox.TextProperty, _text);
            _editBox.KeyDown += OnEditBoxKeyDown;
        }

        UpdateEditVisibility(); // push the current mode into the freshly-realized parts
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_editBox is not null)
            _editBox.KeyDown -= OnEditBoxKeyDown;

        UnhookDropDown();

        _chipsHost = null;
        _overflowChip = null;
        _editBox = null;
        _dropDown = null;
        base.OnTemplateDetaching(old);
    }

    // The drop-down part is a real control with a live Popup surface, not an inert Border: a template swap
    // while a list is up must take that surface down with it, and it must stop driving the chip it hangs
    // from. (Provider is cleared too — the outgoing popup keeps whatever tree it lands in from re-querying
    // the list this bar is about to refill.)
    private void UnhookDropDown()
    {
        if (_dropDown is null)
            return;

        _dropDown.Close();
        _dropDown.Picked -= OnDropDownPicked;
        _dropDown.Closed -= OnDropDownClosed;
        _dropDown.Anchor = null;
        _dropDown.Provider = null;
        _dropDownIsOverflow = false;
        _dropDownEntries.Clear();
        _dropDownItems.Clear();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        ClearPendingFocus(); // a pick armed for an in-flight navigation must not outlive the bar's attachment
        CloseDropDown(); // close the popup FIRST so no surface is stranded past the detach
        base.OnDetachedFromTree(in e);
    }

    // ───────────────────────────── the fold (BreadcrumbBarPanel reports here) ─────────────────────────────

    /// <summary>
    /// The panel reports its fold: <paramref name="firstVisibleIndex"/> leading segments are elided. Flips the
    /// ellipsis chip's visibility (and its tab-stop participation) and the read-only overflow state.
    /// </summary>
    /// <param name="firstVisibleIndex">The index of the first chip that survived the fold.</param>
    /// <param name="containerCount">The panel's authoritative child count (0 ⇒ nothing realized).</param>
    internal void SetOverflowState(int firstVisibleIndex, int containerCount)
    {
        _firstVisibleIndex = containerCount == 0 ? 0 : firstVisibleIndex;

        var hasOverflow = _firstVisibleIndex > 0;

        SetAndRaise(HasOverflowProperty, ref _hasOverflow, hasOverflow);
        SetAndRaise(OverflowCountProperty, ref _overflowCount, _firstVisibleIndex);

        if (_overflowChip is not null)
        {
            _overflowChip.SetCurrentValue(VisibilityProperty, hasOverflow ? Visibility.Visible : Visibility.Collapsed);
            _overflowChip.IsTabStop = hasOverflow;
        }

        // The trail widened until nothing is folded: an ellipsis drop-down still standing would list segments that
        // are now on screen. A separator's sibling list is unrelated to the fold and is left alone.
        if (!hasOverflow && _dropDownIsOverflow)
            CloseDropDown();

        ResolvePendingFocusAfterLayout();
    }

    // A drop-down pick armed for a host that navigates ASYNCHRONOUSLY lands HERE rather than on the generator's
    // ContainersChanged. That channel fires while the new container is not yet in the tree — the ItemsPresenter
    // adopts it from the same event, and this bar subscribes first — so the chip is not focusable yet and a
    // lookup there silently finds nothing. By the time the panel has arranged, the containers are adopted AND the
    // fold has settled, which is also what makes "is this chip actually visible" answerable.
    private void ResolvePendingFocusAfterLayout()
    {
        if (!_hasPendingFocus || _dropDown is { IsOpen: true })
            return; // still open ⇒ Closed owns the landing (and focus is on the menu, not the bar)

        // Whether the arm is still live was decided in OnLostFocus, by HOW focus left rather than by where it is
        // now: a user walking away (Tab / pointer / directional / access key) clears it, a repair displacing it
        // does not. Checking IsKeyboardFocusWithin here instead would abandon exactly the case this exists for,
        // because a Clear()+Add() rebuild destroys the focused chip and the repair lands outside the bar.
        TryLandPendingFocus();
    }

    // ───────────────────────────── activation ─────────────────────────────

    /// <summary>Raises <see cref="ItemActivated"/> for the item at <paramref name="index"/> (a no-op out of range).</summary>
    /// <param name="index">The index of the segment to report.</param>
    public void ActivateItem(int index)
    {
        if (index < 0 || index >= ItemContainerGenerator.ContainerCount)
            return;

        var item = ItemContainerGenerator.ItemFromIndex(index);

        // Arm the SAME landing a drop-down pick uses. Activating a segment is a navigation request the host
        // answers by REPLACING the items — that is this control's whole contract — so the chip just activated
        // is detached along with everything after it. Focus is then repaired to whatever happens to be nearby,
        // which is typically a DEEPER chip that is itself about to be removed, and the second detach leaves
        // focus nowhere visible at all: activating "mike.strobel" in "/ ▸ Users ▸ mike.strobel ▸ Applications"
        // lands on it, slides to the doomed "Applications", then vanishes. The activated item is where focus
        // belongs; say so, and let the post-layout resolve place it once the rebuilt trail exists.
        //
        // This covers the overflow list too — its entries call straight back into here — so picking an elided
        // ancestor lands on that ancestor rather than on whatever happened to survive the fold.
        _pendingFocusChild = item;
        _pendingFocusAnchor = index;
        _hasPendingFocus = true;

        RaiseEvent(new BreadcrumbBarItemEventArgs(
            ItemActivatedEvent,
            this,
            item,
            index,
            ItemContainerGenerator.ContainerFromIndex(index) as BreadcrumbBarItem));
    }

    // A chip was clicked / Enter'd / Space'd (BreadcrumbBarItem.OnClick funnels every path here, after the chip's own
    // Click + Command). The template's ellipsis chip is not a segment — its activation drops the elided list instead.
    internal void NotifyItemActivated(BreadcrumbBarItem chip)
    {
        if (ReferenceEquals(chip, _overflowChip))
        {
            ShowOverflowDropDown();
            return;
        }

        ActivateItem(ItemContainerGenerator.IndexFromContainer(chip));
    }

    // A chip gained focus: the active chip IS the focused chip, resolved from the live focus event rather than a
    // cached index that a collection edit could leave stale (the ListBox keyboard-cursor rule, CD-P9-16).
    internal void NotifyItemFocused(BreadcrumbBarItem chip)
    {
        var index = ReferenceEquals(chip, _overflowChip) ? -1 : ItemContainerGenerator.IndexFromContainer(chip);
        SetAndRaise(ActiveIndexProperty, ref _activeIndex, index);
    }

    // A chip's "▸" was pressed: ask the host for that segment's children (Explorer's sibling list). No handler, or a
    // handler that fills nothing, ⇒ the affordance is inert — the bar has no hierarchy of its own to enumerate.
    internal void RequestDropDown(BreadcrumbBarItem chip)
    {
        if (ReferenceEquals(chip, _overflowChip))
        {
            ShowOverflowDropDown();
            return;
        }

        RequestDropDownAt(ItemContainerGenerator.IndexFromContainer(chip));
    }

    /// <summary>
    /// Opens the drop-down for the segment at <paramref name="index"/> — the keyboard path (<c>↓</c>) and the
    /// pointer path (a chip's <c>▸</c>) funnel through here, so both raise the same events in the same order.
    /// A negative index, or a host that fills no children, leaves the affordance inert.
    /// </summary>
    public void RequestDropDownAt(int index)
    {
        if (index < 0 || index >= ItemContainerGenerator.ContainerCount)
            return;

        if (ItemContainerGenerator.ContainerFromIndex(index) is not BreadcrumbBarItem chip)
            return;

        var item = ItemContainerGenerator.ItemFromIndex(index);
        var opening = new BreadcrumbBarDropDownEventArgs(DropDownOpeningEvent, this, item, index, new List<object?>());

        RaiseEvent(opening);

        if (opening.Children.Count == 0)
            return;

        var children = new List<object?>(opening.Children); // snapshot: the args are the host's list, not ours
        var entries = new List<DropDownEntry>(children.Count);

        for (var order = 0; order < children.Count; order++)
        {
            var captured = children[order];

            entries.Add(new DropDownEntry(ToCandidate(captured), order, () =>
            {
                // Remember what was picked BEFORE the host reshapes the trail, and where the drop-down hung from.
                // The landing spot is resolved on Closed — see OnDropDownClosed for why it cannot be done here.
                _pendingFocusChild = captured;
                _pendingFocusAnchor = index;
                _hasPendingFocus = true;

                // The ORIGINAL host object, never the candidate the bar wrapped it in: the file dialog reads
                // SelectedChild back as its own FileDialogPathSegment and the gallery as its own TrailNode.
                RaiseEvent(new BreadcrumbBarDropDownEventArgs(ChildActivatedEvent, this, item, index, children, captured));
            }));
        }

        ShowDropDown(chip, entries, isOverflow: false);
    }

    /// <summary>
    /// The host's child as a drop-down candidate. A host that already speaks the completion vocabulary hands
    /// over a <see cref="CompletionItem"/> and keeps its icon, kind label and
    /// <see cref="CompletionItem.SortGroup"/> verbatim; anything else is wrapped around its
    /// <see cref="object.ToString"/>, which is exactly what the drop-down showed when it was a menu.
    /// </summary>
    private static CompletionItem ToCandidate(object? child)
        => child as CompletionItem ?? new CompletionItem(child?.ToString() ?? "");

    // ───────────────────────────── edit mode ─────────────────────────────

    /// <summary>
    /// Flips to the raw-text edit box with the text pre-selected (the <c>F2</c> gesture, also callable by a host
    /// that puts an "edit path" affordance elsewhere). Raises <see cref="EditingStarted"/> first, so a handler can
    /// supply the text to edit. Returns whether the bar entered edit mode.
    /// </summary>
    public bool BeginEdit()
    {
        if (_isEditing || !IsEditable)
            return false;

        ApplyTemplate(); // the box may not be realized yet if the bar has never been measured
        if (_editBox is null)
            return false;

        ClearPendingFocus(); // entering the edit box supersedes any pick still waiting on a navigation

        var args = new BreadcrumbBarEditEventArgs(EditingStartedEvent, this, _text);
        RaiseEvent(args);

        // ── ORDER MATTERS ────────────────────────────────────────────────────────────────────────────────
        // Raise the box and move focus into it BEFORE collapsing the chips. Collapsing the chip that holds
        // focus first drops focus out of the bar entirely, which fires OnLostFocus — and the focus-out rule
        // there commits the edit, so F2 used to enter and leave edit mode in the same keystroke. Focus never
        // leaves the bar if the box already has it. (_switchingMode is the belt to that suspenders: any focus
        // gap a host's handler manages to open mid-transition must not be read as "the user left".)
        _switchingMode = true;

        try
        {
            _editBox.SetCurrentValue(VisibilityProperty, Visibility.Visible);
            _editBox.SetCurrentValue(TextBox.TextProperty, args.Text);
            _editBox.Focus(FocusNavigationMethod.Programmatic);
            _editBox.SelectAll(); // "pre-selected": type to replace, End to append
            SetEditing(true);     // …and only now do the chips give way
        }
        finally
        {
            _switchingMode = false;
        }

        return true;
    }

    /// <summary>
    /// Commits the edited text: raises <see cref="EditCommitted"/> and, unless the handler set
    /// <see cref="BreadcrumbBarEditEventArgs.KeepEditing"/>, adopts the text into <see cref="Text"/> and returns to
    /// the chips. Returns whether the bar left edit mode.
    /// </summary>
    public bool CommitEdit() => CommitEdit(restoreFocus: true);

    /// <summary>
    /// Abandons the edit — the typed text is DISCARDED (<see cref="Text"/> keeps its committed value) and the chips
    /// come back. Raises <see cref="EditCanceled"/>. Returns whether the bar left edit mode.
    /// </summary>
    public bool CancelEdit()
    {
        if (!_isEditing)
            return false;

        var typed = _editBox?.Text ?? "";
        _switchingMode = true;

        try
        {
            SetEditing(false);
            _editBox?.SetCurrentValue(TextBox.TextProperty, _text); // the box re-seeds from the COMMITTED value
            RestoreChipFocus();
        }
        finally
        {
            _switchingMode = false;
        }

        RaiseEvent(new BreadcrumbBarEditEventArgs(EditCanceledEvent, this, typed));
        return true;
    }

    private bool CommitEdit(bool restoreFocus)
    {
        if (!_isEditing)
            return false;

        var args = new BreadcrumbBarEditEventArgs(EditCommittedEvent, this, _editBox?.Text ?? "");
        RaiseEvent(args);

        if (args.KeepEditing)
        {
            // Validation rejected the path: stay in the box with the text intact. A focus-out commit
            // (restoreFocus: false) can NOT be held open this way — the user is already somewhere else, and pulling
            // focus back would trap them; OnLostFocus drops edit mode after this returns.
            if (restoreFocus)
                _editBox?.Focus(FocusNavigationMethod.Programmatic);

            return false;
        }

        _switchingMode = true;

        try
        {
            SetText(args.Text);
            SetEditing(false);

            if (restoreFocus)
                RestoreChipFocus();
        }
        finally
        {
            _switchingMode = false;
        }

        return true;
    }

    private void SetText(string? value)
    {
        value ??= "";

        if (!SetAndRaise(TextProperty, ref _text, value))
            return;

        if (!_isEditing)
            _editBox?.SetCurrentValue(TextBox.TextProperty, value); // keep the (hidden) box in sync for the next entry
    }

    private void SetEditing(bool value)
    {
        if (!SetAndRaise(IsEditingProperty, ref _isEditing, value))
            return;

        PseudoClasses.Set(":editing", value); // DirectProperty-backed ⇒ set imperatively (cf. ComboBox's :open)
        UpdateEditVisibility();
    }

    private void UpdateEditVisibility()
    {
        _chipsHost?.SetCurrentValue(VisibilityProperty, _isEditing ? Visibility.Collapsed : Visibility.Visible);
        _editBox?.SetCurrentValue(VisibilityProperty, _isEditing ? Visibility.Visible : Visibility.Collapsed);
    }

    private void RestoreChipFocus()
    {
        BuildFocusRing();

        if (_ring.Count == 0)
            return;

        // Back to the chip that was active before the edit; if it is gone (or was never set) the CURRENT segment is
        // the sane landing spot — it is the one the trail is "about".
        var active = _activeIndex >= 0 ? ItemContainerGenerator.ContainerFromIndex(_activeIndex) : null;
        var index = active is null ? -1 : _ring.IndexOf(active);

        _ring[index >= 0 ? index : _ring.Count - 1].Focus(FocusNavigationMethod.Programmatic);
    }

    private static void OnIsEditableChanged(UIObject sender, bool oldValue, bool newValue)
    {
        // Revoking editability mid-edit must not strand the box on screen.
        if (sender is BreadcrumbBar { _isEditing: true } bar && !newValue)
            bar.CancelEdit();
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return; // the chip already claimed it (Enter/Space activate through ButtonBase)

        if (_isEditing)
        {
            // The backstop for the (rare) shape where the edit-box handler did not run — e.g. focus is on another
            // element inside the bar, or the template was swapped mid-edit and PART_EditBox is gone. The normal
            // path is OnEditBoxKeyDown, at the source.
            HandleEditKey(e);
            return;
        }

        switch (e.Key)
        {
            case Key.LeftArrow:
                MoveActive(-1);
                break;
            case Key.RightArrow:
                MoveActive(+1);
                break;
            case Key.Home:
                MoveActiveToEdge(first: true);
                break;
            case Key.End:
                MoveActiveToEdge(first: false);
                break;

            // The keyboard twin of pressing a chip's "▸". Outside edit mode this is the bar's primary drill
            // gesture — without it the child list is reachable only by pointer, which strands the keyboard user
            // on the ancestors the trail happens to already contain.
            case Key.DownArrow:
                if (ActiveIndex is var index and >= 0)
                    RequestDropDownAt(index);
                else if (_overflowChip?.IsKeyboardFocusWithin is true)
                    ShowOverflowDropDown();
                break;

            case Key.F2 when IsEditable:
                BeginEdit();
                break;

            // Key.Escape is DELIBERATELY absent: in chip mode the bar leaves Escape entirely unhandled so the host
            // (a dialog, a search surface) decides what it means and where focus goes. See the type remarks.
            default:
                return;
        }

        e.Handled = true;
    }

    // Handled at the SOURCE (an instance handler on the box) rather than only on the bubble to the bar, because the
    // bar must claim Enter/Escape before any ancestor default sees them — otherwise committing a path in a dialog
    // also fires the dialog's default button, and abandoning an edit also closes the dialog.
    private void OnEditBoxKeyDown(object? sender, KeyEventArgs e) => HandleEditKey(e);

    // Enter commits, Escape reverts. Both reach a handler at all only because a single-line TextBox leaves them
    // unhandled (it claims Enter only when AcceptsReturn); everything else stays with the box.
    private void HandleEditKey(KeyEventArgs e)
    {
        if (e.Handled || !_isEditing)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                CommitEdit();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Handled) return;

        if (IsEditing is false)
        {
            BeginEdit();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        // A pending drop-down pick is abandoned only when the USER walked away — Tab, a pointer press, directional
        // navigation, an access key. It must NOT be abandoned when focus was merely DISPLACED: a host that rebuilds
        // its trail with Clear()+Add() (which is what a view-model recomputing segments from a path does) resets
        // every container, so the focused chip is destroyed and the framework repairs focus programmatically —
        // landing it on whatever is focusable nearby, typically the first button in the enclosing toolbar. Treating
        // that repair as "the user moved on" is what made a keyboard pick end up on the Back button.
        if (_hasPendingFocus && !IsKeyboardFocusWithin && e.Method.IsUserInitiated())
            ClearPendingFocus();

        // Focus left the bar AND its drop-down (IsKeyboardFocusWithin covers the popup, whose Child is a logical
        // descendant). An edit left dangling behind the user's back commits — the WPF editable-ComboBox rule — but
        // without dragging focus back to a chip.
        if (IsKeyboardFocusWithin || !_isEditing || _switchingMode)
            return;

        CommitEdit(restoreFocus: false);

        // A handler that rejected the text cannot hold focus hostage on the way out: leave chip mode regardless.
        if (_isEditing)
            SetEditing(false);
    }

    // The arrow ring: the leading ellipsis chip (when the fold raised it) followed by the chips that SURVIVED the
    // fold. Elided chips are Collapsed — still live, focusable elements — so they must be filtered out here or ←
    // would walk focus into something that paints nothing.
    private void BuildFocusRing()
    {
        _ring.Clear();

        if (_overflowChip is { Visibility: Visibility.Visible, Focusable: true, IsEffectivelyEnabled: true } chip)
            _ring.Add(chip);

        var count = ItemContainerGenerator.ContainerCount;

        for (var i = 0; i < count; i++)
        {
            var container = ItemContainerGenerator.ContainerFromIndex(i);

            if (container is { Visibility: Visibility.Visible, Focusable: true, IsEffectivelyEnabled: true })
                _ring.Add(container);
        }
    }

    // Moves the FOCUS only — never navigation. Arrowing along a trail must be free: the user is looking for the
    // segment to jump to, and Enter is the commitment.
    private void MoveActive(int delta)
    {
        BuildFocusRing();

        if (_ring.Count == 0)
            return;

        var current = -1;

        for (var i = 0; i < _ring.Count; i++)
        {
            if (_ring[i].IsKeyboardFocusWithin)
            {
                current = i;
                break;
            }
        }

        // Focus is not on a chip yet (the bar was entered programmatically): step in from the edge the arrow came from.
        if (current < 0)
            current = delta > 0 ? -1 : _ring.Count;

        var next = Math.Clamp(current + delta, 0, _ring.Count - 1); // edges hold — no wrap (Home/End are the jumps)

        _ring[next].Focus(FocusNavigationMethod.Directional);
    }

    private void MoveActiveToEdge(bool first)
    {
        BuildFocusRing();

        if (_ring.Count > 0)
            _ring[first ? 0 : ^1].Focus(FocusNavigationMethod.Directional);
    }

    // ───────────────────────────── the drop-downs ─────────────────────────────

    // The elided ancestors, in trail order, behind the leading "…" chip.
    private void ShowOverflowDropDown()
    {
        if (_overflowChip is null || _firstVisibleIndex <= 0)
            return;

        var entries = new List<DropDownEntry>(_firstVisibleIndex);

        for (var i = 0; i < _firstVisibleIndex; i++)
        {
            var index = i;

            // SortGroup := the segment's DEPTH, overriding whatever the item carried. The overflow list is an
            // ordered path PREFIX, not a sibling set — "Home ▸ Projects ▸ assets" re-sorted alphabetically
            // would destroy the one thing it means — and SortGroup is the first key of both the sort below and
            // the picker's ranking, so one segment per group pins trail order through the initial sort AND
            // through every later fuzzy filter.
            var candidate = ToCandidate(ItemContainerGenerator.ItemFromIndex(index)) with { SortGroup = index };
            entries.Add(new DropDownEntry(candidate, index, () => ActivateItem(index)));
        }

        ShowDropDown(_overflowChip, entries, isOverflow: true);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Both drop-downs ride a CompletionPopup in PICKER mode rather than a ContextMenu.
    //
    // A menu was the wrong container for what these lists actually are. A ContextMenu does not SCROLL, so a
    // directory with three hundred folders produced a menu taller than the terminal with no way to reach the
    // bottom of it, and its TextSearch is a prefix jump next to the picker's fuzzy filtering. A CompletionList
    // is a ListBox, so scrolling and virtualization are already there; the pattern the user types filters and
    // highlights through the same FuzzyMatcher the completion overlay uses.
    //
    // The mechanics the menu used to hand us for free are all still here, just spelled differently: the picker
    // never takes focus, so the anchor chip keeps it and drives the list through its own PreviewKeyDown — which
    // is also why ShowDropDown focuses the chip before opening. Re-anchoring under a DIFFERENT chip is a plain
    // Anchor write followed by Open(); the popup's own OpenOrRelocate does the close-then-reopen a Popup needs
    // to move (it only places on the closed→open edge). Light dismiss survives the swap too — a target-less
    // CompletionPopup arms it, exactly because a chooser is not the hint-over-a-field the type usually is.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void ShowDropDown(UIElement anchor, List<DropDownEntry> entries, bool isOverflow)
    {
        if (entries.Count == 0 || !IsAttachedToTree)
            return;

        ApplyTemplate(); // the drop-down part may not be realized yet if the bar has never been measured

        if (_dropDown is null)
            return; // a template without PART_DropDown: the affordance is inert, exactly as it is without PART_EditBox

        // …and the part's OWN template too, for the same reason one level down. This is the one place the
        // swap away from ContextMenu needed a second ApplyTemplate: a menu built its surface on demand, but
        // PART_DropDown reaches its Popup through its template, and CompletionPopup.OpenOrRelocate returns
        // silently when that part is missing — so a drop-down requested before the bar's first measure (a
        // host calling RequestDropDownAt straight after ShowRoot) came up empty and never recovered, because
        // the later template expansion only re-opens a popup that already believes it is open.
        _dropDown.ApplyTemplate();

        // Take whatever list is already up DOWN first, and only then fill ours. The close raises Closed, and
        // that handler drops the open list's state — so filling before closing would wipe the entries we just
        // built. (Two paths get here with a list already up: pressing a DIFFERENT chip's "▸", and the anchor
        // focus move below, which closes the old picker through its own LostFocus.)
        CloseDropDown();

        // ── SORTED, and sorted HERE rather than by the picker ────────────────────────────────────────────
        // An EMPTY filter pattern deliberately preserves PROVIDER order (running it through the matcher's
        // length-then-ordinal tiebreak would re-sort a directory listing by name length), so "sorted before
        // anything is typed" is something the provider has to arrange — it is not a property the picker
        // supplies. SortGroup leads because it is also the picker's first ranking key, so the order the user
        // sees before typing is the order that survives the first keystroke.
        entries.Sort(CompareCandidates);

        _dropDownEntries.Clear();
        _dropDownItems.Clear();

        foreach (var entry in entries)
        {
            _dropDownEntries.Add(entry);
            _dropDownItems.Add(entry.Candidate);
        }

        _dropDownIsOverflow = isOverflow;

        // Focus the chip the list hangs from FIRST. The picker is non-focusable by design, so the anchor's
        // PreviewKeyDown is the ONLY thing that can drive it — and the pointer path deliberately marks the
        // "▸" press handled before ButtonBase.OnMouseDown gets to focus the chip itself, so without this the
        // list would come up with the keyboard pointed at whatever the user was on before.
        anchor.Focus(FocusNavigationMethod.Programmatic);

        anchor.PseudoClasses.SetInternal(ExpandedChipPseudoClass, true);
        _dropDown.Anchor = anchor;
        _dropDown.Open();
    }

    // The bar does not know its hosts' semantics, so the only ordering it can defensibly impose is by the
    // text it shows, case-insensitively (a folder named "Documents" must not sort miles away from one named
    // "assets"). SortGroup comes first so a host can still group — the file dialog's folders-before-files —
    // and the host's fill order is the final tiebreak, because List.Sort is UNSTABLE and two same-named
    // children would otherwise swap places between one opening of the list and the next.
    private static int CompareCandidates(DropDownEntry left, DropDownEntry right)
    {
        if (left.Candidate.SortGroup != right.Candidate.SortGroup)
            return left.Candidate.SortGroup.CompareTo(right.Candidate.SortGroup);

        var display = string.Compare(left.Candidate.Display, right.Candidate.Display, StringComparison.OrdinalIgnoreCase);
        return display != 0 ? display : left.Order.CompareTo(right.Order);
    }

    private void OnDropDownPicked(object? sender, CompletionPickedEventArgs e)
    {
        // Reference identity, NOT List.IndexOf: CompletionItem is a record, so two children that display the
        // same string compare EQUAL, and a value-based lookup would invoke the wrong one of them.
        foreach (var entry in _dropDownEntries)
        {
            if (ReferenceEquals(entry.Candidate, e.Item))
            {
                entry.Invoke();
                return;
            }
        }
    }

    private void CloseDropDown()
    {
        _dropDown?.Close(); // Closed → ReleaseDropDown, which is where the state actually drops
        ReleaseDropDown();  // …and again for the already-closed case, which raises nothing
    }

    // Everything the open list owned, dropped the moment it is down. The Anchor matters most: the picker
    // hooks the chip's LostFocus and PreviewKeyDown, and a host that answers a pick by rebuilding the trail
    // DETACHES that chip — holding the reference would leave the picker subscribed to a dead element until
    // the next drop-down happened to replace it.
    private void ReleaseDropDown()
    {
        _dropDownIsOverflow = false;
        _dropDownEntries.Clear();
        _dropDownItems.Clear();

        if (_dropDown is { Anchor: {} anchor })
        {
            anchor.PseudoClasses.SetInternal(ExpandedChipPseudoClass, false);
            _dropDown.Anchor = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Where focus goes after a drop-down pick.
    //
    // This CANNOT be done in the pick handler itself. The pick runs while the list is still up, and the close
    // that follows it can move focus out from under anything we set: a host that answers ChildActivated by
    // rebuilding the trail DETACHES the chip that held focus, and the framework then repairs focus onto
    // whatever is focusable nearby — typically the first button in the enclosing toolbar, which is neither the
    // pick nor the chip the user was on. (Under the old ContextMenu the same thing happened for a second
    // reason: Popup.CloseCore restores focus to the opening element, after our handler and before Closed, and
    // it skips that restore entirely when the element has been detached.)
    //
    // So: land it on Closed, which runs after all of that. The pick is what the user chose, so the pick is what
    // gets focus; if the host did not put it in the trail (it may have navigated elsewhere, or ignored the pick),
    // fall back to the anchor chip, and finally to the ring's usual landing spot.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnDropDownClosed(object? sender, EventArgs e)
    {
        ReleaseDropDown();

        if (!_hasPendingFocus)
            return; // a plain dismiss (Esc, an outside press, focus walking off the chip) — nothing to land

        if (!IsAttachedToTree)
        {
            ClearPendingFocus();
            return;
        }

        if (TryLandPendingFocus())
            return;

        // The pick is not in the trail YET. A host that navigates ASYNCHRONOUSLY (the file dialogs enumerate off
        // the UI thread so a slow share cannot stall the frame loop) has not rebuilt anything by the time this
        // runs, so there is nothing to focus — the arm STAYS SET and OnContainersChanged lands it once the new
        // trail arrives. Meanwhile put focus somewhere sane rather than leaving it stranded.
        var anchor = _pendingFocusAnchor;
        if (anchor >= 0 && ItemContainerGenerator.ContainerFromIndex(anchor) is { } anchorChip && _ring.Contains(anchorChip))
            SetActiveContainer(anchorChip);
        else
            RestoreChipFocus();
    }

    /// <summary>
    /// Lands a pending drop-down pick on its chip if that chip now exists and is focusable, clearing the arm.
    /// Returns whether it landed. Called once on Closed (the synchronous host lands here) and again on every
    /// structural change (the asynchronous host lands there).
    /// </summary>
    private bool TryLandPendingFocus()
    {
        if (!_hasPendingFocus || _isEditing || !IsAttachedToTree)
            return false;

        BuildFocusRing();

        if (_ring.Count == 0)
            return false;

        if (FocusableContainerForItem(_pendingFocusChild) is not { } picked)
            return false;

        ClearPendingFocus();
        SetActiveContainer(picked);
        return true;
    }

    private void ClearPendingFocus()
    {
        _pendingFocusChild = null;
        _pendingFocusAnchor = -1;
        _hasPendingFocus = false;
    }

    /// <summary>The focusable container currently bound to <paramref name="item"/>, or null when the host did not
    /// place it in the trail (or the fold has collapsed it out of the ring).</summary>
    private UIElement? FocusableContainerForItem(object? item)
    {
        var count = ItemContainerGenerator.ContainerCount;
        for (var i = 0; i < count; i++)
        {
            if (Equals(ItemContainerGenerator.ItemFromIndex(i), item) &&
                ItemContainerGenerator.ContainerFromIndex(i) is { } container &&
                _ring.Contains(container))
            {
                return container;
            }
        }

        return null;
    }

    private void SetActiveContainer(UIElement container)
    {
        var index = ItemContainerGenerator.IndexFromContainer(container);
        if (index >= 0)
            _activeIndex = index; // keep ←/→ continuing from where focus actually landed

        container.Focus(FocusNavigationMethod.Programmatic);
    }

    // ───────────────────────────── :current bookkeeping ─────────────────────────────

    private void OnContainersChanged(object? sender, ContainersChangedEventArgs e)
    {
        UpdateCurrentFlags();

        // NOTE: a pending drop-down pick is deliberately NOT resolved here — the new container is not in the tree
        // yet at this point (see ResolvePendingFocusAfterLayout). It lands after the panel arranges.
    }

    // :current is POSITIONAL — the trailing segment. Re-stamped on every container-set change (owner-written with
    // SetCurrentValue so an author's own binding on IsCurrent survives).
    private void UpdateCurrentFlags()
    {
        var count = ItemContainerGenerator.ContainerCount;
        var isFocusScope = FocusManager.GetIsFocusScope(this);

        if (isFocusScope)
            ClearValue(FocusManager.FocusedElementProperty);

        for (var i = 0; i < count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is BreadcrumbBarItem chip)
            {
                chip.SetCurrentValue(BreadcrumbBarItem.IsCurrentProperty, i == count - 1);

                if (isFocusScope && (_activeIndex < 0 ? i == count - 1 : _activeIndex == i))
                    FocusManager.SetFocusedElement(this, chip);
            }
        }
    }

    // ───────────────────────────── the drop-down's completion seam ─────────────────────────────

    /// <summary>One row of an open drop-down: what the picker shows, where the host put it, and what picking it does.</summary>
    /// <param name="Candidate">The candidate handed to the picker — the host's own <see cref="CompletionItem"/>
    /// when it supplied one, otherwise the bar's wrapper around <c>ToString()</c>.</param>
    /// <param name="Order">The host's fill order, kept as the sort's final tiebreak (<c>List.Sort</c> is unstable).</param>
    /// <param name="Invoke">What picking this row does — raise <c>ChildActivated</c>, or activate an elided ancestor.</param>
    private readonly record struct DropDownEntry(CompletionItem Candidate, int Order, Action Invoke);

    /// <summary>
    /// The drop-down's completion provider: hand back the snapshot the host filled, and let the picker do the
    /// filtering, ranking and highlighting. The query's <c>Text</c> is the picker's own filter pattern (there
    /// is no field), so the span it reports is the whole of it — nothing is ever spliced anywhere, but a
    /// context has to carry one.
    /// </summary>
    private sealed class DropDownProvider(List<CompletionItem> items) : ICompletionProvider
    {
        public CompletionContext? GetCompletions(in CompletionQuery query)
            => items.Count == 0 ? null : new CompletionContext(0, query.Text.Length, query.Text, items);
    }
}
