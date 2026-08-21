using System.Collections.ObjectModel;
using System.Collections.Specialized;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.ViewModels;

public class DateControlsViewModel : PageViewModel
{
    public DateControlsViewModel()
    {
        CalendarSelectionModes =
        [
            new Described<CalendarSelectionMode>(CalendarSelectionMode.SingleDate, "Single Date"),
            new Described<CalendarSelectionMode>(CalendarSelectionMode.SingleRange, "Single Range"),
            new Described<CalendarSelectionMode>(CalendarSelectionMode.MultipleRange, "Multiple Range"),
            new Described<CalendarSelectionMode>(CalendarSelectionMode.None, "None")
        ];

        SelectedCalendarSelectionMode = CalendarSelectionModes[2]; // MultipleRange — the demo's pre-rework opening state
    }

    public override string Title => "Date Controls";
    public override string Summary => "A standalone calendar and a DatePicker with a drop-down calendar.";

    public ObservableCollection<DateOnly>? SelectedDates
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field?.CollectionChanged -= OnSelectedDatesCollectionChanged;
            Set(ref field, value);
            field?.CollectionChanged += OnSelectedDatesCollectionChanged;
            RebuildSelectedDateRanges();
        }
    } = new();


    public ObservableCollection<Described<CalendarSelectionMode>> CalendarSelectionModes { get; }

    public Described<CalendarSelectionMode> SelectedCalendarSelectionMode { get; set => Set(ref field, value); }

    public ObservableCollection<DateRange> SelectedDateRanges { get; } = new();

    private void OnSelectedDatesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildSelectedDateRanges();
    }

    private void RebuildSelectedDateRanges()
    {
        SelectedDateRanges.Clear();

        if (SelectedDates is null || SelectedDates.Count == 0)
            return;

        var sortedDates = SelectedDates.OrderBy(d => d).ToList();

        DateOnly rangeStart = sortedDates[0];
        DateOnly rangeEnd = sortedDates[0];

        for (int i = 1; i < sortedDates.Count; i++)
        {
            var currentDate = sortedDates[i];

            // Check if current date is contiguous (next day after rangeEnd)
            if (currentDate == rangeEnd.AddDays(1))
            {
                rangeEnd = currentDate;
            }
            else
            {
                // Non-contiguous, so add the current range and start a new one
                SelectedDateRanges.Add(new DateRange(new CalendarDateRange(rangeStart, rangeEnd)));
                rangeStart = currentDate;
                rangeEnd = currentDate;
            }
        }

        // Add the final range
        SelectedDateRanges.Add(new DateRange(new CalendarDateRange(rangeStart, rangeEnd)));
    }

    public sealed record DateRange(CalendarDateRange Range)
    {
        public DateOnly Start => Range.Start;
        public DateOnly End => Range.End;

        public override string ToString()
        {
            if (Start == End)
                return $"{Start:d MMM yyyy}";

            return $"{Start:d MMM yyyy} – {End:d MMM yyyy}";
        }
    }
}