using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// Invokes a named public instance method on a target when the trigger fires (design doc §5). The method
/// is matched by name on the target's type: a <c>(object? sender, object? parameter)</c>-compatible
/// two-parameter overload is preferred (it receives the firing context), else a parameterless one. An
/// unresolvable target/method throws — never a silent no-op. (A typed-delegate fast path is the
/// generator-lane optimization; this runtime action is reflective by nature.)
/// </summary>
public class CallMethodAction : TriggerAction
{
    /// <summary>The object whose method is invoked; default: the firing trigger's host (the sender).</summary>
    public object? TargetObject { get; set; }

    /// <summary>The public instance method name to invoke.</summary>
    public string? MethodName { get; set; }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Named-method resolution is the runtime action's contract; a generator bakes the call.")]
    protected override void Invoke(object? sender, object? parameter)
    {
        var target = TargetObject ?? sender
            ?? throw new InvalidOperationException("CallMethodAction has no target (no TargetObject and a null sender).");

        if (string.IsNullOrEmpty(MethodName))
            throw new InvalidOperationException("CallMethodAction requires a MethodName.");

        MethodInfo? parameterless = null;

        foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!string.Equals(method.Name, MethodName, StringComparison.Ordinal))
                continue;

            var parameters = method.GetParameters();

            // Preferred: a (sender, parameter)-compatible two-parameter overload.
            if (parameters.Length == 2 &&
                (sender is null || parameters[0].ParameterType.IsInstanceOfType(sender) || parameters[0].ParameterType == typeof(object)) &&
                (parameter is null || parameters[1].ParameterType.IsInstanceOfType(parameter) || parameters[1].ParameterType == typeof(object)))
            {
                method.Invoke(target, [sender, parameter]);
                return;
            }

            if (parameters.Length == 0)
                parameterless = method;
        }

        if (parameterless is not null)
        {
            parameterless.Invoke(target, null);
            return;
        }

        throw new InvalidOperationException(
            $"CallMethodAction could not resolve a public instance method '{MethodName}' on {target.GetType().Name} " +
            "taking (sender, parameter) or no parameters.");
    }
}
