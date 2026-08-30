using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Cursorial.Input;
using Cursorial.Rendering.Text;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A date picker surface (design doc §12 — the WPF <c>Calendar</c> analog). In the default <see cref="DisplayMode"/>
/// (<see cref="CalendarMode.Month"/>) it shows the <see cref="DisplayDate"/>'s month as a 7-column grid (a
/// culture-ordered day-of-week header row + up to six week rows of <see cref="CalendarDayButton"/> cells); clicking a
/// day (or arrow-key navigation) sets <see cref="SelectedDate"/>. The header label is a button that <b>drills up</b>
/// (Month → <see cref="CalendarMode.Year"/> → <see cref="CalendarMode.Decade"/>), and the Year/Decade views are 4×3
/// grids of <see cref="CalendarButton"/> cells (the 12 months of a year / the 10 years of a decade + leading/trailing
/// fill) — clicking one <b>drills down</b>. The grid restamps <c>:today</c>/<c>:selected</c>/<c>:inactive</c> per cell.
/// Culture drives the day-name abbreviations (<see cref="DateTimeFormatInfo.ShortestDayNames"/>) and the default
/// <see cref="FirstDayOfWeek"/>. Date bounds (<see cref="DisplayDateStart"/>/<see cref="DisplayDateEnd"/>) +
/// <see cref="BlackoutDates"/> clamp the view and gate selection.
/// </summary>
[TemplatePart(PartMonthView, typeof(StackPanel))]
[TemplatePart(PartPreviousButton, typeof(Button))]
[TemplatePart(PartNextButton, typeof(Button))]
[TemplatePart(PartHeaderText, typeof(TextBlock))]
[TemplatePart(PartHeaderButton, typeof(Button))]
public class Calendar : Control
{
    private const string PartMonthView = "PART_MonthView";
    private const string PartPreviousButton = "PART_PreviousButton";
    private const string PartNextButton = "PART_NextButton";
    private const string PartHeaderText = "PART_HeaderText";
    private const string PartHeaderButton = "PART_HeaderButton";

    private const int CellWidth = 4;     // a uniform 7-column day grid
    private const int ModeCellWidth = 7; // the Year/Decade cells (4 columns × 7 ≈ the day grid's 7 × 4)
    private const int ModeColumns = 4;   // the Year/Decade grid is 4 columns × 3 rows = 12 cells
    private const int ModeRows = 3;
    private const int ModeVerticalMargin = 1;

    private static readonly DateOnly MinMonth = new(1, 1, 1);
    private static readonly DateOnly MaxMonth = new(9999, 12, 1);

    /// <summary>The month shown (any day in it; the grid renders that month). Defaults to <see cref="Today"/>; clamped to [<see cref="DisplayDateStart"/>, <see cref="DisplayDateEnd"/>].</summary>
    public static readonly StyledProperty<DateOnly> DisplayDateProperty =
        UIProperty.Register<Calendar, DateOnly>(nameof(DisplayDate), coerce: CoerceDisplayDate,
                                                changed: OnDisplayDateChanged);

    /// <summary>The selected date (<c>null</c> = none), two-way bindable; a day in an adjacent month moves <see cref="DisplayDate"/>. An out-of-range / blacked-out value coerces to <c>null</c>.</summary>
    public static readonly StyledProperty<DateOnly?> SelectedDateProperty =
        UIProperty.Register<Calendar, DateOnly?>(nameof(SelectedDate), coerce: CoerceSelectedDate,
                                                 changed: OnSelectedDateChanged);

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
                                            changed: static (s, _, _) => (s as Calendar)?.RebuildView());

    /// <summary>Non-selectable date ranges (<c>:blackout</c>, disabled). <c>null</c>/empty = none.</summary>
    public static readonly StyledProperty<IReadOnlyList<CalendarDateRange>?> BlackoutDatesProperty =
        UIProperty.Register<Calendar, IReadOnlyList<CalendarDateRange>?>(
            nameof(BlackoutDates), changed: OnBlackoutChanged);

    /// <summary>The drill-down level shown: <see cref="CalendarMode.Month"/> (the day grid, default),
    /// <see cref="CalendarMode.Year"/> (the 12 months), or <see cref="CalendarMode.Decade"/> (the decade's years).</summary>
    public static readonly StyledProperty<CalendarMode> DisplayModeProperty =
        UIProperty.Register<Calendar, CalendarMode>(nameof(DisplayMode), changed: OnDisplayModeChanged);

    /// <summary>How dates may be selected (the WPF <c>SelectionMode</c> analog); default <see cref="CalendarSelectionMode.SingleDate"/>.</summary>
    public static readonly StyledProperty<CalendarSelectionMode> SelectionModeProperty =
        UIProperty.Register<Calendar, CalendarSelectionMode>(nameof(SelectionMode), changed: OnSelectionModeChanged);

    /// <inheritdoc cref="SelectedDatesProperty"/>
    private static readonly UIPropertyKey<CalendarSelectedDatesCollection> SelectedDatesPropertyKey =
        UIProperty.RegisterReadOnly<Calendar, CalendarSelectedDatesCollection>(nameof(SelectedDates));

    /// <summary>The set of selected dates (the WPF <c>SelectedDates</c> analog). Directly mutable only when
    /// <see cref="SelectionMode"/> is <see cref="CalendarSelectionMode.SingleRange"/> or
    /// <see cref="CalendarSelectionMode.MultipleRange"/>; <see cref="SelectedDate"/> is the primary (first) date.</summary>
    public static readonly StyledProperty<CalendarSelectedDatesCollection> SelectedDatesProperty =
        SelectedDatesPropertyKey.Property;

    private readonly Dictionary<DateOnly, CalendarDayButton> _cells = new();
    private readonly List<CalendarButton> _modeButtons = new(); // the Year/Decade view's month/year cells
    private bool _suppressRebuild; // coalesce the DisplayDate + DisplayMode writes of one drill into a single rebuild
    private StackPanel? _monthView;
    private Button? _previousButton;
    private Button? _nextButton;
    private Button? _headerButton;
    private TextBlock? _headerText;

    private DateOnly? _rangeAnchor;              // the fixed end of an in-progress range (Shift-extend / drag)
    private List<DateOnly> _activeRange = new(); // dates in the range currently being built (replaced on extend)
    private bool _dragging;                      // a mouse range-drag is in flight (the Calendar holds capture)
    private DateOnly? _lastDragDate;             // the last cell a drag extended to (dedupe per-move work)
    private bool _syncingSelection;              // reentrancy guard between SelectedDate ⇄ SelectedDates

    /// <summary>Creates a calendar showing the current month.</summary>
    public Calendar()
    {
        SelectedDates = new CalendarSelectedDatesCollection(this);
        var today = DateOnly.FromDateTime(DateTime.Now);
        SetValue(TodayProperty, today);
        SetValue(DisplayDateProperty, today);
        SetValue(FirstDayOfWeekProperty, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
    }

    static Calendar()
    {
        PaddingProperty.OverrideDefaultValue<Calendar>(new(1, 0));
    }

    /// <inheritdoc cref="DisplayDateProperty"/>
    public DateOnly DisplayDate
    {
        get => GetValue(DisplayDateProperty);
        set => SetValue(DisplayDateProperty, value);
    }

    /// <inheritdoc cref="SelectedDateProperty"/>
    public DateOnly? SelectedDate
    {
        get => GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <inheritdoc cref="TodayProperty"/>
    public DateOnly Today
    {
        get => GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    /// <inheritdoc cref="FirstDayOfWeekProperty"/>
    public DayOfWeek FirstDayOfWeek
    {
        get => GetValue(FirstDayOfWeekProperty);
        set => SetValue(FirstDayOfWeekProperty, value);
    }

    /// <inheritdoc cref="DisplayDateStartProperty"/>
    public DateOnly? DisplayDateStart
    {
        get => GetValue(DisplayDateStartProperty);
        set => SetValue(DisplayDateStartProperty, value);
    }

    /// <inheritdoc cref="DisplayDateEndProperty"/>
    public DateOnly? DisplayDateEnd
    {
        get => GetValue(DisplayDateEndProperty);
        set => SetValue(DisplayDateEndProperty, value);
    }

    /// <inheritdoc cref="IsTodayHighlightedProperty"/>
    public bool IsTodayHighlighted
    {
        get => GetValue(IsTodayHighlightedProperty);
        set => SetValue(IsTodayHighlightedProperty, value);
    }

    /// <inheritdoc cref="BlackoutDatesProperty"/>
    public IReadOnlyList<CalendarDateRange>? BlackoutDates
    {
        get => GetValue(BlackoutDatesProperty);
        set => SetValue(BlackoutDatesProperty, value);
    }

    /// <inheritdoc cref="DisplayModeProperty"/>
    public CalendarMode DisplayMode
    {
        get => GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    /// <inheritdoc cref="SelectionModeProperty"/>
    public CalendarSelectionMode SelectionMode
    {
        get => GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    /// <inheritdoc cref="SelectedDatesProperty"/>
    public CalendarSelectedDatesCollection SelectedDates
    {
        get => GetValue(SelectedDatesProperty);
        private set => SetValue(SelectedDatesPropertyKey, value);
    }

    /// <summary>Raised when <see cref="DisplayMode"/> changes (old → new) — a drill up/down or a direct assignment.</summary>
    public event EventHandler<CalendarModeChangedEventArgs>? DisplayModeChanged;

    /// <summary>Raised when <see cref="SelectedDate"/> changes (old → new) — including arrow-key <i>browse</i> moves.</summary>
    public event EventHandler<CalendarSelectedDateChangedEventArgs>? SelectedDateChanged;

    /// <summary>Raised when a date is <b>committed</b> by a click or Enter/Space on a day cell (the
    /// confirm gesture, vs an arrow-key browse). Fires even when the committed date equals the current selection — a
    /// drop-down host (<see cref="DatePicker"/>) closes on this, not on <see cref="SelectedDateChanged"/>.</summary>
    public event EventHandler<CalendarSelectedDateChangedEventArgs>? DateCommitted;

    /// <summary>Raised when the <see cref="SelectedDates"/> set changes — a range/multi selection edit, or the sync
    /// from a <see cref="SelectedDate"/> assignment. Carries the dates added to / removed from the selection.</summary>
    public event EventHandler<CalendarSelectedDatesChangedEventArgs>? SelectedDatesChanged;

    /// <summary>Raised when <see cref="DisplayDate"/> changes (old → new) — navigation, a drill, or a direct assignment.</summary>
    public event EventHandler<CalendarDateChangedEventArgs>? DisplayDateChanged;

    // Test/inspection seams (the grids are built in code, so these expose them for assertions).
    internal CalendarDayButton? CellForDate(DateOnly date) => _cells.GetValueOrDefault(date);
    internal int DayCellCount => _cells.Count;
    internal Button? PreviousButton => _previousButton;
    internal Button? NextButton => _nextButton;
    internal Button? HeaderButton => _headerButton;
    internal TextBlock? HeaderTextPart => _headerText;
    internal IReadOnlyList<CalendarButton> ModeButtons => _modeButtons;

    internal CalendarButton? ModeButtonForRepresentative(DateOnly representative) =>
        _modeButtons.Find(b => b.RepresentativeDate == representative);

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_previousButton is not null)
            _previousButton.Click -= OnPreviousClick;

        if (_nextButton is not null)
            _nextButton.Click -= OnNextClick;

        if (_headerButton is not null)
            _headerButton.Click -= OnHeaderClick;

        _monthView = GetTemplatePart<StackPanel>(PartMonthView);
        _headerText = GetTemplatePart<TextBlock>(PartHeaderText);
        _headerButton = GetTemplatePart<Button>(PartHeaderButton);
        _previousButton = GetTemplatePart<Button>(PartPreviousButton);
        _nextButton = GetTemplatePart<Button>(PartNextButton);

        if (_previousButton is not null)
            _previousButton.Click += OnPreviousClick;

        if (_nextButton is not null)
            _nextButton.Click += OnNextClick;

        if (_headerButton is not null)
            _headerButton.Click += OnHeaderClick;

        RebuildView();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_previousButton is not null)
            _previousButton.Click -= OnPreviousClick;

        if (_nextButton is not null)
            _nextButton.Click -= OnNextClick;

        if (_headerButton is not null)
            _headerButton.Click -= OnHeaderClick;

        _monthView = null;
        _headerText = null;
        _headerButton = null;
        _previousButton = null;
        _nextButton = null;
        _cells.Clear();
        _modeButtons.Clear();
        base.OnTemplateDetaching(old);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        var handled = DisplayMode == CalendarMode.Month ? HandleMonthKey(e) : HandleModeKey(e);

        if (handled)
            e.Handled = true;
    }

    // Month-mode keyboard: arrows move the selected day (±1 / ±7), Home/End the first/last selectable day of the
    // month, PageUp/PageDown the displayed month (refocusing a cell in the new month so keys keep routing here).
    // Ctrl+arrow moves keyboard focus WITHOUT touching the selection, and Ctrl+Space toggles the focused day —
    // together the keyboard-only path to disjoint ranges (arrow to a new day, toggle it, Shift+arrow to grow it).
    private bool HandleMonthKey(KeyEventArgs e)
    {
        var extend = (e.Modifiers & KeyModifiers.Shift) != 0;

        // Ctrl+arrow is a pure focus move; Shift wins when both are held, so Ctrl+Shift+arrow still extends.
        var focusOnly = (e.Modifiers & KeyModifiers.Control) != 0 && !extend;

        switch (e.Key)
        {
            case Key.LeftArrow:  MoveDay(e, -1, extend, focusOnly); break;
            case Key.RightArrow: MoveDay(e, 1, extend, focusOnly); break;
            case Key.UpArrow:    MoveDay(e, -7, extend, focusOnly); break;
            case Key.DownArrow:  MoveDay(e, 7, extend, focusOnly); break;
            case Key.Home:       SelectInMonth(forward: true); break;
            case Key.End:        SelectInMonth(forward: false); break;

            case Key.PageUp:
                Navigate(-1);
                FocusViewCell();
                break;

            case Key.PageDown:
                Navigate(1);
                FocusViewCell();
                break;

            default:
                // Ctrl+Space toggles the focused day's selected state. A day cell ignores Ctrl+Space (only the
                // modifier-free spacebar activates it — ND10), so it bubbles here; both wire forms of the spacebar
                // are matched via ButtonBase.IsActivationSpace. The toggle runs the standard per-mode gesture (the
                // same one a Ctrl-click runs), so in MultipleRange it adds/removes the day and the other modes
                // degrade exactly as they do for a Ctrl-click.
                if (ButtonBase.IsActivationSpace(e, KeyModifiers.Control))
                {
                    ToggleFocusedDay(ResolveAnchorDate(e));
                    return true;
                }

                return false; // not a calendar-nav key — leave unhandled
        }

        return true;
    }

    // One arrow keypress: Ctrl+arrow moves keyboard focus only (selection untouched); otherwise move the selection
    // (a plain arrow re-selects the target, Shift+arrow extends the range in a range mode).
    private void MoveDay(KeyEventArgs e, int deltaDays, bool extend, bool focusOnly)
    {
        var anchor = ResolveAnchorDate(e);

        if (focusOnly)
            MoveFocusToDay(anchor, deltaDays);
        else
            MoveSelection(anchor, deltaDays, extend);
    }

    // Year/Decade-mode keyboard: arrows move focus among the 4-column cell grid (±1 / ±ModeColumns, skipping disabled
    // cells, clamped within the shown page), Home/End jump to the first/last enabled cell, PageUp/PageDown page the
    // mode unit (∓1 year / ∓10 years). Enter/Space drill down via ButtonBase.Click on the focused cell.
    private bool HandleModeKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.LeftArrow:  MoveModeFocus(e, -1); break;
            case Key.RightArrow: MoveModeFocus(e, 1); break;
            case Key.UpArrow:    MoveModeFocus(e, -ModeColumns); break;
            case Key.DownArrow:  MoveModeFocus(e, ModeColumns); break;
            case Key.Home:       FocusModeEdge(forward: true); break;
            case Key.End:        FocusModeEdge(forward: false); break;

            case Key.PageUp:
                Navigate(-1);
                FocusViewCell();
                break;

            case Key.PageDown:
                Navigate(1);
                FocusViewCell();
                break;

            default: return false;
        }

        return true;
    }

    /// <summary>Moves keyboard focus onto the best cell of the current view (the selected day's cell, or the first of
    /// the shown month, in Month mode) — e.g. when a <see cref="DatePicker"/> drops this calendar open. A no-op until
    /// the grid has been laid out.</summary>
    public void FocusDate() => FocusViewCell();

    // Move keyboard focus onto the best cell of the current view (after a mode/page change rebuilt the grid).
    private void FocusViewCell()
    {
        if (DisplayMode == CalendarMode.Month)
            FocusDisplayMonthCell();
        else
            BestModeButton()?.Focus(FocusNavigationMethod.Directional);
    }

    // The best Year/Decade cell to land focus on: the selected month/year, else today's, else the first enabled cell.
    private CalendarButton? BestModeButton()
    {
        CalendarButton? selected = null, today = null, firstEnabled = null;

        foreach (var b in _modeButtons)
        {
            if (!b.IsEnabled)
                continue;

            firstEnabled ??= b;

            if (b.IsSelected)
                selected ??= b;

            if (b.IsToday)
                today ??= b;
        }

        return selected ?? today ?? firstEnabled;
    }

    // Step focus among the Year/Decade cells by `delta` (±1 horizontally, ±ModeColumns vertically), skipping disabled
    // cells in the direction of travel and clamping within the shown page. The skip steps by the FULL `delta` stride
    // (not its sign) so a vertical move stays in its column when it has to hop over a disabled cell (audit CD-P2I-1).
    private void MoveModeFocus(KeyEventArgs e, int delta)
    {
        var idx = CurrentModeIndex(e);

        if (idx < 0)
        {
            FocusViewCell();
            return;
        }

        for (var target = idx + delta; target >= 0 && target < _modeButtons.Count; target += delta)
            if (_modeButtons[target].IsEnabled)
            {
                _modeButtons[target].Focus(FocusNavigationMethod.Directional);
                return;
            }
    }

    private void FocusModeEdge(bool forward)
    {
        if (forward)
        {
            foreach (var b in _modeButtons)
            {
                if (b.IsEnabled)
                {
                    b.Focus(FocusNavigationMethod.Directional);
                    return;
                }
            }
        }
        else
        {
            for (var i = _modeButtons.Count - 1; i >= 0; i--)
            {
                if (_modeButtons[i].IsEnabled)
                {
                    _modeButtons[i].Focus(FocusNavigationMethod.Directional);
                    return;
                }
            }
        }
    }

    // The index of the focused Year/Decade cell (walking the source up to a CalendarButton), or −1 if none.
    private int CurrentModeIndex(KeyEventArgs e)
    {
        for (UIElement? node = e.OriginalSource; node is not null; node = node.VisualParent)
        {
            if (node is CalendarButton cell)
            {
                var i = _modeButtons.IndexOf(cell);

                if (i >= 0)
                    return i;
            }
        }

        return -1;
    }

    // The keyboard-cursor anchor: the focused day cell if one holds focus (the ListBox.ResolveCurrent idiom), else a
    // day in the SHOWN month — never a stale SelectedDate/Today from another month, which would yank the view back
    // on the first arrow after a PageUp/PageDown/prev-next (audit CD-P2D-1).
    private DateOnly ResolveAnchorDate(KeyEventArgs e)
    {
        for (UIElement? node = e.OriginalSource; node is not null; node = node.VisualParent)
        {
            if (node is CalendarDayButton cell)
                return cell.Date;
        }

        return InMonthAnchor();
    }

    private DateOnly InMonthAnchor()
    {
        if (SelectedDate is {} s && s.Year == DisplayDate.Year && s.Month == DisplayDate.Month)
            return s;

        if (Today.Year == DisplayDate.Year && Today.Month == DisplayDate.Month)
            return Today;

        return new DateOnly(DisplayDate.Year, DisplayDate.Month, 1);
    }

    // Step from the anchor, clamped to [DisplayDateStart, DisplayDateEnd] (and the representable DateOnly range), and
    // skipping blacked-out dates in the direction of travel (WPF parity).
    private void MoveSelection(DateOnly anchor, int deltaDays, bool extend)
    {
        var lo = EffectiveMin();
        var hi = EffectiveMax();
        var clamped = Math.Clamp(anchor.DayNumber + deltaDays, lo, hi);

        if (NearestSelectable(clamped, deltaDays >= 0 ? 1 : -1, lo, hi) is not {} target)
            return;

        // Shift+arrow (in a range mode) extends the range from the fixed anchor to the new target; a plain arrow
        // collapses to a single selection at the target and re-anchors there.
        if (extend && SelectionMode is CalendarSelectionMode.SingleRange or CalendarSelectionMode.MultipleRange)
        {
            _rangeAnchor ??= anchor;

            ApplyDaySelection(target, shift: true, ctrl: false);

            if (_cells.TryGetValue(target, out var cell))
                cell.Focus(FocusNavigationMethod.Directional);
        }
        else
        {
            SelectAndFocus(target);
            _rangeAnchor = target;
            _activeRange.Clear();
            _activeRange.Add(target);
        }
    }

    // Ctrl+arrow: move keyboard focus to the adjacent selectable day (the same target an arrow would pick, blackouts
    // skipped, view-follow included) but leave the selection and its anchor untouched — the keyboard-only route to a
    // fresh range. The anchor is fixed later by Ctrl+Space (the toggle), so Shift+arrow then grows the range there.
    private void MoveFocusToDay(DateOnly anchor, int deltaDays)
    {
        var lo = EffectiveMin();
        var hi = EffectiveMax();
        var clamped = Math.Clamp(anchor.DayNumber + deltaDays, lo, hi);

        if (NearestSelectable(clamped, deltaDays >= 0 ? 1 : -1, lo, hi) is {} target)
            FocusDay(target);
    }

    // Move keyboard focus onto a day cell, bringing its month into view first if it is outside the shown grid
    // (mirroring the SelectedDate view-follow in OnSelectedDateChanged — but selection-neutral, since a DisplayDate
    // change only rebuilds the grid).
    private void FocusDay(DateOnly target)
    {
        if (target.Year != DisplayDate.Year || target.Month != DisplayDate.Month)
            SetCurrentValue(DisplayDateProperty, new DateOnly(target.Year, target.Month, 1)); // rebuilds the grid

        if (_cells.TryGetValue(target, out var cell))
            cell.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible
    }

    // Ctrl+Space: toggle the focused day through the standard per-mode selection gesture (ApplyDaySelection with the
    // Ctrl flag — the same one a Ctrl-click runs), then keep focus on it. In MultipleRange this adds/removes the day
    // (disjoint ranges by keyboard); SingleDate/SingleRange select it; None is a no-op. SingleDate rebuilds the grid,
    // so re-resolve the cell before focusing.
    private void ToggleFocusedDay(DateOnly date)
    {
        if (!IsSelectable(date))
            return;

        ApplyDaySelection(date, shift: false, ctrl: true);

        if (_cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Directional);
    }

    // Select the first/last selectable day of the shown month within [Start, End] (Home / End).
    private void SelectInMonth(bool forward)
    {
        var lo = Math.Max(EffectiveMin(), new DateOnly(DisplayDate.Year, DisplayDate.Month, 1).DayNumber);

        var hi = Math.Min(EffectiveMax(),
                          new DateOnly(DisplayDate.Year, DisplayDate.Month,
                                       DateTime.DaysInMonth(DisplayDate.Year, DisplayDate.Month)).DayNumber);

        if (lo > hi)
            return;

        if (NearestSelectable(forward ? lo : hi, forward ? 1 : -1, lo, hi) is {} target)
            SelectAndFocus(target);
    }

    // Select a date (keyboard / Home / End) and move keyboard focus onto its freshly-stamped cell.
    private void SelectAndFocus(DateOnly date)
    {
        SelectDate(date);

        if (_cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Directional); // ⇒ :focus-visible
    }

    private void SelectDate(DateOnly date) =>
        SetCurrentValue(SelectedDateProperty, date); // SetCurrentValue preserves a two-way binding

    // After a keyboard month change (which rebuilt the grid and dropped the focused cell), move focus onto a cell in
    // the new month — the selected day if it is shown, else the first — so subsequent keys keep routing to the Calendar.
    private void FocusDisplayMonthCell()
    {
        // Focus a SELECTABLE cell — a disabled (out-of-range/blackout) target would no-op and leave focus nowhere.
        if (BestFocusDate() is {} date && _cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Directional);
    }

    // The best cell to land keyboard focus on in the shown month: the selection (if shown + selectable), else today
    // (if shown + selectable), else the first selectable day of the month within [Start, End].
    private DateOnly? BestFocusDate()
    {
        if (SelectedDate is {} s && s.Year == DisplayDate.Year && s.Month == DisplayDate.Month && IsSelectable(s))
            return s;

        if (Today.Year == DisplayDate.Year && Today.Month == DisplayDate.Month && IsSelectable(Today))
            return Today;

        var lo = Math.Max(EffectiveMin(), new DateOnly(DisplayDate.Year, DisplayDate.Month, 1).DayNumber);

        var hi = Math.Min(EffectiveMax(),
                          new DateOnly(DisplayDate.Year, DisplayDate.Month,
                                       DateTime.DaysInMonth(DisplayDate.Year, DisplayDate.Month)).DayNumber);

        return lo > hi ? null : NearestSelectable(lo, 1, lo, hi);
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

    // Move the displayed year by `years`, clamped to the representable [0001, 9999] range (AddYears throws past it).
    private void ChangeYears(int years)
    {
        var newYear = Math.Clamp(DisplayDate.Year + years, DateOnly.MinValue.Year, DateOnly.MaxValue.Year);

        if (newYear == DisplayDate.Year)
            return;

        SetCurrentValue(DisplayDateProperty, new DateOnly(newYear, DisplayDate.Month, 1));
    }

    // prev/next (and PageUp/PageDown) page by the mode's unit: ∓1 month / ∓1 year / ∓10 years.
    private void Navigate(int direction)
    {
        switch (DisplayMode)
        {
            case CalendarMode.Year:   ChangeYears(direction); break;
            case CalendarMode.Decade: ChangeYears(direction * 10); break;
            default:                  ChangeMonth(direction); break;
        }
    }

    private void OnPreviousClick(object? sender, ClickEventArgs e) => Navigate(-1);
    private void OnNextClick(object? sender, ClickEventArgs e) => Navigate(1);

    // Header click drills UP one level (Month → Year → Decade); Decade is the top, so it is a no-op there.
    private void OnHeaderClick(object? sender, ClickEventArgs e) => DrillUp();

    /// <summary>Drills the view up one level: <see cref="CalendarMode.Month"/> → <see cref="CalendarMode.Year"/> →
    /// <see cref="CalendarMode.Decade"/> (a no-op at <see cref="CalendarMode.Decade"/>, the top level).</summary>
    public void DrillUp()
    {
        switch (DisplayMode)
        {
            case CalendarMode.Month: SetCurrentValue(DisplayModeProperty, CalendarMode.Year); break;
            case CalendarMode.Year:  SetCurrentValue(DisplayModeProperty, CalendarMode.Decade); break;
        }
    }

    // A Year/Decade cell click drills DOWN: Year → Month on the clicked month; Decade → Year on the clicked year.
    // Neither selects a date (only a day click does). The DisplayDate + DisplayMode writes coalesce into one rebuild.
    private void OnModeCellClick(object? sender, ClickEventArgs e)
    {
        var representative = ((CalendarButton) sender!).RepresentativeDate;
        var target = DisplayMode == CalendarMode.Decade ? CalendarMode.Year : CalendarMode.Month;

        _suppressRebuild = true;

        try
        {
            SetCurrentValue(DisplayDateProperty, representative);
        }
        finally
        {
            _suppressRebuild = false;
        }

        SetCurrentValue(DisplayModeProperty, target); // one rebuild, in the new mode at the new date

        FocusViewCell(); // the rebuild detached the clicked cell — land focus in the new view so keys keep routing
    }

    private void OnDayClick(object? sender, ClickEventArgs e)
    {
        var date = ((CalendarDayButton) sender!).Date;
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
    {
        if (sender is not Calendar calendar)
            return;

        calendar.RebuildView();
        calendar.DisplayDateChanged?.Invoke(calendar, new CalendarDateChangedEventArgs(oldValue, newValue));
    }

    private static void OnSelectionModeChanged(UIObject sender, CalendarSelectionMode oldValue,
                                               CalendarSelectionMode newValue)
    {
        if (sender is not Calendar calendar)
            return;

        calendar.ReconcileSelectionToMode();
        calendar.RebuildView();
    }

    private static void OnTodayChanged(UIObject sender, DateOnly oldValue, DateOnly newValue)
        => (sender as Calendar)?.RebuildView();

    private static void OnFirstDayOfWeekChanged(UIObject sender, DayOfWeek oldValue, DayOfWeek newValue)
        => (sender as Calendar)?.RebuildView();

    private static void OnDisplayModeChanged(UIObject sender, CalendarMode oldValue, CalendarMode newValue)
    {
        if (sender is not Calendar c)
            return;

        c.RebuildView();
        c.DisplayModeChanged?.Invoke(c, new CalendarModeChangedEventArgs(oldValue, newValue));
    }

    private static void OnBoundsChanged(UIObject sender, DateOnly? oldValue, DateOnly? newValue)
    {
        if (sender is not Calendar c)
            return;

        c.CoerceValue(DisplayDateProperty); // re-clamp the view into the new range
        c.PruneUnselectable();              // drop now-out-of-range dates (syncs SelectedDate to the new primary)
        c.RebuildView();
    }

    private static void OnBlackoutChanged(UIObject sender, IReadOnlyList<CalendarDateRange>? oldValue,
                                          IReadOnlyList<CalendarDateRange>? newValue)
    {
        if (sender is not Calendar c)
            return;

        c.PruneUnselectable(); // drop now-blacked-out dates (syncs SelectedDate to the new primary)
        c.RebuildView();
    }

    private static DateOnly CoerceDisplayDate(UIObject sender, DateOnly value)
    {
        if (sender is not Calendar c)
            return value;

        var dayNumber = value.DayNumber;

        if (c.DisplayDateStart is {} start)
            dayNumber = Math.Max(dayNumber, start.DayNumber);

        if (c.DisplayDateEnd is {} end)
            dayNumber = Math.Min(dayNumber, end.DayNumber);

        return DateOnly.FromDayNumber(Math.Clamp(dayNumber, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber));
    }

    private static DateOnly? CoerceSelectedDate(UIObject sender, DateOnly? value)
    {
        if (sender is not Calendar c || value is not {} d)
            return value;

        if (c.SelectionMode == CalendarSelectionMode.None)
            return null; // no selection is possible in None mode

        return c.IsSelectable(d) ? value : null; // out-of-range / blackout ⇒ cleared
    }

    // ── bounds + blackout helpers ───────────────────────────────────────────────────────────────────────

    private int EffectiveMin() => DisplayDateStart?.DayNumber ?? DateOnly.MinValue.DayNumber;
    private int EffectiveMax() => DisplayDateEnd?.DayNumber ?? DateOnly.MaxValue.DayNumber;
    private bool IsInRange(DateOnly d) => d.DayNumber >= EffectiveMin() && d.DayNumber <= EffectiveMax();

    private bool IsBlackoutDate(DateOnly d)
    {
        if (BlackoutDates is not {} ranges)
            return false;

        foreach (var range in ranges)
        {
            if (range.Contains(d))
                return true;
        }

        return false;
    }

    private bool IsSelectable(DateOnly d) => IsInRange(d) && !IsBlackoutDate(d);

    // The nearest selectable date to `fromDayNumber` within [lo, hi], preferring direction `dir` then the reverse.
    private DateOnly? NearestSelectable(int fromDayNumber, int dir, int lo, int hi)
    {
        for (var n = fromDayNumber; n >= lo && n <= hi; n += dir)
        {
            if (!IsBlackoutDate(DateOnly.FromDayNumber(n)))
                return DateOnly.FromDayNumber(n);
        }

        for (var n = fromDayNumber - dir; n >= lo && n <= hi; n -= dir)
        {
            if (!IsBlackoutDate(DateOnly.FromDayNumber(n)))
                return DateOnly.FromDayNumber(n);
        }

        return null;
    }

    // ── multi / range selection (WPF SelectedDates parity) ───────────────────────────────────────────────

    /// <summary>Throws when <see cref="SelectedDates"/> can't be mutated directly in the current mode (WPF: only the multi modes).</summary>
    internal void VerifyMultiSelect()
    {
        if (SelectionMode is CalendarSelectionMode.None or CalendarSelectionMode.SingleDate)
            throw new InvalidOperationException(
                "The SelectedDates collection can only be changed when SelectionMode is SingleRange or MultipleRange. " +
                "Use SelectedDate for None / SingleDate.");
    }

    /// <summary>Validates one date about to enter <see cref="SelectedDates"/> — mode capacity + selectability (WPF's collection guards).</summary>
    internal void ValidateAddSelectedDate(int currentCount, DateOnly date)
    {
        switch (SelectionMode)
        {
            case CalendarSelectionMode.None:
                throw new InvalidOperationException("Dates cannot be selected when SelectionMode is None.");

            case CalendarSelectionMode.SingleDate when currentCount >= 1:
                throw new InvalidOperationException(
                    "Only one date can be selected when SelectionMode is SingleDate. Use SelectedDate.");
        }

        if (!IsInRange(date))
            throw new ArgumentOutOfRangeException(nameof(date),
                                                  "The date is outside the DisplayDateStart / DisplayDateEnd range.");

        if (IsBlackoutDate(date))
            throw new InvalidOperationException("The date is in a BlackoutDates range and cannot be selected.");
    }

    /// <summary>Whether <paramref name="date"/> is currently selectable (in range and not blacked out).</summary>
    internal bool IsSelectableDate(DateOnly date) => IsSelectable(date);

    private bool IsDateSelected(DateOnly date) => SelectedDates.Contains(date);

    // Whether a Year/Decade cell should read :selected — any selected date in that month (Year) / year (Decade).
    private bool IsRepresentativeSelected(DateOnly representative)
    {
        foreach (var s in SelectedDates)
        {
            if (DisplayMode == CalendarMode.Year
                    ? s.Year == representative.Year && s.Month == representative.Month
                    : s.Year == representative.Year)
            {
                return true;
            }
        }

        return false;
    }

    // The collection tells us it changed (single edit or a coalesced batch): keep SelectedDate (the primary) in sync,
    // restamp the visible cells in place (no rebuild — preserves a drag capture / keyboard focus), and raise the event.
    internal void OnSelectedDatesCollectionChanged(IReadOnlyList<DateOnly> added, IReadOnlyList<DateOnly> removed)
    {
        var wasSync = _syncingSelection;
        _syncingSelection = true;

        try
        {
            SetCurrentValue(SelectedDateProperty, SelectedDates.Count > 0 ? SelectedDates[0] : (DateOnly?) null);
        }
        finally
        {
            _syncingSelection = wasSync;
        }

        RestampSelection();
        SelectedDatesChanged?.Invoke(this, new CalendarSelectedDatesChangedEventArgs(added, removed));
    }

    // Restamp :selected across the current view's cells without rebuilding the grid.
    private void RestampSelection()
    {
        if (DisplayMode == CalendarMode.Month)
        {
            foreach (var (date, cell) in _cells)
                cell.IsSelected = IsDateSelected(date);
        }
        else
        {
            foreach (var button in _modeButtons)
                button.IsSelected = IsRepresentativeSelected(button.RepresentativeDate);
        }
    }

    // Drop any selected dates that are no longer selectable (a bounds / blackout change) as one batched edit.
    private void PruneUnselectable()
    {
        if (SelectedDates.Count == 0)
            return;

        List<DateOnly>? drop = null;

        foreach (var d in SelectedDates)
        {
            if (!IsSelectable(d))
                (drop ??= new List<DateOnly>()).Add(d);
        }

        if (drop is not null)
        {
            SelectedDates.Edit(() =>
                               {
                                   foreach (var d in drop)
                                       SelectedDates.Remove(d);
                               });
        }
    }

    // Narrow / keep the current selection to fit a new SelectionMode.
    private void ReconcileSelectionToMode()
    {
        switch (SelectionMode)
        {
            case CalendarSelectionMode.None:
                SelectedDates.Clear();
                _rangeAnchor = null;
                _activeRange.Clear();
                break;

            case CalendarSelectionMode.SingleDate:
                if (SelectedDates.Count > 1)
                    SelectedDates.ReplaceAll(new[] { SelectedDates[0] });

                ResetRangeAnchor();
                break;

            case CalendarSelectionMode.SingleRange:
                if (SelectedDates.Count > 1 && !IsContiguousSelection())
                    SelectedDates.ReplaceAll(new[] { SelectedDates[0] });

                ResetRangeAnchor();
                break;

            case CalendarSelectionMode.MultipleRange:
                ResetRangeAnchor();
                break;
        }
    }

    private void ResetRangeAnchor()
    {
        _rangeAnchor = SelectedDates.Count > 0 ? SelectedDates[0] : null;
        _activeRange.Clear();

        if (SelectedDates.Count > 0)
            _activeRange.AddRange(SelectedDates);
    }

    private bool IsContiguousSelection()
    {
        if (SelectedDates.Count <= 1)
            return true;

        var sorted = new List<DateOnly>(SelectedDates);
        sorted.Sort();

        for (var i = 1; i < sorted.Count; i++)
            if (sorted[i].DayNumber != sorted[i - 1].DayNumber + 1)
                return false;

        return true;
    }

    // The contiguous run of selectable dates from `anchor` toward `target` (inclusive), stopping before the first
    // non-selectable day so a range never straddles a blackout / out-of-range gap (WPF's interactive-range behavior).
    private List<DateOnly> ContiguousSelectableRange(DateOnly anchor, DateOnly target)
    {
        var run = new List<DateOnly>();

        if (!IsSelectable(anchor))
            return run;

        var step = target.DayNumber >= anchor.DayNumber ? 1 : -1;

        for (var n = anchor.DayNumber; n >= DateOnly.MinValue.DayNumber && n <= DateOnly.MaxValue.DayNumber; n += step)
        {
            var d = DateOnly.FromDayNumber(n);

            if (!IsSelectable(d))
                break;

            run.Add(d);

            if (n == target.DayNumber)
                break;
        }

        return run;
    }

    // The central day-selection gesture: applies a click / drag / Shift-arrow to the selection per SelectionMode.
    private void ApplyDaySelection(DateOnly date, bool shift, bool ctrl)
    {
        switch (SelectionMode)
        {
            case CalendarSelectionMode.None:
                return;

            case CalendarSelectionMode.SingleDate:
                SelectDate(date);
                _rangeAnchor = date;
                _activeRange.Clear();
                _activeRange.Add(date);
                return;

            case CalendarSelectionMode.SingleRange:
                if (shift && _rangeAnchor is {} singleAnchor)
                {
                    SelectedDates.ReplaceAll(ContiguousSelectableRange(singleAnchor, date));
                }
                else
                {
                    _rangeAnchor = date;
                    SelectedDates.ReplaceAll(ContiguousSelectableRange(date, date));
                }

                _activeRange.Clear();
                _activeRange.AddRange(SelectedDates);
                return;

            case CalendarSelectionMode.MultipleRange:
                if (shift && _rangeAnchor is {} multiAnchor)
                {
                    var newRange = ContiguousSelectableRange(multiAnchor, date);
                    var previous = _activeRange;

                    SelectedDates.Edit(() =>
                                       {
                                           foreach (var d in previous)
                                               if (!newRange.Contains(d))
                                                   SelectedDates.Remove(d);

                                           foreach (var d in newRange)
                                               SelectedDates.Add(d);
                                       });

                    _activeRange = newRange;
                }
                else if (ctrl)
                {
                    if (SelectedDates.Contains(date))
                        SelectedDates.Remove(date);
                    else if (IsSelectable(date))
                        SelectedDates.Add(date);

                    _rangeAnchor = date;
                    _activeRange.Clear();

                    if (SelectedDates.Contains(date))
                        _activeRange.Add(date);
                }
                else
                {
                    _rangeAnchor = date;
                    SelectedDates.ReplaceAll(ContiguousSelectableRange(date, date));
                    _activeRange.Clear();
                    _activeRange.AddRange(SelectedDates);
                }

                return;
        }
    }

    // Hit-test the pointer to a day cell in the month grid (day cells are CellWidth wide, one row tall).
    private DateOnly? DayCellAt(MouseEventArgs e)
    {
        foreach (var (date, cell) in _cells)
        {
            var p = e.GetPosition(cell);

            if (p.Column >= 0 && p.Column < CellWidth && p.Row >= 0 && p.Row < 1)
                return date;
        }

        return null;
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;

        _dragging = false;
        _lastDragDate = null;
        ReleaseMouseCapture();
    }

    // ── mouse range selection (Shift/Ctrl + drag; the day button's own click is suppressed in range modes) ──

    /// <inheritdoc/>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (e.Handled || e.Button != MouseButton.Left || DisplayMode != CalendarMode.Month)
            return;

        if (SelectionMode is not (CalendarSelectionMode.SingleRange or CalendarSelectionMode.MultipleRange))
            return;

        if (DayCellAt(e) is not {} date || !IsSelectable(date))
            return;

        var shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        var ctrl = (e.Modifiers & KeyModifiers.Control) != 0;

        Focus(FocusNavigationMethod.Pointer);
        ApplyDaySelection(date, shift, ctrl);

        _dragging = true;
        _lastDragDate = date;
        CaptureMouse();

        if (_cells.TryGetValue(date, out var cell))
            cell.Focus(FocusNavigationMethod.Pointer);

        e.Handled = true; // suppress the day button's own press/click — range selection is Calendar-driven
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
            return;

        if ((e.ButtonsHeld & MouseButtons.Left) == 0)
        {
            EndDrag();
            return;
        }

        if (DayCellAt(e) is {} date && date != _lastDragDate && IsSelectable(date))
        {
            _lastDragDate = date;
            ApplyDaySelection(date, shift: true, ctrl: false); // extend the active range to the hovered day
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_dragging && e.Button == MouseButton.Left)
            EndDrag();
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        _lastDragDate = null;
    }

    private static void OnSelectedDateChanged(UIObject sender, DateOnly? oldValue, DateOnly? newValue)
    {
        if (sender is not Calendar calendar)
            return;

        // A change that originated from the SelectedDates collection (the sync push below): don't reconcile back or
        // rebuild — the collection path already restamped in place — just surface the singular SelectedDateChanged.
        if (calendar._syncingSelection)
        {
            calendar.SelectedDateChanged?.Invoke(
                calendar, new CalendarSelectedDateChangedEventArgs(oldValue, newValue));

            return;
        }

        // An external SelectedDate assignment (or binding push): make the collection hold exactly this primary date.
        calendar._syncingSelection = true;

        try
        {
            if (newValue is {} sel)
                calendar.SelectedDates.ReplaceAll(new[] { sel });
            else
                calendar.SelectedDates.Clear();

            calendar._rangeAnchor = newValue;
            calendar._activeRange.Clear();

            if (newValue is {} anchor)
                calendar._activeRange.Add(anchor);
        }
        finally
        {
            calendar._syncingSelection = false;
        }

        // Selecting a day in an adjacent month moves the view (DisplayDate change rebuilds); otherwise restamp in place.
        if (newValue is {} d && (d.Year != calendar.DisplayDate.Year || d.Month != calendar.DisplayDate.Month))
            calendar.SetCurrentValue(DisplayDateProperty, new DateOnly(d.Year, d.Month, 1));
        else
            calendar.RebuildView();

        calendar.SelectedDateChanged?.Invoke(calendar, new CalendarSelectedDateChangedEventArgs(oldValue, newValue));
    }

    // Whether any selectable day (in [Start, End] and not blacked out) exists in [first, last] — gates a Year/Decade
    // month/year cell. Reuses the day-level NearestSelectable scan, bounded by the (month/year) window length.
    private bool HasSelectableDay(DateOnly first, DateOnly last)
    {
        var lo = Math.Max(EffectiveMin(), first.DayNumber);
        var hi = Math.Min(EffectiveMax(), last.DayNumber);
        return lo <= hi && NearestSelectable(lo, 1, lo, hi) is not null;
    }

    // Rebuild the PART_MonthView host for the current DisplayMode. Idempotent; no-op pre-template; suppressed mid-drill.
    private void RebuildView()
    {
        if (_suppressRebuild)
            return;

        switch (DisplayMode)
        {
            case CalendarMode.Year:   RebuildYearView(); break;
            case CalendarMode.Decade: RebuildDecadeView(); break;
            default:                  RebuildMonthView(); break;
        }
    }

    // Repopulate the 7×7 grid for DisplayDate's month: a culture-ordered day-of-week header row + six week rows of
    // CalendarDayButton cells (leading/trailing adjacent-month days marked :inactive). Idempotent; no-op pre-template.
    private void RebuildMonthView()
    {
        if (_monthView is null)
            return;

        _monthView.Children.Clear();
        _cells.Clear();
        _modeButtons.Clear();

        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;

        var header = new StackPanel { Orientation = Orientation.Horizontal };

        for (var i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek) (((int) FirstDayOfWeek + i) % 7);

            header.Children.Add(new TextBlock
                                {
                                    Text = dtf.ShortestDayNames[(int) dow], Width = CellWidth,
                                    TextAlignment = TextAlignment.Center
                                });
        }

        _monthView.Children.Add(header);

        var firstOfMonth = new DateOnly(DisplayDate.Year, DisplayDate.Month, 1);
        var lead = ((int) firstOfMonth.DayOfWeek - (int) FirstDayOfWeek + 7) % 7;

        lead = Math.Min(
            lead, firstOfMonth.DayNumber); // don't underflow DateOnly.MinValue (Jan 0001) computing the start

        var startDayNumber = firstOfMonth.DayNumber - lead;

        for (var week = 0; week < 6; week++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            for (var day = 0; day < 7; day++)
            {
                var dayNumber = startDayNumber + week * 7 + day;

                if (dayNumber > DateOnly.MaxValue.DayNumber)
                {
                    row.Children.Add(new TextBlock
                                     { Width = CellWidth }); // a blank slot past the representable range (Dec 9999)

                    continue;
                }

                var date = DateOnly.FromDayNumber(dayNumber);
                var blackout = !IsSelectable(date); // out of [Start,End] or in a BlackoutDates range

                var cell = new CalendarDayButton
                           {
                               Date = date,
                               Content = date.Day.ToString(CultureInfo.CurrentCulture),
                               Width = CellWidth,
                               Padding = new(1, 0, 0, 0),
                               IsToday = IsTodayHighlighted && date == Today &&
                                         !blackout, // a blacked-out/out-of-range today isn't highlighted
                               IsSelected = IsDateSelected(date),
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

    // The Year view: a 4×3 grid of the 12 months of DisplayDate.Year (abbreviated names). :today/:selected mark the
    // month containing Today/SelectedDate; a month entirely outside [Start, End] (or fully blacked out) is disabled.
    private void RebuildYearView()
    {
        if (_monthView is null)
            return;

        _monthView.Children.Clear();
        _cells.Clear();
        _modeButtons.Clear();

        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        var year = DisplayDate.Year;

        for (var row = 0; row < ModeRows; row++)
        {
            var rowPanel = new StackPanel
                           {
                               Orientation = Orientation.Horizontal,
                               Margin = new(0, row == 0 ? ModeVerticalMargin : 0, 0, ModeVerticalMargin)
                           };

            for (var col = 0; col < ModeColumns; col++)
            {
                var month = row * ModeColumns + col + 1; // 1..12
                var first = new DateOnly(year, month, 1);
                var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
                var selectable = HasSelectableDay(first, last);

                var cell = new CalendarButton
                           {
                               RepresentativeDate = first,
                               Content = dtf.AbbreviatedMonthNames[month - 1],
                               HorizontalContentAlignment = HorizontalAlignment.Center,
                               Width = ModeCellWidth,
                               IsToday = IsTodayHighlighted && Today.Year == year && Today.Month == month && selectable,
                               IsSelected = IsRepresentativeSelected(first),
                               IsBlackout = !selectable,
                               IsEnabled = selectable,
                           };

                cell.Click += OnModeCellClick;
                rowPanel.Children.Add(cell);
                _modeButtons.Add(cell);
            }

            _monthView.Children.Add(rowPanel);
        }

        if (_headerText is not null)
            _headerText.Text = year.ToString(CultureInfo.CurrentCulture);
    }

    // The Decade view: a 4×3 grid of the decade's years (decadeStart−1 … decadeStart+10 — the leading/trailing pair
    // :inactive). :today/:selected mark the year containing Today/SelectedDate; an unselectable year is disabled.
    private void RebuildDecadeView()
    {
        if (_monthView is null)
            return;

        _monthView.Children.Clear();
        _cells.Clear();
        _modeButtons.Clear();

        var decadeStart = DisplayDate.Year / 10 * 10;

        for (var row = 0; row < ModeRows; row++)
        {
            var rowPanel = new StackPanel
                           {
                               Orientation = Orientation.Horizontal,
                               Margin = new(0, row == 0 ? ModeVerticalMargin : 0, 0, ModeVerticalMargin)
                           };

            for (var col = 0; col < ModeColumns; col++)
            {
                var offset = row * ModeColumns + col - 1; // -1 .. 10
                var year = decadeStart + offset;

                if (year < DateOnly.MinValue.Year || year > DateOnly.MaxValue.Year)
                {
                    rowPanel.Children.Add(new TextBlock { Width = ModeCellWidth }); // an unrepresentable year slot
                    continue;
                }

                var first = new DateOnly(year, 1, 1);
                var last = new DateOnly(year, 12, 31);
                var selectable = HasSelectableDay(first, last);

                var cell = new CalendarButton
                           {
                               RepresentativeDate = first,
                               Content = year.ToString(CultureInfo.CurrentCulture),
                               HorizontalContentAlignment = HorizontalAlignment.Center,
                               Width = ModeCellWidth,
                               IsToday = IsTodayHighlighted && Today.Year == year && selectable,
                               IsSelected = IsRepresentativeSelected(first),
                               IsInactive =
                                   offset is < 0 or > 9, // outside the shown decade (the leading/trailing fill)
                               IsBlackout = !selectable,
                               IsEnabled = selectable,
                           };

                cell.Click += OnModeCellClick;
                rowPanel.Children.Add(cell);
                _modeButtons.Add(cell);
            }

            _monthView.Children.Add(rowPanel);
        }

        if (_headerText is not null)
        {
            // Clamp the displayed range to representable years so a years-1..9 decade reads "1-9", not the
            // nonexistent "0-9" (decadeStart = 0 there); mirrors the per-cell representable guard above (audit CD-P2I-1).
            var lo = Math.Max(decadeStart, DateOnly.MinValue.Year);
            var hi = Math.Min(decadeStart + 9, DateOnly.MaxValue.Year);
            _headerText.Text = $"{lo}-{hi}";
        }
    }
}

/// <summary>The drill-down level a <see cref="Calendar"/> shows (the WPF <c>CalendarMode</c> analog).</summary>
public enum CalendarMode
{
    /// <summary>The day grid for one month (the default).</summary>
    Month,

    /// <summary>The 12 months of one year (click a month to drill into <see cref="Month"/>).</summary>
    Year,

    /// <summary>The 10 years of one decade (click a year to drill into <see cref="Year"/>).</summary>
    Decade,
}

/// <summary>The <see cref="Calendar.SelectedDateChanged"/> payload — the old and new selected dates.</summary>
public sealed class CalendarSelectedDateChangedEventArgs(DateOnly? oldDate, DateOnly? newDate) : EventArgs
{
    /// <summary>The previously selected date (null if none).</summary>
    public DateOnly? OldDate { get; } = oldDate;

    /// <summary>The newly selected date (null if cleared).</summary>
    public DateOnly? NewDate { get; } = newDate;
}

/// <summary>The <see cref="Calendar.DisplayModeChanged"/> payload — the old and new <see cref="CalendarMode"/>.</summary>
public sealed class CalendarModeChangedEventArgs(CalendarMode oldMode, CalendarMode newMode) : EventArgs
{
    /// <summary>The previous display mode.</summary>
    public CalendarMode OldMode { get; } = oldMode;

    /// <summary>The new display mode.</summary>
    public CalendarMode NewMode { get; } = newMode;
}

/// <summary>An inclusive date range for <see cref="Calendar.BlackoutDates"/> (order-agnostic).</summary>
public readonly record struct CalendarDateRange(DateOnly Start, DateOnly End)
{
    /// <summary>A single-date range.</summary>
    public CalendarDateRange(DateOnly date) : this(date, date) {}

    /// <summary>Whether <paramref name="date"/> falls within [<see cref="Start"/>, <see cref="End"/>] (inclusive).</summary>
    public bool Contains(DateOnly date)
    {
        var lo = Start <= End ? Start : End;
        var hi = Start <= End ? End : Start;
        return date >= lo && date <= hi;
    }
}

/// <summary>How a <see cref="Calendar"/> lets dates be selected (the WPF <c>CalendarSelectionMode</c> analog).</summary>
public enum CalendarSelectionMode
{
    /// <summary>One date at a time (the default) — via <see cref="Calendar.SelectedDate"/>; <see cref="Calendar.SelectedDates"/> holds 0–1.</summary>
    [Display(Name = "Single Date")]
    SingleDate,

    /// <summary>A single contiguous range of dates — Shift+click / Shift+arrow / mouse-drag to extend.</summary>
    [Display(Name = "Single Range")]
    SingleRange,

    /// <summary>Multiple disjoint dates / ranges — Ctrl adds a date, Shift extends the active range.</summary>
    [Display(Name = "Multiple Range")]
    MultipleRange,

    /// <summary>Selection is disabled — no date can be selected.</summary>
    [Display(Name = "None")]
    None
}

/// <summary>The <see cref="Calendar.SelectedDatesChanged"/> payload — the dates added to / removed from the selection.</summary>
public sealed class CalendarSelectedDatesChangedEventArgs(IReadOnlyList<DateOnly> addedDates,
                                                          IReadOnlyList<DateOnly> removedDates) : EventArgs
{
    /// <summary>The dates newly added to the selection.</summary>
    public IReadOnlyList<DateOnly> AddedDates { get; } = addedDates;

    /// <summary>The dates removed from the selection.</summary>
    public IReadOnlyList<DateOnly> RemovedDates { get; } = removedDates;
}

/// <summary>The <see cref="Calendar.DisplayDateChanged"/> payload — the old and new <see cref="Calendar.DisplayDate"/>.</summary>
public sealed class CalendarDateChangedEventArgs(DateOnly oldDate, DateOnly newDate) : EventArgs
{
    /// <summary>The previous display date.</summary>
    public DateOnly OldDate { get; } = oldDate;

    /// <summary>The new display date.</summary>
    public DateOnly NewDate { get; } = newDate;
}