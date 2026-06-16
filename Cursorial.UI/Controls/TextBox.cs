using System.Text;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Text;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
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
/// paste (<c>TextInput{FromPaste}</c>). Undo/redo and multi-line are deferred (spec §15).
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

    /// <summary>The bubbling event raised whenever <see cref="Text"/> changes.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> TextChangedEvent =
        RoutedEvent<RoutedEventArgs>.Register(nameof(TextChanged), RoutingStrategy.Bubble, typeof(TextBox));

    private TextPresenter? _presenter;
    private int _caretIndex;       // the active end of the selection (where the caret blinks)
    private int _selectionAnchor;  // the fixed end of the selection (== caret when there is no selection)
    private bool _dragging;        // a left-button drag is extending the selection

    static TextBox()
    {
        FocusableProperty.OverrideDefaultValue<TextBox>(true); // an editable field takes keyboard focus
        BindsTwoWayByDefault<TextBox>(TextProperty);           // two-way is the binding default (§3.9)
        // :empty is driven directly (PseudoClassMapping only applies on a value CHANGE, never the initial
        // default — and the default Text "" must read as :empty for a never-edited field).
        PseudoClassMapping.Register<TextBox>(IsReadOnlyProperty, ":readonly");
        AffectsMeasure<TextBox>(TextProperty);
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

    /// <summary>CLR sugar over <see cref="TextChangedEvent"/>.</summary>
    public event EventHandler<RoutedEventArgs>? TextChanged { add => AddHandler(TextChangedEvent, value!); remove => RemoveHandler(TextChangedEvent, value!); }

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
        set => ReplaceSelection(value ?? "");
    }

    /// <summary>The selection as a normalized half-open <c>[start, end)</c> char range (consumed by the presenter).</summary>
    internal (int Start, int End) SelectionBounds
        => (Math.Min(_selectionAnchor, _caretIndex), Math.Max(_selectionAnchor, _caretIndex));

    /// <summary>The realized <c>PART_TextPresenter</c> (test observability; null before the template applies).</summary>
    internal TextPresenter? Presenter => _presenter;

    /// <summary>Selects the whole text (caret at the end).</summary>
    public void SelectAll() => SetCaretAndSelection(anchor: 0, caret: Text.Length);

    /// <summary>Clears the text.</summary>
    public void Clear() => Text = "";

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
        RefreshPresenter(); // hide the caret
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
            case Key.Home:
                MoveCaret(0, shift);
                break;
            case Key.End:
                MoveCaret(Text.Length, shift);
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

    private void MoveCaret(int newCaret, bool extend)
    {
        if (extend)
            SetCaretAndSelection(_selectionAnchor, newCaret); // keep the anchor, move the active end
        else
            SetCaretAndSelection(newCaret, newCaret);          // collapse the selection at the caret
    }

    private void SetCaretAndSelection(int anchor, int caret)
    {
        var layout = GraphemeLayout.Build(Text);
        anchor = layout.PinToBoundary(Math.Clamp(anchor, 0, Text.Length));
        caret = layout.PinToBoundary(Math.Clamp(caret, 0, Text.Length));
        if (anchor == _selectionAnchor && caret == _caretIndex)
            return;

        _selectionAnchor = anchor;
        _caretIndex = caret;
        RefreshPresenter();
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

        var filtered = FilterInput(input, fromPaste);
        if (filtered.Length > 0)
            ReplaceSelection(filtered);
    }

    private void ReplaceSelection(string replacement)
    {
        if (IsReadOnly)
            return;

        var (start, end) = SelectionBounds;
        var current = Text;
        replacement = TrimToMaxLength(replacement, removed: end - start, currentLength: current.Length);

        if (replacement.Length == 0 && end == start)
            return; // nothing to insert and nothing selected to remove

        var newCaret = start + replacement.Length;
        Text = string.Concat(current.AsSpan(0, start), replacement, current.AsSpan(end)); // fires OnTextChanged
        CaretIndex = newCaret;                                                             // position the caret in the new text
    }

    private void DeleteBackward(bool word)
    {
        if (IsReadOnly)
            return;
        if (SelectionLength > 0) { ReplaceSelection(""); return; }
        if (_caretIndex <= 0)
            return;

        var layout = GraphemeLayout.Build(Text);
        var from = word ? layout.PinToBoundary(TextNavigation.PrevWord(Text, _caretIndex)) : layout.PrevBoundary(_caretIndex);
        DeleteRange(from, _caretIndex);
    }

    private void DeleteForward(bool word)
    {
        if (IsReadOnly)
            return;
        if (SelectionLength > 0) { ReplaceSelection(""); return; }
        if (_caretIndex >= Text.Length)
            return;

        var layout = GraphemeLayout.Build(Text);
        var to = word ? layout.PinToBoundary(TextNavigation.NextWord(Text, _caretIndex)) : layout.NextBoundary(_caretIndex);
        DeleteRange(_caretIndex, to);
    }

    private void DeleteRange(int from, int to)
    {
        if (to <= from)
            return;

        var current = Text;
        Text = string.Concat(current.AsSpan(0, from), current.AsSpan(to));
        CaretIndex = from;
    }

    // ───────────────────────────── clipboard ─────────────────────────────

    // Returns whether the gesture was consumed (there was a selection to copy).
    private bool Copy()
    {
        var text = SelectedText;
        if (text.Length == 0)
            return false;

        UIApplication.Current?.Clipboard.SetText(text);
        return true;
    }

    private bool Cut()
    {
        if (IsReadOnly || SelectionLength == 0)
            return false;

        UIApplication.Current?.Clipboard.SetText(SelectedText);
        ReplaceSelection("");
        return true;
    }

    private static void Paste()
    {
        // v1: OSC 52 reads are not negotiated, so Ctrl+V / Shift+Insert are a consumed no-op. The supported
        // inbound path is the terminal's own paste (a PasteEvent ⇒ TextInput{FromPaste}) which OnTextInput
        // absorbs. A future read implementation would round-trip here when IClipboardService.CanRead is true.
    }

    // ───────────────────────────── pointer hit / word select ─────────────────────────────

    private int IndexFromPointer(MouseEventArgs e)
    {
        if (_presenter is null)
            return _caretIndex;

        var local = e.GetPosition(_presenter);
        var column = Math.Max(0, local.Column) + _presenter.ScrollOffset;
        var layout = GraphemeLayout.Build(Text);

        var before = layout.CharIndexAtOrBeforeColumn(column);
        var after = layout.NextBoundary(before);
        // Round to the nearer cluster boundary so a click on a wide glyph's right half lands after it.
        return column - layout.ColumnOf(before) <= layout.ColumnOf(after) - column ? before : after;
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

    private static string FilterInput(string input, bool fromPaste)
    {
        var builder = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c is '\r' or '\n')
            {
                if (fromPaste)
                {
                    builder.Append(' '); // flatten a newline to a space on paste (collapse CRLF to one)
                    if (c == '\r' && i + 1 < input.Length && input[i + 1] == '\n')
                        i++;
                }

                continue;
            }

            if (c == '\t')
            {
                if (fromPaste)
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

        // Keep the caret/anchor valid for the new text and pinned to cluster boundaries.
        var layout = GraphemeLayout.Build(newValue);
        box._caretIndex = layout.PinToBoundary(Math.Clamp(box._caretIndex, 0, newValue.Length));
        box._selectionAnchor = layout.PinToBoundary(Math.Clamp(box._selectionAnchor, 0, newValue.Length));
        box.RefreshPresenter();

        if (box.IsAttachedToTree)
            box.RaiseEvent(box.RentEvent(TextChangedEvent));
    }

    private static void OnPlaceholderChanged(UIObject sender, string? oldValue, string? newValue)
        => (sender as TextBox)?._presenter?.InvalidateVisual();

    private static void OnSelectionBrushChanged(UIObject sender, IBrush? oldValue, IBrush? newValue)
        => (sender as TextBox)?._presenter?.InvalidateVisual();
}
