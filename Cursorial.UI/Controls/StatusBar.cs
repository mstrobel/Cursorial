namespace Cursorial.UI.Controls;

/// <summary>
/// A status bar (the WPF/Avalonia <c>StatusBar</c>): an <see cref="ItemsControl"/> that lays its
/// <see cref="StatusBarItem"/> containers along a one-row docked bar. The default panel is a
/// <see cref="DockPanel"/> with <see cref="DockPanel.LastChildFill"/> off, so items dock left by default and a
/// <see cref="StatusBarItem"/> (or any item element) can be right-aligned with <c>DockPanel.SetDock(item, Dock.Right)</c>;
/// a <see cref="Separator"/> renders a rule between groups. A plain item is wrapped in a <see cref="StatusBarItem"/>;
/// a <see cref="StatusBarItem"/> or <see cref="Separator"/> is used as its own container.
/// </summary>
public class StatusBar : ItemsControl
{
    /// <summary>Creates a status bar whose default panel docks items left-to-right.</summary>
    public StatusBar()
        => ItemsPanel = new ItemsPanelTemplate(static _ => new DockPanel { LastChildFill = false });

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new StatusBarItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is StatusBarItem or Separator;

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(UIElement container, object? item)
    {
        base.PrepareContainerForItemOverride(container, item);
        // A Separator in a one-row bar reads as an upright divider between cells, not a horizontal rule.
        if (container is Separator separator)
        {
            separator.Orientation = Orientation.Vertical;
            separator.Width = 1;
        }
    }
}
