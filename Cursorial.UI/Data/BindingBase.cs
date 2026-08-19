using System.Globalization;

namespace Cursorial.UI.Data;

/// <summary>
/// The construction-immutable, instance-shareable binding descriptor base (design doc §6.1). One
/// descriptor in a <c>Setter</c> or <c>DataCondition</c> serves every element it is armed on — all
/// per-target state lives in the <see cref="BindingExpressionBase"/> produced by
/// <see cref="CreateExpression"/>, the single engine contract.
/// </summary>
public abstract class BindingBase
{
    /// <summary>
    /// The value that should be used to specify a true <c>null</c> value for optional method arguments
    /// corresponding to <see cref="BindingBase.FallbackValue"/> and <see cref="BindingBase.TargetNullValue"/>.
    /// Passing a literal <c>null</c> is indistinguishable from leaving the argument unspecified, and will
    /// result in the default value of <see cref="UIProperty.UnsetValue"/> being used instead. 
    /// </summary>
    public static readonly object NullValue = new UIProperty.SentinelValue($"{nameof(BindingBase)}.{nameof(NullValue)}");

    /// <summary>The value-flow direction. <see cref="BindingMode.Default"/> resolves at install (BD10).</summary>
    public BindingMode Mode { get; init; } = BindingMode.Default;

    /// <summary>When a target edit flushes to the source. <see cref="UpdateSourceTrigger.Default"/> ⇒ <see cref="UpdateSourceTrigger.PropertyChanged"/>.</summary>
    public UpdateSourceTrigger UpdateSourceTrigger { get; init; } = UpdateSourceTrigger.Default;

    /// <summary>The value pushed when the path is unresolvable; <see cref="UIProperty.UnsetValue"/> = "no fallback".</summary>
    public object? FallbackValue { get; init; } = UIProperty.UnsetValue;

    /// <summary>The value substituted when the source produces <see langword="null"/>; <see cref="UIProperty.UnsetValue"/> = "not specified".</summary>
    public object? TargetNullValue { get; init; } = UIProperty.UnsetValue;

    /// <summary>A composite format applied to string/object targets (forward) and reverse-parsed only when exactly <c>"{0}"</c>.</summary>
    public string? StringFormat { get; init; }

    /// <summary>The conversion culture; <see langword="null"/> ⇒ <see cref="CultureInfo.CurrentCulture"/> (design doc §6.11.3).</summary>
    public CultureInfo? ConverterCulture { get; init; }

    /// <summary>Whether this binding emits verbose diagnostics regardless of the global <c>Level</c>.</summary>
    public bool Trace { get; init; }

    /// <summary>Whether <see cref="FallbackValue"/> was specified (not the unset sentinel).</summary>
    internal bool HasFallbackValue => !ReferenceEquals(FallbackValue, UIProperty.UnsetValue);

    /// <summary>Whether <see cref="TargetNullValue"/> was specified (not the unset sentinel).</summary>
    internal bool HasTargetNullValue => !ReferenceEquals(TargetNullValue, UIProperty.UnsetValue);

    /// <summary>The effective conversion culture.</summary>
    internal CultureInfo EffectiveCulture => ConverterCulture ?? CultureInfo.CurrentCulture;

    /// <summary>
    /// THE engine contract: builds the per-target expression. All descriptor lanes produce the same
    /// expression shape. Internal — installation goes through <see cref="BindingOperations"/>.
    /// </summary>
    internal abstract BindingExpressionBase CreateExpression(in BindingActivationContext context);

    /// <summary>
    /// Resolves the actual effective value to use for <see cref="FallbackValue"/> or <see cref="TargetNullValue"/>.
    /// A <c>null</c> literal resolves to the default <see cref="UIProperty.UnsetValue"/>, and <see cref="NullValue"/>
    /// resolves to a true <c>null</c> value.
    /// </summary>
    protected internal static object? ResolveFallbackValue(object? value)
    {
        if (value is null) return UIProperty.UnsetValue;
        if (ReferenceEquals(value, NullValue)) return null;
        return value;
    }
}
