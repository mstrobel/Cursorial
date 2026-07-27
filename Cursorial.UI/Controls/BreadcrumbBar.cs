using Cursorial.Input;
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
public class BreadcrumbBar : ItemsControl
{
    private const string PartChipsHost = "PART_ChipsHost";
    private const string PartOverflowChip = "PART_OverflowChip";
    private const string PartEditBox = "PART_EditBox";

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

    /// <summary>Raised when a chip's <c>▸</c> separator is pressed: fill
    /// <see cref="BreadcrumbBarDropDownEventArgs.Children"/> to offer the Explorer-style sibling list (leave it
    /// empty and the press is inert).</summary>
    public static readonly RoutedEvent<BreadcrumbBarDropDownEventArgs> DropDownOpeningEvent =
        RoutedEvent<BreadcrumbBarDropDownEventArgs>.Register(nameof(DropDownOpening), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    /// <summary>Raised when the user picks an entry out of a separator's sibling drop-down
    /// (<see cref="BreadcrumbBarDropDownEventArgs.SelectedChild"/> is the pick, under
    /// <see cref="BreadcrumbBarDropDownEventArgs.Item"/>).</summary>
    public static readonly RoutedEvent<BreadcrumbBarDropDownEventArgs> ChildActivatedEvent =
        RoutedEvent<BreadcrumbBarDropDownEventArgs>.Register(nameof(ChildActivated), RoutingStrategy.Bubble, typeof(BreadcrumbBar));

    private readonly List<UIElement> _ring = []; // reused arrow-navigation ring (no per-keystroke allocation)

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
    private ContextMenu? _dropDown;
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
        ItemsPanel = new ItemsPanelTemplate(static _ => new BreadcrumbBarPanel());

        // :current follows POSITION, so it is re-stamped whenever the container set changes (add/remove/reset).
        ItemContainerGenerator.ContainersChanged += OnContainersChanged;
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
    internal ContextMenu? DropDownPart => _dropDown;

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

        _chipsHost = GetTemplatePart<UIElement>(PartChipsHost);
        _overflowChip = GetTemplatePart<BreadcrumbBarItem>(PartOverflowChip);
        _editBox = GetTemplatePart<TextBox>(PartEditBox);

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

        _chipsHost = null;
        _overflowChip = null;
        _editBox = null;
        base.OnTemplateDetaching(old);
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

        // Only steal focus back if focus is STILL inside the bar. If the user tabbed away, clicked the listing, or
        // the host moved focus itself while the navigation was in flight, the pick is stale — yanking focus out of
        // wherever they now are is worse than not restoring it. This also stops the arm lingering across an
        // unrelated later layout pass.
        if (!IsKeyboardFocusWithin)
        {
            ClearPendingFocus();
            return;
        }

        TryLandPendingFocus();
    }

    // ───────────────────────────── activation ─────────────────────────────

    /// <summary>Raises <see cref="ItemActivated"/> for the item at <paramref name="index"/> (a no-op out of range).</summary>
    /// <param name="index">The index of the segment to report.</param>
    public void ActivateItem(int index)
    {
        if (index < 0 || index >= ItemContainerGenerator.ContainerCount)
            return;

        RaiseEvent(new BreadcrumbBarItemEventArgs(
            ItemActivatedEvent,
            this,
            ItemContainerGenerator.ItemFromIndex(index),
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
        var entries = new List<(object? Header, Action Invoke)>(children.Count);

        foreach (var child in children)
        {
            var captured = child;
            entries.Add((captured, () =>
            {
                // Remember what was picked BEFORE the host reshapes the trail, and where the drop-down hung from.
                // The landing spot is resolved on Closed — see OnDropDownClosed for why it cannot be done here.
                _pendingFocusChild = captured;
                _pendingFocusAnchor = index;
                _hasPendingFocus = true;

                RaiseEvent(new BreadcrumbBarDropDownEventArgs(ChildActivatedEvent, this, item, index, children, captured));
            }));
        }

        ShowDropDown(chip, entries, isOverflow: false);
    }

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
                RequestDropDownAt(ActiveIndex);
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

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

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

        if (_overflowChip is { Visibility: Visibility.Visible, Focusable: true } chip && chip.IsEffectivelyEnabled)
            _ring.Add(chip);

        var count = ItemContainerGenerator.ContainerCount;

        for (var i = 0; i < count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Visibility: Visibility.Visible, Focusable: true } container
                && container.IsEffectivelyEnabled)
            {
                _ring.Add(container);
            }
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

        var entries = new List<(object? Header, Action Invoke)>(_firstVisibleIndex);

        for (var i = 0; i < _firstVisibleIndex; i++)
        {
            var index = i;
            entries.Add((ItemContainerGenerator.ItemFromIndex(index), () => ActivateItem(index)));
        }

        ShowDropDown(_overflowChip, entries, isOverflow: true);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Both drop-downs ride a ContextMenu rather than a hand-rolled Popup + ListBox, on purpose. A Popup only
    // PLACES on the closed→open edge and PlacementRect is never read, so re-anchoring one under a DIFFERENT chip
    // (which is exactly what the separator list does) means close-then-reopen plus manual offset arithmetic —
    // and light-dismiss, Escape, focus-into and the on-close focus restore would all have to be rebuilt.
    // ContextMenu.Open already funnels every one of those, including the close-then-reopen relocation.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void ShowDropDown(UIElement anchor, List<(object? Header, Action Invoke)> entries, bool isOverflow)
    {
        if (entries.Count == 0 || !IsAttachedToTree)
            return;

        if (_dropDown is null)
        {
            _dropDown = new ContextMenu();
            _dropDown.Closed += OnDropDownClosed;
        }

        _dropDown.Items.Clear();

        foreach (var (header, invoke) in entries)
        {
            var menuItem = new MenuItem { Header = header };
            var action = invoke;
            menuItem.Click += (_, _) => action();
            _dropDown.Items.Add(menuItem);
        }

        _dropDownIsOverflow = isOverflow;
        _dropDown.Open(anchor, new CellPosition(0, 0)); // Bottom placement under the pressed chip
    }

    private void CloseDropDown()
    {
        _dropDown?.Close();
        _dropDownIsOverflow = false;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Where focus goes after a drop-down pick.
    //
    // This CANNOT be done in the pick handler itself. Popup.CloseCore restores focus to the element that was
    // focused when the popup opened — the chip the drop-down hung from — and it does that AFTER our handler has
    // run and BEFORE it raises Closed, so anything we focus during the pick is immediately overwritten.
    //
    // Worse, the restore is guarded on `restoreFocusTo is { IsAttachedToTree: true }`. A host that answers
    // ChildActivated by rebuilding the trail detaches that very chip, so the restore is SKIPPED, focus is left
    // wherever the teardown dropped it, and the next focus query lands on the first chip in the ring — the
    // leftmost visible one, which is neither the pick nor the chip the user was on.
    //
    // So: land it on Closed, which runs after the restore. The pick is what the user chose, so the pick is what
    // gets focus; if the host did not put it in the trail (it may have navigated elsewhere, or ignored the pick),
    // fall back to the anchor chip, and finally to the ring's usual landing spot.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnDropDownClosed(object? sender, RoutedEventArgs e)
    {
        if (!_hasPendingFocus)
            return; // a plain dismiss (Esc / light-dismiss) — leave the popup's own restore alone

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

        for (var i = 0; i < count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is BreadcrumbBarItem chip)
                chip.SetCurrentValue(BreadcrumbBarItem.IsCurrentProperty, i == count - 1);
        }
    }
}
