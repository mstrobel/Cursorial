using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

public sealed class TaskDialogService(UIApplication application) : ITaskDialogService
{
    private readonly UIApplication _application = application ?? throw new ArgumentNullException(nameof(application));

    /// <inheritdoc/>
    public async Task<TaskDialogResult> ShowAsync(TaskDialogRequest request,
                                                  CancellationToken cancellationToken = default)
    {
        return await TaskDialog.ShowAsync(_application, request, cancellationToken);
    }
}