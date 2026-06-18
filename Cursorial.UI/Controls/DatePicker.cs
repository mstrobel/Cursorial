using System.Globalization;

using Cursorial.Input;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A drop-down date field (design doc §12 — the WPF <c>DatePicker</c> / WinUI <c>CalendarDatePicker</c> analog, the
/// <b>calendar</b> variant). A read-only display of <see cref="SelectedDate"/> (or the <see cref="Watermark"/>) plus a
/// drop button (<c>PART_Button</c>) that opens a <see cref="Popup"/> hosting a <see cref="Calendar"/>
/// (<c>PART_Calendar</c>); picking a day commits the date and closes. The <b>inline</b> variant is the standalone
/// <see cref="Controls.Calendar"/> control (a complete always-visible month picker). Editable text entry of a date is
/// a v2 deferral.
/// </summary>
[TemplatePart(PartPopup, typeof(Popup))]
[TemplatePart(PartCalendar, typeof(Calendar))]
[TemplatePart(PartDisplayText, typeof(TextBlock))]
public class DatePicker : Control
{
    private const string PartPopup = "PART_Popup";
    private const string PartCalendar = "PART_Calendar";
    private const string PartDisplayText = "PART_DisplayText";

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

    private bool _isDropDownOpen;
    private Popup? _popup;
    private Calendar? _calendar;
    private TextBlock? _displayText;

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

    /// <summary>Raised when <see cref="SelectedDate"/> changes (old → new).</summary>
    public event EventHandler<CalendarSelectedDateChangedEventArgs>? SelectedDateChanged;

    // Test/inspection seams (the parts are template-private).
    internal Calendar? CalendarPart => _calendar;
    internal string? DisplayText => _displayText?.Text;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
        if (_calendar is not null)
            _calendar.DateCommitted -= OnCalendarDateCommitted;

        _popup = GetTemplatePart<Popup>(PartPopup);
        _calendar = GetTemplatePart<Calendar>(PartCalendar);
        _displayText = GetTemplatePart<TextBlock>(PartDisplayText);

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

        UpdateDisplay();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_popup is not null)
            _popup.Closed -= OnPopupClosed;
        if (_calendar is not null)
            _calendar.DateCommitted -= OnCalendarDateCommitted;

        _popup = null;
        _calendar = null;
        _displayText = null;
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        SetDropDownOpen(false); // close so the Popup surface doesn't leak on detach
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        // A left click on the field (not on a popup day — those are on the Popup's own surface) toggles the calendar.
        if (!e.Handled && e.Button == MouseButton.Left)
        {
            Focus();
            SetDropDownOpen(!_isDropDownOpen);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        if (!_isDropDownOpen)
        {
            if (e.Key is Key.DownArrow or Key.Enter or Key.F4 || IsSpace(e))
            {
                SetDropDownOpen(true);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            SetDropDownOpen(false);
            e.Handled = true;
        }
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
            Focus(); // restore focus to the field when the drop-down closes
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
        picker.SelectedDateChanged?.Invoke(picker, new CalendarSelectedDateChangedEventArgs(oldValue, newValue));
    }

    // Modifier-free Space is (Key.Character, " ") on every wire (ND10); Key.Space is only NUL→Ctrl+Space.
    private static bool IsSpace(KeyEventArgs e)
        => e.Modifiers == KeyModifiers.None
           && (e.Key == Key.Space || (e is { Key: Key.Character, Text.Length: 1 } && e.Text.Span[0] == ' '));
}
