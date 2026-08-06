using Cursorial.Animation;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// One property's timeline inside a <see cref="Storyboard"/> (design doc §9.3) — the non-generic base the
/// storyboard holds in one list. A track is a <b>description</b>; <see cref="Storyboard.Begin"/> resolves it
/// against a scope and starts a per-(igniter, scope) instance.
/// </summary>
public abstract class AnimationTrack
{
    /// <summary>The named target inside the scope (null ⇒ the <see cref="Storyboard.Begin"/> scope itself); resolved via S2's template-aware <see cref="UIElement.FindName"/>.</summary>
    public string? TargetName { get; set; }

    /// <summary>The animated property (required; a <see cref="StyledProperty{T}"/> of the track's <c>T</c>).</summary>
    public UIProperty? TargetProperty { get; set; }

    /// <summary>A stagger before this track starts; the property is untouched until then (no handle).</summary>
    public TimeSpan BeginTime { get; set; }

    /// <summary>How many times the built animation repeats (default <see cref="RepeatBehavior.Once"/>).</summary>
    public RepeatBehavior Repeat { get; set; } = RepeatBehavior.Once;

    /// <summary>Whether each iteration plays forward then backward.</summary>
    public bool AutoReverse { get; set; }

    /// <summary>What the property does at the end (default <see cref="FillBehavior.HoldEnd"/>).</summary>
    public FillBehavior Fill { get; set; } = FillBehavior.HoldEnd;

    /// <summary>The track's value type (<c>T</c>).</summary>
    internal abstract Type ValueType { get; }

    /// <summary>Element-independent validation (design doc §9.3): T vs property type, Source/Repeat overflow. Idempotent.</summary>
    internal abstract void Seal();

    /// <summary>
    /// Resolves the target/property against <paramref name="scope"/>, builds the animation (snapshotting
    /// <c>From</c> at track start when unset — AD4), and starts a child instance under <paramref name="owner"/>.
    /// Returns <see langword="false"/> with a <paramref name="failure"/> message on an unresolvable target/property
    /// or a build error (the caller decides throw vs. <see cref="AnimationDiagnostics"/> per the error-policy split).
    /// </summary>
    internal abstract bool BeginOn(AnimationScheduler scheduler, StoryboardInstance owner, UIElement scope, out string? failure);
}

/// <summary>
/// A typed property timeline (design doc §9.3). Build inputs are mutually exclusive layers: an explicit
/// <see cref="Source"/> animation; or <see cref="Keyframes"/>; or the <see cref="From"/>/<see cref="To"/>/
/// <see cref="Duration"/> two-point form. <see cref="AnimationTrack.Repeat"/>/<see cref="AnimationTrack.AutoReverse"/>
/// wrap the result uniformly via <see cref="RepeatAnimation{T}"/>.
/// </summary>
/// <typeparam name="T">The animated value type.</typeparam>
public class AnimationTrack<T> : AnimationTrack
{
    /// <summary>The start value; unset ⇒ snapshot <c>GetValue(property)</c> at track start (§9.4).</summary>
    public Optional<T> From { get; set; }

    /// <summary>The end value (required for the two-point form).</summary>
    public Optional<T> To { get; set; }

    /// <summary>The two-point form's duration.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>The easing for the two-point form (null ⇒ linear).</summary>
    public Easing? Easing { get; set; }

    /// <summary>An explicit interpolator (null ⇒ <see cref="Interpolator.For{T}"/>).</summary>
    public IInterpolator<T>? Interpolator { get; set; }

    /// <summary>Keyframes (mutually exclusive with the two-point form and <see cref="Source"/>).</summary>
    public IList<Keyframe<T>>? Keyframes { get; set; }

    /// <summary>A code-built animation escape hatch; <see cref="AnimationTrack.Repeat"/>/<see cref="AnimationTrack.AutoReverse"/> wrap it uniformly.</summary>
    public IAnimation<T>? Source { get; set; }

    private bool _sealed;

    internal override Type ValueType => typeof(T);

    internal override void Seal()
    {
        if (_sealed)
            return;

        if (TargetProperty is null)
            throw new InvalidOperationException($"AnimationTrack<{typeof(T).Name}>.TargetProperty is required.");

        if (TargetProperty.PropertyType != typeof(T))
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}> targets property '{TargetProperty}' whose value type is " +
                $"{TargetProperty.PropertyType.Name}; the track type must match the property type.");

        if (BeginTime < TimeSpan.Zero)
            throw new InvalidOperationException("AnimationTrack.BeginTime must be non-negative.");

        if (Source is null && Keyframes is not { Count: > 0 })
        {
            if (!To.HasValue)
                throw new InvalidOperationException(
                    $"AnimationTrack<{typeof(T).Name}> requires To (or Keyframes, or Source).");
            if (Duration < TimeSpan.Zero)
                throw new InvalidOperationException("AnimationTrack.Duration must be non-negative.");
        }

        // Build once with a placeholder From — exercises the RepeatAnimation overflow / perpetual-repeat guard
        // (the single source of truth) so a bad Repeat surfaces at seal, not first hover (§9.3).
        _ = BuildAnimation(From.GetValueOrDefault(default!));
        _sealed = true;
    }

    /// <summary>Builds the concrete <see cref="IAnimation{T}"/>, snapshotting <c>From</c> from <paramref name="fromSnapshot"/> when unset.</summary>
    internal IAnimation<T> BuildAnimation(T fromSnapshot)
    {
        IAnimation<T> core;
        if (Source is not null)
        {
            core = Source;
        }
        else if (Keyframes is { Count: > 0 } keyframes)
        {
            core = new KeyframeAnimation<T>(keyframes, Interpolator ?? Cursorial.Animation.Interpolator.For<T>());
        }
        else
        {
            var from = From.HasValue ? From.Value : fromSnapshot;
            var to = To.HasValue ? To.Value
                : throw new InvalidOperationException($"AnimationTrack<{typeof(T).Name}> requires To (or Keyframes, or Source).");
            core = new Animation<T>(from, to, Duration, Interpolator ?? Cursorial.Animation.Interpolator.For<T>(), Easing);
        }

        var count = Repeat.IterationCount; // null ⇒ Forever
        if (count is 1 && !AutoReverse)
            return core;

        return new RepeatAnimation<T>(core, count, AutoReverse);
    }

    internal override bool BeginOn(AnimationScheduler scheduler, StoryboardInstance owner, UIElement scope, out string? failure)
    {
        failure = null;

        var target = TargetName is null ? scope : scope.FindName(TargetName) as UIElement;
        if (target is null)
        {
            failure = $"AnimationTrack TargetName '{TargetName}' did not resolve to a UIElement in the scope.";
            return false;
        }

        if (TargetProperty is not StyledProperty<T> property)
        {
            failure = $"AnimationTrack.TargetProperty '{TargetProperty}' is not a StyledProperty<{typeof(T).Name}>.";
            return false;
        }

        IAnimation<T> animation;
        try
        {
            animation = BuildAnimation(target.GetValue(property)); // From snapshot at track start (AD4)
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }

        scheduler.BeginStoryboardChild(owner, target, property, animation, new AnimationStartOptions(BeginTime, Fill));
        return true;
    }
}

/// <summary>A <see cref="double"/> property track (XAML-friendly; design doc §9.3).</summary>
public sealed class DoubleTrack : AnimationTrack<double>;

/// <summary>An <see cref="int"/> property track.</summary>
public sealed class Int32Track : AnimationTrack<int>;

/// <summary>A <see cref="Color"/> property track.</summary>
public sealed class ColorTrack : AnimationTrack<Color>;

/// <summary>An <see cref="IBrush"/> property track (allocates one brush per sample — the documented exception, AD8).</summary>
public sealed class BrushTrack : AnimationTrack<IBrush>;

/// <summary>A <see cref="Rect"/> property track.</summary>
public sealed class RectTrack : AnimationTrack<Rect>;

/// <summary>A <see cref="Size"/> property track.</summary>
public sealed class SizeTrack : AnimationTrack<Size>;

/// <summary>A <see cref="Margins"/> property track (signed interpolation, AD13).</summary>
public sealed class MarginsTrack : AnimationTrack<Margins>;
