using Cursorial.UI;
using Cursorial.UI.Input;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// Moves keyboard focus to a target element when the trigger fires (design doc §5), through the S3
/// <see cref="FocusManager"/> (<see cref="FocusNavigationMethod.Programmatic"/> — the <c>:focus-visible</c>
/// policy applies as for any programmatic move). The target is <see cref="Target"/>, else the firing
/// trigger's host. A fire outside a running <see cref="UIApplication"/> is a no-op (there is no focus
/// system to move); a non-element target throws.
/// </summary>
public class SetFocusAction : TriggerAction
{
    /// <summary>The element to focus; default: the firing trigger's host.</summary>
    public UIElement? Target { get; set; }

    /// <inheritdoc/>
    protected override void Invoke(object? sender, object? parameter)
    {
        var target = Target ?? sender as UIElement
            ?? throw new InvalidOperationException(
                "SetFocusAction has no target (set Target, or attach the trigger to a UIElement).");

        UIApplication.Current?.FocusManager.SetFocus(target);
    }
}
