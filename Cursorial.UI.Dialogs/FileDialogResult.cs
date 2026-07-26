using System.Threading;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The outcome of <see cref="FileOpenDialog.ShowAsync"/> / <see cref="FileSaveDialog.ShowAsync"/> — the WPF
/// <c>FileDialog.FileName</c> + <c>FilterIndex</c> pair, except that "the user cancelled" is expressed in the
/// payload rather than in a separate <c>bool?</c> return: an EMPTY payload <b>is</b> dismissal
/// (<see cref="IsDismissed"/>), exactly as a null <see cref="TaskDialogResult.Button"/> is.
/// </summary>
/// <param name="FilePath">
/// The chosen file's provider-rooted absolute path, or <see langword="null"/> when the dialog was dismissed
/// without a choice — Cancel, the window chrome's ✕, or a canceled <see cref="CancellationToken"/>. Callers
/// should treat dismissal as cancel.
/// </param>
/// <param name="Filter">
/// The type filter that was selected when the dialog closed, or <see langword="null"/> on dismissal. A Save
/// caller reads this to learn which FORMAT was asked for — the extension on <see cref="FilePath"/> answers
/// that too, but only when the filter names one (<see cref="FileDialogFilter.DefaultExtension"/>), so the
/// filter itself is the authoritative answer. Compare it against a well-known static
/// (<see cref="FileDialogFilter.AllFiles"/>) or against the instances you supplied — records compare by value.
/// </param>
public sealed record FileDialogResult(string? FilePath, FileDialogFilter? Filter)
{
    /// <summary>
    /// The shared empty payload — what every dismissal path returns. A singleton so a caller may compare by
    /// reference as well as by value, and so the no-throw cancellation contract has exactly one answer to hand
    /// back (see <see cref="FileOpenDialog.ShowAsync"/>).
    /// </summary>
    public static FileDialogResult Dismissed { get; } = new(null, null);

    /// <summary>Whether the dialog was dismissed without a choice (treat as cancel).</summary>
    public bool IsDismissed => FilePath is null;
}
