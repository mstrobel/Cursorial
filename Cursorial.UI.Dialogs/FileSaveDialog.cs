using System;
using System.Threading;
using System.Threading.Tasks;

using Cursorial.UI.Input;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The Save As dialog — the same chrome as <see cref="FileOpenDialog"/>, re-tasked for writing: a New Folder
/// action (<c>Alt+N</c>) that drops an inline editor into the listing, a "Save as type" selector whose
/// extension completes an extension-less typed name, and the overwrite confirmation when the name collides.
/// The terminal counterpart of WPF's <c>Microsoft.Win32.SaveFileDialog</c> and Avalonia's
/// <c>IStorageProvider.SaveFilePickerAsync</c>.
/// <para>
/// <b>The overwrite prompt is a real <see cref="TaskDialog"/>.</b> It is a modal question with a main
/// instruction, supporting content, a warning severity and a two-button answer — precisely what the dialog
/// suite already has — so hand-rolling a second modal look here would produce a prompt that drifts from every
/// other one in the application. The view-model exposes it as a seam
/// (<see cref="FileDialogViewModel.OverwriteConfirmation"/>) so the rule stays testable without a window.
/// </para>
/// </summary>
public sealed class FileSaveDialog : Window
{
    private readonly FileDialogViewModel _model;
    private readonly FileDialogView _view;

    /// <summary>Control themes resolve exact-key, so a <see cref="Window"/> subclass must opt into the base
    /// window chrome, or it has no template at all and measures 0×0.</summary>
    protected override object ControlThemeKey => typeof(Window);

    private FileSaveDialog(FileDialogViewModel model, string title)
    {
        _model = model;
        _view = new FileDialogView(model);

        Title = title;
        DataContext = model;
        Content = _view.Root;

        FileOpenDialog.ApplyDialogAppearance(this);

        model.Completed += (_, result) => Close(result);
        ContentRendered += OnContentRendered;
    }

    /// <summary>
    /// Shows the dialog modally over <paramref name="application"/>'s active surface and completes with the
    /// named file when it closes.
    /// </summary>
    /// <param name="application">
    /// The owning application. When called off the UI thread the call marshals to its dispatcher.
    /// </param>
    /// <param name="request">The dialog's configuration — the hierarchy, the starting directory, the filters.</param>
    /// <param name="cancellationToken">
    /// Force-closes the dialog when canceled. Cancellation never throws through this call — it completes with
    /// <see cref="FileDialogResult.Dismissed"/>, the same outcome as Cancel or a window-chrome close.
    /// </param>
    public static async Task<FileDialogResult> ShowAsync(UIApplication application,
                                                         FileSaveDialogRequest request,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(request);

        // The TaskDialog threading contract verbatim — see FileOpenDialog.ShowAsync for the reasoning.
        if (application.Dispatcher.CheckAccess())
            return await ShowOnUIThreadAsync(application, request, viaMarshal: false, cancellationToken).ConfigureAwait(false);

        try
        {
            return await application.Dispatcher
                                    .InvokeAsync(() => ShowOnUIThreadAsync(application, request, viaMarshal: true, cancellationToken))
                                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FileDialogResult.Dismissed;
        }
    }

    private static async Task<FileDialogResult> ShowOnUIThreadAsync(UIApplication application,
                                                                    FileSaveDialogRequest request,
                                                                    bool viaMarshal,
                                                                    CancellationToken cancellationToken)
    {
        if (viaMarshal && UIApplication.Current?.WindowManager is null)
            return FileDialogResult.Dismissed;

        var model = new FileDialogViewModel(request)
                    {
                        OverwriteConfirmation = (path, token) => ConfirmOverwriteAsync(application, request.FileSystem, path, token)
                    };

        try
        {
            var dialog = new FileSaveDialog(model, request.Title);

            await model.InitializeAsync(cancellationToken).ConfigureAwait(true);

            return await dialog.ShowDialogAsync<FileDialogResult>(cancellationToken).ConfigureAwait(true)
                   ?? FileDialogResult.Dismissed;
        }
        catch (OperationCanceledException)
        {
            return FileDialogResult.Dismissed;
        }
        finally
        {
            model.Dispose();
        }
    }

    /// <summary>The design page's amber "already exists… replace?" prompt, as a task dialog.</summary>
    private static async Task<bool> ConfirmOverwriteAsync(UIApplication application,
                                                          IFileSystemProvider fileSystem,
                                                          string path,
                                                          CancellationToken cancellationToken)
    {
        var replace = new TaskDialogButton("replace", "_Replace") { IsDefault = true };

        var result = await TaskDialog.ShowAsync(application,
                                                new TaskDialogRequest($"{LeafOf(fileSystem, path)} already exists in this folder.")
                                                {
                                                    Title = "Confirm Save As",
                                                    Content = "Do you want to replace it?",
                                                    Severity = TaskDialogSeverity.Warning,
                                                    Buttons = [replace, TaskDialogButton.Cancel]
                                                },
                                                cancellationToken)
                                     .ConfigureAwait(true);

        // Dismissal (Esc, ✕, a canceled token) is NOT consent: the save is abandoned and the dialog stays up,
        // which is the only reading of "the user did not answer" that cannot lose someone's file.
        return result.Button == replace;
    }

    private static string LeafOf(IFileSystemProvider fileSystem, string path)
    {
        var index = path.LastIndexOfAny([fileSystem.DirectorySeparator, '/', '\\']);
        return index >= 0 && index + 1 < path.Length ? path[(index + 1)..] : path;
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // The accelerators tunnel so they beat the controls' own keys; see FileOpenDialog.OnPreviewKeyDown.
        if (!e.Handled && _view.HandlePreviewKey(e))
            e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!e.Handled && _view.HandleKey(e))
            e.Handled = true;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;

        // Save opens with the NAME field focused and its stem selected — the user is here to type a name, and
        // the listing is context. (Open focuses the listing, where its user is here to pick.)
        _view.FocusFileName();
    }
}
