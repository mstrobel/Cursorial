using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The file-dialog seam, mirroring <see cref="ITaskDialogService"/>: a view-model that needs to ask the user
/// for a file takes this interface instead of calling <see cref="FileOpenDialog"/> / <see cref="FileSaveDialog"/>
/// statically, so it can be unit-tested against a stub that returns a canned
/// <see cref="FileDialogResult"/> with no window, no dispatcher and no file system.
/// <para>
/// There is no DI container in this framework — the interface exists for the SEAM, not for a container to
/// resolve. An application composes <see cref="FileDialogService"/> once and hands it to whatever needs it.
/// </para>
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Shows the Open File dialog modally and completes with the chosen file when it closes.
    /// </summary>
    /// <param name="request">The dialog's configuration.</param>
    /// <param name="cancellationToken">
    /// Force-closes the dialog when canceled. Cancellation never throws through this call — it completes with
    /// <see cref="FileDialogResult.Dismissed"/>, the same outcome as Cancel or a window-chrome close.
    /// </param>
    Task<FileDialogResult> ShowOpenAsync(FileOpenDialogRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the Save As dialog modally and completes with the named file when it closes.
    /// </summary>
    /// <param name="request">The dialog's configuration.</param>
    /// <param name="cancellationToken">
    /// Force-closes the dialog when canceled. Cancellation never throws through this call — it completes with
    /// <see cref="FileDialogResult.Dismissed"/>.
    /// </param>
    Task<FileDialogResult> ShowSaveAsync(FileSaveDialogRequest request, CancellationToken cancellationToken = default);
}
