using System.Threading;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The outcome of an <see cref="ITaskDialogService.ShowAsync"/> call.
/// </summary>
/// <param name="Button">
/// The activated button (one of <see cref="TaskDialogRequest.Buttons"/>), or null when the dialog was
/// dismissed without a choice — force-closed by a canceled <see cref="CancellationToken"/> or closed
/// through window chrome. Callers should treat dismissal as cancel.
/// </param>
/// <param name="VerificationChecked">
/// The verification checkbox's final state. When the implementation does not render the checkbox
/// (M1), this echoes <see cref="TaskDialogRequest.VerificationChecked"/> unchanged.
/// </param>
public sealed record TaskDialogResult(TaskDialogButton? Button, bool VerificationChecked)
{
    /// <summary>Whether the dialog was dismissed without a button choice (treat as cancel).</summary>
    public bool IsDismissed => Button is null;
}
