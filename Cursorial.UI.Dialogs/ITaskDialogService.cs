using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The task-dialog seam (architecture §3.2 resolution 3, FB-12): M1/M3/M4 prompts — the
/// unsaved-changes save triad, paste-to-table offer, Open-replace confirmation — call this interface;
/// M1 backs it with <see cref="MessageBoxTaskDialogService"/> and M6 swaps in the full
/// <c>TaskDialog</c> implementation with callers unchanged.
/// </summary>
public interface ITaskDialogService
{
    /// <summary>
    /// Shows the dialog modally and completes with the activated button when it closes.
    /// </summary>
    /// <param name="request">The dialog in the FB-12 shape.</param>
    /// <param name="cancellationToken">
    /// Force-closes the dialog when canceled. Cancellation never throws through this call — it
    /// completes with a dismissed result (<see cref="TaskDialogResult.IsDismissed"/>), the same
    /// outcome as a window-chrome close.
    /// </param>
    Task<TaskDialogResult> ShowAsync(TaskDialogRequest request, CancellationToken cancellationToken = default);
}
