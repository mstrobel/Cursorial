// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A rejected producer-fed value (design doc §2.3 — "validation at the mouth"): a binding entry or
/// animation handle pushed a value the property's <c>Validate</c> rejected. The value is discarded
/// and the previous value kept; this hook is the diagnostic channel (there is deliberately no
/// <c>BindingNotification</c>-weight data-validation plumbing in v1, §2.6-8).
/// </summary>
/// <param name="target">The object the rejected write was aimed at.</param>
/// <param name="property">The property whose validator rejected the value.</param>
/// <param name="rejectedValue">The rejected value, boxed (cold path).</param>
public delegate void RejectedValueHandler(UIObject target, UIProperty property, object? rejectedValue);

/// <summary>
/// Process-global diagnostics hooks for the property engine. Local <c>SetValue</c> /
/// <c>SetCurrentValue</c> throw on invalid values; producer mouths (binding entries, animation
/// handles) discard and report here instead — a producer cannot meaningfully catch.
/// </summary>
public static class UIDiagnostics
{
    /// <summary>Raised when a producer-fed value is discarded by a property's <c>Validate</c>.</summary>
    public static event RejectedValueHandler? RejectedValue;

    /// <summary>Raises <see cref="RejectedValue"/>.</summary>
    internal static void OnRejectedValue(UIObject target, UIProperty property, object? rejectedValue)
        => RejectedValue?.Invoke(target, property, rejectedValue);
}
