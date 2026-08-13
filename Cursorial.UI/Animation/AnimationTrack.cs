using Cursorial.Animation;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI.Data;

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

    /// <summary>The explicit target; takes precedence over <see cref="TargetName"/>.</summary>
    public UIObject? Target { get; set; }

    /// <summary>
    /// The animated property, a <see cref="StyledProperty{T}"/> of the track's <c>T</c> directly on the resolved
    /// target. Mutually exclusive with <see cref="TargetPath"/> — set exactly one (validated at <see cref="Seal"/>).
    /// </summary>
    public UIProperty? TargetProperty { get; set; }

    /// <summary>
    /// A property PATH from the resolved target (<see cref="Target"/>/<see cref="TargetName"/>) down through
    /// intermediate sub-object-valued properties to the animated <see cref="StyledProperty{T}"/> — the path's
    /// <b>terminal</b> segment (design doc §9.3, task #26). This is what makes an inline-declared sub-object (e.g. a
    /// <c>PhaseShiftedBrush</c> written straight into an element's <c>Foreground</c>) animatable WITHOUT promoting it
    /// to a resource: <c>new PropertyPath(TextBlock.ForegroundProperty, PhaseShiftedBrush.PhaseProperty)</c>, or the
    /// string form <c>"Foreground.Phase"</c> / the type-qualified <c>"(TextBlock.Foreground).(PhaseShiftedBrush.Phase)"</c>.
    /// <para>
    /// The grammar is Cursorial's own binding <see cref="PropertyPath"/> (WPF's <c>PropertyPath</c> analog, design doc
    /// §6.3) reused verbatim — NOT a second path dialect — so a storyboard target path and a <c>{Binding Path=…}</c>
    /// read the same. Only <see cref="PathSegmentKind.Property"/> (bare identifier) and
    /// <see cref="PathSegmentKind.TypeQualified"/> (<c>(Owner.Member)</c>) segments are meaningful here; each hop must
    /// name a registered <see cref="UIProperty"/> (indexers and CLR-only members are not animatable targets and fail
    /// with a clear message at <see cref="BeginOn"/>).
    /// </para>
    /// <para>
    /// <b>Resolution / freshness.</b> The path is resolved ONCE at track <see cref="BeginOn">Begin</see> — each
    /// intermediate segment is read (<see cref="UIProperty"/> value) to reach the next sub-object, and the resolved
    /// terminal sub-object is HELD for the animation's life, exactly as <see cref="Target"/> resolves once. If an
    /// intermediate property's value is later replaced (a different brush swapped into <c>Foreground</c>), the running
    /// animation stays on the object it resolved at Begin — it does NOT re-target the new value (WPF-consistent). Re-run
    /// the storyboard to re-resolve.
    /// </para>
    /// Mutually exclusive with <see cref="TargetProperty"/> — set exactly one (validated at <see cref="Seal"/>).
    /// </summary>
    public PropertyPath? TargetPath { get; set; }

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

    /// <summary>Element-independent validation (design doc §9.3): exactly one of <see cref="TargetProperty"/>/
    /// <see cref="TargetPath"/>, T vs property (or statically-resolved path-terminal) type, Source/Repeat overflow.
    /// Idempotent.</summary>
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

        SealTarget();

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

    /// <summary>
    /// Validates the targeting form (§9.3, task #26): exactly one of <see cref="AnimationTrack.TargetProperty"/> /
    /// <see cref="AnimationTrack.TargetPath"/> is set, and the terminal property's value type matches <c>T</c> when it
    /// is statically knowable. The single-property form checks the type in full here; a path checks it only when the
    /// terminal segment resolved its <see cref="UIProperty"/> at parse time (the type-qualified / compile-time-checked
    /// <see cref="PropertyPath"/> forms) — a bare-identifier terminal has no static owner type and is checked at Begin.
    /// </summary>
    private void SealTarget()
    {
        if (TargetProperty is not null && TargetPath is not null)
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}> sets both TargetProperty and TargetPath; they are mutually " +
                "exclusive — set TargetPath for a sub-object property path, or TargetProperty for a direct property " +
                "on the target.");

        if (TargetPath is not null)
        {
            SealTargetPath();
            return;
        }

        if (TargetProperty is null)
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}>.TargetProperty (or TargetPath) is required.");

        if (TargetProperty.PropertyType != typeof(T))
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}> targets property '{TargetProperty}' whose value type is " +
                $"{TargetProperty.PropertyType.Name}; the track type must match the property type.");
    }

    private void SealTargetPath()
    {
        BindingPath path;
        try
        {
            path = TargetPath!.ToBindingPath(null); // code-first default resolver; the compile-time form is pre-resolved
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}>.TargetPath '{TargetPath}' is not a valid property path: {ex.Message}",
                ex);
        }

        if (path.IsEmpty)
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}>.TargetPath is empty; a path must name at least the animated " +
                "property (its last segment).");

        // Terminal type check when the last segment resolved a UIProperty statically (the type-qualified /
        // PropertyPath(props) forms). A bare-identifier terminal resolves against the runtime target at Begin.
        var terminal = path.Segments[^1];
        if (terminal is { Kind: PathSegmentKind.TypeQualified, QualifiedProperty.UIProperty: { } terminalProperty } &&
            terminalProperty.PropertyType != typeof(T))
        {
            throw new InvalidOperationException(
                $"AnimationTrack<{typeof(T).Name}> targets path terminal property '{terminalProperty}' whose value " +
                $"type is {terminalProperty.PropertyType.Name}; the track type must match the property type.");
        }
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

        // The BASE object — resolved once, exactly as before. TargetPath and the single-property form share it
        // (they differ only in how the animated property is reached FROM the base).
        var baseObject = Target ?? (TargetName is null ? scope : scope.FindName(TargetName) as UIObject);
        if (baseObject is null)
        {
            failure = $"AnimationTrack TargetName '{TargetName}' did not resolve to a UIObject in the scope.";
            return false;
        }

        UIObject target;
        StyledProperty<T> property;

        if (TargetPath is not null)
        {
            // Resolve the path ONCE, HOLDING the terminal sub-object (freshness note on TargetPath): a later swap of an
            // intermediate value does not re-target this animation. The resolved sub-object is a standalone sub-object
            // begin — it flows through the SAME BeginStoryboardChild below that #25's retirement/leak machinery covers
            // (the brush is already watched from the attached tree via the slot it lives in; the storyboard group
            // retires it by Scope on detach regardless).
            if (!TryResolvePathTarget(baseObject, out target!, out property!, out failure))
                return false;
        }
        else
        {
            target = baseObject;
            if (TargetProperty is not StyledProperty<T> typed)
            {
                failure = $"AnimationTrack.TargetProperty '{TargetProperty}' is not a StyledProperty<{typeof(T).Name}>.";
                return false;
            }

            property = typed;
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

    /// <summary>
    /// Walks <see cref="AnimationTrack.TargetPath"/> from <paramref name="baseObject"/>: each intermediate segment is
    /// resolved to a <see cref="UIProperty"/> against the CURRENT object's runtime type (so a bare identifier or a
    /// type-qualified owner both work) and READ to reach the next sub-object; the terminal segment must resolve to a
    /// <see cref="StyledProperty{T}"/> on the object reached. On any unresolvable / non-descendable hop it reports a
    /// positioned <paramref name="failure"/> and returns <see langword="false"/> (the caller throws or diagnoses per
    /// the error-policy split). The path is non-empty and parseable (checked at <see cref="Seal"/>).
    /// </summary>
    private bool TryResolvePathTarget(UIObject baseObject, out UIObject? target, out StyledProperty<T>? property, out string? failure)
    {
        target = null;
        property = null;
        failure = null;

        var segments = TargetPath!.ToBindingPath(null).Segments; // cached parse from Seal — no re-parse, no re-throw
        var current = baseObject;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (ResolveSegmentProperty(in segment, current.GetType()) is not { } resolved)
            {
                failure = $"AnimationTrack.TargetPath could not resolve segment '{DescribeSegment(in segment)}' to a " +
                          $"UIProperty on {current.GetType().Name}.";
                return false;
            }

            if (i == segments.Length - 1)
            {
                if (resolved is not StyledProperty<T> terminal)
                {
                    failure = $"AnimationTrack.TargetPath terminal property '{resolved}' is not a " +
                              $"StyledProperty<{typeof(T).Name}>.";
                    return false;
                }

                target = current;
                property = terminal;
                return true;
            }

            object? value;
            try
            {
                value = resolved.GetValueUntyped(current);
            }
            catch (Exception ex) // a non-value-bearing kind (e.g. attached property with no untyped read) — clean fail
            {
                failure = $"AnimationTrack.TargetPath cannot read intermediate property '{resolved}' on " +
                          $"{current.GetType().Name}: {ex.Message}";
                return false;
            }

            if (value is not UIObject next)
            {
                failure = $"AnimationTrack.TargetPath cannot descend through '{resolved}' on {current.GetType().Name}: " +
                          (value is null ? "its value is null." : $"the value ({value.GetType().Name}) is not a UIObject.");
                return false;
            }

            current = next;
        }

        failure = "AnimationTrack.TargetPath is empty."; // unreachable — Seal rejects the empty path
        return false;
    }

    /// <summary>Resolves one path segment to a registered <see cref="UIProperty"/> — a bare identifier against the
    /// runtime type, a type-qualified segment against its baked (or registry-resolved) owner. Indexer / CLR-only
    /// segments have no <see cref="UIProperty"/> and yield <see langword="null"/> (not an animatable hop).</summary>
    private static UIProperty? ResolveSegmentProperty(in PathSegment segment, Type runtimeType)
        => segment.Kind switch
        {
            PathSegmentKind.Property => UIPropertyRegistry.Find(runtimeType, segment.Name!),
            PathSegmentKind.TypeQualified => segment.QualifiedProperty.UIProperty ??
                                             UIPropertyRegistry.Find(segment.QualifierType!, segment.Name!),
            _ => null
        };

    private static string DescribeSegment(in PathSegment segment) => segment.Kind switch
    {
        PathSegmentKind.Property => segment.Name!,
        PathSegmentKind.TypeQualified => $"({segment.QualifierType!.Name}.{segment.Name})",
        PathSegmentKind.IntIndexer => $"[{segment.IntIndex}]",
        PathSegmentKind.StringIndexer => $"['{segment.Name}']",
        _ => segment.Kind.ToString()
    };
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
