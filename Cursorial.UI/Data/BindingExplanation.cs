using System.Collections.Immutable;

namespace Cursorial.UI.Data;

/// <summary>The lane an explained expression occupies (design doc §6.10).</summary>
public enum BindingLane : byte
{
    /// <summary>A free-standing local install (replace-and-dispose lane; <c>GetBindingExpression</c> returns it).</summary>
    LocalValue,

    /// <summary>A frame-hosted install (binding-valued style setter / template content).</summary>
    FrameHosted,

    /// <summary>A watch-only arming (<c>BindingOperations.Watch</c>).</summary>
    WatchOnly,

    /// <summary>A binding targeting a <c>DirectProperty</c> (no store entry).</summary>
    DirectProperty,
}

/// <summary>One explained expression line (design doc §6.10).</summary>
/// <param name="Lane">The lane the expression occupies.</param>
/// <param name="Path">The binding path text.</param>
/// <param name="Status">The expression's current status.</param>
/// <param name="EffectiveMode">The resolved binding mode.</param>
/// <param name="ResolvedSourceChain">A human-readable description of the resolved source/anchor chain.</param>
/// <param name="LastProducedValue">The last value produced to the target, or <see langword="null"/>.</param>
/// <param name="LastFailure">The last failure kind, or <see cref="BindingFailureKind.None"/>.</param>
public readonly record struct BindingExpressionExplanation(
    BindingLane Lane,
    string Path,
    BindingStatus Status,
    BindingMode EffectiveMode,
    string ResolvedSourceChain,
    object? LastProducedValue,
    BindingFailureKind LastFailure);

/// <summary>
/// The result of <see cref="BindingDiagnostics.Explain"/> — every expression across all lanes on a
/// <c>(target, property)</c> pair, strongest-first (design doc §6.10).
/// </summary>
/// <param name="TargetDescription">The target descriptor (e.g. <c>Widget#a.Text</c>).</param>
/// <param name="Expressions">The explained expression lines, strongest-first.</param>
public readonly record struct BindingExplanation(
    string TargetDescription,
    ImmutableArray<BindingExpressionExplanation> Expressions)
{
    /// <summary>Whether any expression is tracked for the pair.</summary>
    public bool HasBindings => !Expressions.IsDefaultOrEmpty;
}
