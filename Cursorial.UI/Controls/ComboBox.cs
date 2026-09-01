using Cursorial.Input;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>The argument to <see cref="ComboBox.DropDownClosedEvent"/> — whether the close was a commit
/// gesture (Enter / Space / item click) rather than a dismissal (Escape, light-dismiss, focus-out, toggle,
/// detach), mirroring the <see cref="ComboBox.OnDropDownClosed(bool)"/> distinction for plain handlers.</summary>
public sealed class DropDownClosedEventArgs : RoutedEventArgs
{
    /// <summary>Creates caller-owned args ready to pass to <see cref="UIElement.RaiseEvent"/>.</summary>
    public DropDownClosedEventArgs(RoutedEvent routedEvent, UIElement source, bool committed)
        : base(routedEvent, source)
        => Committed = committed;

    /// <summary>Whether the close was a commit gesture rather than a dismissal.</summary>
    public bool Committed { get; }
}

/// <summary>
/// A single-selection drop-down list (design doc §12.11 — the ListBox-in-Popup recipe), with an optional editable
/// text-entry mode (WPF/Avalonia parity). Non-editable: the face (<c>PART_ContentSite</c>) shows the
/// <see cref="SelectingItemsControl.SelectedItem"/> and a click / keyboard opens the <see cref="Popup"/>
/// (<c>PART_Popup</c>); type-ahead jumps the selection (the shared <see cref="TextSearch"/>). Editable
/// (<see cref="IsEditable"/>): the face is a <see cref="TextBox"/> (<c>PART_EditableTextBox</c>) — typing edits
/// <see cref="Text"/> as free text, the drop button (<c>PART_DropDown</c>) opens the list, navigating it updates the
/// text, and Enter / focus-loss commits (an exact item match selects it; otherwise the free text is kept and the
/// selection clears). While the list is open, typing inline-completes against the items: the matched item is
/// tentatively highlighted and its trailer is appended selected ("al" + Alpha → <c>al|[pha]</c>, each keystroke
/// overwrites it); a commit adopts the match, any dismissal restores the pre-open selection. Containers are
/// <see cref="ComboBoxItem"/>s.
/// </summary>
[TemplatePart(PartPopup, typeof(Popup))]
[TemplatePart(PartContentSite, typeof(ContentPresenter))]
[TemplatePart(PartEditableTextBox, typeof(TextBox))]
[TemplatePart(PartDropDown, typeof(ButtonBase))]
public class ComboBox : SelectingItemsControl
{
    private const string PartPopup = "PART_Popup";
    private const string PartContentSite = "PART_ContentSite";
    private const string PartEditableTextBox = "PART_EditableTextBox";
    private const string PartDropDown = "PART_DropDown";

    /// <summary>Whether the drop-down is open (<c>:open</c>; two-way with the <see cref="Popup"/>).</summary>
    public static readonly DirectProperty<ComboBox, bool> IsDropDownOpenProperty =
        UIProperty.RegisterDirect<ComboBox, bool>(nameof(IsDropDownOpen), static c => c._isDropDownOpen, static (c, v) => c.SetDropDownOpen(v));

    /// <summary>Whether the face is an editable text box (<c>:editable</c>; free text is allowed).</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        UIProperty.Register<ComboBox, bool>(nameof(IsEditable), defaultValue: false, changed: OnIsEditableChanged);

    /// <summary>The editable text (two-way). Mirrors the selected item's display while not editing; free text otherwise.</summary>
    public static readonly DirectProperty<ComboBox, string?> TextProperty =
        UIProperty.RegisterDirect<ComboBox, string?>(nameof(Text), static c => c._text, static (c, v) => c.SetText(v, fromUser: false));

    /// <summary>When editable, whether the text box rejects typing (the list still drives the selection).</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        UIProperty.Register<ComboBox, bool>(nameof(IsReadOnly), defaultValue: false);

    /// <summary>The prompt shown when the editable text box is empty.</summary>
    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        UIProperty.Register<ComboBox, string?>(nameof(PlaceholderText));

    /// <summary>The maximum height of the drop-down list (default unbounded).</summary>
    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        UIProperty.Register<ComboBox, double>(nameof(MaxDropDownHeight), defaultValue: double.PositiveInfinity);

    /// <summary>When editable, whether typing opens / keeps the drop-down open (default <c>false</c>).</summary>
    public static readonly StyledProperty<bool> StaysOpenOnEditProperty =
        UIProperty.Register<ComboBox, bool>(nameof(StaysOpenOnEdit), defaultValue: false);

    /// <summary>Where the drop-down list opens relative to the face (default <see cref="PlacementMode.Bottom"/>). A
    /// command-bar Toolbar flips this to <see cref="PlacementMode.Left"/> when the combo is folded into its
    /// (right-anchored) overflow menu, so the list flies out to the SIDE rather than opening downward over the menu
    /// rows below (which would overlap them and hijack the menu's up/down navigation).</summary>
    public static readonly StyledProperty<PlacementMode> DropDownPlacementProperty =
        UIProperty.Register<ComboBox, PlacementMode>(
            nameof(DropDownPlacement), PlacementMode.Bottom, changed: OnDropDownPlacementChanged);

    /// <summary>
    /// The value shown in the face (read-only; the template's <c>PART_ContentSite</c> binds its <c>Content</c> to
    /// this). It is the <see cref="SelectingItemsControl.SelectedItem"/> <b>unwrapped to its content</b> when the
    /// item is its own <see cref="ComboBoxItem"/> container — the live container element belongs to the drop-down
    /// and must never be hosted in the face too (a <see cref="UIElement"/> cannot be in two places; hosting it
    /// would reparent it out of the list and route the face's mouse interaction to the container). WPF
    /// <c>SelectionBoxItem</c> parity (doc §12.11).
    /// </summary>
    public static readonly DirectProperty<ComboBox, object?> SelectionBoxItemProperty =
        UIProperty.RegisterDirect<ComboBox, object?>(nameof(SelectionBoxItem), static c => c._selectionBoxItem);

    /// <summary>Raised after the drop-down opens (<see cref="IsDropDownOpen"/> ⇒ <see langword="true"/>; direct — no route walk).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> DropDownOpenedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(DropDownOpened), RoutingStrategy.Direct, typeof(ComboBox));

    /// <summary>Raised after the drop-down closes (<see cref="IsDropDownOpen"/> ⇒ <see langword="false"/>; direct — no
    /// route walk). The args carry <see cref="DropDownClosedEventArgs.Committed"/>.</summary>
    public static readonly RoutedEvent<DropDownClosedEventArgs> DropDownClosedEvent =
        RoutedEvent<DropDownClosedEventArgs>.Register(nameof(DropDownClosed), RoutingStrategy.Direct, typeof(ComboBox));

    private bool _isDropDownOpen;
    private object? _selectionBoxItem;
    private string? _text = ""; // never null (matches the empty PART_EditableTextBox; the public Text never reports null)
    private bool _syncingText;  // guards the ComboBox.Text ↔ PART_EditableTextBox.Text round-trip
    private bool _committing;   // guards the selection→Text echo while committing free text (keeps the typed text)
    private bool _userEditing;  // the user has uncommitted typed text — a model selection change must not clobber it
    private bool _userSelectionInProgress; // a selection change is being applied by a user gesture (click / list navigation)
    private bool _selectionFromTextMatch;  // the CURRENT selection was set tentatively by the edit-text search (uncommitted)
    private bool _textMatchSelectionSync;  // guards the tentative-select / snapshot-restore Select — highlight only, no text echo
    private bool _sessionSelectionMovedByUser; // a user gesture (text match / list navigation) moved the selection THIS session
    private int _preOpenSelectedIndex = -1; // drop-down session snapshot: what a dismissal restores…
    private string _preOpenText = "";       // …and what Escape reverts the text to when nothing was selected at open
    private string _typedText = "";         // what the user actually typed (no completion trailer, never case-transformed)
    private Popup? _popup;
    private ContentPresenter? _contentSite;
    private TextBox? _editableTextBox;
    private ButtonBase? _dropDown;

    /// <summary>Creates a combo box (single selection; focusable — the face is the tab stop).</summary>
    public ComboBox()
    {
        SelectionMode = SelectionMode.Single;
        Focusable = true;
        SelectionChanged += OnSelectionChangedSync; // editable: the face text follows the selection
    }

    /// <inheritdoc/>
    protected internal override bool HandlesScrolling => true;

    /// <inheritdoc cref="IsDropDownOpenProperty"/>
    public bool IsDropDownOpen { get => _isDropDownOpen; set => SetDropDownOpen(value); }

    /// <inheritdoc cref="IsEditableProperty"/>
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text { get => _text; set => SetText(value, fromUser: false); }

    /// <inheritdoc cref="IsReadOnlyProperty"/>
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    /// <inheritdoc cref="PlaceholderTextProperty"/>
    public string? PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }

    /// <inheritdoc cref="MaxDropDownHeightProperty"/>
    public double MaxDropDownHeight { get => GetValue(MaxDropDownHeightProperty); set => SetValue(MaxDropDownHeightProperty, value); }

    /// <inheritdoc cref="StaysOpenOnEditProperty"/>
    public bool StaysOpenOnEdit { get => GetValue(StaysOpenOnEditProperty); set => SetValue(StaysOpenOnEditProperty, value); }

    /// <inheritdoc cref="DropDownPlacementProperty"/>
    public PlacementMode DropDownPlacement { get => GetValue(DropDownPlacementProperty); set => SetValue(DropDownPlacementProperty, value); }

    /// <inheritdoc cref="SelectionBoxItemProperty"/>
    public object? SelectionBoxItem => _selectionBoxItem;

    // Test/inspection seams (template-private parts).
    internal TextBox? EditableTextBoxPart => _editableTextBox;
    internal ContentPresenter? ContentSitePart => _contentSite;
    internal ButtonBase? DropDownPart => _dropDown;

    // True in BOTH modes: non-editable keys the inherited list type-ahead; editable never sees it while the text
    // box has focus (the box handles its TextInput), and any stray unhandled input routes through
    // OnTextSearchMatch, which in editable mode only highlights (the box keeps the typing surface).
    protected override bool TextSearchNavigates => true;

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new ComboBoxItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is ComboBoxItem;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
        if (_editableTextBox is not null)
            _editableTextBox.TextChanged -= OnEditableTextChanged;
        if (_dropDown is not null)
            _dropDown.Click -= OnDropDownClick;

        _popup = GetTemplatePart<Popup>(PartPopup);
        _contentSite = GetTemplatePart<ContentPresenter>(PartContentSite);
        _editableTextBox = GetTemplatePart<TextBox>(PartEditableTextBox);
        _dropDown = GetTemplatePart<ButtonBase>(PartDropDown);

        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = DropDownPlacement; // Bottom by default; a Toolbar flips it to Left inside its overflow menu
            _popup.KeepOpenOnAnchorPress = true; // a face click closes via OnMouseDown's toggle, not dismiss-then-reopen
            _popup.Closed += OnPopupClosed;
            _popup.SetCurrentValue(Popup.IsOpenProperty, _isDropDownOpen);
        }

        if (_editableTextBox is not null)
        {
            _editableTextBox.IsTabStop = false;
            _editableTextBox.SetCurrentValue(TextBox.TextProperty, _text ?? "");
            _editableTextBox.TextChanged += OnEditableTextChanged;
        }

        // The face templates SelectionBoxItem with the ItemTemplate — a binding (not an imperative set) so it follows
        // a runtime ItemTemplate change, wired here rather than in the control template so every ComboBox template
        // gets it without repeating the binding. SelectionBoxItem stays the ITEM; the presenter builds the display
        // copy (never the drop-down's live container).
        _contentSite?.SetBinding(ContentPresenter.ContentTemplateProperty,
                                 TemplateBinding.From(ItemTemplateProperty));

        if (_dropDown is not null)
            _dropDown.Click += OnDropDownClick;

        UpdateEditableState();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
        if (_editableTextBox is not null)
            _editableTextBox.TextChanged -= OnEditableTextChanged;
        if (_dropDown is not null)
            _dropDown.Click -= OnDropDownClick;

        _popup = null;
        _contentSite = null;
        _editableTextBox = null;
        _dropDown = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        SetDropDownOpen(false); // close (a dismissal — the tentative match rolls back) so the Popup surface doesn't leak on detach
        base.OnDetachedFromTree(e);
    }

    /// <inheritdoc/>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        // Editable: focus delegates to the text box (so its caret publishes and typing lands there).
        if (IsEditable && _editableTextBox is { IsFocused: false } box && ReferenceEquals(e.Source, this))
            box.Focus(e.Method);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        // Non-editable: a click anywhere on the face toggles the list. Editable: the text box owns its clicks and the
        // PART_DropDown button toggles — so the face press here does nothing (it would steal the caret click).
        if (!IsEditable)
        {
            Focus();
            SetDropDownOpen(!_isDropDownOpen);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnTextSearchMatch(int containerIndex)
    {
        if (IsEditable)
        {
            TentativelySelect(containerIndex); // highlight-only: typing must keep editing the box, never navigate it
            return;
        }

        base.OnTextSearchMatch(containerIndex); // selection-follows (the face updates)

        if (_isDropDownOpen) // when open, move the keyboard highlight too, so :focus-visible tracks the match
            ItemContainerGenerator.ContainerFromIndex(containerIndex)?.Focus(FocusNavigationMethod.Directional);
    }

    // Picking an item (click) commits + closes (ComboBoxItem calls this after selecting through the base). Enter and
    // (non-editable) Space on an open list route here too — the COMMIT closes, vs. every other close (Escape,
    // light-dismiss, focus-out, a face/drop-button toggle, detach), which is a dismissal. OnDropDownClosed receives
    // the distinction.
    internal void CommitAndClose()
    {
        _closeIsCommit = true;
        SetDropDownOpen(false);
    }

    private void OnDropDownClick(object? sender, ClickEventArgs e)
    {
        SetDropDownOpen(!_isDropDownOpen);
        e.Handled = true;
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e) => SetDropDownOpen(false); // light-dismiss / Esc

    private bool _closeIsCommit; // set by CommitAndClose just before its close; consumed (and always reset) below

    private void SetDropDownOpen(bool value)
    {
        var committed = _closeIsCommit;

        _closeIsCommit = false; // consume unconditionally — a stale flag must never leak into a later close

        if (!SetAndRaise(IsDropDownOpenProperty, ref _isDropDownOpen, value))
            return;

        PseudoClasses.Set(":open", value); // DirectProperty-backed, so the :open class is set imperatively (cf. MenuItem)
        _popup?.SetCurrentValue(MinWidthProperty, Bounds.Columns);
        _popup?.SetCurrentValue(Popup.IsOpenProperty, value);

        if (!value)
        {
            // The ONE close decision: a commit gesture (Enter / item click) commits the text; every dismissal
            // (Escape, light-dismiss, toggle, focus-out, detach) rolls the tentative match back to the pre-open
            // selection. The typed text itself survives a dismissal — only Escape reverts it (OnKeyDown).
            if (committed && IsEditable)
                CommitText();
            else
                RestoreTentativeSelection();

            _sessionSelectionMovedByUser = false; // the session is over either way
            RestoreFaceFocus(); // restore focus to the face / text box when the drop-down closes
            OnDropDownClosed(committed);
        }
        else
        {
            // Session snapshot FIRST (a derived OnDropDownOpened and the pre-seed both see/change selection state).
            _preOpenSelectedIndex = SelectedIndex;
            _preOpenText = _text ?? "";
            _typedText = _text ?? "";
            _sessionSelectionMovedByUser = false;

            OnDropDownOpened();

            // Pre-seed: EVERY open path (keyboard, drop-button, programmatic) highlights the item matching the
            // current text — highlight-only, so mere opening never rewrites the text or loses the committed
            // selection (searching from SelectedIndex keeps an exact-matching selection where it is).
            if (IsEditable && !string.IsNullOrEmpty(_text))
                RunEditTextSearch(_text!, allowCompletion: false);
        }
    }

    /// <summary>CLR sugar over <see cref="DropDownOpenedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? DropDownOpened
    {
        add => AddHandler(DropDownOpenedEvent, value!);
        remove => RemoveHandler(DropDownOpenedEvent, value!);
    }

    /// <summary>CLR sugar over <see cref="DropDownClosedEvent"/>.</summary>
    public event EventHandler<DropDownClosedEventArgs>? DropDownClosed
    {
        add => AddHandler(DropDownClosedEvent, value!);
        remove => RemoveHandler(DropDownClosedEvent, value!);
    }

    /// <summary>
    /// Called after the drop-down opens (the <see cref="IsDropDownOpen"/> transition to <see langword="true"/>, any
    /// path — keyboard, face click, programmatic). A derived control snapshots per-session state here (a
    /// <c>BarComboBox</c> captures the pre-open selection so a dismissal can restore it).
    /// </summary>
    protected virtual void OnDropDownOpened()
    {
        RaiseEvent(new RoutedEventArgs(DropDownOpenedEvent, this));
    }

    /// <summary>
    /// Called after the drop-down closes. <paramref name="committed"/> distinguishes the commit gestures — Enter or
    /// (non-editable) Space on the open list, or a click on a <see cref="ComboBoxItem"/> — from every dismissal
    /// (Escape, light-dismiss, focus-out, a face / drop-button toggle, detach). Selection-follows-highlight means the
    /// selection ALREADY reflects the last highlight either way; a derived control that must treat highlighting as
    /// tentative (a <c>BarComboBox</c> driving a live preview through an <see cref="IValueCommandParameter"/>)
    /// commits or rolls back here.
    /// </summary>
    /// <param name="committed">Whether the close was a commit gesture (Enter / Space / item click) rather than a dismissal.</param>
    protected virtual void OnDropDownClosed(bool committed)
    {
        RaiseEvent(new DropDownClosedEventArgs(DropDownClosedEvent, this, committed));
    }

    private void RestoreFaceFocus()
    {
        if (IsEditable && _editableTextBox is { } box)
            box.Focus();
        else
            Focus();
    }

    // ── editable text ↔ selection (WPF model) ───────────────────────────────────────────────────────────

    private void SetText(string? value, bool fromUser, bool moveCaretToEnd = false, bool fromPaste = false)
    {
        value ??= "";
        var previousTyped = _typedText;
        var previousText = _text ?? ""; // the text as it stood BEFORE this write (the auto-open Escape baseline)
        _userEditing = fromUser; // the user typed (uncommitted) vs. a programmatic / selection / commit sync that replaces it
        if (fromUser)
            _typedText = value; // a user edit consumed/overwrote any completion trailer: the box text IS the typed text

        var changed = SetAndRaise(TextProperty, ref _text, value);
        if (changed)
            PushTextToBox(moveCaretToEnd); // keep the part in sync (no-op when the user is the source)

        // Typing may open the drop-down (the "type to filter" affordance) when opted in.
        if (fromUser && IsEditable && StaysOpenOnEdit && !_isDropDownOpen && value.Length > 0)
        {
            SetDropDownOpen(true);

            // The session was opened BY this keystroke, so the open-time snapshot ran after the new text landed
            // and captured the first typed character as its baseline. Escape must revert to the text as it was
            // before the keystroke that started the session.
            _preOpenText = previousText;
        }

        if (fromUser && _isDropDownOpen && !_userSelectionInProgress && !fromPaste)
        {
            // Complete (append the selected trailer) only on forward typing at the end of the text: a truncation
            // (Backspace/Delete) or a mid-string edit still re-highlights, but appending there would make the
            // deleted text un-deletable. Paste never searches at all (the base type-ahead's FromPaste rule).
            var completes = !previousTyped.StartsWith(value, StringComparison.Ordinal) &&
                            _editableTextBox is { } box && box.CaretIndex == value.Length;
            RunEditTextSearch(value, allowCompletion: completes);
        }
        else if (!fromUser && changed)
        {
            RestoreTentativeSelection(); // an external Text write supersedes the tentative match
        }
    }

    private void PushTextToBox(bool moveCaretToEnd)
    {
        if (_editableTextBox is null)
            return;

        _syncingText = true;
        try
        {
            _editableTextBox.SetCurrentValue(TextBox.TextProperty, _text ?? "");

            // List navigation and a commit replace the text wholesale — the caret belongs at the end. Any other
            // programmatic sync leaves it where the TextBox clamps it (an app rewrite must not yank a mid-string caret).
            if (moveCaretToEnd)
                _editableTextBox.CaretIndex = _editableTextBox.Text.Length;
        }
        finally
        {
            _syncingText = false;
        }
    }

    private void OnEditableTextChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingText || _editableTextBox is null)
            return;

        // The user typed — adopt it as free text. Paste provenance rides the box's in-edit flag (the event is
        // raised synchronously inside the edit funnel), so pasted text never drives the search.
        SetText(_editableTextBox.Text, fromUser: true, fromPaste: _editableTextBox.IsApplyingPasteInput);
    }

    // Match the typed text to an item (exact, case-insensitive) and commit it; no match keeps the free text + clears
    // the selection (WPF editable semantics). Used on Enter and focus-loss.
    private void CommitText()
    {
        var match = -1;

        // The tentative text-match selection is trusted only while it still speaks for the text (a programmatic
        // selection change mid-edit clears the flag, and this equality check is the belt to that suspender).
        if (_selectionFromTextMatch && SelectedIndex >= 0 &&
            string.Equals(ItemText(ItemFromIndex(SelectedIndex)), _text, StringComparison.OrdinalIgnoreCase))
        {
            match = SelectedIndex;
        }
        else
        {
            for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
            {
                if (string.Equals(ItemText(ItemFromIndex(i)), _text, StringComparison.OrdinalIgnoreCase))
                {
                    match = i;
                    break;
                }
            }
        }

        _selectionFromTextMatch = false; // consumed either way — a commit ends the tentative session

        _committing = true;

        try
        {
            Selection.Select(match); // −1 clears the selection (free text); else selects the exact match
        }
        finally
        {
            _committing = false;
        }

        _userEditing = false;

        if (match >= 0)
            SetText(ItemText(ItemFromIndex(match)), fromUser: false, moveCaretToEnd: true); // normalize the casing to the item's display (WPF parity)

        _typedText = _text ?? ""; // the committed text is the new typing baseline
    }

    private void OnSelectionChangedSync(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionBox(); // the read-only face value follows the selection (both modes)

        if (_textMatchSelectionSync)
            return; // the edit-text search's own tentative select / snapshot restore: highlight only, never a text echo

        _selectionFromTextMatch = false; // any other selection change supersedes the tentative match
        if (_isDropDownOpen)
            _sessionSelectionMovedByUser = _userSelectionInProgress; // a gesture moved it; a programmatic change is the app's call and stands

        // Editable: the face text follows the selection (list navigation / a programmatic select) — but NOT while
        // committing free text (even for a re-entrant gesture — a handler inside a gesture scope may select
        // programmatically), and NOT while the user has uncommitted typed text (a background model change that
        // merely drops the old selection must not wipe what the user is typing — WPF parity).
        if (IsEditable && !_committing && (_userSelectionInProgress || !_userEditing))
            SetText(ItemText(SelectedItem), fromUser: false, moveCaretToEnd: true);
    }

    protected override void SelectByGesture(int index, KeyModifiers modifiers)
    {
        var wasUserSelectionInProgress = _userSelectionInProgress;

        _userSelectionInProgress = true;

        try
        {
            base.SelectByGesture(index, modifiers);
        }
        finally
        {
            _userSelectionInProgress = wasUserSelectionInProgress; // save/restore — a nested gesture must not clear an outer scope
        }
    }

    // Computes the value shown in the (non-editable) face. The SelectedItem, but a ComboBoxItem (its own container)
    // is a LIVE element already hosted in the drop-down; a UIElement cannot live in two places, so the face's
    // ContentPresenter would steal it from the list and mouse interaction with the face would drive the stolen
    // container. Unwrap a ComboBoxItem to its content; fall back to a text representation for any remaining
    // UIElement (a UIElement-valued content, or a raw UIElement item) so the face never hosts a live element.
    private void UpdateSelectionBox()
    {
        var value = SelectedItem;
        if (value is ComboBoxItem item)
            value = item.Content;
        if (value is UIElement element)
            value = ItemText(element);
        SetAndRaise(SelectionBoxItemProperty, ref _selectionBoxItem, value);
    }

    private string ItemText(object? item)
    {
        if (item is ComboBoxItem cbi)
            item = cbi.Content; // a ComboBoxItem displays its content, not the container's type name (editable face / match / seed)
        if (item is null)
            return "";
        if (TextSearch.GetTextPath(this) is { Length: > 0 } path)
            return TextSearch.EvaluatePath(item, path) ?? "";
        return item.ToString() ?? "";
    }

    private static void OnIsEditableChanged(UIObject sender, bool oldValue, bool newValue)
        => (sender as ComboBox)?.UpdateEditableState();

    private static void OnDropDownPlacementChanged(UIObject sender, PlacementMode oldValue, PlacementMode newValue)
    {
        if (sender is ComboBox { _popup: { } popup })
            popup.Placement = newValue;
    }

    private void UpdateEditableState()
    {
        var editable = IsEditable; // the combo stays the single tab stop in both modes (the box part sets IsTabStop=false)

        if (_contentSite is not null)
            _contentSite.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

        if (_editableTextBox is not null)
        {
            _editableTextBox.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;

            if (editable)
            {
                if (string.IsNullOrEmpty(_text))
                    SetText(ItemText(SelectedItem), fromUser: false); // seed the editable text from the selection
                PushTextToBox(moveCaretToEnd: false); // GUARDED sync to the (possibly freshly-realized) box — never mistaken for a user edit
            }
        }

        // A runtime IsEditable flip while focused must move the caret: into the text box when turning editable, back
        // to the face when leaving it (OnGotFocus only delegates on a real focus event, not a property change).
        if (editable && IsFocused && _editableTextBox is { } box)
            box.Focus();
        else if (!editable && _editableTextBox is { IsFocused: true })
            Focus();
    }

    // ── keyboard ────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        var editable = IsEditable;

        if (!_isDropDownOpen)
        {
            if (e.Key == Key.Enter && editable)
            {
                CommitText(); // commit the free text without opening
                e.Handled = true;
                return;
            }

            // A SIDE placement (Left/Right — a combo folded into a vertical overflow menu, per the Toolbar) opens only
            // on the arrow toward its flyout; Down/Up are left UNHANDLED so they bubble to the parent menu's row
            // navigation (otherwise the combo swallows Down and you can't move past it to the lower menu items). The
            // default Bottom placement opens on Down/Up/F4 as usual (WPF parity).
            var placement = DropDownPlacement;
            var opens = e.Key switch
            {
                Key.F4 or Key.Enter => true,
                Key.DownArrow or Key.UpArrow => placement is not (PlacementMode.Left or PlacementMode.Right),
                Key.LeftArrow => placement is PlacementMode.Left,
                Key.RightArrow => placement is PlacementMode.Right,
                _ => !editable && IsSpace(e), // Space opens only in non-editable mode (it types in editable)
            };

            if (!opens)
                return; // not a combo-open gesture (e.g. Down/Up under a side placement) → let it bubble to the menu

            SetDropDownOpen(true);
            e.Handled = true;
            return;
        }

        var count = ItemContainerGenerator.ContainerCount;

        switch (e.Key)
        {
            case Key.DownArrow when count > 0:
                MoveSelection(SelectedIndex < 0 ? 0 : Math.Min(count - 1, SelectedIndex + 1));
                break;
            case Key.UpArrow when count > 0:
                MoveSelection(SelectedIndex < 0 ? 0 : Math.Max(0, SelectedIndex - 1));
                break;
            // Side-placement back-out (a combo folded into a vertical overflow menu): the arrow OPPOSITE the flyout
            // (Right for a Left placement, Left for a Right placement) closes the list and returns focus to the face,
            // menu-like — mirroring the drop-opener buttons' back arrow.
            case Key.RightArrow when !editable && DropDownPlacement is PlacementMode.Left:
            case Key.LeftArrow when !editable && DropDownPlacement is PlacementMode.Right:
                SetDropDownOpen(false);
                Focus(FocusNavigationMethod.Directional); // return focus to the combo face
                break;
            // Non-editable Bottom/Top placement: Left/Right also move next/prev so a horizontally-laid-out drop-down (a
            // BarGallery's WrapPanel of swatches) is traversable by arrow in its flow direction, not only by Up/Down.
            // (Editable keeps Left/Right for the text caret; a side placement uses them to back out, above.)
            case Key.RightArrow when count > 0 && !editable && DropDownPlacement is not (PlacementMode.Left or PlacementMode.Right):
                MoveSelection(SelectedIndex < 0 ? 0 : Math.Min(count - 1, SelectedIndex + 1));
                break;
            case Key.LeftArrow when count > 0 && !editable && DropDownPlacement is not (PlacementMode.Left or PlacementMode.Right):
                MoveSelection(SelectedIndex < 0 ? 0 : Math.Max(0, SelectedIndex - 1));
                break;
            case Key.Home when count > 0 && !editable:
                MoveSelection(0);
                break;
            case Key.End when count > 0 && !editable:
                MoveSelection(count - 1);
                break;
            case Key.PageUp when count > 0 && !editable:
                MoveSelection(Math.Max(0, (SelectedIndex < 0 ? 0 : SelectedIndex) - ItemsPerPage()));
                break;
            case Key.PageDown when count > 0 && !editable:
                MoveSelection(Math.Min(count - 1, (SelectedIndex < 0 ? 0 : SelectedIndex) + ItemsPerPage()));
                break;
            case Key.Enter:
                CommitAndClose(); // the commit-close: the close policy commits the text/tentative match (editable)
                break;
            case Key.Escape:
                // Capture before the close consumes the session: revert to the pre-open selection's text, or —
                // when nothing was selected at open — to the pre-open (possibly free) text itself.
                var revertText = _preOpenSelectedIndex >= 0 && _preOpenSelectedIndex < ItemContainerGenerator.ContainerCount
                    ? ItemText(ItemFromIndex(_preOpenSelectedIndex))
                    : _preOpenText;
                var restoreSelection = editable && _sessionSelectionMovedByUser;
                SetDropDownOpen(false); // a dismissal — the close policy rolls back a tentative text match
                if (editable)
                {
                    if (restoreSelection)
                        SelectSnapshotIndex(); // Escape abandons the WHOLE session: a navigated selection rolls back too
                    SetText(revertText, fromUser: false, moveCaretToEnd: true);
                }
                break;
            default:
                if (editable || !IsSpace(e))
                    return;

                CommitAndClose(); // non-editable Space accepts the highlighted item, like Enter (WPF parity)
                break;
        }

        e.Handled = true;
    }

    // Selects `index` as the TENTATIVE text-match highlight: the selection moves (selection-follows, so the list
    // highlight and any preview track it) but the text is not echoed back (the sync guard) — the typed text stays
    // the source of truth until a commit adopts the match or a dismissal restores the pre-open selection.
    private void TentativelySelect(int index)
    {
        _textMatchSelectionSync = true;
        try
        {
            Selection.Select(index);
        }
        finally
        {
            _textMatchSelectionSync = false;
        }

        _selectionFromTextMatch = true;
        _sessionSelectionMovedByUser = true;
    }

    // Rolls a tentative text-match selection back to the pre-open snapshot (dismissal / no-match / external write).
    private void RestoreTentativeSelection()
    {
        if (!_selectionFromTextMatch)
            return;

        _selectionFromTextMatch = false;
        SelectSnapshotIndex();
    }

    // Selects the pre-open snapshot index (echo-guarded; −1 when the snapshot item is gone).
    private void SelectSnapshotIndex()
    {
        var index = _preOpenSelectedIndex;
        if (index >= ItemContainerGenerator.ContainerCount)
            index = -1; // the snapshot item is gone (the items changed mid-session)

        _textMatchSelectionSync = true;
        try
        {
            Selection.Select(index);
        }
        finally
        {
            _textMatchSelectionSync = false;
        }
    }

    // The edit-text search: matches the typed text against the items (prefix, stateless — each call searches the
    // FULL current text, so there is no type-ahead accumulator to go stale) and tentatively highlights the match.
    // With `allowCompletion`, also appends the matched item's trailer to the box, selected back-to-front so the
    // caret sits right after the typed prefix and the next keystroke overwrites it ("al" + Alpha → al|[pha]); the
    // typed prefix keeps the user's casing until commit normalizes it.
    private void RunEditTextSearch(string typed, bool allowCompletion)
    {
        if (!IsEditable || !IsTextSearchEnabled)
            return;

        var match = typed.Length == 0
            ? -1
            : TextSearchMatcher.FindMatchIndex(ItemContainerGenerator.ContainerCount,
                                               i => ItemText(ItemFromIndex(i)),
                                               SelectedIndex, // search starts AT the selection, so an exact-matching selection stays put
                                               typed,
                                               repeat: false,
                                               IsTextSearchCaseSensitive);

        if (match < 0)
        {
            RestoreTentativeSelection(); // the text stopped matching: the highlight returns to the pre-open selection
            return;
        }

        TentativelySelect(match);

        if (allowCompletion && _editableTextBox is { } box)
        {
            var itemText = ItemText(ItemFromIndex(match));
            if (itemText.Length > typed.Length)
            {
                var completed = typed + itemText[typed.Length..];

                _syncingText = true;
                try
                {
                    box.SetCurrentValue(TextBox.TextProperty, completed);
                    box.SelectRange(anchor: completed.Length, caret: typed.Length);
                }
                finally
                {
                    _syncingText = false;
                }

                // Mirror the box directly — not SetText: the trailer is not a user edit (it must not re-search or
                // become the typing baseline) and not a programmatic write (it must not undo its own highlight).
                SetAndRaise(TextProperty, ref _text, completed);
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        // Focus left the control AND its drop-down (IsKeyboardFocusWithin covers the popup, whose Child is a logical
        // descendant): close the drop-down so it never lingers open with focus elsewhere, and — when editable — commit
        // the typed text. Moving focus INTO the drop-down keeps IsKeyboardFocusWithin true, so it stays open.
        if (IsKeyboardFocusWithin)
            return;

        if (IsDropDownOpen)
            SetDropDownOpen(false); // a dismissal — the tentative highlight rolls back…
        if (IsEditable)
            CommitText(); // …and the commit then adopts exactly what the box visibly holds (trailer included)
    }

    // Selection-follows-highlight while open: change the selection (the face updates live). Non-editable also focuses
    // the container for the :focus-visible cue; editable keeps focus in the text box (the selection drives its text).
    private void MoveSelection(int index)
    {
        var wasUserSelectionInProgress = _userSelectionInProgress;

        _userSelectionInProgress = true;

        try
        {
            _userEditing = false; // navigating the list is a deliberate gesture: let the selection drive the face text
            Selection.Select(index);

            if (!IsEditable)
                ItemContainerGenerator.ContainerFromIndex(index)?.Focus(FocusNavigationMethod.Directional);
        }
        finally
        {
            _userSelectionInProgress = wasUserSelectionInProgress;
        }
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); Key.Space is only NUL→Ctrl+Space.
    private static bool IsSpace(KeyEventArgs e)
        => e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' ');
}
