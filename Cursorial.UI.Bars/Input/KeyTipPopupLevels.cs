using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// KeyTip levels over OPENED popup surfaces (keytips-design §6's deferred v2 leg, built 2026-09-09): a menu's
/// submenu, a bar dropdown / split / popup button's dropdown, a collapsed ribbon group's flyout, the collapsed QAT,
/// the toolbar overflow. One rule serves them all: the popup's content is walked for its accelerator-eligible
/// controls (<see cref="KeyTipTree.CollectAccessKeyTargets"/>); a control that opens a further popup (a submenu
/// header, a nested dropdown) is a <see cref="KeyTipTargetKind.DrillPopup"/> whose next level is built the same
/// way, everything else activates. A level built before the popup's content has realized is null, so the controller
/// retries at the next post-layout hook (the park-until-popup-surface leg). Esc pops a level and its retract closes
/// the popup it opened.
/// </summary>
internal static class KeyTipPopupLevels
{
    /// <summary>Adds <paramref name="target"/> to <paramref name="into"/> as a leaf or, when it opens a popup, as a drill into that popup.</summary>
    public static void Add(KeyTipLevelBuilder into, UIElement target)
    {
        switch (target)
        {
            case MenuItem { HasItems: true } header:
                into.AddDrill(
                    header, KeyTipTargetKind.DrillPopup,
                    reveal: () => KeyTipController.ActivateLeaf(header), // the access-key open: submenu + focus into it
                    buildNext: () => BuildOverItems(header),
                    retract: () => header.IsSubmenuOpen = false);
                break;

            case BarDropDownButton opener:
                into.AddDrill(
                    opener, KeyTipTargetKind.DrillPopup,
                    reveal: () => opener.IsDropDownOpen = true,
                    buildNext: () => BuildOver(opener.DropDownContent as UIElement),
                    retract: () => opener.IsDropDownOpen = false);
                break;

            default:
                into.AddActivate(target, () => KeyTipController.ActivateLeaf(target));
                break;
        }
    }

    /// <summary>The level over an opened submenu's rows (null until the rows have realized).</summary>
    public static KeyTipLevel? BuildOverItems(MenuItem header)
    {
        var builder = new KeyTipLevelBuilder();
        var items = header.ItemContainerGenerator;
        for (var i = 0; i < items.ContainerCount; i++)
        {
            if (items.ContainerFromIndex(i) is { } row && row.IsEffectivelyVisible && HasSurface(row))
                Add(builder, row);
        }

        return builder.HasPending ? builder.Build() : null;
    }

    /// <summary>The level over an opened popup's <paramref name="content"/> (null when there is no content, or none of it has realized on a surface yet).</summary>
    public static KeyTipLevel? BuildOver(UIElement? content)
    {
        if (content is null)
            return null;

        var targets = new List<UIElement>();
        KeyTipTree.CollectAccessKeyTargets(content, targets);

        var builder = new KeyTipLevelBuilder();
        foreach (var target in targets)
        {
            if (HasSurface(target))
                Add(builder, target);
        }

        return builder.HasPending ? builder.Build() : null;
    }

    // A popup's content has a screen position only once its surface exists; before that a badge could not be placed.
    private static bool HasSurface(UIElement element)
        => UIApplication.Current?.WindowManager is not { } wm || wm.SurfaceForElement(element) is not null;
}
