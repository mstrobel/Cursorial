// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// A horizontal menu bar (design doc §12.7) — an <see cref="ItemsControl"/> whose items are top-level
/// <see cref="MenuItem"/>s laid out in a row; each opens its submenu downward. (Menu-mode entry via Alt/F10 and
/// the logical focus scope land in P9.4c; keyboard cycling in P9.4b.)
/// </summary>
public class Menu : ItemsControl
{
    /// <summary>Creates a menu bar (items stack horizontally).</summary>
    public Menu() =>
        ItemsPanel = new FuncTemplateContent(static _ => new StackPanel { Orientation = Orientation.Horizontal });

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new MenuItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is MenuItem or Separator;
}
