using System.Globalization;

using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A drop-down date field (design doc §12 — the WPF <c>DatePicker</c> / WinUI <c>CalendarDatePicker</c> analog, the
/// <b>calendar</b> variant). Non-editable (default): a read-only display of <see cref="SelectedDate"/> (or the
/// <see cref="Watermark"/>) plus a drop button that opens a <see cref="Popup"/> hosting a <see cref="Calendar"/>
/// (<c>PART_Calendar</c>); picking a day commits the date and closes. Editable (<see cref="IsEditable"/>): the face is
/// a <see cref="TextBox"/> (<c>PART_EditableTextBox</c>) — typing a date and pressing Enter (or leaving the field)
/// parses it (culture-aware) and commits to <see cref="SelectedDate"/>, reverting to the last value if it doesn't
/// parse; the <c>PART_DropDown</c> button still opens the calendar to pick visually. The <b>inline</b> variant is the
/// standalone <see cref="Controls.Calendar"/> control.
/// </summary>
[TemplatePart(PartPopup, typeof(Popup))]
[TemplatePart(PartCalendar, typeof(Calendar))]
[TemplatePart(PartDisplayText, typeof(TextBlock))]
[TemplatePart(PartEditableTextBox, typeof(TextBox))]
[TemplatePart(PartDropDown, typeof(ButtonBase))]
public class DatePicker : Control
{
    private const string PartPopup = "PART_Popup";
    private const string PartCalendar = "PART_Calendar";
    private const string PartDisplayText = "PART_DisplayText";
    private const string PartEditableTextBox = "PART_EditableTextBox";
    private const string PartDropDown = "PART_DropDown";

    /// <summary>The picked date (<c>null</c> = none), two-way bindable.</summary>
    public static readonly StyledProperty<DateOnly?> SelectedDateProperty =
        UIProperty.Register<DatePicker, DateOnly?>(nameof(SelectedDate), changed: OnSelectedDateChanged);

    /// <summary>The month the drop-down opens on (defaults to <see cref="SelectedDate"/>'s month, else today).</summary>
    public static readonly StyledProperty<DateOnly> DisplayDateProperty =
        UIProperty.Register<DatePicker, DateOnly>(nameof(DisplayDate));

    /// <summary>The prompt shown when no date is selected (<c>:empty</c>-style placeholder).</summary>
    public static readonly StyledProperty<string?> WatermarkProperty =
        UIProperty.Register<DatePicker, string?>(nameof(Watermark), changed: static (s, _, _) => (s as DatePicker)?.UpdateDisplay());

    /// <summary>Whether the calendar drop-down is open (<c>:open</c>; two-way with the <see cref="Popup"/>).</summary>
    public static readonly DirectProperty<DatePicker, bool> IsDropDownOpenProperty =
        UIProperty.RegisterDirect<DatePicker, bool>(nameof(IsDropDownOpen), static d => d._isDropDownOpen, static (d, v) => d.SetDropDownOpen(v));

    /// <summary>Whether the field is an editable text box (type a date) rather than read-only (<c>:editable</c>).</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        UIProperty.Register<DatePicker, bool>(nameof(IsEditable), defaultValue: false, changed: OnIsEditableChanged);

    private bool _isDropDownOpen;
    private bool _editing;  // the user has typed an uncommitted draft (so a SelectedDate sync must not clobber it)
    private bool _syncing;  // guards the SelectedDate → box push so it isn't mistaken for a user edit
    private Popup? _popup;
    private Calendar? _calendar;
    private TextBlock? _displayText;
    private TextBox? _editableTextBox;
    private ButtonBase? _dropDown;

    /// <summary>Creates a date picker showing the current month when first opened.</summary>
    public DatePicker()
    {
        Focusable = true;
        SetValue(DisplayDateProperty, DateOnly.FromDateTime(DateTime.Now));
    }

    /// <inheritdoc cref="SelectedDateProperty"/>
    public DateOnly? SelectedDate { get => GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

    /// <inheritdoc cref="DisplayDateProperty"/>
    public DateOnly DisplayDate { get => GetValue(DisplayDateProperty); set => SetValue(DisplayDateProperty, value); }

    /// <inheritdoc cref="WatermarkProperty"/>
    public string? Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }

    /// <inheritdoc cref="IsDropDownOpenProperty"/>
    public bool IsDropDownOpen { get => _isDropDownOpen; set => SetDropDownOpen(value); }

    /// <inheritdoc cref="IsEditableProperty"/>
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }

    /// <summary>Raised when <see cref="SelectedDate"/> changes (old → new).</summary>
    public event EventHandler<CalendarSelectedDateChangedEventArgs>? SelectedDateChanged;

    // Test/inspection seams (the parts are template-private).
    internal Calendar? CalendarPart => _calendar;
    internal TextBox? EditableTextBoxPart => _editableTextBox;
    internal string? DisplayText => _displayText?.Text;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
        if (_calendar is not null)
            _calendar.DateCommitted -= OnCalendarDateCommitted;
        if (_dropDown is not null)
            _dropDown.Click -= OnDropDownClick;
        if (_editableTextBox is not null)
            _editableTextBox.TextChanged -= OnEditableTextChanged;

        _popup = GetTemplatePart<Popup>(PartPopup);
        _calendar = GetTemplatePart<Calendar>(PartCalendar);
        _displayText = GetTemplatePart<TextBlock>(PartDisplayText);
        _editableTextBox = GetTemplatePart<TextBox>(PartEditableTextBox);
        _dropDown = GetTemplatePart<ButtonBase>(PartDropDown);

        if (_editableTextBox is not null)
            _editableTextBox.TextChanged += OnEditableTextChanged;

        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
            _popup.Placement = PlacementMode.Bottom;
            _popup.KeepOpenOnAnchorPress = true; // a field click closes via OnMouseDown's toggle, not dismiss-then-reopen
            _popup.Closed += OnPopupClosed;
            _popup.SetCurrentValue(Popup.IsOpenProperty, _isDropDownOpen);
        }

        if (_calendar is not null)
            _calendar.DateCommitted += OnCalendarDateCommitted;
        if (_dropDown is not null)
            _dropDown.Click += OnDropDownClick;

        UpdateEditableState();
        UpdateDisplay();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
        if (_calendar is not null)
            _calendar.DateCommitted -= OnCalendarDateCommitted;
        if (_dropDown is not null)
            _dropDown.Click -= OnDropDownClick;
        if (_editableTextBox is not null)
            _editableTextBox.TextChanged -= OnEditableTextChanged;

        _popup = null;
        _calendar = null;
        _displayText = null;
        _editableTextBox = null;
        _dropDown = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        SetDropDownOpen(false); // close so the Popup surface doesn't leak on detach
        base.OnDetachedFromTree(in e);
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
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        // Editable: leaving the field commits the typed text (parse, or revert to the last value).
        if (IsEditable && !IsKeyboardFocusWithin)
            ParseAndCommit(close: false);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        // Non-editable: a click anywhere on the field toggles the calendar. Editable: the text box owns its clicks and
        // PART_DropDown toggles, so the field press here does nothing (it would steal the caret click).
        if (!IsEditable)
        {
            Focus();
            SetDropDownOpen(!_isDropDownOpen);
            e.Handled = true;
        }
    }

    private void OnDropDownClick(object? sender, ClickEventArgs e)
    {
        SetDropDownOpen(!_isDropDownOpen);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        var editable = IsEditable;

        if (!_isDropDownOpen)
        {
            switch (e.Key)
            {
                case Key.DownArrow or Key.F4:
                    SetDropDownOpen(true);
                    break;
                case Key.Enter when editable:
                    ParseAndCommit(close: false); // commit the typed text without opening
                    break;
                case Key.Enter:
                    SetDropDownOpen(true);
                    break;
                case Key.Escape when editable:
                    _editing = false;
                    PushDateToBox(); // revert the uncommitted draft to the current selection
                    break;
                default:
                    if (editable || !IsSpace(e)) // Space opens only in non-editable mode (it types when editable)
                        return;
                    SetDropDownOpen(true);
                    break;
            }

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when editable:
                ParseAndCommit(close: true);
                break;
            case Key.Escape:
                if (editable)
                {
                    _editing = false;
                    PushDateToBox(); // revert the edit to the current selection
                }

                SetDropDownOpen(false);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnEditableTextChanged(object? sender, RoutedEventArgs e)
    {
        if (!_syncing)
            _editing = true; // a real user keystroke (not our own SelectedDate→box push)
    }

    // Parse the editable text (culture-aware) and commit it to SelectedDate; an unparseable value reverts the box to
    // the current selection. Closes the drop-down when requested (Enter while open).
    private void ParseAndCommit(bool close)
    {
        _editing = false;
        if (_editableTextBox is { } box && DateOnly.TryParse(box.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
            SetCurrentValue(SelectedDateProperty, date);

        PushDateToBox(); // canonical form on success; revert to the current SelectedDate on a parse failure
        if (close)
            SetDropDownOpen(false);
    }

    // Push the SelectedDate (culture short-date) into the editable text box (guarded so it isn't read as a user edit).
    private void PushDateToBox()
    {
        if (_editableTextBox is null)
            return;
        _syncing = true;
        try
        {
            _editableTextBox.SetCurrentValue(TextBox.TextProperty, SelectedDate?.ToString("d", CultureInfo.CurrentCulture) ?? "");
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void OnIsEditableChanged(UIObject sender, bool oldValue, bool newValue)
        => (sender as DatePicker)?.UpdateEditableState();

    private void UpdateEditableState()
    {
        var editable = IsEditable;
        IsTabStop = !editable; // editable ⇒ the text box is the tab stop; non-editable ⇒ the field is

        if (_displayText is not null)
            _displayText.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
        if (_editableTextBox is not null)
        {
            _editableTextBox.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
            if (editable)
                PushDateToBox();
        }

        // A runtime IsEditable flip while focused moves the caret into / out of the text box.
        if (editable && IsFocused && _editableTextBox is { } box)
            box.Focus();
        else if (!editable && _editableTextBox is { IsFocused: true })
            Focus();
    }

    private void OnCalendarDateCommitted(object? sender, CalendarSelectedDateChangedEventArgs e)
    {
        // Fires only on a click / Enter / Space (a COMMIT) — never on an arrow browse or the open-time push, so the
        // drop-down stays open while browsing and closes on the confirm gesture (even re-picking the current day).
        SetCurrentValue(SelectedDateProperty, e.NewDate); // SetCurrentValue keeps a two-way binding
        SetDropDownOpen(false);
    }

    private void OnPopupClosed(object? sender, PopupClosedEventArgs e) => SetDropDownOpen(false); // light-dismiss / Esc

    private void SetDropDownOpen(bool value)
    {
        if (!SetAndRaise(IsDropDownOpenProperty, ref _isDropDownOpen, value))
            return;

        PseudoClasses.Set(":open", value); // DirectProperty-backed, set imperatively (cf. ComboBox)

        if (value && _calendar is not null)
        {
            // Open the calendar on the selected date's month (or the picker's DisplayDate) and reflect the selection.
            // This is a property push, not a commit (the close rides DateCommitted), so it can't close the popup.
            _calendar.SetCurrentValue(Calendar.DisplayDateProperty, SelectedDate is { } d ? new DateOnly(d.Year, d.Month, 1) : DisplayDate);
            _calendar.SetCurrentValue(Calendar.SelectedDateProperty, SelectedDate);
        }

        _popup?.SetCurrentValue(Popup.IsOpenProperty, value);

        if (value)
            _calendar?.FocusDate(); // best-effort keyboard entry into the grid (a no-op until the popup lays out)
        else
            RestoreFaceFocus(); // restore focus to the field / text box when the drop-down closes
    }

    private void RestoreFaceFocus()
    {
        if (IsEditable && _editableTextBox is { } box)
            box.Focus();
        else
            Focus();
    }

    private void UpdateDisplay()
    {
        if (_displayText is null)
            return;

        _displayText.Text = SelectedDate is { } date
            ? date.ToString("d", CultureInfo.CurrentCulture)
            : Watermark ?? string.Empty;
    }

    private static void OnSelectedDateChanged(UIObject sender, DateOnly? oldValue, DateOnly? newValue)
    {
        if (sender is not DatePicker picker)
            return;

        picker.UpdateDisplay();
        // Editable: reflect the new date in the box — unless the user is mid-edit with an uncommitted draft.
        if (picker.IsEditable && !picker._editing)
            picker.PushDateToBox();
        picker.SelectedDateChanged?.Invoke(picker, new CalendarSelectedDateChangedEventArgs(oldValue, newValue));
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); Key.Space is only NUL→Ctrl+Space.
    private static bool IsSpace(KeyEventArgs e)
        => e.Modifiers == KeyModifiers.None
           && (e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' '));
}
