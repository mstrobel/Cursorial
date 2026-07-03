using Cursorial.UI.Input;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// The ribbon KeyTip host (keytips-design §6) — the multi-level centerpiece. Level 0 badges the tab headers (a
/// <see cref="KeyTipTargetKind.DrillTab"/> that selects + floats the band, then pushes the group level), the File
/// tab (an <see cref="KeyTipTargetKind.Activate"/> that opens Backstage), and the QAT items (digit badges). Level 1
/// badges the selected tab's groups (each a <see cref="KeyTipTargetKind.DrillGroup"/> pushing its control level).
/// Level 2 badges the group's leaf controls + the ⋰ dialog launcher.
/// </summary>
/// <remarks>
/// v1 scope: the bar-control dropdown drill, the collapsed-group flyout drill, and the collapsed-QAT (⋯▾) drill are
/// treated as <see cref="KeyTipTargetKind.Activate"/> (open the surface, then exit — the user navigates it with
/// arrows). Drilling a KeyTip level over an opened popup surface is the park-until-popup-surface leg deferred to v2
/// (keytips-design §6/§14/Risk 3); the ribbon L0→L1→L2 drill already proves the parked-layout machinery.
/// </remarks>
internal sealed class RibbonKeyTipHost(Ribbon ribbon) : IKeyTipHost
{
    /// <inheritdoc/>
    public UIElement SurfaceElement => ribbon;

    /// <inheritdoc/>
    public void BuildRootLevel(KeyTipLevelBuilder into)
    {
        // Tabs: a content tab drills into its groups; the File tab opens Backstage (reusing the routed event that
        // RibbonTab's own click/access-key path raises).
        var tabs = ribbon.ItemContainerGenerator;
        for (var i = 0; i < tabs.ContainerCount; i++)
        {
            if (tabs.ContainerFromIndex(i) is not RibbonTab tab)
                continue;

            if (tab.IsFileTab)
            {
                var file = tab;
                into.AddActivate(tab, () => file.RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, file)));
            }
            else
            {
                var index = i;
                into.AddDrill(
                    tab, KeyTipTargetKind.DrillTab,
                    reveal: () =>
                    {
                        ribbon.SelectedIndex = index;
                        if (ribbon.IsMinimized)
                            ribbon.FloatBand();
                    },
                    buildNext: BuildGroupLevel);
            }
        }

        // QAT items get digit badges (guide convention) so they never collide with the tab letters.
        if (ribbon.ActiveQuickAccessToolbarForTests is { } qat)
        {
            var items = qat.ItemContainerGenerator;
            var digit = 1;
            for (var i = 0; i < items.ContainerCount && digit <= 9; i++)
            {
                if (items.ContainerFromIndex(i) is not { } item || item is not IAccessKeyTarget { IsAccessKeyEligible: true })
                    continue;

                var leaf = item;
                into.AddExplicit(item, digit.ToString(), KeyTipTargetKind.Activate, activate: () => KeyTipController.ActivateLeaf(leaf), reveal: null);
                digit++;
            }
        }

        // The collapsed-QAT opener (⋯▾): v1 opens the flyout then exits (drill deferred).
        if (ribbon is { IsQuickAccessCollapsedForTests: true, QatCollapsedButtonForTests: { } opener })
            into.AddExplicit(opener, "0", KeyTipTargetKind.Activate, activate: () => KeyTipController.ActivateLeaf(opener), reveal: null);
    }

    // L1: the selected tab's groups (each drills to its controls). Returns null until the band's groups realize
    // (the controller retries next frame — the park proof for a minimized/floated band).
    private KeyTipLevel? BuildGroupLevel()
    {
        var groups = new List<RibbonGroup>();
        KeyTipTree.CollectGroups(ribbon, groups);
        if (groups.Count == 0)
            return null;

        var builder = new KeyTipLevelBuilder();
        foreach (var group in groups)
        {
            if (group is { Density: RibbonGroupDensity.Collapsed, KeyTipCollapsedButton: { } collapsedOpener })
            {
                // A collapsed group: v1 opens its flyout then exits (drill into the flyout deferred to v2).
                builder.AddActivate(group, () => KeyTipController.ActivateLeaf(collapsedOpener));
            }
            else
            {
                var g = group;
                builder.AddDrill(
                    group, KeyTipTargetKind.DrillGroup,
                    reveal: static () => { },                 // the band is already shown — no reveal
                    buildNext: () => BuildControlLevel(g));
            }
        }

        return builder.Build();
    }

    // ── SuperTip hop-sequence resolution (KeyTip.GetHopSequence) ──────────────────────────────────────
    // Report the SURVIVING KeyTip letter an element would carry in the level the overlay builds, so a SuperTip hop
    // hint inherits the SAME eligibility filter + collision policy as the badges (a disabled or collision-dropped
    // element resolves to null → no hop). Building a level has no side effects (the drill closures aren't invoked).

    /// <summary>The selected tab's surviving KeyTip letter (from the root level), or null.</summary>
    internal string? ResolveTabKeyTip(RibbonTab tab)
    {
        var builder = new KeyTipLevelBuilder();
        BuildRootLevel(builder);
        return builder.Build().KeyTipFor(tab);
    }

    /// <summary>A group's surviving KeyTip letter (from the selected tab's group level), or null.</summary>
    internal string? ResolveGroupKeyTip(RibbonGroup group) => BuildGroupLevel()?.KeyTipFor(group);

    /// <summary>A control's surviving KeyTip letter (from its group's control level), or null.</summary>
    internal static string? ResolveControlKeyTip(RibbonGroup group, UIElement control) => BuildControlLevel(group)?.KeyTipFor(control);

    // L2: a group's leaf controls + its ⋰ dialog launcher. A dropdown/split control opens on activation (drill over
    // its items deferred to v2).
    private static KeyTipLevel? BuildControlLevel(RibbonGroup group)
    {
        var builder = new KeyTipLevelBuilder();

        foreach (var control in group.KeyTipContainers)
        {
            if (control is not IAccessKeyTarget { IsAccessKeyEligible: true })
                continue;

            var leaf = control;
            builder.AddActivate(control, () => KeyTipController.ActivateLeaf(leaf));
        }

        if (group.KeyTipDialogLauncher is { } launcher)
            builder.AddActivate(launcher, () => KeyTipController.ActivateLeaf(launcher));

        var level = builder.Build();
        return level.Entries.Count > 0 ? level : null;
    }
}
