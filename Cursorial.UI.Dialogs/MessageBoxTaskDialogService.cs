using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The M1 <see cref="ITaskDialogService"/> implementation (architecture §3.2 resolution 3): maps
/// requests onto the ported <see cref="MessageBox"/> until M6's full <c>TaskDialog</c> replaces it.
/// The mapping is honest to the FB-12 shape but deliberately lossy where MessageBox has no
/// vocabulary:
/// <list type="bullet">
/// <item>The main instruction and content render as one word-wrapped message (blank-line separated).</item>
/// <item>Command-link-style buttons render as plain dialog buttons — labels kept,
/// <see cref="TaskDialogButton.Explanation"/> dropped.</item>
/// <item><see cref="TaskDialogRequest.Severity"/> is ignored (no icon vocabulary in MessageBox).</item>
/// <item>The verification checkbox (<see cref="TaskDialogRequest.VerificationText"/>) is not rendered;
/// <see cref="TaskDialogResult.VerificationChecked"/> echoes the request's initial value unchanged.</item>
/// <item><see cref="TaskDialogRequest.ExpandedInformation"/> is not rendered.</item>
/// </list>
/// </summary>
/// <param name="application">The owning application the dialogs are shown over.</param>
public sealed class MessageBoxTaskDialogService(UIApplication application) : ITaskDialogService
{
    private readonly UIApplication _application =
        application ?? throw new ArgumentNullException(nameof(application));

    /// <inheritdoc/>
    public async Task<TaskDialogResult> ShowAsync(TaskDialogRequest request,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<TaskDialogButton> buttons =
            request.Buttons is { Count: > 0 } ? request.Buttons : [TaskDialogButton.Ok];

        var defaultIndex = IndexOf(buttons, static b => b.IsDefault);
        var cancelIndex = IndexOf(buttons, static b => b.IsCancel);

        if (defaultIndex < 0)
            defaultIndex = 0; // no marked default: the first button takes Enter, like MessageBox

        var definitions = new MessageBoxButtonDefinition[buttons.Count];

        for (var i = 0; i < buttons.Count; i++)
            definitions[i] = new MessageBoxButtonDefinition(buttons[i].Label,
                                                            IsDefault: i == defaultIndex,
                                                            IsCancel: i == cancelIndex);

        var message = string.IsNullOrEmpty(request.Content)
            ? request.MainInstruction
            : request.MainInstruction + "\n\n" + request.Content;

        var chosen = await MessageBox.ShowCoreAsync(_application,
                                                    message,
                                                    request.Title,
                                                    definitions,
                                                    focusedIndex: defaultIndex,
                                                    cancellationToken).ConfigureAwait(false);

        return new TaskDialogResult(chosen >= 0 ? buttons[chosen] : null, request.VerificationChecked);
    }

    private static int IndexOf(IReadOnlyList<TaskDialogButton> buttons, Func<TaskDialogButton, bool> predicate)
    {
        for (var i = 0; i < buttons.Count; i++)
        {
            if (predicate(buttons[i]))
                return i;
        }

        return -1;
    }
}
