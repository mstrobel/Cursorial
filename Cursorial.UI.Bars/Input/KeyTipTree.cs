using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// Visual-tree walks the KeyTip controller and hosts use (keytips-design §6). Uses the public
/// <see cref="UIElement.VisualChildrenCount"/>/<see cref="UIElement.GetVisualChild"/> cross-assembly walk, so it
/// needs no engine internals. Walks are shallow-stopping (a matched host/group is not re-descended) and run only on
/// rare Alt-driven build passes, so cost is negligible.
/// </summary>
internal static class KeyTipTree
{
    /// <summary>Collects the bar-surface hosts under <paramref name="root"/> (Ribbon/Toolbar/Menu), wrapping each in
    /// its adapter and not descending into it — so a Ribbon's embedded QAT toolbar isn't discovered as its own host.</summary>
    public static void CollectHosts(UIElement root, List<IKeyTipHost> into)
    {
        switch (root)
        {
            case Ribbon ribbon:
                into.Add(new RibbonKeyTipHost(ribbon));
                return;
            case Toolbar toolbar:
                into.Add(new ToolbarKeyTipHost(toolbar));
                return;
            case Menu menu:
                into.Add(new MenuKeyTipHost(menu));
                return;
        }

        for (var i = 0; i < root.VisualChildrenCount; i++)
            CollectHosts(root.GetVisualChild(i), into);
    }

    /// <summary>Collects the realized <see cref="RibbonGroup"/>s under <paramref name="root"/> (only the selected
    /// tab's band is realized in a TabControl, so this yields exactly the current tab's groups).</summary>
    public static void CollectGroups(UIElement root, List<RibbonGroup> into)
    {
        if (root is RibbonGroup group)
        {
            into.Add(group);
            return; // groups don't nest
        }

        for (var i = 0; i < root.VisualChildrenCount; i++)
            CollectGroups(root.GetVisualChild(i), into);
    }
}
