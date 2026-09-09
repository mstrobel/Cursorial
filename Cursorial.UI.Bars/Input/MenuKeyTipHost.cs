using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// The menu-bar KeyTip host (keytips-design §6): one badge per top-level <see cref="MenuItem"/>. A header with a
/// submenu is a <see cref="KeyTipTargetKind.DrillPopup"/> — choosing it opens the submenu (its access-key open,
/// focus into it) and pushes a level over the submenu's rows, nested submenus drilling the same way and a leaf row
/// activating (<see cref="KeyTipPopupLevels"/>, 2026-09-09); a top-level item without a submenu activates.
/// </summary>
internal sealed class MenuKeyTipHost(Menu menu) : IKeyTipHost
{
    /// <inheritdoc/>
    public UIElement SurfaceElement => menu;

    /// <inheritdoc/>
    public void BuildRootLevel(KeyTipLevelBuilder into)
    {
        var items = menu.ItemContainerGenerator;
        for (var i = 0; i < items.ContainerCount; i++)
        {
            if (items.ContainerFromIndex(i) is not MenuItem item || item is not IAccessKeyTarget { IsAccessKeyEligible: true })
                continue;

            KeyTipPopupLevels.Add(into, item);
        }
    }
}
