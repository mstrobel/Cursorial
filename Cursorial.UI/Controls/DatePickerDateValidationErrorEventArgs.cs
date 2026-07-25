namespace Cursorial.UI.Controls;

/// <summary>
/// The <see cref="DatePicker.DateValidationError"/> payload — the text a user typed into an editable
/// <see cref="DatePicker"/> that could not be parsed as a date (the WPF <c>DatePickerDateValidationErrorEventArgs</c> /
/// Avalonia <c>CalendarDatePicker.DateValidationError</c> analog). The field silently reverts to the last valid
/// <see cref="DatePicker.SelectedDate"/>; a host can surface an error cue from this notification.
/// </summary>
public sealed class DatePickerDateValidationErrorEventArgs(string text) : EventArgs
{
    /// <summary>The unparseable text the user entered.</summary>
    public string Text { get; } = text;
}
