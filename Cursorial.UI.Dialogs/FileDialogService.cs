using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The production <see cref="IFileDialogService"/>: forwards to the <see cref="FileOpenDialog"/> /
/// <see cref="FileSaveDialog"/> statics against a fixed <see cref="UIApplication"/>, exactly as
/// <see cref="TaskDialogService"/> forwards to <see cref="TaskDialog"/>. It adds no behaviour — binding the
/// application once is the whole of it, and that is what lets a caller hold the seam without also holding the
/// application.
/// </summary>
/// <param name="application">The owning application; calls made off its UI thread marshal to its dispatcher.</param>
public sealed class FileDialogService(UIApplication application) : IFileDialogService
{
    private readonly UIApplication _application = application ?? throw new ArgumentNullException(nameof(application));

    /// <inheritdoc/>
    public Task<FileDialogResult> ShowOpenAsync(FileOpenDialogRequest request, CancellationToken cancellationToken = default)
        => FileOpenDialog.ShowAsync(_application, request, cancellationToken);

    /// <inheritdoc/>
    public Task<FileDialogResult> ShowSaveAsync(FileSaveDialogRequest request, CancellationToken cancellationToken = default)
        => FileSaveDialog.ShowAsync(_application, request, cancellationToken);
}
