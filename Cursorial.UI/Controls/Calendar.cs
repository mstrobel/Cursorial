using System.Globalization;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A month-view date picker surface (design doc §12 — the WPF <c>Calendar</c> analog, month mode only). It shows the
/// <see cref="DisplayDate"/>'s month as a 7-column grid (a culture-ordered day-of-week header row + up to six week
/// rows of <see cref="CalendarDayButton"/> cells), with a header label and previous/next month buttons. Clicking a
/// day (or arrow-key navigation) sets <see cref="SelectedDate"/>; the grid restamps <c>:today</c>/<c>:selected</c>/
/// <c>:inactive</c> per cell. Culture drives the day-name abbreviations (<see cref="DateTimeFormatInfo.ShortestDayNames"/>)
/// and the default <see cref="FirstDayOfWeek"/>. The year/decade drill-down modes and date bounds are v2 deferrals.
/// </summary>
[TemplatePart(PartMonthView, typeof(StackPanel))]
[TemplatePart(PartPreviousButton, typeof(Button))]
[TemplatePart(PartNextButton, typeof(Button))]
[TemplatePart(PartHeaderText, typeof(TextBlock))]
public class Calendar : Control
{
    private const string PartMonthView = "PART_MonthView";
    private const string PartPreviousButton = "PART_PreviousButton";
    private const string PartNextButton = "PART_NextButton";
    private const string PartHeaderText = "PART_HeaderText";

    private const int CellWidth = 4; // a uniform 7-column grid

    private static readonly DateOnly MinMonth = new(1, 1, 1);
    private static readonly DateOnly MaxMonth = new(9999, 12, 1);

    /// <summary>The month shown (any day in it; the grid renders that month). Defaults to <see cref="Today"/>.</summary>
    public static readonly StyledProperty<DateOnly> DisplayDateProperty =
        UIProperty.Register<Calendar, DateOnly>(nameof(DisplayDate), changed: OnDisplayDateChanged);

    /// <summary>The selected date (<c>null</c> = none), two-way bindable; selecting a day in an adjacent month moves <see cref="DisplayDate"/>.</summary>
    public static readonly StyledProperty<DateOnly?> SelectedDateProperty =
        UIProperty.Register<Calendar, DateOnly?>(nameof(SelectedDate), changed: OnSelectedDateChanged);

    /// <summary>The "today" reference cell (<c>:today</c>); defaults to the system date at construction (settable for determinism).</summary>
    public static readonly StyledProperty<DateOnly> TodayProperty =
        UIProperty.Register<Calendar, DateOnly>(nameof(Today), changed: OnTodayChanged);

    /// <summary>The first column's day of week; defaults to the current culture's <see cref="DateTimeFormatInfo.FirstDayOfWeek"/>.</summary>
    public static readonly StyledProperty<DayOfWeek> FirstDayOfWeekProperty =
        UIProperty.Register<Calendar, DayOfWeek>(nameof(FirstDayOfWeek), changed: OnFirstDayOfWeekChanged);

    private readonly Dictionary<DateOnly, CalendarDayButton> _cells = new();
    private StackPanel? _monthView;
    private Button? _previousButton;
    private Button? _nextButton;
    private TextBlock? _headerText;

    /// <summary>Creates a calendar showing the current month.</summary>
    public Calendar()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        SetValue(TodayProperty, today);
        SetValue(DisplayDateProperty, today);
        SetValue(FirstDayOfWeekProperty, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
    }

    /// <inheritdoc cref="DisplayDateProperty"/>
    public DateOnly DisplayDate { get => GetValue(DisplayDateProperty); set => SetValue(DisplayDateProperty, value); }

    /// <inheritdoc cref="SelectedDateProperty"/>
    public DateOnly? SelectedDate { get => GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

    /// <inheritdoc cref="TodayProperty"/>
    public DateOnly Today { get => GetValue(TodayProperty); set => SetValue(TodayProperty, value); }

    /// <inheritdoc cref="FirstDayOfWeekProperty"/>
    public DayOfWeek FirstDayOfWeek { get => GetValue(FirstDayOfWeekProperty); set => SetValue(FirstDayOfWeekProperty, value); }

    /// <summary>Raised when <see cref="SelectedDate"/> changes (old → new) — including arrow-key <i>browse</i> moves.</summary>
    public event EventHandler<CalendarSelectedDateChangedEventArgs>? SelectedDateChanged;

    /// <summary>Raised when a date is <b>committed</b> by a click or Enter/Space on a day cell (the
    /// confirm gesture, vs an arrow-key browse). Fires even when the committed date equals the current selection — a
    /// drop-down host (<see cref="DatePicker"/>) closes on this, not on <see cref="SelectedDateChanged"/>.</summary>
    public event EventHandler<CalendarSelectedDateChangedEventArgs>? DateCommitted;

    // Test/inspection seams (the day grid is built in code, so these expose it for assertions).
    internal CalendarDayButton? CellForDate(DateOnly date) => _cells.GetValueOrDefault(date);
    internal int DayCellCount => _cells.Count;
    internal Button? PreviousButton => _previousButton;
    internal Button? NextButton => _nextButton;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_previousButton is not null)
            _previousButton.Click -= OnPreviousClick;
        if (_nextButton is not null)
            _nextButton.Click -= OnNextClick;

        _monthView = GetTemplatePart<StackPanel>(PartMonthView);
        _headerText = GetTemplatePart<TextBlock>(PartHeaderText);
        _previousButton = GetTemplatePart<Button>(PartPreviousButton);
        _nextButton = GetTemplatePart<Button>(PartNextButton);

        if (_previousButton is not null)
            _previousButton.Click += OnPreviousClick;
        if (_nextButton is not null)
            _nextButton.Click += OnNextClick;

        RebuildMonthView();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_previousButton is not null)
            _previousButton.Click -= OnPreviousClick;
        if (_nextButton is not null)
            _nextButton.Click -= OnNextClick;

        _monthView = null;
        _headerText = null;
        _previousButton = null;
        _nextButton = null;
        _cells.Clear();
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        switch (e.Key)
        {
            case Key.LeftArrow: MoveSelection(ResolveAnchorDate(e), -1); break;
            case Key.RightArrow: MoveSelection(ResolveAnchorDate(e), 1); break;
            case Key.UpArrow: MoveSelection(ResolveAnchorDate(e), -7); break;
            case Key.DownArrow: MoveSelection(ResolveAnchorDate(e), 7); break;
            case Key.Home: SelectAndFocus(new DateOnly(DisplayDate.Year, DisplayDate.Month, 1)); break;
            case Key.End: SelectAndFocus(new DateOnly(DisplayDate.Year, DisplayDate.Month, DateTime.DaysInMonth(DisplayDate.Year, DisplayDate.Month))); break;
            case Key.PageUp: ChangeMonth(-1); FocusDisplayMonthCell(); break;   // refocus a cell in the new month so
            case Key.PageDown: ChangeMonth(1); FocusDisplayMonthCell(); break;  // the next key still routes here
            default: return; // not a calendar-nav key — leave unhandled
        }

        e.Handled = true;
    }

    /// <summary>Moves keyboard focus onto the selected day's cell (or the first of the shown month) — e.g. when a
    /// <see cref="DatePicker"/> drops this calendar open. A no-op until the month grid has been laid out.</summary>
    public void FocusDate() => FocusDisplayMonthCell();

    // The keyboard-cursor anchor: the focused day cell if one holds focus (the ListBox.ResolveCurrent idiom), else a
    // day in the SHOWN month — never a stale SelectedDate/Today from another month, which would yank the view back
    // on the first arrow after a PageUp/PageDown/prev-next (audit CD-P2D-1).
    private DateOnly ResolveAnchorDate(KeyEventArgs e)
    {
        for (UIElement? node = e.OriginalSource; node is not null; node = node.VisualParent)
            if (node is CalendarDayButton cell)
                return cell.Date;

        return InMonthAnchor();
    }

    private DateOnly InMonthAnchor()
    {
        if (SelectedDate is { } s && s.Year == DisplayDate.Year && s.Month == DisplayDate.Month)
            return s;
        if (Today.Year == DisplayDate.Year && Today.Month == DisplayDate.Month)
            return Today;
        return new DateOnly(DisplayDate.Year, DisplayDate.Month, 1);
    }

    // Step from the anchor, clamped to the representable DateOnly range (AddDays throws past Min/MaxValue).
    private void MoveSelection(DateOnly anchor, int deltaDays)
    {
        var dayNumber = Math.Clamp(anchor.DayNumber + deltaDays, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber);
        SelectAndFocus(DateOnly.FromDayNumber(dayNumber));
    }

    // Select a date (keyboard / Home / End) and move keyboard focus onto its freshly-stamped cell.
    private void SelectAndFocus(DateOnly date)
    {
        SelectDate(date);
        if (_cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible
    }

    private void SelectDate(DateOnly date) => SetCurrentValue(SelectedDateProperty, date); // SetCurrentValue preserves a two-way binding

    // After a keyboard month change (which rebuilt the grid and dropped the focused cell), move focus onto a cell in
    // the new month — the selected day if it is shown, else the first — so subsequent keys keep routing to the Calendar.
    private void FocusDisplayMonthCell()
    {
        var date = SelectedDate is { } s && s.Year == DisplayDate.Year && s.Month == DisplayDate.Month
            ? s
            : new DateOnly(DisplayDate.Year, DisplayDate.Month, 1);
        if (_cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Directional);
    }

    private void ChangeMonth(int months)
    {
        var firstOfMonth = new DateOnly(DisplayDate.Year, DisplayDate.Month, 1);
        if (months < 0 && firstOfMonth <= MinMonth)
            return; // clamp at the representable bounds (AddMonths throws past DateOnly.Min/MaxValue)
        if (months > 0 && firstOfMonth >= MaxMonth)
            return;
        SetCurrentValue(DisplayDateProperty, firstOfMonth.AddMonths(months));
    }

    private void OnPreviousClick(object? sender, ClickEventArgs e) => ChangeMonth(-1);
    private void OnNextClick(object? sender, ClickEventArgs e) => ChangeMonth(1);

    private void OnDayClick(object? sender, ClickEventArgs e)
    {
        var date = ((CalendarDayButton)sender!).Date;
        var old = SelectedDate;
        SelectDate(date);
        // Re-focus the picked day: SelectDate rebuilt the grid (detaching the clicked cell), so without this focus
        // repair would jump to the first tab stop — the prev-month button (audit CD-P2D-1).
        if (_cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Pointer);

        // A click — or Enter/Space, which a CalendarDayButton routes through Click — is a COMMIT (vs an arrow browse),
        // and fires even when the date is unchanged so a drop-down host closes on a re-pick of the current day.
        DateCommitted?.Invoke(this, new CalendarSelectedDateChangedEventArgs(old, date));
    }

    private static void OnDisplayDateChanged(UIObject sender, DateOnly oldValue, DateOnly newValue)
        => (sender as Calendar)?.RebuildMonthView();

    private static void OnTodayChanged(UIObject sender, DateOnly oldValue, DateOnly newValue)
        => (sender as Calendar)?.RebuildMonthView();

    private static void OnFirstDayOfWeekChanged(UIObject sender, DayOfWeek oldValue, DayOfWeek newValue)
        => (sender as Calendar)?.RebuildMonthView();

    private static void OnSelectedDateChanged(UIObject sender, DateOnly? oldValue, DateOnly? newValue)
    {
        if (sender is not Calendar calendar)
            return;

        // Selecting a day in an adjacent month moves the view (DisplayDate change rebuilds); otherwise restamp in place.
        if (newValue is { } d && (d.Year != calendar.DisplayDate.Year || d.Month != calendar.DisplayDate.Month))
            calendar.SetCurrentValue(DisplayDateProperty, new DateOnly(d.Year, d.Month, 1));
        else
            calendar.RebuildMonthView();

        calendar.SelectedDateChanged?.Invoke(calendar, new CalendarSelectedDateChangedEventArgs(oldValue, newValue));
    }

    // Repopulate the 7×7 grid for DisplayDate's month: a culture-ordered day-of-week header row + six week rows of
    // CalendarDayButton cells (leading/trailing adjacent-month days marked :inactive). Idempotent; no-op pre-template.
    private void RebuildMonthView()
    {
        if (_monthView is null)
            return;

        _monthView.Children.Clear();
        _cells.Clear();

        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)FirstDayOfWeek + i) % 7);
            header.Children.Add(new TextBlock { Text = dtf.ShortestDayNames[(int)dow], Width = CellWidth, TextAlignment = TextAlignment.Center });
        }

        _monthView.Children.Add(header);

        var firstOfMonth = new DateOnly(DisplayDate.Year, DisplayDate.Month, 1);
        var lead = ((int)firstOfMonth.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        lead = Math.Min(lead, firstOfMonth.DayNumber); // don't underflow DateOnly.MinValue (Jan 0001) computing the start
        var startDayNumber = firstOfMonth.DayNumber - lead;

        for (var week = 0; week < 6; week++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (var day = 0; day < 7; day++)
            {
                var dayNumber = startDayNumber + week * 7 + day;
                if (dayNumber > DateOnly.MaxValue.DayNumber)
                {
                    row.Children.Add(new TextBlock { Width = CellWidth }); // a blank slot past the representable range (Dec 9999)
                    continue;
                }

                var date = DateOnly.FromDayNumber(dayNumber);
                var cell = new CalendarDayButton
                {
                    Date = date,
                    Content = date.Day.ToString(CultureInfo.CurrentCulture),
                    Width = CellWidth,
                    IsToday = date == Today,
                    IsSelected = SelectedDate == date,
                    IsInactive = date.Month != DisplayDate.Month,
                };
                cell.Click += OnDayClick;
                row.Children.Add(cell);
                _cells[date] = cell;
            }

            _monthView.Children.Add(row);
        }

        if (_headerText is not null)
            _headerText.Text = DisplayDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }
}

/// <summary>The <see cref="Calendar.SelectedDateChanged"/> payload — the old and new selected dates.</summary>
public sealed class CalendarSelectedDateChangedEventArgs(DateOnly? oldDate, DateOnly? newDate) : EventArgs
{
    /// <summary>The previously selected date (null if none).</summary>
    public DateOnly? OldDate { get; } = oldDate;

    /// <summary>The newly selected date (null if cleared).</summary>
    public DateOnly? NewDate { get; } = newDate;
}
