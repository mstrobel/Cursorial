using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A bar gallery (the bars guide's <c>BarGallery</c> — "a combobox is a gallery in disguise"): a
/// <see cref="BarComboBox"/>-styled picker whose drop-down lays its choices out as a GRID of preview cells (a
/// <see cref="WrapPanel"/> items host) instead of a vertical list. In a toolbar it stays a dropdown; the same gallery
/// expands into a ribbon in-place grid at a later phase. Inherits every ComboBox behavior (selection, the drop-down,
/// type-ahead); its rows are ordinary <c>ComboBoxItem</c>s wrapped into the grid.
/// </summary>
public class BarGallery : ComboBox
{

    /// <summary>Creates a gallery (its drop-down items wrap into a grid rather than stacking).</summary>
    public BarGallery()
    {
        ItemsPanel = new ItemsPanelTemplate(static _ => new WrapPanel { Orientation = Orientation.Horizontal });
    }
}
