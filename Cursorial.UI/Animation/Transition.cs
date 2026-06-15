using Cursorial.Animation;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

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
    /// <summary>The attached property holding an element's transitions (style-settable; ordinary resource object).</summary>
    public static readonly AttachedProperty<TransitionCollection?> TransitionsProperty =
        UIProperty.RegisterAttached<Transition, UIElement, TransitionCollection?>(
            "Transitions", changed: OnTransitionsChanged);

    /// <summary>Gets the transitions attached to <paramref name="element"/>.</summary>
    public static TransitionCollection? GetTransitions(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TransitionsProperty);
    }

    /// <summary>Sets the transitions attached to <paramref name="element"/> (arms/re-arms the per-element manager).</summary>
    public static void SetTransitions(UIElement element, TransitionCollection? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TransitionsProperty, value);
    }

    private static void OnTransitionsChanged(UIObject sender, TransitionCollection? oldValue, TransitionCollection? newValue)
    {
        if (sender is UIElement element)
            TransitionManager.OnTransitionsPropertyChanged(element, newValue);
    }

    /// <summary>The animated property (a <see cref="StyledProperty{T}"/> of the transition's <c>T</c>).</summary>
    public UIProperty Property { get; }

    /// <summary>The fade duration (default 150ms).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>A stagger before the fade starts.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>The fade easing (default linear).</summary>
    public Easing Easing { get; init; } = Easings.Linear;

    private protected Transition(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property;
    }

    /// <summary>Subscribes the winning-base channel for this transition's property on <paramref name="target"/>.</summary>
    internal abstract IDisposable Subscribe(UIElement target, TransitionManager manager);
}

/// <summary>A typed transition (design doc §9.5). Sealed value-type leaves derive from this.</summary>
/// <typeparam name="T">The animated value type (must have a registered interpolator).</typeparam>
public abstract class Transition<T> : Transition
{
    private protected Transition(StyledProperty<T> property) : base(property)
    {
    }

    /// <summary>The interpolator for the fade (default: the process-global registry's, AD12).</summary>
    protected virtual IInterpolator<T> Interpolator => Cursorial.Animation.Interpolator.For<T>();

    internal sealed override IDisposable Subscribe(UIElement target, TransitionManager manager)
        => target.AddObserver((StyledProperty<T>)Property, new Watch(manager, this), new ObserverOptions { IncludeBaseChanges = true });

    private void Ignite(UIElement target, T oldBase, T newBase, bool isAnimated)
    {
        var property = (StyledProperty<T>)Property;

        // §9.5: From = isAnimated ? GetValue (the live interpolated value) : oldEffectiveBase. In the un-animated
        // case GetValue already equals the NEW base at delivery (Fork A mutates before notifying), so the old base
        // from the observer is the correct start.
        var from = isAnimated ? target.GetValue(property) : oldBase;
        if (EqualityComparer<T>.Default.Equals(from, newBase))
            return; // equal From/To ⇒ no run (§9.5)

        // Animation-priority run, Fill.Stop (zero steady-state slot). The Animation write is NOT a base change,
        // so it never re-enters the winning-base channel — structural-loop safety by construction.
        target.BeginAnimation(property, new Animation<T>(from, newBase, Duration, Interpolator, Easing),
            new AnimationStartOptions(BeginTime: Delay, Fill: FillBehavior.Stop));
    }

    private sealed class Watch(TransitionManager manager, Transition<T> transition) : IValueObserver<T>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
        {
            // base channel only — the ordinary channel is not used by transitions.
        }

        public void OnBaseValueChanged(UIObject source, UIProperty property, T oldBaseValue, T newBaseValue, bool isAnimated)
        {
            if (manager.ShouldTransition())
                transition.Ignite((UIElement)source, oldBaseValue, newBaseValue, isAnimated);
        }
    }
}

/// <summary>A <see cref="double"/> transition (e.g. <c>Opacity</c>).</summary>
public sealed class DoubleTransition(StyledProperty<double> property) : Transition<double>(property);

/// <summary>An <see cref="int"/> transition (e.g. a render offset).</summary>
public sealed class Int32Transition(StyledProperty<int> property) : Transition<int>(property);

/// <summary>A <see cref="Color"/> transition.</summary>
public sealed class ColorTransition(StyledProperty<Color> property) : Transition<Color>(property);

/// <summary>An <see cref="IBrush"/> transition (allocates one brush per sample — the documented exception, AD8).</summary>
public sealed class BrushTransition(StyledProperty<IBrush> property) : Transition<IBrush>(property);

/// <summary>A <see cref="Margins"/> transition (signed interpolation, LD19).</summary>
public sealed class MarginsTransition(StyledProperty<Margins> property) : Transition<Margins>(property);
