using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The Completion page's popup: a plain <see cref="CompletionPopup"/> whose two non-bindable seams are wired to the
/// page's <see cref="CompletionViewModel"/> when the DataContext arrives.
/// <para>
/// <see cref="CompletionPopup.Target"/> and <see cref="CompletionPopup.Provider"/> are styled properties and are
/// therefore set straight from the XAML. The other two are not: <see cref="CompletionPopup.CommitHandler"/> is a
/// plain CLR delegate property (a binding needs a <see cref="UIProperty"/> target) and
/// <see cref="CompletionPopup.Committed"/> is an event, so the gallery declares
/// <c>&lt;pages:GalleryCompletionPopup&gt;</c> in the runtime-loaded XAML and this subclass does the hook-up —
/// the same shape <c>GalleryRibbon</c> uses for the code-only Quick Access collections.
/// </para>
/// </summary>
internal sealed class GalleryCompletionPopup : CompletionPopup
{
    private CompletionViewModel? _connected;
    private bool _commitClosedTheSession;

    // Opt into the base control theme: control themes resolve exact-key, so without this a GalleryCompletionPopup
    // would render untemplated (WPF DefaultStyleKey parity).
    protected override object ControlThemeKey => typeof(CompletionPopup);

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        Connect(DataContext as CompletionViewModel); // the inherited page view-model is resolvable once attached
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        Connect(null);
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        // Backup hook: the DataContext can also be (re)assigned after the attach.
        if (ReferenceEquals(args.Property, DataContextProperty))
            Connect(DataContext as CompletionViewModel);
    }

    private void Connect(CompletionViewModel? viewModel)
    {
        if (ReferenceEquals(_connected, viewModel))
            return;

        if (_connected is not null)
        {
            Committed -= OnCommitted;
            Closed -= OnClosed;
            CommitHandler = null;
        }

        _connected = viewModel;

        if (viewModel is null)
            return;

        CommitHandler = viewModel.Commit;
        Committed += OnCommitted;
        Closed += OnClosed;
    }

    private void OnCommitted(object? sender, CompletionCommittedEventArgs e)
    {
        _connected?.ReportCommit(e);

        // An accept that ENDS the session raises Committed and then, immediately, Closed. Without this latch the
        // dismissal report would land second and paint over the far more informative accept report — which is
        // exactly the state a user sees after every ordinary completion.
        _commitClosedTheSession = !e.Commit.KeepOpen;
    }

    // Closed fires for every dismissal — Escape, focus-out, a filter that matched nothing, the page going away.
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_commitClosedTheSession)
        {
            _commitClosedTheSession = false;
            return;
        }

        _connected?.ReportDismissed();
    }
}
