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
}
