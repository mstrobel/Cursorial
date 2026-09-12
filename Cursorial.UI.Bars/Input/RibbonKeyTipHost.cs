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
/// Popup drills (2026-09-09): a dropdown-bearing bar control, a collapsed group's flyout opener and the collapsed
/// QAT's ⋯▾ opener are <see cref="KeyTipTargetKind.DrillPopup"/> entries — choosing one opens its popup and pushes a
/// level over the popup's controls (<see cref="KeyTipPopupLevels"/>), the badges re-anchored above that popup.
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
                if (file.ContextMenu is {} contextMenu)
                {
                    into.AddDrill(
                        tab,
                        KeyTipTargetKind.DrillPopup,
                        reveal: () =>
                                {
                                    var backstageArgs = new RoutedEventArgs(Ribbon.BackstageRequestedEvent, file);
                                    file.RaiseEvent(backstageArgs);
                                    if (backstageArgs.Handled)
                                    {
                                        if (UIApplication.Current is {} app)
                                        {
                                            app.Dispatcher.Post(() =>
                                                                {
                                                                    if (app.AccessKeys.KeyTipController is {} ktc)
                                                                    {
                                                                        ktc.TryPopLevel();
                                                                        ktc.Exit(viaActivationOverride: true);
                                                                    }
                                                                });
                                        }
                                        return;
                                    }
                                    var renderOffsetRow = contextMenu.RenderOffsetRow;
                                    contextMenu.RenderOffsetRow = -1;
                                    contextMenu.Open(file, placement: PlacementMode.Bottom);
                                    contextMenu.RenderOffsetRow = renderOffsetRow;
                                },
                        buildNext: () =>
                                   {
                                       if (contextMenu.IsOpen is false) return null;
                                       var menuBuilder = new KeyTipLevelBuilder();
                                       new ContextMenuKeyTipHost(contextMenu).BuildRootLevel(menuBuilder);
                                       return menuBuilder.Build();
                                   },
                        retract: () => contextMenu.Close());
                }
                else
                {
                    into.AddActivate(
                        tab,
                        () => file.RaiseEvent(new RoutedEventArgs(Ribbon.BackstageRequestedEvent, file)));
                }
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

        // The QAT customize ▾ (maintainer, 2026-09-12: it had no badge — its face is a glyph, nothing to derive —
        // and its checklist is a raw Popup, not a dropdown button): the explicit "00" badge, in the QAT's digit
        // family beside the items' 1..9 and the collapsed opener's 0, drills into the checklist (the candidate
        // check boxes, "More Commands…", "Show Below the Ribbon").
        if (ribbon is { QatCustomizeForTests: { IsEffectivelyVisible: true } customize, QatPopupForTests: { } qatPopup })
        {
            into.AddExplicit(
                customize, "00", KeyTipTargetKind.DrillPopup,
                activate: null,
                reveal: () => qatPopup.IsOpen = true,
                buildNext: () => KeyTipPopupLevels.BuildOver(qatPopup.Child),
                retract: () => qatPopup.IsOpen = false);
        }

        // The collapsed-QAT opener (⋯▾) drills into its flyout (the re-hosted QAT commands).
        if (ribbon is { IsQuickAccessCollapsedForTests: true, QatCollapsedButtonForTests: { } opener })
        {
            if (opener is BarDropDownButton dropOpener)
            {
                into.AddExplicit(
                    opener, "0", KeyTipTargetKind.DrillPopup,
                    activate: null,
                    reveal: () => dropOpener.IsDropDownOpen = true,
                    buildNext: () => KeyTipPopupLevels.BuildOver(dropOpener.DropDownContent as UIElement),
                    retract: () => dropOpener.IsDropDownOpen = false);
            }
            else
            {
                into.AddExplicit(opener, "0", KeyTipTargetKind.Activate, activate: () => KeyTipController.ActivateLeaf(opener), reveal: null);
            }
        }
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
                // A collapsed group: its badge (the group letter) opens the flyout and drills into the group's controls.
                builder.AddDrill(
                    group, KeyTipTargetKind.DrillPopup,
                    reveal: () => collapsedOpener.IsDropDownOpen = true,
                    buildNext: () => KeyTipPopupLevels.BuildOver(collapsedOpener.DropDownContent as UIElement),
                    retract: () => collapsedOpener.IsDropDownOpen = false);
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

    // L2: a group's leaf controls + its ⋰ dialog launcher. A dropdown-bearing control drills into its dropdown.
    private static KeyTipLevel? BuildControlLevel(RibbonGroup group)
    {
        var builder = new KeyTipLevelBuilder();

        foreach (var control in group.KeyTipContainers)
        {
            if (control is not IAccessKeyTarget { IsAccessKeyEligible: true })
                continue;

            KeyTipPopupLevels.Add(builder, control);
        }

        if (group.KeyTipDialogLauncher is { } launcher)
            builder.AddActivate(launcher, () => KeyTipController.ActivateLeaf(launcher));

        var level = builder.Build();
        return level.Entries.Count > 0 ? level : null;
    }
}
