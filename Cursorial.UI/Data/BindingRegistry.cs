using System.Collections.Immutable;

namespace Cursorial.UI.Data;

/// <summary>
/// The per-target expression registry (design doc §6.5/§6.10; BD19) — one inline list per
/// <c>UIObject</c> with live expressions, stored in the Fork-A-reserved opaque
/// <c>UIObject.BindingHostState</c> slot (null when unused — no <c>ConditionalWeakTable</c>). It
/// backs replace-and-dispose at LocalValue, <c>GetBindingExpression</c>,
/// <c>BindingDiagnostics.Explain</c>, and the teardown sweep. Release-build (not just DEBUG).
/// </summary>
internal sealed class BindingRegistry
{
    private readonly List<BindingExpressionBase> _expressions = [];

    private BindingRegistry()
    {
    }

    /// <summary>The host registry for <paramref name="target"/>, allocating it lazily on first install (matrix B119).</summary>
    public static BindingRegistry GetOrCreate(UIObject target)
    {
        if (target.BindingHostState is BindingRegistry existing)
            return existing;

        var registry = new BindingRegistry();
        target.BindingHostState = registry;
        return registry;
    }

    /// <summary>The host registry for <paramref name="target"/>, or <see langword="null"/> when it never bound.</summary>
    public static BindingRegistry? Get(UIObject target) => target.BindingHostState as BindingRegistry;

    /// <summary>Registers <paramref name="expression"/> and applies replace-and-dispose for the LocalValue lane (BD6).</summary>
    public void Register(BindingExpressionBase expression)
    {
        if (expression.Lane == BindingLane.LocalValue)
        {
            // Replace-and-dispose: dispose any existing LocalValue-lane expression for the same
            // property BEFORE adding the new one (one live LocalValue expression per pair). Snapshot
            // first — Dispose mutates the list.
            for (var i = _expressions.Count - 1; i >= 0; i--)
            {
                var existing = _expressions[i];
                if (existing.Lane == BindingLane.LocalValue && existing.TargetProperty.Id == expression.TargetProperty.Id)
                    existing.Dispose();
            }
        }

        _expressions.Add(expression);
    }

    /// <summary>Removes <paramref name="expression"/> on its disposal.</summary>
    public void Unregister(BindingExpressionBase expression) => _expressions.Remove(expression);

    /// <summary>The LocalValue-lane expression for <paramref name="property"/>, or <see langword="null"/> (BD7).</summary>
    public BindingExpressionBase? GetLocalValueExpression(UIProperty property)
    {
        foreach (var expression in _expressions)
        {
            if (expression.Lane == BindingLane.LocalValue && expression.TargetProperty.Id == property.Id)
                return expression;
        }

        return null;
    }

    /// <summary>
    /// The detach-walk write-back gate: quiesces the reverse lane of every expression on
    /// <paramref name="target"/> (and discards pending flushes) so the severance cascade never writes a
    /// phantom edit into a source — "a departing view must not write" (the chooser/dialog selection-loss
    /// fix). Cheap for unbound elements (a null <c>BindingHostState</c> probe).
    /// </summary>
    public static void QuiesceReverse(UIObject target)
    {
        if (target.BindingHostState is not BindingRegistry registry)
            return;

        foreach (var expression in registry._expressions)
        {
            if (expression is BindingExpressionCore core)
                core.QuiesceReverse();
        }
    }

    /// <summary>The attach-walk half: re-arms the reverse lane (a re-hosted view binds two-way again).</summary>
    public static void ResumeReverse(UIObject target)
    {
        if (target.BindingHostState is not BindingRegistry registry)
            return;

        foreach (var expression in registry._expressions)
        {
            if (expression is BindingExpressionCore core)
                core.ResumeReverse();
        }
    }

    /// <summary>The teardown-sweep half (design doc §6.5): disposes every still-live tracked expression on <paramref name="target"/>.</summary>
    public static void TearDown(UIObject target)
    {
        if (target.BindingHostState is not BindingRegistry registry)
            return;

        // Snapshot — Dispose mutates the list and a disposing expression may dispose siblings (B120).
        var snapshot = registry._expressions.ToArray();
        foreach (var expression in snapshot)
            expression.Dispose();

        target.BindingHostState = null;
    }

    /// <summary>
    /// The SELECTIVE teardown: disposes only the expressions on <paramref name="target"/> whose
    /// binding is anchored to <paramref name="source"/>, leaving every other binding in place. This
    /// is what a host uses to retract its own forwards from content it does not own — borrowed
    /// element content, or a <c>DeferredContent</c> realization that is CACHED and therefore shared
    /// with whoever realizes it next. A blanket <see cref="TearDown"/> there would take the author's
    /// bindings with it; leaving the forwards installed would pin the content to this host.
    /// </summary>
    public static void TearDownAnchoredTo(UIObject target, object source)
    {
        if (target.BindingHostState is not BindingRegistry registry)
            return;

        // Snapshot — Dispose mutates the list (B120). The registry is NOT cleared: the survivors are
        // the author's and stay live.
        var snapshot = registry._expressions.ToArray();
        foreach (var expression in snapshot)
        {
            if (expression.ParentBinding is Binding { Source: { } anchored } && ReferenceEquals(anchored, source))
                expression.Dispose();
        }
    }

    /// <summary>Builds the <see cref="BindingExplanation"/> for a <c>(target, property)</c> pair (design doc §6.10).</summary>
    public static BindingExplanation Explain(UIObject target, UIProperty property)
    {
        var description = DescribeTarget(target, property);
        if (target.BindingHostState is not BindingRegistry registry)
            return new BindingExplanation(description, ImmutableArray<BindingExpressionExplanation>.Empty);

        var lines = ImmutableArray.CreateBuilder<BindingExpressionExplanation>();
        foreach (var expression in registry._expressions)
        {
            if (expression.TargetProperty.Id == property.Id)
                lines.Add(expression.Explain());
        }

        // Strongest-first: LocalValue (0) < FrameHosted (1) < WatchOnly (2) < DirectProperty (3) per
        // the enum; LocalValue/DirectProperty are the "winning" producing lanes.
        lines.Sort(static (a, b) => a.Lane.CompareTo(b.Lane));
        return new BindingExplanation(description, lines.ToImmutable());
    }

    internal static string DescribeTarget(UIObject target, UIProperty property)
    {
        var name = target is UIElement { Name: { Length: > 0 } elementName } ? $"#{elementName}" : string.Empty;
        return $"{target.GetType().Name}{name}.{property.Name}";
    }
}
