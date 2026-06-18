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
/// and the default <see cref="FirstDayOfWeek"/>. Date bounds (<see cref="DisplayDateStart"/>/<see cref="DisplayDateEnd"/>)
/// + <see cref="BlackoutDates"/> clamp the view and gate selection. The year/decade drill-down modes are a follow-on (v2b).
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

    /// <summary>The month shown (any day in it; the grid renders that month). Defaults to <see cref="Today"/>; clamped to [<see cref="DisplayDateStart"/>, <see cref="DisplayDateEnd"/>].</summary>
    public static readonly StyledProperty<DateOnly> DisplayDateProperty =
        UIProperty.Register<Calendar, DateOnly>(nameof(DisplayDate), coerce: CoerceDisplayDate, changed: OnDisplayDateChanged);

    /// <summary>The selected date (<c>null</c> = none), two-way bindable; a day in an adjacent month moves <see cref="DisplayDate"/>. An out-of-range / blacked-out value coerces to <c>null</c>.</summary>
    public static readonly StyledProperty<DateOnly?> SelectedDateProperty =
        UIProperty.Register<Calendar, DateOnly?>(nameof(SelectedDate), coerce: CoerceSelectedDate, changed: OnSelectedDateChanged);

    /// <summary>The "today" reference cell (<c>:today</c>); defaults to the system date at construction (settable for determinism).</summary>
    public static readonly StyledProperty<DateOnly> TodayProperty =
        UIProperty.Register<Calendar, DateOnly>(nameof(Today), changed: OnTodayChanged);

    /// <summary>The first column's day of week; defaults to the current culture's <see cref="DateTimeFormatInfo.FirstDayOfWeek"/>.</summary>
    public static readonly StyledProperty<DayOfWeek> FirstDayOfWeekProperty =
        UIProperty.Register<Calendar, DayOfWeek>(nameof(FirstDayOfWeek), changed: OnFirstDayOfWeekChanged);

    /// <summary>The earliest selectable/displayable date (<c>null</c> = unbounded).</summary>
    public static readonly StyledProperty<DateOnly?> DisplayDateStartProperty =
        UIProperty.Register<Calendar, DateOnly?>(nameof(DisplayDateStart), changed: OnBoundsChanged);

    /// <summary>The latest selectable/displayable date (<c>null</c> = unbounded).</summary>
    public static readonly StyledProperty<DateOnly?> DisplayDateEndProperty =
        UIProperty.Register<Calendar, DateOnly?>(nameof(DisplayDateEnd), changed: OnBoundsChanged);

    /// <summary>Whether the <c>:today</c> cell is highlighted (default <c>true</c>).</summary>
    public static readonly StyledProperty<bool> IsTodayHighlightedProperty =
        UIProperty.Register<Calendar, bool>(nameof(IsTodayHighlighted), defaultValue: true,
            changed: static (s, _, _) => (s as Calendar)?.RebuildMonthView());

    /// <summary>Non-selectable date ranges (<c>:blackout</c>, disabled). <c>null</c>/empty = none.</summary>
    public static readonly StyledProperty<IReadOnlyList<CalendarDateRange>?> BlackoutDatesProperty =
        UIProperty.Register<Calendar, IReadOnlyList<CalendarDateRange>?>(nameof(BlackoutDates), changed: OnBlackoutChanged);

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

    /// <inheritdoc cref="DisplayDateStartProperty"/>
    public DateOnly? DisplayDateStart { get => GetValue(DisplayDateStartProperty); set => SetValue(DisplayDateStartProperty, value); }

    /// <inheritdoc cref="DisplayDateEndProperty"/>
    public DateOnly? DisplayDateEnd { get => GetValue(DisplayDateEndProperty); set => SetValue(DisplayDateEndProperty, value); }

    /// <inheritdoc cref="IsTodayHighlightedProperty"/>
    public bool IsTodayHighlighted { get => GetValue(IsTodayHighlightedProperty); set => SetValue(IsTodayHighlightedProperty, value); }

    /// <inheritdoc cref="BlackoutDatesProperty"/>
    public IReadOnlyList<CalendarDateRange>? BlackoutDates { get => GetValue(BlackoutDatesProperty); set => SetValue(BlackoutDatesProperty, value); }

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
            case Key.Home: SelectInMonth(forward: true); break;
            case Key.End: SelectInMonth(forward: false); break;
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

    // Step from the anchor, clamped to [DisplayDateStart, DisplayDateEnd] (and the representable DateOnly range), and
    // skipping blacked-out dates in the direction of travel (WPF parity).
    private void MoveSelection(DateOnly anchor, int deltaDays)
    {
        var lo = EffectiveMin();
        var hi = EffectiveMax();
        var clamped = Math.Clamp(anchor.DayNumber + deltaDays, lo, hi);
        if (NearestSelectable(clamped, deltaDays >= 0 ? 1 : -1, lo, hi) is { } target)
            SelectAndFocus(target);
    }

    // Select the first/last selectable day of the shown month within [Start, End] (Home / End).
    private void SelectInMonth(bool forward)
    {
        var lo = Math.Max(EffectiveMin(), new DateOnly(DisplayDate.Year, DisplayDate.Month, 1).DayNumber);
        var hi = Math.Min(EffectiveMax(), new DateOnly(DisplayDate.Year, DisplayDate.Month, DateTime.DaysInMonth(DisplayDate.Year, DisplayDate.Month)).DayNumber);
        if (lo > hi)
            return;
        if (NearestSelectable(forward ? lo : hi, forward ? 1 : -1, lo, hi) is { } target)
            SelectAndFocus(target);
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

    private static void OnBoundsChanged(UIObject sender, DateOnly? oldValue, DateOnly? newValue)
    {
        if (sender is not Calendar c)
            return;
        c.CoerceValue(DisplayDateProperty);  // re-clamp the view into the new range
        c.CoerceValue(SelectedDateProperty); // clear a now-out-of-range selection
        c.RebuildMonthView();
    }

    private static void OnBlackoutChanged(UIObject sender, IReadOnlyList<CalendarDateRange>? oldValue, IReadOnlyList<CalendarDateRange>? newValue)
    {
        if (sender is not Calendar c)
            return;
        c.CoerceValue(SelectedDateProperty); // clear a now-blacked-out selection
        c.RebuildMonthView();
    }

    private static DateOnly CoerceDisplayDate(UIObject sender, DateOnly value)
    {
        if (sender is not Calendar c)
            return value;
        var dayNumber = value.DayNumber;
        if (c.DisplayDateStart is { } start)
            dayNumber = Math.Max(dayNumber, start.DayNumber);
        if (c.DisplayDateEnd is { } end)
            dayNumber = Math.Min(dayNumber, end.DayNumber);
        return DateOnly.FromDayNumber(Math.Clamp(dayNumber, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber));
    }

    private static DateOnly? CoerceSelectedDate(UIObject sender, DateOnly? value)
        => sender is Calendar c && value is { } d && !c.IsSelectable(d) ? null : value; // out-of-range / blackout ⇒ cleared

    // ── bounds + blackout helpers ───────────────────────────────────────────────────────────────────────

    private int EffectiveMin() => DisplayDateStart?.DayNumber ?? DateOnly.MinValue.DayNumber;
    private int EffectiveMax() => DisplayDateEnd?.DayNumber ?? DateOnly.MaxValue.DayNumber;
    private bool IsInRange(DateOnly d) => d.DayNumber >= EffectiveMin() && d.DayNumber <= EffectiveMax();

    private bool IsBlackoutDate(DateOnly d)
    {
        if (BlackoutDates is not { } ranges)
            return false;
        foreach (var range in ranges)
            if (range.Contains(d))
                return true;
        return false;
    }

    private bool IsSelectable(DateOnly d) => IsInRange(d) && !IsBlackoutDate(d);

    // The nearest selectable date to `fromDayNumber` within [lo, hi], preferring direction `dir` then the reverse.
    private DateOnly? NearestSelectable(int fromDayNumber, int dir, int lo, int hi)
    {
        for (var n = fromDayNumber; n >= lo && n <= hi; n += dir)
            if (!IsBlackoutDate(DateOnly.FromDayNumber(n)))
                return DateOnly.FromDayNumber(n);
        for (var n = fromDayNumber - dir; n >= lo && n <= hi; n -= dir)
            if (!IsBlackoutDate(DateOnly.FromDayNumber(n)))
                return DateOnly.FromDayNumber(n);
        return null;
    }

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
                var blackout = !IsSelectable(date); // out of [Start,End] or in a BlackoutDates range
                var cell = new CalendarDayButton
                {
                    Date = date,
                    Content = date.Day.ToString(CultureInfo.CurrentCulture),
                    Width = CellWidth,
                    IsToday = IsTodayHighlighted && date == Today,
                    IsSelected = SelectedDate == date,
                    IsInactive = date.Month != DisplayDate.Month,
                    IsBlackout = blackout,
                    IsEnabled = !blackout, // disabled ⇒ ButtonBase raises no Click, so it can't be picked
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

/// <summary>An inclusive date range for <see cref="Calendar.BlackoutDates"/> (order-agnostic).</summary>
public readonly record struct CalendarDateRange(DateOnly Start, DateOnly End)
{
    /// <summary>A single-date range.</summary>
    public CalendarDateRange(DateOnly date) : this(date, date) { }

    /// <summary>Whether <paramref name="date"/> falls within [<see cref="Start"/>, <see cref="End"/>] (inclusive).</summary>
    public bool Contains(DateOnly date)
    {
        var lo = Start <= End ? Start : End;
        var hi = Start <= End ? End : Start;
        return date >= lo && date <= hi;
    }
}
