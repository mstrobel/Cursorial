using Cursorial.UI.Bars.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// The attached properties that declare a control's KeyTip badge (the Alt-overlay accelerator, keytips-design §4).
/// Set <see cref="KeyProperty"/> to pin an explicit badge (<c>bars:KeyTip.Key="FP"</c>); leave it unset and the
/// badge letter is auto-derived from the control's access-key / command text / display text (the derivation ladder
/// in <see cref="KeyTipModel"/>). Set <see cref="AutoAssignProperty"/> to <see langword="false"/> to suppress a
/// badge entirely on a control that would otherwise auto-derive one.
/// </summary>
public sealed class KeyTip
{
    private KeyTip() { } // an attached-property holder — never instantiated (a non-static owner is required for RegisterAttached)

    /// <summary>The explicit badge text (1–2 chars). Uppercased at derivation. When set, it wins the derivation
    /// ladder and is never dropped on a collision (auto-derived colliders are dropped instead).</summary>
    public static readonly AttachedProperty<string?> KeyProperty =
        UIProperty.RegisterAttached<KeyTip, UIElement, string?>("Key");

    /// <summary>Whether a badge is auto-derived when no explicit <see cref="KeyProperty"/> is set (default
    /// <see langword="true"/>). Set false to keep a control out of the KeyTip overlay.</summary>
    public static readonly AttachedProperty<bool> AutoAssignProperty =
        UIProperty.RegisterAttached<KeyTip, UIElement, bool>("AutoAssign", defaultValue: true);

    /// <inheritdoc cref="KeyProperty"/>
    public static string? GetKey(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(KeyProperty);
    }

    /// <inheritdoc cref="KeyProperty"/>
    public static void SetKey(UIElement element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(KeyProperty, value);
    }

    /// <inheritdoc cref="AutoAssignProperty"/>
    public static bool GetAutoAssign(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(AutoAssignProperty);
    }

    /// <inheritdoc cref="AutoAssignProperty"/>
    public static void SetAutoAssign(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(AutoAssignProperty, value);
    }

    /// <summary>
    /// The KeyTip drill sequence a keyboard user presses to reach <paramref name="target"/> — e.g.
    /// <c>"Alt, H, F, B"</c> for a Bold button in the Home tab's Font group, or <c>"Alt, X"</c> for a flat toolbar
    /// button. Computed from the SAME derivation the badges use (<see cref="KeyTipModel"/>), walking the control's
    /// live ancestry: its <see cref="RibbonGroup"/> and the ribbon's currently-selected tab (a hoverable control is
    /// always in the visible tab). Returns <see langword="null"/> when KeyTips isn't enabled on the app, the control
    /// has no derivable badge, or it isn't reachable by the v1 drill (a QAT digit / an item inside a popup — deferred
    /// with the v2 popup drill). Used by <see cref="SuperTip"/> to show the accelerator hops in its hover help.
    /// </summary>
    public static string? GetHopSequence(UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (UIApplication.Current?.AccessKeys.KeyTipController is null)
            return null; // KeyTips not enabled on this app — the hop hint would be misleading

        RibbonGroup? group = null;
        Ribbon? ribbon = null;
        Toolbar? toolbar = null;
        for (var element = target.VisualParent; element is not null; element = element.VisualParent)
        {
            group ??= element as RibbonGroup;
            toolbar ??= element as Toolbar;
            if (element is Ribbon found)
            {
                ribbon = found;
                break;
            }
        }

        // Resolve each hop against the level the OVERLAY would build (not a bare KeyTipModel.Resolve), so the hint
        // inherits the eligibility filter AND the per-level collision policy: a disabled control, or one whose badge
        // was dropped by a same-letter collision, resolves to null → no hop (it isn't reachable by that drill).

        // A ribbon BAND control: Alt → tab → group → control.
        if (ribbon is not null && group is not null)
        {
            var host = new RibbonKeyTipHost(ribbon);
            var tab = ribbon.SelectedIndex >= 0
                ? ribbon.ItemContainerGenerator.ContainerFromIndex(ribbon.SelectedIndex) as RibbonTab
                : null;
            var tabLetter = tab is not null ? host.ResolveTabKeyTip(tab) : null;
            var groupLetter = host.ResolveGroupKeyTip(group);
            var controlLetter = RibbonKeyTipHost.ResolveControlKeyTip(group, target);
            if (tabLetter is null || groupLetter is null || controlLetter is null)
                return null;

            return $"Alt␣{tabLetter}␣{groupLetter}␣{controlLetter}";
        }

        // A flat toolbar control (not the ribbon's QAT): Alt → control. QAT digits + popup items are the v2 legs.
        if (toolbar is not null && ribbon is null)
            return ToolbarKeyTipHost.ResolveControlKeyTip(toolbar, target) is { } controlLetter ? $"Alt␣{controlLetter}" : null;

        return null;
    }
}
