using System;
using System.Threading;
using System.Threading.Tasks;

using Cursorial.Rendering;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// The Open File dialog: a places rail, a breadcrumb path bar with completion, a sortable listing with
/// switchable views, and a File name + type-filter footer — the terminal counterpart of WPF's
/// <c>Microsoft.Win32.OpenFileDialog</c> and Avalonia's <c>IStorageProvider.OpenFilePickerAsync</c>, except
/// that it is an ordinary <see cref="Window"/> in the application's own tree rather than a shell dialog, so it
/// is themed, keyboard-complete and testable frame by frame.
/// <para>
/// All the behavior lives in <see cref="FileDialogViewModel"/> and all the chrome in
/// <see cref="FileDialogView"/>; this type is the entry point, the window, and the mapping from "the
/// view-model says it is finished" to "the window closes with that result".
/// </para>
/// </summary>
public sealed class FileOpenDialog : Window
{
    private readonly FileDialogView _view;

    /// <summary>Control themes resolve exact-key, so a <see cref="Window"/> subclass must opt into the base
    /// window chrome, or it has no template at all and measures 0×0.</summary>
    protected override object ControlThemeKey => typeof(Window);

    public FileOpenDialog()
        : this(new FileDialogViewModel(
                   new FileOpenDialogRequest("Select File")
                   {
                       FileSystem = PhysicalFileSystemProvider.Instance,
                       InitialDirectory = Environment.CurrentDirectory,
                       ShowHiddenEntries = true
                   }),
               "Select File") {}

    private FileOpenDialog(FileDialogViewModel model, string title)
    {
        _view = new FileDialogView(model);

        ApplyDialogAppearance(this);

        Title = title;
        DataContext = model;
        Content = _view;

        model.Completed += (_, result) => Close(result);
        ContentRendered += OnContentRendered;
    }

    internal static void ApplyDialogAppearance(Window w)
    {
        Size maxSize;

        if (UIApplication.Current?.WindowManager?.ScreenSize is {} sz)
            maxSize = sz;
        else
            maxSize = new Size(80, 24);

        var elasticWidthCap = maxSize.Columns * 2 / 3;
        var elasticHeightCap = maxSize.Rows * 4 / 5;

        w.Width = Math.Clamp(elasticWidthCap, 78, 120);
        w.Height = Math.Clamp(elasticHeightCap, 22, 32);
        w.MinWidth = 48;
        w.MinHeight = 14;

        w.Padding = Margins.Zero;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.AutoFitToViewport = true;
        w.Shadow = WindowShadow.Default;
    }

    /// <summary>
    /// Shows the dialog modally over <paramref name="application"/>'s active surface and completes with the
    /// chosen file when it closes.
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
                                                         FileOpenDialogRequest request,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(request);

        // The TaskDialog threading contract verbatim. On the UI thread the show runs unguarded: a missing
        // WindowManager there is programmer error (a dialog raised before RunAsync composed one) and must fail
        // loudly rather than be silently reported as a dismissal.
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
            // A shut-down dispatcher returns a canceled task without ever running the delegate, so the
            // in-dialog cancellation handling never engages — the no-throw contract maps this to dismissal.
            return FileDialogResult.Dismissed;
        }
    }

    private static async Task<FileDialogResult> ShowOnUIThreadAsync(UIApplication application,
                                                                    FileOpenDialogRequest request,
                                                                    bool viaMarshal,
                                                                    CancellationToken cancellationToken)
    {
        // Shutdown race — the MARSHALED path ONLY: a marshaled show can be dispatched while teardown removes
        // the window manager on this same UI thread, and ShowDialogAsync would throw. Scoping the check here
        // keeps a direct on-UI-thread show against a not-yet-started application failing loudly.
        if (viaMarshal && application.WindowManager is null)
            return FileDialogResult.Dismissed;

        var model = new FileDialogViewModel(request);

        try
        {
            var dialog = new FileOpenDialog(model, request.Title);

            // The first listing is loaded BEFORE the window is shown, so the dialog's first frame is the real
            // one — no empty listing flashing past on the way to the directory the caller asked for.
            await model.InitializeAsync(cancellationToken).ConfigureAwait(true);

            return await dialog.ShowDialogAsync<FileDialogResult>(cancellationToken).ConfigureAwait(true)
                   ?? FileDialogResult.Dismissed;
        }
        catch (OperationCanceledException)
        {
            return FileDialogResult.Dismissed; // dismissal-by-cancellation: the forced close carries no choice
        }
        finally
        {
            model.Dispose();
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;

        if (_view.FocusFileList())
            return;

        if (UIApplication.Current is { Dispatcher.ShutdownToken.IsCancellationRequested: false } app)
            app.Dispatcher.InvokeAsync(() => _view.FocusFileList());
    }
}
