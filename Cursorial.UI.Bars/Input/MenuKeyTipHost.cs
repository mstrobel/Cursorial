using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// The menu-bar KeyTip host (keytips-design §6) — single level in v1: one <see cref="KeyTipTargetKind.Activate"/>
/// badge per top-level <see cref="MenuItem"/>. Activating opens the item's submenu (its
/// <see cref="IAccessKeyTarget.OnAccessKey"/>, matching the current access-key behavior), then exits — the user
/// walks the open submenu with arrows. Submenu KeyTip drill is deferred to v2 (the same park-until-popup-surface
/// dependency as the toolbar overflow).
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

            var leaf = item;
            into.AddActivate(item, () => KeyTipController.ActivateLeaf(leaf));
        }
    }
}
