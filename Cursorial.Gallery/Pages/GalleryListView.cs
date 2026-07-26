using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The List View page's list: a plain <see cref="ListView"/> that hands itself to the page's
/// <see cref="ListViewPageViewModel"/> so the view-model can listen on the three things a binding cannot carry —
/// <see cref="SelectingItemsControl.SelectionChanged"/> (the selection count; <c>SelectedItems</c> is a snapshot
/// with no change notification), <see cref="ListView.Sorting"/> and <see cref="ListView.ItemInvoked"/>.
/// </summary>
internal sealed class GalleryListView : ListView
{
    private ListViewPageViewModel? _connected;

    /// <summary>Opt into the base <see cref="ListView"/> control theme (control themes resolve exact-key).</summary>
    protected override object ControlThemeKey => typeof(ListView);

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        Connect(DataContext as ListViewPageViewModel);
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

        if (ReferenceEquals(args.Property, DataContextProperty))
            Connect(DataContext as ListViewPageViewModel);
    }

    private void Connect(ListViewPageViewModel? viewModel)
    {
        if (ReferenceEquals(_connected, viewModel))
            return;

        _connected?.Disconnect(this);
        _connected = viewModel;
        viewModel?.Connect(this);
    }
}
