using Cursorial.Gallery.ViewModels;
using Cursorial.UI;
using Cursorial.UI.Bars;

namespace Cursorial.Gallery;

/// <summary>
/// The gallery's Ribbon: a plain <see cref="Ribbon"/> that self-populates its Quick Access Toolbar from its
/// <see cref="RibbonViewModel"/> DataContext. QAT membership (<see cref="Ribbon.QuickAccessCommands"/> /
/// <see cref="Ribbon.QuickAccessCandidates"/>) is a code-only collection — not XAML-settable, and its items are the
/// view-model's shared <see cref="BarCommand"/> instances — so the gallery declares <c>&lt;gallery:GalleryRibbon&gt;</c>
/// in the runtime-loaded XAML and this subclass does the one-time population when the DataContext arrives.
/// </summary>
public sealed class GalleryRibbon : Ribbon
{
    private bool _quickAccessPopulated;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        PopulateQuickAccess(); // the inherited RibbonViewModel DataContext is resolvable once attached
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        // Backup hook: if the DataContext is (re)assigned after attach, populate then too (guarded, so at most once).
        if (ReferenceEquals(args.Property, DataContextProperty))
            PopulateQuickAccess();
    }

    private void PopulateQuickAccess()
    {
        if (_quickAccessPopulated || DataContext is not RibbonViewModel viewModel)
            return;

        _quickAccessPopulated = true;
        foreach (var command in viewModel.QuickAccessCandidates)
            QuickAccessCandidates.Add(command);
        foreach (var command in viewModel.QuickAccessDefaults)
            QuickAccessCommands.Add(command);
    }
}
