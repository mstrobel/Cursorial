namespace Cursorial.UI.Controls;

/// <summary>
/// The container for a <see cref="ListView"/> item (WPF's <c>ListViewItem</c>). Everything about
/// selection — the two-way <see cref="SelectableItemContainer.IsSelected"/>, the <c>:selected</c>
/// pseudo-class, the Ctrl/Shift-aware pointer gesture and the double-click activation — is inherited
/// verbatim from <see cref="ListBoxItem"/>; the only thing this type adds is the <b>view-mode
/// pseudo-class</b> (<c>:details</c> / <c>:list</c> / <c>:small-icons</c> / <c>:tiles</c>) that lets
/// a theme give a tile a different padding than a details row without a second container type.
/// <para>
/// The visible content is built by the <c>PART_Cells</c> <see cref="ListViewCellsPresenter"/> in the
/// template, not by a <c>ContentPresenter</c>: one container type has to render four different
/// compositions, and <see cref="ItemsControl.GetContainerForItemOverride"/> cannot vary by item.
/// </para>
/// </summary>
[TemplatePart(PartCells, typeof(ListViewCellsPresenter), IsRequired = true)]
public class ListViewItem : ListBoxItem
{
    private const string PartCells = "PART_Cells";

    private ListViewViewMode _view = ListViewViewMode.Details;

    /// <summary>Creates a list-view item.</summary>
    public ListViewItem() => UpdateViewPseudoClasses();

    /// <summary>The view mode the owning <see cref="ListView"/> is in — pushed by the owner, never set directly.</summary>
    public ListViewViewMode View => _view;

    /// <summary>The <c>PART_Cells</c> presenter, or <see langword="null"/> before the template expands.</summary>
    internal ListViewCellsPresenter? CellsPresenter { get; private set; }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        CellsPresenter = GetTemplatePart<ListViewCellsPresenter>(PartCells);
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        CellsPresenter = null;
        base.OnTemplateDetaching(old);
    }

    /// <summary>The owner pushes its view mode down so the container can carry the matching pseudo-class.</summary>
    internal void SetViewFromOwner(ListViewViewMode view)
    {
        if (_view == view)
            return;

        _view = view;
        UpdateViewPseudoClasses();
    }

    // Plain-field backed (the owner is the only writer, and a styled property would invite a binding that
    // fights it), so the pseudo-classes are set imperatively — the ComboBox `:open` idiom.
    private void UpdateViewPseudoClasses()
    {
        PseudoClasses.Set(":details", _view == ListViewViewMode.Details);
        PseudoClasses.Set(":list", _view == ListViewViewMode.List);
        PseudoClasses.Set(":small-icons", _view == ListViewViewMode.SmallIcons);
        PseudoClasses.Set(":tiles", _view == ListViewViewMode.Tiles);
    }
}
