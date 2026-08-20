namespace Cursorial.UI.Data;

/// <summary>
/// The binding-engine entry points (design doc §6.2). All are UI-thread-only (<c>VerifyAccess</c>
/// debug-asserted — invariant 6).
/// </summary>
public static class BindingOperations
{
    /// <summary>
    /// Installs a binding at <see cref="BindingPriority.LocalValue"/>. <b>Replace-and-dispose</b>: an
    /// existing LocalValue-lane expression for <c>(target, property)</c> is disposed before the new
    /// install (BD6). Throws <see cref="ArgumentException"/> when the property metadata is
    /// <see cref="PropertyEffects.NotDataBindable"/>.
    /// </summary>
    public static BindingExpressionBase Install(UIObject target, UIProperty property, BindingBase binding)
        => InstallCore(target, property, binding, hostFrame: null);

    /// <summary>
    /// Frame-hosted install (binding-valued style setters; template content): the produced entry
    /// lives in <paramref name="hostFrame"/> and is evicted — disposing the expression — when the
    /// frame is removed/retracted. Exempt from replace-and-dispose (frames stack).
    /// </summary>
    public static BindingExpressionBase Install(UIObject target, UIProperty property, BindingBase binding, ValueFrame hostFrame)
    {
        ArgumentNullException.ThrowIfNull(hostFrame);
        return InstallCore(target, property, binding, hostFrame);
    }

    private static BindingExpressionBase InstallCore(UIObject target, UIProperty property, BindingBase binding, ValueFrame? hostFrame)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(binding);
        target.VerifyAccess();

        if (!property.IsDirect)
        {
            var effects = property.GetEffects(target.GetType());
            if ((effects & PropertyEffects.NotDataBindable) != 0)
            {
                throw new ArgumentException(
                    $"Property '{property}' is marked NotDataBindable and cannot be data-bound.", nameof(property));
            }
        }

        // DataContext lives on UIElement and does not flow to a plain UIObject. A UIElement target anchors on
        // itself; a non-UIElement target (an InputBinding/KeyBinding whose Command="{Binding}") has a null
        // anchor here and re-anchors on the nearest UIElement up its inheritance chain inside the expression —
        // see BindingExpressionCore.Activate / its InheritanceParentChanged hook (BD13).
        var anchor = target as UIElement;
        // §20/PD24: a free-standing binding installed inside a template-instantiation scope captures
        // the Template lane now (the entry may materialize on a later attach, outside the scope).
        // Frame-hosted installs ignore this (they contribute at Style).
        var context = new BindingActivationContext(
            target, property, anchor, hostFrame, watchCallback: null,
            installPriority: TemplateInstantiationScope.CurrentInstallPriority);
        return binding.CreateExpression(in context);
    }

    /// <summary>
    /// WPF-shaped alias for <see cref="Install(UIObject, UIProperty, BindingBase)"/> (the
    /// <c>System.Windows.Data.BindingOperations.SetBinding</c> analog): installs <paramref name="binding"/>
    /// at LocalValue (replace-and-dispose) and returns the expression.
    /// </summary>
    public static BindingExpressionBase SetBinding(UIObject target, UIProperty property, BindingBase binding)
        => Install(target, property, binding);

    /// <summary>
    /// Removes the LocalValue-lane binding for <c>(target, property)</c> (the
    /// <c>System.Windows.Data.BindingOperations.ClearBinding</c> analog): disposes the tracked
    /// expression (unsubscribing its sources) and clears the local value via
    /// <see cref="UIObject.ClearValue"/> (ledger A9 — <c>ClearValue</c> is the binding kill, evicting
    /// the local-priority entry). A no-op when no LocalValue-lane expression is installed. Frame-hosted
    /// and watch-only expressions are not touched (they die through frame retraction / watch disposal).
    /// </summary>
    public static void ClearBinding(UIObject target, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        target.VerifyAccess();

        var expression = BindingRegistry.Get(target)?.GetLocalValueExpression(property);
        expression?.Dispose();

        // ClearValue is the documented local-binding kill (A9); it also evicts any local entry the
        // expression's dispose did not (a freestanding Bind entry is store-evicted by Dispose, but
        // ClearValue is idempotent and the canonical surface). Skip for the watch-only sentinel.
        if (!ReferenceEquals(property, UIProperty.UnsetTargetProperty))
            target.ClearValue(property);
    }

    /// <summary>
    /// The LocalValue-lane expression tracked for <c>(target, property)</c>, or <see langword="null"/>.
    /// Frame-hosted, watch-only, and DirectProperty expressions return <see langword="null"/> (BD7);
    /// <see cref="BindingDiagnostics.Explain"/> covers all lanes.
    /// </summary>
    public static BindingExpressionBase? GetBindingExpression(UIObject target, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        target.VerifyAccess();
        return BindingRegistry.Get(target)?.GetLocalValueExpression(property);
    }

    /// <summary>
    /// The <see cref="BindingBase"/> descriptor of the LocalValue-lane expression for
    /// <c>(target, property)</c>, or <see langword="null"/> (the
    /// <c>System.Windows.Data.BindingOperations.GetBinding</c> analog — useful for cloning/inspecting
    /// the installed descriptor). The same lane scoping as
    /// <see cref="GetBindingExpression"/> applies (frame-hosted / watch-only / DirectProperty return
    /// <see langword="null"/>).
    /// </summary>
    public static BindingBase? GetBinding(UIObject target, UIProperty property)
        => GetBindingExpression(target, property)?.ParentBinding;

    /// <summary>
    /// Watch-only arming (design doc §6.8) — the styling engine's <c>When</c>/<c>DataCondition</c>
    /// seam. No store entry; the value (or <see cref="UIProperty.UnsetValue"/> when unresolved) is
    /// delivered to <paramref name="onValueChanged"/>. DataContext changes on the anchor rebind and
    /// re-deliver automatically; delivery is synchronous on the UI thread.
    /// </summary>
    public static IBindingWatch Watch(UIElement anchor, BindingBase binding, Action<object?> onValueChanged)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(onValueChanged);
        anchor.VerifyAccess();

        var watch = new BindingWatch();
        var context = new BindingActivationContext(
            anchor, UIProperty.UnsetTargetProperty, anchor, hostFrame: null,
            watchCallback: value =>
            {
                watch.SetValue(value);
                onValueChanged(value);
            });

        // CreateExpression is the single engine contract (BindingBase): reflective and compiled
        // descriptors both build watch-mode expressions — the shared core delivers to the callback.
        watch.Expression = binding.CreateExpression(in context);
        return watch;
    }

    /// <summary>
    /// The teardown-sweep half owned by this engine (design doc §6.5): disposes every
    /// registry-tracked expression on <paramref name="target"/> not already dead via store eviction —
    /// DirectProperty-targeted expressions and watches anchored here. Called by the tree/window fork
    /// during permanent-detach teardown, AFTER <c>ValueStore.TearDown()</c>. Closes the P1
    /// <c>UIElement.TearDown</c> S2 gap.
    /// </summary>
    public static void TearDown(UIObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.VerifyAccess();
        BindingRegistry.TearDown(target);
    }

    /// <summary>
    /// Retracts only the bindings on <paramref name="target"/> anchored to <paramref name="source"/>.
    /// The teardown for content a host does NOT own — borrowed elements, or a shared
    /// <c>DeferredContent</c> realization — where a blanket <see cref="TearDown"/> would destroy the
    /// author's bindings but leaving the host's forwards installed would pin the content to the host.
    /// </summary>
    public static void TearDownAnchoredTo(UIObject target, object source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.VerifyAccess();
        BindingRegistry.TearDownAnchoredTo(target, source);
    }

    private sealed class BindingWatch : IBindingWatch
    {
        private object? _value = UIProperty.UnsetValue;

        public BindingExpressionBase? Expression { get; set; }

        public object? Value => _value;

        public void SetValue(object? value) => _value = value;

        public void Dispose() => Expression?.Dispose();
    }
}

/// <summary>Code-first binding-install sugar (design doc §6.2).</summary>
public static class BindingExtensions
{
    /// <summary>Installs <paramref name="binding"/> at LocalValue and returns the expression.</summary>
    public static BindingExpressionBase SetBinding(this UIObject target, UIProperty property, BindingBase binding)
        => BindingOperations.Install(target, property, binding);
}
