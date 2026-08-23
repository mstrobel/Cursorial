using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>How <see cref="ChangePropertyAction"/> writes a registered <c>UIProperty</c> (design doc §6).</summary>
public enum ChangePropertyMode
{
    /// <summary>A LocalValue <c>SetValue</c> — the imperative "the user acted, pin this" write (pierces
    /// styles/template values; a later <c>ClearValue</c> reverts). The default.</summary>
    SetValue,

    /// <summary>A <c>SetCurrentValue</c> — nudges the effective value WITHOUT seizing LocalValue provenance,
    /// so a later binding/style/animation still wins and <c>ClearValue</c> reverts universally.</summary>
    SetCurrentValue,
}

/// <summary>
/// Sets a property on a target when the trigger fires (design doc §6): an IMPERATIVE, event-driven,
/// one-shot set — semantically identical to code-behind writing the property in an event handler. It is
/// deliberately NOT lane-aware and has NO auto-revert: conditional "while this holds" value-setting
/// belongs to <c>Style.When</c> (the styling engine owns the retractable lattice, §0). The property
/// resolves via the framework's <c>{Name}Property</c> static-field convention (a registered
/// <c>UIProperty</c> → untyped <c>SetValue</c> / <see cref="ChangePropertyMode.SetCurrentValue"/>), else
/// a CLR property set. An unresolvable target/property throws — never a silent no-op.
/// </summary>
public class ChangePropertyAction : TriggerAction
{
    private static readonly MethodInfo SetCurrentValueMethod =
        typeof(ChangePropertyAction).GetMethod(nameof(SetCurrentValueCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The object to set; default: the firing trigger's host (the sender).</summary>
    public object? TargetObject { get; set; }

    /// <summary>The property to set (a registered <c>UIProperty</c> name or a CLR property name).</summary>
    public string? PropertyName { get; set; }

    /// <summary>The value to assign (assigned as-is; no conversion ladder — author the typed value).</summary>
    public object? Value { get; set; }

    /// <summary>The write mode for a registered <c>UIProperty</c> (ignored for a CLR property).</summary>
    public ChangePropertyMode Mode { get; set; } = ChangePropertyMode.SetValue;

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The {Name}Property/CLR-property convention is the runtime action's resolution path.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "SetCurrentValue closes a generic over the property's value type at runtime (the reflective lane).")]
    protected override void Invoke(object? sender, object? parameter)
    {
        var target = TargetObject ?? sender
            ?? throw new InvalidOperationException("ChangePropertyAction has no target (no TargetObject and a null sender).");

        if (string.IsNullOrEmpty(PropertyName))
            throw new InvalidOperationException("ChangePropertyAction requires a PropertyName.");

        // A registered UIProperty first (the {Name}Property static-field convention over the target's
        // hierarchy — the same idiom the XAML lanes resolve): the property-system write honors the lattice.
        if (target is UIObject uiTarget &&
            target.GetType().GetField(PropertyName + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                ?.GetValue(null) is UIProperty registered)
        {
            if (Mode == ChangePropertyMode.SetCurrentValue)
                SetCurrentValueMethod.MakeGenericMethod(registered.PropertyType)
                    .Invoke(null, [uiTarget, registered, Value]);
            else
                uiTarget.SetValue(registered, Value);
            return;
        }

        // A plain CLR property (settable) — the non-property-system fallback.
        var clr = target.GetType().GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (clr is { CanWrite: true })
        {
            clr.SetValue(target, Value);
            return;
        }

        throw new InvalidOperationException(
            $"ChangePropertyAction could not resolve a settable property '{PropertyName}' on {target.GetType().Name} " +
            "(no registered UIProperty field and no writable CLR property).");
    }

    private static void SetCurrentValueCore<T>(UIObject target, UIProperty property, object? value)
        => target.SetCurrentValue((StyledProperty<T>)property, (T)value!);
}
