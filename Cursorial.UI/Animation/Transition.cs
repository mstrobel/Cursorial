using System.Runtime.CompilerServices;

using Cursorial.Animation;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// An implicit animation (design doc §9.5): when a property's <b>effective base</b> changes (the winner among
/// sub-Animation priorities — a Style/LocalValue flip, not an animation write), a transition starts an
/// Animation-priority run from the old base to the new with <see cref="FillBehavior.Stop"/>, so the change
/// fades instead of snapping. Set a <see cref="TransitionCollection"/> via the attached
/// <see cref="TransitionsProperty"/> (style-settable; themes declare hover fades).
/// </summary>
public abstract class Transition
{
    private readonly ConditionalWeakTable<UIObject, AnimationHandle> ActiveAnimations = new();
    
    /// <summary>The attached property holding an element's transitions (style-settable; ordinary resource object).</summary>
    public static readonly AttachedProperty<TransitionCollection?> TransitionsProperty =
        UIProperty.RegisterAttached<Transition, UIElement, TransitionCollection?>(
            "Transitions", changed: OnTransitionsChanged);

    /// <summary>
    /// Gets the transitions attached to <paramref name="element"/> — a PURE read of the effective value
    /// (possibly style/theme-provided, possibly null). Never allocates or writes: a get-or-create here
    /// would pin a LocalValue that permanently masks a style-provided collection (the BuiltIn Window
    /// theme's inactive-fade is exactly such a setter) and, on an attached element, would hand back a
    /// born-sealed empty collection. Use <see cref="GetOrCreateTransitions"/> for construction-time fill.
    /// </summary>
    public static TransitionCollection? GetTransitions(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TransitionsProperty);
    }

    /// <summary>
    /// The construction-time fill accessor (the XAML loader's <c>GetOrCreate{Name}</c> attached-collection
    /// convention): returns the element's own transitions, creating and attaching an empty collection on
    /// first access. Intended for a DETACHED element under construction — the created collection stays
    /// mutable until the attach edge arms it (seal + subscribe; animation-matrix §17). On an attached
    /// element the synchronous arm seals immediately, so runtime changes should REPLACE via
    /// <see cref="SetTransitions"/> (the N149 replace-to-change contract). The write is a LocalValue pin:
    /// authoring transitions on the element deliberately outranks a style-provided collection.
    /// </summary>
    public static TransitionCollection GetOrCreateTransitions(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.GetValue(TransitionsProperty) is { } existing)
            return existing;

        var collection = new TransitionCollection();
        element.SetValue(TransitionsProperty, collection); // the changed callback arms the manager
        return collection;
    }

    /// <summary>
    /// Sets the transitions attached to <paramref name="element"/> (arms/re-arms the per-element manager).
    /// Validates EVERY transition before writing (all-or-nothing, the W2b-audit half-applied-arm finding):
    /// a wrong-typed/unset <see cref="Property"/> throws here, BEFORE the store mutates — the element's
    /// prior transitions stay intact. Framework-driven arms (style application, the attach edge) instead
    /// SKIP an invalid transition with an <see cref="AnimationDiagnostics.Warning"/> — a markup typo must
    /// not abort a style transaction or an attach walk.
    /// </summary>
    public static void SetTransitions(UIElement element, TransitionCollection? value)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (value is not null)
        {
            foreach (var transition in value)
            {
                if (transition.ValidateForArm() is { } error)
                    throw new InvalidOperationException(error);
            }
        }

        element.SetValue(TransitionsProperty, value);
    }

    private static void OnTransitionsChanged(UIObject sender, TransitionCollection? oldValue, TransitionCollection? newValue)
    {
        if (sender is UIElement element)
            TransitionManager.OnTransitionsPropertyChanged(element, newValue);
    }

    /// <summary>
    /// The animated property — a <see cref="StyledProperty{T}"/> of the transition's <c>T</c>. Base-typed
    /// <see cref="UIProperty"/> and init-settable so MARKUP can author it (the W2 CR5/CR6 shape: the XAML
    /// loader assigns the parse-resolved property; the typed subclass validates the downcast ONCE at arm,
    /// so the per-frame pipeline stays unboxed). The typed constructors remain the code-first fast path
    /// with compile-time checking.
    /// </summary>
    public UIProperty? Property { get; init; }

    /// <summary>The fade duration (default 150ms).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>A stagger before the fade starts.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>The fade easing (default linear).</summary>
    public Easing Easing { get; init; } = Easings.Linear;

    /// <summary>The markup construction path (CR10) — <see cref="Property"/> arrives via init.</summary>
    private protected Transition()
    {
    }

    private protected Transition(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property;
    }

    /// <summary>
    /// Validates this transition's configuration for arming — null when armable, else the diagnostic
    /// message (the CR6 check: the typed subclass demands a matching <c>StyledProperty&lt;T&gt;</c>).
    /// Consulted by <see cref="SetTransitions"/> (throws, pre-write) and the manager's arm (skips with an
    /// <see cref="AnimationDiagnostics.Warning"/> — framework walks never throw on an authored typo).
    /// </summary>
    internal abstract string? ValidateForArm();

    /// <summary>Subscribes the winning-base channel for this transition's property on <paramref name="target"/>.</summary>
    internal IDisposable Subscribe(UIElement target, TransitionManager manager)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(manager);

        return Disposable.Combine(
            SubscribeCore(target, manager),
            Disposable.Create(() => CancelActiveAnimation(target)));
    }

    private protected abstract IDisposable SubscribeCore(UIElement target, TransitionManager manager);
    
    private void OnAnimationCompleted(AnimationHandle handle)
    {
        handle.Completed -= OnAnimationCompleted;

        lock (ActiveAnimations)
        {
            ActiveAnimations.Remove(handle.Target);
        }
    }

    protected internal void CancelActiveAnimation(UIObject target)
    {
        AnimationHandle? handle;

        lock (ActiveAnimations)
        {
            ActiveAnimations.Remove(target, out handle);
        }
        
        handle?.Completed -= OnAnimationCompleted;
        handle?.Stop();
    }
    
    protected void RegisterAnimationHandle(AnimationHandle handle)
    {
        if (handle.State is AnimationState.Stopped or AnimationState.Completed)
        {
            // A run that finished synchronously at Begin (zero-duration / reduced motion): nothing live to track;
            // just drop any prior entry for this target.
            CancelActiveAnimation(handle.Target);
            return;
        }

        handle.Completed += OnAnimationCompleted;

        lock (ActiveAnimations)
        {
            // A re-ignite (the base changed again before the prior run finished) replaces the prior handle —
            // which BeginAnimation already retired via SnapshotAndReplace, and SILENTLY (no Completed), so
            // OnAnimationCompleted never ran to drop it. AddOrUpdate (not Add, which throws on a duplicate key)
            // overwrites; unhook the stale handle's completion first so it can't fire against the new entry.
            if (ActiveAnimations.TryGetValue(handle.Target, out var prior))
                prior.Completed -= OnAnimationCompleted;

            ActiveAnimations.AddOrUpdate(handle.Target, handle);
        }
    }
}

/// <summary>A typed transition (design doc §9.5). Sealed value-type leaves derive from this.</summary>
/// <typeparam name="T">The animated value type (must have a registered interpolator).</typeparam>
public abstract class Transition<T> : Transition
{
    private protected Transition()
    {
    }

    private protected Transition(StyledProperty<T> property) : base(property)
    {
    }

    /// <summary>The interpolator for the fade (default: the process-global registry's, AD12).</summary>
    protected virtual IInterpolator<T> Interpolator => Cursorial.Animation.Interpolator.For<T>();

    /// <inheritdoc/>
    internal sealed override string? ValidateForArm()
        => Property is StyledProperty<T>
               ? null
               : $"{GetType().Name} cannot arm: Property is " +
                 $"{(Property is null ? "unset" : $"'{Property.Name}' (value type {Property.PropertyType.Name})")} — " +
                 $"a StyledProperty<{typeof(T).Name}> is required.";

    private protected sealed override IDisposable SubscribeCore(UIElement target, TransitionManager manager)
    {
        // The CR6 arm-time validation — the ONE downcast between the base-typed markup member and the
        // typed pipeline. Past here every cast is safe and the per-frame path never touches Property.
        // Normally unreachable: SetTransitions throws pre-write and the manager's arm skips invalid
        // entries, so this is the backstop for a direct internal subscribe.
        if (Property is not StyledProperty<T> typed)
            throw new InvalidOperationException(ValidateForArm());

        return target.AddObserver(typed,
                                  new Watch(manager, this),
                                  new ObserverOptions { IncludeBaseChanges = true });
    }

    private void Ignite(UIElement target, T oldBase, T newBase, bool isAnimated)
    {
        // Only reachable through an armed Watch — SubscribeCore validated the downcast (CR6).
        var property = (StyledProperty<T>)Property!;

        // §9.5: From = isAnimated ? GetValue (the live interpolated value) : oldEffectiveBase. In the un-animated
        // case GetValue already equals the NEW base at delivery (Fork A mutates before notifying), so the old base
        // from the observer is the correct start.
        var from = isAnimated ? target.GetValue(property) : oldBase;
        if (EqualityComparer<T>.Default.Equals(from, newBase))
            return; // equal From/To ⇒ no run (§9.5)

        // Animation-priority run, Fill.Stop (zero steady-state slot). The Animation write is NOT a base change,
        // so it never re-enters the winning-base channel — structural-loop safety by construction.
        var handle = target.BeginAnimation(property, new Animation<T>(from, newBase, Duration, Interpolator, Easing),
                                           new AnimationStartOptions(BeginTime: Delay, Fill: FillBehavior.Stop));

        RegisterAnimationHandle(handle);
    }

    private sealed class Watch(TransitionManager manager, Transition<T> transition) : IValueObserver<T>, IDisposable
    {
        private int _disposed;

        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
        {
            // base channel only — the ordinary channel is not used by transitions.
        }

        public void OnBaseValueChanged(UIObject source, UIProperty property, T oldBaseValue, T newBaseValue, bool isAnimated)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (manager.ShouldTransition())
                transition.Ignite((UIElement)source, oldBaseValue, newBaseValue, isAnimated);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}

/// <summary>A <see cref="double"/> transition (e.g. <c>Opacity</c>).</summary>
public sealed class DoubleTransition : Transition<double>
{
    /// <summary>The markup construction path — set <see cref="Transition.Property"/> via init.</summary>
    public DoubleTransition() {}

    /// <summary>The typed code-first path.</summary>
    public DoubleTransition(StyledProperty<double> property) : base(property) {}
}

/// <summary>An <see cref="int"/> transition (e.g. a render offset).</summary>
public sealed class Int32Transition : Transition<int>
{
    /// <inheritdoc cref="DoubleTransition()"/>
    public Int32Transition() {}

    /// <inheritdoc cref="DoubleTransition(StyledProperty{double})"/>
    public Int32Transition(StyledProperty<int> property) : base(property) {}
}

/// <summary>A <see cref="Color"/> transition.</summary>
public sealed class ColorTransition : Transition<Color>
{
    /// <inheritdoc cref="DoubleTransition()"/>
    public ColorTransition() {}

    /// <inheritdoc cref="DoubleTransition(StyledProperty{double})"/>
    public ColorTransition(StyledProperty<Color> property) : base(property) {}
}

/// <summary>An <see cref="IBrush"/> transition (allocates one brush per sample — the documented exception, AD8).</summary>
public sealed class BrushTransition : Transition<IBrush>
{
    /// <inheritdoc cref="DoubleTransition()"/>
    public BrushTransition() {}

    /// <inheritdoc cref="DoubleTransition(StyledProperty{double})"/>
    public BrushTransition(StyledProperty<IBrush> property) : base(property) {}
}

/// <summary>A <see cref="Margins"/> transition (signed interpolation, LD19).</summary>
public sealed class MarginsTransition : Transition<Margins>
{
    /// <inheritdoc cref="DoubleTransition()"/>
    public MarginsTransition() {}

    /// <inheritdoc cref="DoubleTransition(StyledProperty{double})"/>
    public MarginsTransition(StyledProperty<Margins> property) : base(property) {}
}
