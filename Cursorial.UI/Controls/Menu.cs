using Cursorial.UI.Input;

// ReSharper disable CheckNamespace
namespace Cursorial.UI.Controls;

/// <summary>
/// A horizontal menu bar (design doc §12.7) — an <see cref="ItemsControl"/> whose items are top-level
/// <see cref="MenuItem"/>s laid out in a row; each opens its submenu downward. Registers as the window's main menu
/// with the <see cref="AccessKeyManager"/> (<see cref="IMainMenu"/>), so an Alt tap / F10 enters menu mode and
/// moves focus to the first item.
/// </summary>
public class Menu : ItemsControl, IMainMenu
{
    /// <summary>Creates a menu bar (items stack horizontally).</summary>
    public Menu() =>
        ItemsPanel = new FuncTemplateContent(static _ => new StackPanel { Orientation = Orientation.Horizontal });

    /// <inheritdoc/>
    protected override UIElement GetContainerForItemOverride() => new MenuItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainer(object? item) => item is MenuItem or Separator;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        if (UIApplication.Current?.AccessKeys is { } manager)
            manager.MainMenu = this; // one per app, last wins (doc §7.8)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (UIApplication.Current?.AccessKeys is { } manager && ReferenceEquals(manager.MainMenu, this))
            manager.MainMenu = null;
        base.OnDetachedFromTree(in e);
    }

    /// <summary>Menu-mode entry (Alt tap / F10): focus the first top-level item so keyboard navigation can begin.</summary>
    void IMainMenu.OnEnterMenuMode()
    {
        for (var i = 0; i < ItemContainerGenerator.ContainerCount; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is { Focusable: true } first)
            {
                first.Focus(FocusNavigationMethod.AccessKey);
                return;
            }
    }
}
