using Cursorial.UI.Input;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// The toolbar KeyTip host (keytips-design §6). One badge per realized, eligible bar control in the visible row — a
/// dropdown-bearing control drills into its dropdown. An overflowed control (pocketed into the ⋯/» popup) is not
/// eligible while hidden, so it gets no badge of its own; instead the overflow chevron carries the <c>0</c> badge
/// (the digit convention of the ribbon's collapsed QAT) and drills into the overflow popup, where the pocketed
/// controls are badged (<see cref="KeyTipPopupLevels"/>, 2026-09-09).
/// </summary>
internal sealed class ToolbarKeyTipHost(Toolbar toolbar) : IKeyTipHost
{
    /// <inheritdoc/>
    public UIElement SurfaceElement => toolbar;

    /// <inheritdoc/>
    public void BuildRootLevel(KeyTipLevelBuilder into)
    {
        var items = toolbar.ItemContainerGenerator;
        for (var i = 0; i < items.ContainerCount; i++)
        {
            if (items.ContainerFromIndex(i) is not { } control || control is not IAccessKeyTarget { IsAccessKeyEligible: true })
                continue;

            KeyTipPopupLevels.Add(into, control);
        }

        // The overflow chevron: present (visible) only when something is pocketed. Its popup hosts the overflowed
        // controls; the level over them is built once the popup surface exists.
        if (toolbar.OverflowToggleForTests is { IsEffectivelyVisible: true } chevron)
        {
            into.AddExplicit(
                chevron, "0", KeyTipTargetKind.DrillPopup,
                activate: null,
                reveal: () => toolbar.IsOverflowOpen = true,
                buildNext: () => KeyTipPopupLevels.BuildOver(toolbar.OverflowHostForTests),
                retract: () => toolbar.IsOverflowOpen = false);
        }
    }

    /// <summary>A toolbar control's surviving KeyTip letter (from the built level — inherits the eligibility filter,
    /// so a disabled / overflowed control resolves to null), for <see cref="KeyTip.GetHopSequence"/>.</summary>
    internal static string? ResolveControlKeyTip(Toolbar toolbar, UIElement control)
    {
        var builder = new KeyTipLevelBuilder();
        new ToolbarKeyTipHost(toolbar).BuildRootLevel(builder);
        return builder.Build().KeyTipFor(control);
    }
}
