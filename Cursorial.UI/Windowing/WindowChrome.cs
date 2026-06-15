// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// The window chrome contract (design doc §8.3): a template part declares what it does to its host window
/// through the <see cref="HitTestRoleProperty"/> attached property — the window interprets bubbling
/// <c>ButtonDown</c>/drag on the part by its <see cref="WindowHitTestRole"/>, never by part name (<c>PART_*</c>
/// names are S8-internal <c>GetTemplatePart</c> lookups only, never a cross-subsystem contract). This lets any
/// template — S4's interim one or S8's themed one (C4) — drive window behavior with no command objects.
/// </summary>
/// <remarks>
/// Declared as a sealed class with a private constructor rather than the design's <c>static class</c> because
/// <c>UIProperty.RegisterAttached&lt;TOwner, …&gt;</c> takes the owner as a generic type argument, which a
/// static class cannot be. All members are static; it is never instantiated.
/// </remarks>
public sealed class WindowChrome
{
    private WindowChrome() { }

    /// <summary>The window role a chrome part requests (Drag / Close / Maximize / ResizeSE).</summary>
    public static readonly AttachedProperty<WindowHitTestRole> HitTestRoleProperty =
        UIProperty.RegisterAttached<WindowChrome, UIElement, WindowHitTestRole>("HitTestRole");

    /// <summary>Reads the chrome role on <paramref name="element"/>.</summary>
    public static WindowHitTestRole GetHitTestRole(UIElement element)
    {
        System.ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(HitTestRoleProperty);
    }

    /// <summary>Sets the chrome role on <paramref name="element"/>.</summary>
    public static void SetHitTestRole(UIElement element, WindowHitTestRole value)
    {
        System.ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HitTestRoleProperty, value);
    }
}
