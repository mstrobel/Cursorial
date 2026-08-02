using System.Text;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering.Text;
using Cursorial.UI.Input;

// ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

namespace Cursorial.UI.Controls;

/// <summary>
/// A single-line editable text field (design doc §12.7 / spec-controls "TextBox"). The caret is the
/// <b>real terminal cursor</b> (a <see cref="CursorShape.BlinkingBar"/> published by the
/// <see cref="TextPresenter"/> through S1's caret service — zero re-raster per blink phase, assistive-tech
/// semantics). Caret offsets are pinned to grapheme-cluster boundaries and all horizontal math is in
/// display columns, so a wide cluster occupies two columns and the caret never lands inside a glyph.
/// <para>
/// <see cref="Text"/> is two-way by default and pushes to its binding source <b>per change</b> (the pinned
/// default — validation-reactive UI like a SaveDialog's <c>IsEnabled</c> reacts per keystroke; §3.9). The
/// caret/selection (<see cref="CaretIndex"/>, <see cref="SelectionStart"/>, <see cref="SelectionLength"/>,
/// <see cref="SelectedText"/>) are imperative CLR state, not styleable properties. Clipboard Copy/Cut route
/// through <see cref="UIApplication.Clipboard"/> (OSC 52); paste arrives primarily as the terminal's own
/// paste (<c>TextInput{FromPaste}</c>).
/// </para>
/// <para>
/// <b>Undo/redo</b> (Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, or <see cref="Undo"/>/<see cref="Redo"/>) is an edit-based
/// history: typed runs and same-direction delete runs coalesce into one unit; a caret move, paste, cut,
/// selection-replace, newline, tab, or focus loss seals it. <see cref="IsUndoEnabled"/> /
/// <see cref="UndoLimit"/> gate it (<see cref="PasswordBox"/> forces it off — no plaintext history). A
/// programmatic <see cref="Text"/> set or a binding source push resets the history (WPF parity). Edits never
/// corrupt the text under undo: a stale entry is safely discarded. <em>Caveat:</em> a two-way binding to a
/// source that normalizes per change stays undoable (the actual landed text is recorded), but one that
/// normalizes on a deferred <c>LostFocus</c>/<c>Explicit</c> trigger resets undo when the normalized value
/// echoes back on focus loss.
/// </para>
/// <para>
/// <b>Multi-line</b> is opt-in: <see cref="AcceptsReturn"/> makes <c>Enter</c> insert a newline and
/// <see cref="TextWrapping"/> soft-wraps long lines to the field width (either sets <see cref="IsMultiLine"/>).
/// A multi-line field reserves <see cref="MinLines"/>–<see cref="MaxLines"/> rows then scrolls; the caret
/// navigates by visual line (Up/Down/PageUp/PageDown with a sticky desired column, per-line Home/End,
/// Ctrl+Home/End to the document) over a <see cref="TextLayout"/>.
/// </para>
/// </summary>
[TemplatePart("PART_TextPresenter", typeof(TextPresenter), IsRequired = true)]
public class TextBox : Control
{
    /// <summary>The field text. Two-way by default with per-change source push; <c>:empty</c> when blank. AffectsMeasure.</summary>
    public static readonly StyledProperty<string> TextProperty =
        UIProperty.Register<TextBox, string>(nameof(Text), defaultValue: "", changed: OnTextChanged);

    /// <summary>Whether the field rejects edits (typing/delete/cut) while staying navigable + copyable (<c>:readonly</c>).</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        UIProperty.Register<TextBox, bool>(nameof(IsReadOnly));

    /// <summary>The maximum text length in chars (0 = unlimited). Insertions are trimmed at a cluster boundary.</summary>
    public static readonly StyledProperty<int> MaxLengthProperty =
        UIProperty.Register<TextBox, int>(nameof(MaxLength));

    /// <summary>Placeholder text shown (faint/muted) while the field is empty.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        UIProperty.Register<TextBox, string?>(nameof(Placeholder), changed: OnPlaceholderChanged);

    /// <summary>The selection-highlight brush (themed to <c>Theme.SelectionBrush</c>; NoColor tier uses Inverse).</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        UIProperty.Register<TextBox, IBrush?>(nameof(SelectionBrush), changed: OnSelectionBrushChanged);

    /// <summary>How long lines are laid out: <see cref="WrapMode.NoWrap"/> (horizontal scroll) or
    /// <see cref="WrapMode.WordWrap"/>/<see cref="WrapMode.CharacterWrap"/> (soft wrap to the field width). With
    /// <see cref="AcceptsReturn"/> this is the multi-line gate; hard <c>\n</c> breaks are always honored when
    /// <see cref="AcceptsReturn"/> is set. AffectsMeasure.</summary>
    public static readonly StyledProperty<WrapMode> TextWrappingProperty =
        UIProperty.Register<TextBox, WrapMode>(nameof(TextWrapping), defaultValue: WrapMode.NoWrap);

    /// <summary>When <see langword="true"/>, <c>Enter</c> inserts a newline (multi-line editing). When
    /// <see langword="false"/> (the default), <c>Enter</c> commits and bubbles for IsDefault / form submit (§13).</summary>
    public static readonly StyledProperty<bool> AcceptsReturnProperty =
        UIProperty.Register<TextBox, bool>(nameof(AcceptsReturn));

    /// <summary>When <see langword="true"/>, <c>Tab</c> inserts a tab character; otherwise (the default) <c>Tab</c>
    /// moves focus (§7.7 directional navigation).</summary>
    public static readonly StyledProperty<bool> AcceptsTabProperty =
        UIProperty.Register<TextBox, bool>(nameof(AcceptsTab));

    /// <summary>The minimum height the field reserves, in text lines (default 1). AffectsMeasure.</summary>
    public static readonly StyledProperty<int> MinLinesProperty =
        UIProperty.Register<TextBox, int>(nameof(MinLines), defaultValue: 1);

    /// <summary>The maximum height before the field scrolls vertically, in text lines (default unbounded). AffectsMeasure.</summary>
    public static readonly StyledProperty<int> MaxLinesProperty =
        UIProperty.Register<TextBox, int>(nameof(MaxLines), defaultValue: int.MaxValue);

    /// <summary>Whether edits are recorded for undo/redo (default <see langword="true"/>; <see cref="PasswordBox"/>
    /// forces it off). Setting it <see langword="false"/> discards the existing history.</summary>
    public static readonly StyledProperty<bool> IsUndoEnabledProperty =
        UIProperty.Register<TextBox, bool>(nameof(IsUndoEnabled), defaultValue: true, changed: OnIsUndoEnabledChanged);

    /// <summary>The maximum number of undo units retained (default <c>-1</c> = unlimited, WPF parity; <c>0</c>
    /// disables recording; <c>&gt;0</c> caps the history, dropping the oldest units first).</summary>
    public static readonly StyledProperty<int> UndoLimitProperty =
        UIProperty.Register<TextBox, int>(nameof(UndoLimit), defaultValue: -1, changed: OnUndoLimitChanged);

    /// <summary>The bubbling event raised whenever <see cref="Text"/> changes.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> TextChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(TextChanged), RoutingStrategy.Bubble, typeof(TextBox));

    /// <summary>The bubbling event raised whenever the selection or caret position changes — keyboard navigation,
    /// mouse drag, <see cref="SelectAll"/>, an edit that collapses the caret, or a programmatic
    /// <see cref="CaretIndex"/> / <see cref="SelectionStart"/> / <see cref="SelectionLength"/> set.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> SelectionChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(SelectionChanged), RoutingStrategy.Bubble, typeof(TextBox));

    /// <summary>The bubbling event raised before text is inserted from a paste — both the terminal's bracketed
    /// paste and an OSC 52 clipboard read. A handler vetoes the paste by setting <see cref="RoutedEventArgs.Handled"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> PastingFromClipboardEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(PastingFromClipboard), RoutingStrategy.Bubble, typeof(TextBox));

    /// <summary>The bubbling event raised before the selection is copied to the clipboard (OSC 52). A handler
    /// vetoes the copy by setting <see cref="RoutedEventArgs.Handled"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CopyingToClipboardEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(CopyingToClipboard), RoutingStrategy.Bubble, typeof(TextBox));

    /// <summary>The bubbling event raised before the selection is cut to the clipboard (OSC 52) and deleted. A
    /// handler vetoes the cut by setting <see cref="RoutedEventArgs.Handled"/>.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CuttingToClipboardEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(CuttingToClipboard), RoutingStrategy.Bubble, typeof(TextBox));

    private TextPresenter? _presenter;
    private int _caretIndex;       // the active end of the selection (where the caret blinks)
    private int _selectionAnchor;  // the fixed end of the selection (== caret when there is no selection)
    private bool _dragging;        // a left-button drag is extending the selection
    private int _desiredColumn = -1; // sticky target column for a run of vertical moves; -1 = recompute from the caret
    private bool _caretLineEndAffinity; // when the caret sits on a soft-wrap boundary, true == the earlier line's visual end

    // Undo/redo: edit-based history (each entry is a splice + the caret state to restore). _undo top is the last
    // element; a new edit clears _redo. _canCoalesce gates merging the next same-kind edit into the open unit;
    // _isApplyingEdit marks self-mutations (typing/delete/undo/redo) so OnTextChanged neither re-pins the caret nor
    // treats the change as an external set that resets the history.
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private bool _canCoalesce;
    private bool _isApplyingEdit;

    // How an edit folds into the undo history. Insert (typed run) and the two delete directions each coalesce with
    // their own kind; Other (paste / cut / replace-selection / newline / tab / Clear) is always its own atomic unit.
    // The two delete directions are distinct so a Backspace and a Delete at the same caret never merge into a unit
    // whose undo would reinsert text in the wrong order.
    private enum UndoKind { Insert, DeleteBackward, DeleteForward, Other }

    // One coalescible edit: replacing text[Start, Start+Removed.Length) with Inserted. *Before is the selection to
    // restore on undo; *After is the collapsed caret to restore on redo. Mutable so a coalescing run grows it in
    // place (only Removed/Inserted/Start/*After change — *Before stays from the first edit of the unit).
    private sealed class UndoEntry
    {
        public int Start;
        public string Removed = "";
        public string Inserted = "";
        public int CaretBefore;
        public int AnchorBefore;
        public int CaretAfter;
        public int AnchorAfter;
        public UndoKind Kind;
    }

    static TextBox()
    {
        FocusableProperty.OverrideDefaultValue<TextBox>(true); // an editable field takes keyboard focus
        BindsTwoWayByDefault<TextBox>(TextProperty);           // two-way is the binding default (§3.9)
        // :empty is driven directly (PseudoClassMapping only applies on a value CHANGE, never the initial
        // default — and the default Text "" must read as :empty for a never-edited field).
        PseudoClassMapping.Register<TextBox>(IsReadOnlyProperty, ":readonly");
        AffectsMeasure<TextBox>(TextProperty, TextWrappingProperty, MinLinesProperty, MaxLinesProperty);
    }

    /// <summary>Creates a text field (the mouse pointer is an i-beam over its content).</summary>
    public TextBox()
    {
        Cursor = MouseCursorShape.Text;
        PseudoClasses.Set(":empty", true); // Text defaults to empty
    }

    /// <inheritdoc cref="TextProperty"/>
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value ?? ""); }

    /// <inheritdoc cref="IsReadOnlyProperty"/>
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    /// <inheritdoc cref="MaxLengthProperty"/>
    public int MaxLength { get => GetValue(MaxLengthProperty); set => SetValue(MaxLengthProperty, value); }

    /// <inheritdoc cref="PlaceholderProperty"/>
    public string? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    /// <inheritdoc cref="SelectionBrushProperty"/>
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    /// <inheritdoc cref="TextWrappingProperty"/>
    public WrapMode TextWrapping { get => GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }

    /// <inheritdoc cref="AcceptsReturnProperty"/>
    public bool AcceptsReturn { get => GetValue(AcceptsReturnProperty); set => SetValue(AcceptsReturnProperty, value); }

    /// <inheritdoc cref="AcceptsTabProperty"/>
    public bool AcceptsTab { get => GetValue(AcceptsTabProperty); set => SetValue(AcceptsTabProperty, value); }

    /// <inheritdoc cref="MinLinesProperty"/>
    public int MinLines { get => GetValue(MinLinesProperty); set => SetValue(MinLinesProperty, value); }

    /// <inheritdoc cref="MaxLinesProperty"/>
    public int MaxLines { get => GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }

    /// <inheritdoc cref="IsUndoEnabledProperty"/>
    public bool IsUndoEnabled { get => GetValue(IsUndoEnabledProperty); set => SetValue(IsUndoEnabledProperty, value); }

    /// <inheritdoc cref="UndoLimitProperty"/>
    public int UndoLimit { get => GetValue(UndoLimitProperty); set => SetValue(UndoLimitProperty, value); }

    /// <summary>Whether edits are captured into the undo history. <see cref="PasswordBox"/> hard-overrides this to
    /// <see langword="false"/> (mirroring its Copy/Cut suppression) so no plaintext is ever retained, regardless of
    /// <see cref="IsUndoEnabled"/>.</summary>
    private protected virtual bool RecordsUndo => IsUndoEnabled;

    /// <summary>Whether there is an edit to undo (false while read-only, undo-disabled, or the history is empty).</summary>
    public bool CanUndo => RecordsUndo && !IsReadOnly && _undo.Count > 0;

    /// <summary>Whether there is an undone edit to redo.</summary>
    public bool CanRedo => RecordsUndo && !IsReadOnly && _redo.Count > 0;

    /// <summary>Whether the field lays out as multi-line: hard newlines accepted, soft wrap, or both.
    /// Single-line (the default) keeps the existing horizontal-scroll behavior.</summary>
    internal bool IsMultiLine => AcceptsReturn || TextWrapping != WrapMode.NoWrap;

    /// <summary>CLR sugar over <see cref="TextChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? TextChanged { add => AddHandler(TextChangedEvent, value!); remove => RemoveHandler(TextChangedEvent, value!); }

    /// <summary>CLR sugar over <see cref="SelectionChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? SelectionChanged { add => AddHandler(SelectionChangedEvent, value!); remove => RemoveHandler(SelectionChangedEvent, value!); }

    /// <summary>CLR sugar over <see cref="PastingFromClipboardEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? PastingFromClipboard { add => AddHandler(PastingFromClipboardEvent, value!); remove => RemoveHandler(PastingFromClipboardEvent, value!); }

    /// <summary>CLR sugar over <see cref="CopyingToClipboardEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? CopyingToClipboard { add => AddHandler(CopyingToClipboardEvent, value!); remove => RemoveHandler(CopyingToClipboardEvent, value!); }

    /// <summary>CLR sugar over <see cref="CuttingToClipboardEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? CuttingToClipboard { add => AddHandler(CuttingToClipboardEvent, value!); remove => RemoveHandler(CuttingToClipboardEvent, value!); }

    /// <summary>The caret position as a char offset (pinned to a grapheme-cluster boundary). Setting it collapses the selection.</summary>
    public int CaretIndex
    {
        get => _caretIndex;
        set => SetCaretAndSelection(value, value);
    }

    /// <summary>The selection start as a char offset (the lower of the anchor/caret). Setting it moves the selection, preserving length.</summary>
    public int SelectionStart
    {
        get => Math.Min(_selectionAnchor, _caretIndex);
        set => Select(value, SelectionLength);
    }

    /// <summary>The selection length in chars. Setting it re-selects from <see cref="SelectionStart"/>.</summary>
    public int SelectionLength
    {
        get => Math.Abs(_caretIndex - _selectionAnchor);
        set => Select(SelectionStart, Math.Max(0, value));
    }

    /// <summary>The selected text (empty when there is no selection). Setting it replaces the selection.</summary>
    public string SelectedText
    {
        get { var (start, end) = SelectionBounds; return Text[start..end]; }
        set => ReplaceCore(value ?? "", UndoKind.Other);
    }

    /// <summary>The selection as a normalized half-open <c>[start, end)</c> char range (consumed by the presenter).</summary>
    internal (int Start, int End) SelectionBounds
        => (Math.Min(_selectionAnchor, _caretIndex), Math.Max(_selectionAnchor, _caretIndex));

    /// <summary>The realized <c>PART_TextPresenter</c> (test observability; null before the template applies).</summary>
    internal TextPresenter? Presenter => _presenter;

    /// <summary>
    /// The caret's soft-wrap line affinity (consumed by the presenter when rendering): when the caret offset
    /// sits on a soft-wrap boundary (a wrapped line's content end coincident with the next line's start), this
    /// is <see langword="true"/> if the caret belongs to the earlier line's visual end (left by Down / End) and
    /// <see langword="false"/> if it belongs to the next line's start (the natural affinity of Right / Home /
    /// typing). It is meaningless off a soft-wrap boundary.
    /// </summary>
    internal bool CaretLineEndAffinity => _caretLineEndAffinity;

    // ───────────────────────────── display projection seam (PasswordBox masking) ─────────────────────────────
    // The presenter renders, lays out, scrolls, and measures against these — not the model directly — and the
    // pointer-hit maps back through them. The default is identity, so an unmasked TextBox is unchanged; a
    // PasswordBox overrides them to a per-cluster mask while the model (Text/caret/selection) stays plaintext.

    /// <summary>The text the presenter renders + measures (identity for a TextBox; masked for a PasswordBox).</summary>
    internal virtual string DisplayText => Text;

    /// <summary>Maps a model char offset to its offset in <see cref="DisplayText"/> (identity by default).</summary>
    internal virtual int ToDisplayIndex(int modelIndex) => modelIndex;

    /// <summary>Maps a <see cref="DisplayText"/> char offset back to the model (identity by default) — pointer hit.</summary>
    internal virtual int ToModelIndex(int displayIndex) => displayIndex;

    /// <summary>Selects the whole text (caret at the end).</summary>
    public void SelectAll() => SetCaretAndSelection(anchor: 0, caret: Text.Length);

    /// <summary>Clears the current selection (caret at the end).</summary>
    public void ClearSelection(bool caretToEnd = false)
    {
        if (caretToEnd)
            SetCaretAndSelection(anchor: Text.Length, caret: Text.Length);
        else
            SetCaretAndSelection(anchor: CaretIndex, caret: CaretIndex);
    }

    /// <summary>Clears the text (an undoable edit, WPF parity). No-op while read-only.</summary>
    public void Clear()
    {
        if (!IsReadOnly && Text.Length > 0)
            ApplyTextEdit(0, Text.Length, "", UndoKind.Other);
    }

    /// <summary>Reverts the most recent edit (or coalesced run of edits), restoring the prior selection. No-op when
    /// <see cref="CanUndo"/> is false.</summary>
    public void Undo()
    {
        if (!CanUndo)
            return;

        // Move the entry off the undo stack BEFORE applying it: ApplyReverse mutates Text, which can synchronously
        // re-enter Undo() (e.g. from a TextChanged handler); a popped-first stack keeps that re-entry consistent
        // and never double-pops. On a defensive mismatch ApplyReverse clears both stacks, so the move is moot.
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        _canCoalesce = false; // a fresh edit after an undo starts its own unit
        ApplyReverse(entry, redo: false);
    }

    /// <summary>Re-applies the most recently undone edit. No-op when <see cref="CanRedo"/> is false.</summary>
    public void Redo()
    {
        if (!CanRedo)
            return;

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        _canCoalesce = false;
        ApplyReverse(entry, redo: true);
    }

    // ───────────────────────────── template wiring ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _presenter = GetTemplatePart<TextPresenter>("PART_TextPresenter");
        RefreshPresenter();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        _presenter = null;
        base.OnTemplateDetaching(old);
    }

    // ───────────────────────────── focus (drives the caret) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        RefreshPresenter(); // show the caret
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _canCoalesce = false; // a focus boundary seals the current typing/delete undo unit (WPF parity)
        RefreshPresenter();   // hide the caret
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var ctrl = (e.Modifiers & KeyModifiers.Control) != 0;
        var shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        var layout = GraphemeLayout.Build(Text);

        switch (e.Key)
        {
            case Key.LeftArrow:
                MoveCaret(ctrl ? TextNavigation.PrevWord(Text, _caretIndex) : layout.PrevBoundary(_caretIndex), shift);
                break;
            case Key.RightArrow:
                MoveCaret(ctrl ? TextNavigation.NextWord(Text, _caretIndex) : layout.NextBoundary(_caretIndex), shift);
                break;
            case Key.Home when !ctrl && IsMultiLine && _presenter is { } hp:
                MoveCaret(hp.LineStart(_caretIndex, _caretLineEndAffinity), shift); // per-line; start-affinity
                break;
            case Key.Home:
                MoveCaret(0, shift); // Ctrl, or single-line: document start
                break;
            case Key.End when !ctrl && IsMultiLine && _presenter is { } ep:
                var (endOffset, endAffinity) = ep.LineEnd(_caretIndex, _caretLineEndAffinity); // per-line
                MoveCaret(endOffset, shift, endAffinity);
                break;
            case Key.End:
                MoveCaret(Text.Length, shift); // Ctrl, or single-line: document end
                break;
            case Key.UpArrow when IsMultiLine && _presenter is { }:
                MoveVertical(-1, shift);
                break;
            case Key.DownArrow when IsMultiLine && _presenter is { }:
                MoveVertical(+1, shift);
                break;
            case Key.PageUp when IsMultiLine && _presenter is { } pu:
                MoveVertical(-Math.Max(1, pu.ViewportRows), shift);
                break;
            case Key.PageDown when IsMultiLine && _presenter is { } pd:
                MoveVertical(Math.Max(1, pd.ViewportRows), shift);
                break;
            case Key.Enter when AcceptsReturn && !IsReadOnly:
                ReplaceCore("\n", UndoKind.Other); // a real newline (its own undo unit); single-line leaves Enter unhandled (§13)
                break;
            case Key.Tab when AcceptsTab && !ctrl && !IsReadOnly:
                ReplaceCore("\t", UndoKind.Other); // its own undo unit; Ctrl+Tab still navigates focus out
                break;
            case Key.Backspace:
                DeleteBackward(ctrl);
                break;
            case Key.Delete when shift: // Shift+Delete = cut
                if (!Cut())
                    return; // nothing to cut (no selection / read-only) — bubbles, consistent with Ctrl+X
                break;
            case Key.Delete:
                DeleteForward(ctrl);
                break;
            case Key.Insert when ctrl: // Ctrl+Insert = copy
                if (!Copy())
                    return; // nothing to copy — leave unhandled (bubbles)
                break;
            case Key.Insert when shift: // Shift+Insert = paste
                Paste();
                break;
            case Key.Character when ctrl && IsLetter(e, 'a'):
                SelectAll();
                break;
            case Key.Character when ctrl && IsLetter(e, 'c'):
                if (!Copy())
                    return; // Ctrl+C with no selection is not consumed — bubbles (an app may bind quit)
                break;
            case Key.Character when ctrl && IsLetter(e, 'x'):
                if (!Cut())
                    return;
                break;
            case Key.Character when ctrl && IsLetter(e, 'v'):
                Paste();
                break;
            // Undo/redo arrive as Ctrl(+Shift)+letter character events (there is no Key.Z/Y enum — ND10). The
            // Ctrl+Shift+Z redo arm precedes the Ctrl+Z undo arm so a shifted Z is matched as redo, not undo.
            case Key.Character when ctrl && shift && IsLetter(e, 'z'):
                if (!CanRedo)
                    return; // nothing to redo — bubble (an app may bind Ctrl+Shift+Z)
                Redo();
                break;
            case Key.Character when ctrl && IsLetter(e, 'z'):
                if (!CanUndo)
                    return; // nothing to undo — bubble
                Undo();
                break;
            case Key.Character when ctrl && IsLetter(e, 'y'):
                if (!CanRedo)
                    return;
                Redo();
                break;
            default:
                return; // not ours — leave unhandled (Enter / Escape bubble for IsDefault / IsCancel, spec §13)
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || e.Text.Length == 0)
            return;

        if (IsReadOnly)
        {
            e.Handled = true; // a focused read-only field swallows typing rather than letting it bubble
            return;
        }

        InsertText(e.Text.ToString(), e.FromPaste);
        e.Handled = true;
    }

    // ───────────────────────────── mouse ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        Focus(FocusNavigationMethod.Pointer);
        var index = IndexFromPointer(e);

        if (e.ClickCount >= 3)
        {
            SelectAll();
        }
        else if (e.ClickCount == 2)
        {
            SelectWordAt(index);
        }
        else
        {
            SetCaretAndSelection(index, index);
            if (CaptureMouse())
                _dragging = true;
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        SetCaretAndSelection(_selectionAnchor, IndexFromPointer(e)); // extend toward the pointer
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButton.Left)
            return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        _dragging = false;
        base.OnLostMouseCapture(e);
    }

    // ───────────────────────────── editing core ─────────────────────────────

    private void MoveCaret(int newCaret, bool extend, bool lineEndAffinity = false)
    {
        if (extend)
            SetCaretAndSelection(_selectionAnchor, newCaret, lineEndAffinity); // keep the anchor, move the active end
        else
            SetCaretAndSelection(newCaret, newCaret, lineEndAffinity);          // collapse the selection at the caret
    }

    private void MoveVertical(int delta, bool extend)
    {
        if (_presenter is not { } presenter)
            return;

        var (newCaret, column, affinity) = presenter.MoveVertical(_caretIndex, delta, _desiredColumn, _caretLineEndAffinity);
        MoveCaret(newCaret, extend, affinity); // resets _desiredColumn via SetCaretAndSelection …
        _desiredColumn = column;               // … which we restore so the target column stays sticky across the run
    }

    private void SetCaretAndSelection(int anchor, int caret, bool lineEndAffinity = false)
    {
        _desiredColumn = -1; // any non-vertical caret op forgets the sticky column (MoveVertical restores it)
        if (!_isApplyingEdit)
            _canCoalesce = false; // an explicit caret move (nav, click, SelectAll, CaretIndex=) seals the undo unit
        var layout = GraphemeLayout.Build(Text);
        anchor = layout.PinToBoundary(Math.Clamp(anchor, 0, Text.Length));
        caret = layout.PinToBoundary(Math.Clamp(caret, 0, Text.Length));
        if (anchor == _selectionAnchor && caret == _caretIndex && lineEndAffinity == _caretLineEndAffinity)
            return;

        _selectionAnchor = anchor;
        _caretIndex = caret;
        _caretLineEndAffinity = lineEndAffinity; // set before the refresh so the presenter renders the right line
        RefreshPresenter();
        RaiseSelectionChanged(); // past the no-op guard: the anchor/caret actually moved
    }

    private void Select(int start, int length)
    {
        var layout = GraphemeLayout.Build(Text);
        start = layout.PinToBoundary(Math.Clamp(start, 0, Text.Length));
        var end = layout.PinToBoundary(Math.Clamp(start + Math.Max(0, length), 0, Text.Length));
        SetCaretAndSelection(anchor: start, caret: end);
    }

    private void InsertText(string input, bool fromPaste)
    {
        if (IsReadOnly)
            return;

        // Both paste paths reach the insert through here — the terminal's bracketed paste (TextInput{FromPaste})
        // and the OSC 52 read in CompletePasteAsync (which bypasses the TextInput pipeline) — so this single site
        // raises PastingFromClipboard before the insert on both; a handler vetoes by setting Handled.
        if (fromPaste && RaiseClipboardVeto(PastingFromClipboardEvent))
            return;

        var filtered = FilterInput(input, fromPaste);
        if (filtered.Length == 0)
            return;

        // Typed printable text coalesces into one undo unit; a paste, or typing that replaces a selection, is atomic.
        var kind = fromPaste || SelectionLength > 0 ? UndoKind.Other : UndoKind.Insert;
        ReplaceCore(filtered, kind);
    }

    // Replaces the current selection (or inserts at the collapsed caret) with replacement, recording it as kind.
    private void ReplaceCore(string replacement, UndoKind kind)
    {
        if (IsReadOnly)
            return;

        var (start, end) = SelectionBounds;
        replacement = TrimToMaxLength(replacement, removed: end - start, currentLength: Text.Length);
        ApplyTextEdit(start, end - start, replacement, kind);
    }

    private void DeleteBackward(bool word)
    {
        if (IsReadOnly)
            return;
        if (SelectionLength > 0) { ReplaceCore("", UndoKind.Other); return; } // deleting a selection is one atomic unit
        if (_caretIndex <= 0)
            return;

        var layout = GraphemeLayout.Build(Text);
        var from = word ? layout.PinToBoundary(TextNavigation.PrevWord(Text, _caretIndex)) : layout.PrevBoundary(_caretIndex);
        ApplyTextEdit(from, _caretIndex - from, "", UndoKind.DeleteBackward);
    }

    private void DeleteForward(bool word)
    {
        if (IsReadOnly)
            return;
        if (SelectionLength > 0) { ReplaceCore("", UndoKind.Other); return; }
        if (_caretIndex >= Text.Length)
            return;

        var layout = GraphemeLayout.Build(Text);
        var to = word ? layout.PinToBoundary(TextNavigation.NextWord(Text, _caretIndex)) : layout.NextBoundary(_caretIndex);
        ApplyTextEdit(_caretIndex, to - _caretIndex, "", UndoKind.DeleteForward);
    }

    // ───────────────────────────── text-mutation funnel + undo history ─────────────────────────────

    // The single splice primitive: replace Text[start, start+removedLen) with inserted, then record the edit for
    // undo and raise TextChanged. _isApplyingEdit (saved/restored for re-entrancy) spans the whole splice —
    // including the synchronous two-way binding write-back and any normalized echo it produces (BindingExpressionCore
    // re-reads the source in-band after the write) — so OnTextChanged neither re-pins the caret, clears the history,
    // nor raises the event mid-funnel. The edit is recorded AFTER the splice settles, so when a binding transforms
    // the text (e.g. a normalizing source), the recorded entry matches what actually lands in Text.
    private void ApplyTextEdit(int start, int removedLen, string inserted, UndoKind kind)
    {
        var current = Text;
        start = Math.Clamp(start, 0, current.Length);
        removedLen = Math.Clamp(removedLen, 0, current.Length - start);
        if (removedLen == 0 && inserted.Length == 0)
            return; // no-op (Backspace at 0, Delete at end, trimmed-to-nothing insert)

        var removed = current.Substring(start, removedLen);
        var caretBefore = _caretIndex;
        var anchorBefore = _selectionAnchor;
        var caretAfter = start + inserted.Length;
        var spliced = string.Concat(current.AsSpan(0, start), inserted, current.AsSpan(start + removedLen));

        var wasApplying = _isApplyingEdit;
        _isApplyingEdit = true;
        try
        {
            Text = spliced;
            SetCaretAndSelection(caretAfter, caretAfter); // collapse the caret; _isApplyingEdit keeps the unit open
        }
        finally
        {
            _isApplyingEdit = wasApplying;
        }

        var resulting = Text;
        if (string.Equals(resulting, current, StringComparison.Ordinal))
            return; // a binding (or value-equal splice) left the text unchanged — nothing to record or raise

        if (RecordsUndo && UndoLimit != 0)
        {
            if (string.Equals(resulting, spliced, StringComparison.Ordinal))
                RecordEdit(start, removed, inserted, kind, caretBefore, anchorBefore, caretAfter); // exact splice — coalescable
            else
                // An in-band listener (e.g. a normalizing two-way binding) transformed the text; record the actual
                // before→after as one atomic unit so undo restores the real prior text.
                RecordFullReplace(current, resulting, caretBefore, anchorBefore);
        }

        RaiseTextChanged();
    }

    // Records one edit into the undo history (coalescing into the open unit when eligible). The *Before selection
    // is captured by the funnel before the mutation; the *After caret is the collapsed post-edit caret.
    private void RecordEdit(int start, string removed, string inserted, UndoKind kind, int caretBefore, int anchorBefore, int caretAfter)
    {
        _redo.Clear(); // a new edit invalidates the redo branch

        if (_canCoalesce && _undo.Count > 0 && TryCoalesce(_undo[^1], start, removed, inserted, kind, caretAfter))
            return;

        _undo.Add(new UndoEntry
        {
            Start = start, Removed = removed, Inserted = inserted, Kind = kind,
            CaretBefore = caretBefore, AnchorBefore = anchorBefore,
            CaretAfter = caretAfter, AnchorAfter = caretAfter,
        });
        _canCoalesce = kind is UndoKind.Insert or UndoKind.DeleteBackward or UndoKind.DeleteForward; // Other never coalesces forward
        TrimUndoLimit();
    }

    // Records a whole-text before→after transform as one atomic, non-coalescing unit (used when a binding rewrites
    // the text out from under a precise splice). Undo restores `before`, redo restores `after`.
    private void RecordFullReplace(string before, string after, int caretBefore, int anchorBefore)
    {
        _redo.Clear();
        _undo.Add(new UndoEntry
        {
            Start = 0, Removed = before, Inserted = after, Kind = UndoKind.Other,
            CaretBefore = caretBefore, AnchorBefore = anchorBefore,
            CaretAfter = _caretIndex, AnchorAfter = _caretIndex,
        });
        _canCoalesce = false;
        TrimUndoLimit();
    }

    // Tries to fold a new edit into the open unit. Only a same-kind, contiguous edit merges; the *Before fields are
    // never touched (they stay from the unit's first edit, so undo restores the run's original caret).
    private static bool TryCoalesce(UndoEntry top, int start, string removed, string inserted, UndoKind kind, int caretAfter)
    {
        switch (kind)
        {
            case UndoKind.Insert when top.Kind == UndoKind.Insert
                                      && removed.Length == 0 && start == top.Start + top.Inserted.Length:
                top.Inserted += inserted; // contiguous typing
                break;
            case UndoKind.DeleteBackward when top.Kind == UndoKind.DeleteBackward
                                              && inserted.Length == 0 && start + removed.Length == top.Start:
                top.Removed = removed + top.Removed; // backspace extends the run leftward
                top.Start = start;
                break;
            case UndoKind.DeleteForward when top.Kind == UndoKind.DeleteForward
                                             && inserted.Length == 0 && start == top.Start:
                top.Removed += removed; // forward-delete extends the run rightward (caret fixed)
                break;
            default:
                return false; // kind switch, direction switch, or non-contiguous ⇒ start a new unit
        }

        top.CaretAfter = top.AnchorAfter = caretAfter;
        return true;
    }

    // Reverses (undo) or re-applies (redo) one entry. Defensive: the recorded region must still hold the expected
    // text; if a stale offset would otherwise corrupt the document, abandon the entry and clear the history.
    private bool ApplyReverse(UndoEntry entry, bool redo)
    {
        var current = Text;
        var (expected, replacement, caret, anchor) = redo
            ? (entry.Removed, entry.Inserted, entry.CaretAfter, entry.AnchorAfter)
            : (entry.Inserted, entry.Removed, entry.CaretBefore, entry.AnchorBefore);

        if (entry.Start < 0 || entry.Start + expected.Length > current.Length
            || !current.AsSpan(entry.Start, expected.Length).SequenceEqual(expected))
        {
            ClearUndoHistory(); // the document no longer matches the recorded shape (e.g. an external normalization)
            return false;
        }

        var newText = string.Concat(current.AsSpan(0, entry.Start), replacement, current.AsSpan(entry.Start + expected.Length));
        var wasApplying = _isApplyingEdit;
        _isApplyingEdit = true;
        try
        {
            Text = newText;
            SetCaretAndSelection(anchor, caret); // restore the recorded selection (undo) / collapsed caret (redo)
        }
        finally
        {
            _isApplyingEdit = wasApplying;
        }

        if (!string.Equals(Text, current, StringComparison.Ordinal))
            RaiseTextChanged(); // after the funnel settles — handlers see the restored caret/selection, not a mid-edit state
        return true;
    }

    private void TrimUndoLimit()
    {
        var limit = UndoLimit;
        if (limit < 0)
            return; // unlimited

        while (_undo.Count > limit)
            _undo.RemoveAt(0); // drop the oldest unit

        if (_undo.Count == 0)
            _canCoalesce = false; // nothing left to coalesce into
    }

    private void ClearUndoHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _canCoalesce = false;
    }

    // ───────────────────────────── clipboard ─────────────────────────────

    // Returns whether the gesture was consumed (there was a selection to copy).
    /// <summary>Copies the selection to the clipboard. Returns false when there is nothing to copy (the chord
    /// bubbles). Overridden by <see cref="PasswordBox"/> to suppress copying the plaintext.</summary>
    private protected virtual bool Copy()
    {
        var text = SelectedText;
        if (text.Length == 0)
            return false;

        if (RaiseClipboardVeto(CopyingToClipboardEvent))
            return true; // a handler vetoed the copy — the gesture is still consumed (there was a selection)

        UIApplication.Current?.Clipboard.SetText(text);
        return true;
    }

    /// <summary>Cuts the selection to the clipboard. Returns false when there is nothing to cut. Overridden by
    /// <see cref="PasswordBox"/> to suppress.</summary>
    private protected virtual bool Cut()
    {
        if (IsReadOnly || SelectionLength == 0)
            return false;

        if (RaiseClipboardVeto(CuttingToClipboardEvent))
            return true; // a handler vetoed the cut — the gesture is still consumed, but no write/delete happens

        UIApplication.Current?.Clipboard.SetText(SelectedText);
        ReplaceCore("", UndoKind.Other);
        return true;
    }

    /// <summary>How long a paste chord waits for the terminal's OSC 52 reply — generous because supporting
    /// terminals often interpose a user permission prompt (Kitty asks per read); a denied or unsupported
    /// read completes null and the chord stays a quiet no-op.</summary>
    private static readonly TimeSpan PasteReadTimeout = TimeSpan.FromSeconds(2);

    // At most one OSC 52 read is in flight per box: OSC 52 carries no request id, so a device response completes
    // EVERY pending read with the same text — a held Ctrl+V or an impatient double-tap during the (human-scale)
    // permission-prompt window would otherwise fan one clipboard value into several duplicated insertions.
    private bool _pasteReadInFlight;

    private void Paste()
    {
        // The terminal's own paste (bracketed ⇒ TextInput{FromPaste}) is the PRIMARY inbound path and needs
        // no chord — this is the OSC 52 read fallback for users who type Ctrl+V / Shift+Insert at the app
        // (the chord is consumed either way, so an unsupported/coalesced read still isn't a stray 'v').
        if (IsReadOnly || _pasteReadInFlight || UIApplication.Current is not { } app || !app.Clipboard.CanRead)
            return;

        _pasteReadInFlight = true;
        _ = CompletePasteAsync(app.Clipboard.TryGetTextAsync(PasteReadTimeout));
    }

    private async Task CompletePasteAsync(ValueTask<string?> read)
    {
        // Fire-and-forget from Paste(): route any fault through the framework's exception funnel rather than
        // letting it become an unobserved-task exception (the InsertText path is throw-free today, but a future
        // two-way write-back it triggers might not be). The reply — or the timeout null — resumes on the UI
        // thread via the dispatcher sync context; a stale completion (box went read-only, tree detached, text
        // arrived empty) drops at the guards.
        try
        {
            var text = await read;
            if (!string.IsNullOrEmpty(text) && !IsReadOnly && IsAttachedToTree)
                InsertText(text, fromPaste: true);
        }
        catch (Exception ex)
        {
            UIApplication.Current?.RaiseUnhandled(ex);
        }
        finally
        {
            _pasteReadInFlight = false;
        }
    }

    // ───────────────────────────── pointer hit / word select ─────────────────────────────

    private int IndexFromPointer(MouseEventArgs e)
    {
        if (_presenter is null)
            return _caretIndex;

        var local = e.GetPosition(_presenter);
        if (IsMultiLine)
            return _presenter.OffsetFromPoint(Math.Max(0, local.Column), Math.Max(0, local.Row));

        var column = Math.Max(0, local.Column) + _presenter.ScrollOffset;
        // The pointer hits the DISPLAYED text — round in display space, then map the boundary back to the model
        // (identity for a TextBox; the per-cluster mask correspondence for a PasswordBox).
        var layout = GraphemeLayout.Build(DisplayText);

        var before = layout.CharIndexAtOrBeforeColumn(column);
        var after = layout.NextBoundary(before);
        // Round to the nearer cluster boundary so a click on a wide glyph's right half lands after it.
        var displayIndex = column - layout.ColumnOf(before) <= layout.ColumnOf(after) - column ? before : after;
        return ToModelIndex(displayIndex);
    }

    private void SelectWordAt(int index)
    {
        var text = Text;
        if (text.Length == 0)
        {
            SetCaretAndSelection(0, 0);
            return;
        }

        index = Math.Clamp(index, 0, text.Length);
        int start = index, end = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

        if (start == end) // the click was on whitespace — select the whitespace run instead
        {
            while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
        }

        SetCaretAndSelection(start, end);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private string TrimToMaxLength(string replacement, int removed, int currentLength)
    {
        var max = MaxLength;
        if (max <= 0 || replacement.Length == 0)
            return replacement;

        var available = max - (currentLength - removed);
        if (available <= 0)
            return "";
        if (replacement.Length <= available)
            return replacement;

        // Trim at a cluster boundary so a surrogate pair / combining sequence is never split.
        var pinned = GraphemeLayout.Build(replacement).PinToBoundary(available);
        return replacement[..pinned];
    }

    private string FilterInput(string input, bool fromPaste)
    {
        var builder = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c is '\r' or '\n')
            {
                if (AcceptsReturn)
                    builder.Append('\n');      // keep newlines (normalizing \r / \r\n / \n → \n)
                else if (fromPaste)
                    builder.Append(' ');        // single-line: flatten a newline to a space on paste
                // else (single-line, typed): drop the newline

                if (c == '\r' && i + 1 < input.Length && input[i + 1] == '\n')
                    i++;                         // collapse a \r\n pair either way

                continue;
            }

            if (c == '\t')
            {
                if (AcceptsTab)
                    builder.Append('\t');
                else if (fromPaste)
                    builder.Append(' ');
                continue;
            }

            if (!char.IsControl(c))
                builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsLetter(KeyEventArgs e, char lower)
        => e.Text.Length == 1 && char.ToLowerInvariant(e.Text.Span[0]) == lower;

    private void RefreshPresenter()
    {
        if (_presenter is not { } presenter)
            return;

        presenter.RefreshCaretAndScroll(); // re-anchor scroll + (re)publish the caret
        presenter.InvalidateVisual();       // re-raster the presenter zone (text / selection / placeholder)
    }

    private static void OnTextChanged(UIObject sender, string oldValue, string newValue)
    {
        if (sender is not TextBox box)
            return;

        box.PseudoClasses.Set(":empty", string.IsNullOrEmpty(newValue));

        // A self-edit (typing / delete / undo / redo) — and any synchronous binding echo it triggers — owns the
        // caret, the undo bookkeeping, AND the TextChanged raise explicitly via the funnel (which runs them after
        // the splice settles, so handlers never see a mid-edit caret/stack). Skip everything here for self-edits.
        if (box._isApplyingEdit)
            return;

        // An EXTERNAL change (an app `Text =` assignment or a binding source push) re-pins the caret into the new
        // text and discards the now-stale undo history (WPF resets undo on a programmatic / source-driven set).
        var layout = GraphemeLayout.Build(newValue);
        box._caretIndex = layout.PinToBoundary(Math.Clamp(box._caretIndex, 0, newValue.Length));
        box._selectionAnchor = layout.PinToBoundary(Math.Clamp(box._selectionAnchor, 0, newValue.Length));
        box._caretLineEndAffinity = false;
        if (box.RecordsUndo)
            box.ClearUndoHistory();
        box.RefreshPresenter();
        box.RaiseTextChanged();
    }

    private void RaiseTextChanged()
    {
        if (IsAttachedToTree)
            RaiseEvent(RentEvent(TextChangedEvent));
    }

    private void RaiseSelectionChanged()
    {
        if (IsAttachedToTree)
            RaiseEvent(RentEvent(SelectionChangedEvent));
    }

    // Raises a veto-only clipboard event (Pasting/Copying/Cutting) and reports whether a handler cancelled the
    // operation. Caller-owned args (not the pooled path) because Handled must survive past RaiseEvent — a rented
    // args goes stale on raise completion. A detached box has no route, so nothing can veto: report not-cancelled.
    private bool RaiseClipboardVeto(RoutedEvent<RoutedEventArgs> routedEvent)
    {
        if (!IsAttachedToTree)
            return false;

        var args = new RoutedEventArgs(routedEvent, this);
        RaiseEvent(args);
        return args.Handled;
    }

    private static void OnIsUndoEnabledChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is TextBox box && !newValue)
            box.ClearUndoHistory(); // disabling undo discards the history
    }

    private static void OnUndoLimitChanged(UIObject sender, int oldValue, int newValue)
    {
        if (sender is not TextBox box)
            return;
        if (newValue == 0)
            box.ClearUndoHistory(); // 0 = recording disabled
        else
            box.TrimUndoLimit();    // a tighter cap drops the oldest units now
    }

    private static void OnPlaceholderChanged(UIObject sender, string? oldValue, string? newValue)
        => (sender as TextBox)?._presenter?.InvalidateVisual();

    private static void OnSelectionBrushChanged(UIObject sender, IBrush? oldValue, IBrush? newValue)
        => (sender as TextBox)?._presenter?.InvalidateVisual();
}
