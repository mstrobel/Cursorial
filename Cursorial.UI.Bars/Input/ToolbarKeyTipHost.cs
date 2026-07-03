using Cursorial.UI.Input;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// The toolbar KeyTip host (keytips-design §6) — single level in v1. One <see cref="KeyTipTargetKind.Activate"/>
/// badge per realized, eligible bar control in the visible row. An overflowed control (pocketed into the ⋯/» popup)
/// is not eligible (its <see cref="IAccessKeyTarget.IsAccessKeyEligible"/> is false while hidden), so it gets no
/// badge — the overflow-drill is deferred to v2 (documented).
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

            var leaf = control;
            into.AddActivate(control, () => KeyTipController.ActivateLeaf(leaf));
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
