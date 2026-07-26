using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Gallery.Pages;

/// <summary>
/// The Breadcrumb page's bar: a plain <see cref="BreadcrumbBar"/> that hands itself to the page's
/// <see cref="BreadcrumbViewModel"/>, which answers the six routed events the control raises — activation, the
/// three edit-mode boundaries, and the two separator drop-down events. All of them are conversations rather than
/// values, so none of them can be a binding.
/// </summary>
internal sealed class GalleryBreadcrumbBar : BreadcrumbBar
{
    private BreadcrumbViewModel? _connected;

    /// <summary>Opt into the base <see cref="BreadcrumbBar"/> control theme (control themes resolve exact-key).</summary>
    protected override object ControlThemeKey => typeof(BreadcrumbBar);

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        Connect(DataContext as BreadcrumbViewModel);
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
            Connect(DataContext as BreadcrumbViewModel);
    }

    private void Connect(BreadcrumbViewModel? viewModel)
    {
        if (ReferenceEquals(_connected, viewModel))
            return;

        _connected?.Disconnect(this);
        _connected = viewModel;
        viewModel?.Connect(this);
    }
}
