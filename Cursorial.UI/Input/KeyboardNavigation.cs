namespace Cursorial.UI.Input;

/// <summary>
/// How Tab navigation treats a container's descendants (design doc §7.7). <c>Local</c> is
/// deliberately deferred; <see cref="Once"/> ships in v1 (matrix ND16).
/// </summary>
public enum KeyboardNavigationMode : byte
{
    /// <summary>The default: descendants participate in the surrounding container's tab order.</summary>
    Continue,

    /// <summary>Tab cycles within this container while focus is inside it (window/popup roots behave as Cycle — there is no OS to Tab out to).</summary>
    Cycle,

    /// <summary>Descendants are excluded from tab order entirely (they remain programmatically focusable).</summary>
    None,

    /// <summary>
    /// The container is a single tab stop (ND16): entry in either direction focuses the container's
    /// scope-memory element if valid, else its first tab-ordered eligible descendant, else the
    /// container itself when focusable; the next Tab/Shift+Tab exits past the whole container —
    /// the ListBox shape.
    /// </summary>
    Once
}

/// <summary>
/// How arrow-key directional navigation treats a container (design doc §7.7) — deliberately a
/// policy <b>hook</b>, not a global: lists/menus/toolbars opt in; arrows elsewhere stay with controls.
/// </summary>
public enum DirectionalNavigationMode : byte
{
    /// <summary>The default: arrows never move focus (free for controls).</summary>
    None,

    /// <summary>Arrows move focus among the container's focusables; at an edge, focus stays put.</summary>
    Contained,

    /// <summary>As <see cref="Contained"/>, but at an edge focus wraps to the farthest candidate on the opposite side (ND17).</summary>
    Cycle
}

/// <summary>
/// The keyboard-navigation attached properties (design doc §7.7): per-container Tab policy and the
/// opt-in directional (arrow-key) policy, both consumed by <see cref="FocusManager.MoveFocus"/> and
/// the dispatcher's unhandled navigation tail.
/// </summary>
// ReSharper disable once ConvertToStaticClass - static class cannot be used as generic type argument
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class KeyboardNavigation
{
    private KeyboardNavigation()
    {
    }

    /// <summary>
    /// The container's Tab policy (default <see cref="KeyboardNavigationMode.Continue"/>). Window
    /// and popup roots behave as <see cref="KeyboardNavigationMode.Cycle"/> regardless — on a
    /// terminal there is no next application control to Tab out to, so every surface root traps by
    /// default and modal trapping is free.
    /// </summary>
    public static readonly AttachedProperty<KeyboardNavigationMode> TabNavigationProperty =
        UIProperty.RegisterAttached<KeyboardNavigation, UIElement, KeyboardNavigationMode>("TabNavigation");

    /// <summary>The container's arrow-key policy (default <see cref="DirectionalNavigationMode.None"/> — opt-in per container).</summary>
    public static readonly AttachedProperty<DirectionalNavigationMode> DirectionalNavigationProperty =
        UIProperty.RegisterAttached<KeyboardNavigation, UIElement, DirectionalNavigationMode>("DirectionalNavigation");

    /// <summary>Reads <see cref="TabNavigationProperty"/> from <paramref name="element"/>.</summary>
    public static KeyboardNavigationMode GetTabNavigation(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TabNavigationProperty);
    }

    /// <summary>Sets <see cref="TabNavigationProperty"/> on <paramref name="element"/>.</summary>
    public static void SetTabNavigation(UIElement element, KeyboardNavigationMode mode)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TabNavigationProperty, mode);
    }

    /// <summary>Reads <see cref="DirectionalNavigationProperty"/> from <paramref name="element"/>.</summary>
    public static DirectionalNavigationMode GetDirectionalNavigation(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(DirectionalNavigationProperty);
    }

    /// <summary>Sets <see cref="DirectionalNavigationProperty"/> on <paramref name="element"/>.</summary>
    public static void SetDirectionalNavigation(UIElement element, DirectionalNavigationMode mode)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(DirectionalNavigationProperty, mode);
    }
}
