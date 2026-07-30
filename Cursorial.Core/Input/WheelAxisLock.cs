using System.Runtime.CompilerServices;

using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// An <see cref="IInputTransformer"/> that rails wheel gestures onto their dominant axis — the
/// "scroll dead zone". Trackpads make it nearly impossible to scroll far along one axis without
/// shedding occasional ticks along the other; because terminals quantize scrolling into
/// single-axis notch events, that drift arrives as stray cross-axis <see cref="MouseEventKind.Wheel"/>
/// events interleaved into the dominant stream, and every stray tick visibly nudges whatever
/// scrolls on that axis. This transformer suppresses the strays while letting a genuine change of
/// scroll direction through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Axis locking with a break valve.</b> A gesture is a run of wheel events with no
/// <see cref="WheelAxisLockOptions.GestureTimeout"/>-sized gap; its first event locks the axis.
/// While locked, same-axis events pass through untouched and cross-axis events charge a
/// tug-of-war accumulator: each suppressed cross-axis tick adds its magnitude, and each same-axis
/// tick discharges twice its own. When the charge reaches
/// <see cref="WheelAxisLockOptions.BreakThreshold"/>, the user is genuinely pushing the other way
/// — the lock flips to that axis and the breaking event is delivered. The discharge is what
/// separates intent from noise: drift ticks interleaved with dominant-axis ticks can never
/// accumulate, while a cross-axis run sustained between them — three consecutive notches at the
/// defaults, a fraction of a second even against a decaying momentum tail — breaks through.
/// </para>
/// <para>
/// <b>What the filter costs.</b> A deliberate mid-gesture direction change loses its first two
/// ticks to the dead zone (at trackpad event rates, an imperceptible hesitation), and a gesture
/// whose <i>first</i> tick is drift locks onto the wrong axis until the tug-of-war corrects it —
/// accepted so delivery stays zero-latency and timer-free. Two-axis diagonal scrolling rails onto
/// the dominant axis by design; that is the trade the option advertises, and turning it off
/// restores raw deltas.
/// </para>
/// <para>
/// <b>Raw axes, not scroll semantics.</b> The lock is computed from the wire-level wheel axis and
/// ignores modifiers, so downstream conventions like Shift+wheel scrolling horizontally are
/// unaffected: a Shift+vertical gesture rails on raw Y (its drift is stray raw-X ticks) and maps
/// to the horizontal scroll axis afterwards, exactly as without the filter.
/// </para>
/// <para>
/// <b>Live toggle.</b> <see cref="Enabled"/> may be flipped from any thread while the stream is
/// running (the options UI toggles it without rebuilding the single-shot device chain). Disabled,
/// the transformer is a pure pass-through, and any wheel event passing through clears the axis
/// lock — so a gesture that scrolled across the disabled window starts fresh on re-enable. (A
/// disable/enable round trip with no wheel event in between keeps the prior gesture state, which
/// at human toggling speed the gesture timeout has long since made irrelevant.)
/// </para>
/// <para>
/// <b>Purely synchronous.</b> Like <see cref="MouseClickSynthesizer"/>, no event is fabricated or
/// delayed on a timer — every decision happens inline as input flows through, using each event's
/// <see cref="InputEvent.Timestamp"/>, which makes it deterministic to test. Gesture state is
/// local to a single <see cref="TransformAsync"/> call, so one instance can be reused across
/// streams; only <see cref="Enabled"/> is shared.
/// </para>
/// </remarks>
public sealed class WheelAxisLock : IInputTransformer
{
    /// <summary>
    /// Same-axis ticks discharge the cross-axis accumulator at twice the rate cross-axis ticks
    /// charge it — at 1×, a two-tick drift burst repeated between primary ticks could
    /// out-accumulate the stream it contaminates. For whole-notch terminal input at the default
    /// threshold this is equivalent to a hard reset (one primary notch clears any sub-break
    /// charge); the proportional form matters for sub-notch deltas from synthesized or
    /// high-resolution sources, where a grazing primary component shouldn't erase a deliberate
    /// cross-axis push.
    /// </summary>
    private const int ChargeDecayFactor = 2;

    private readonly WheelAxisLockOptions _options;
    private volatile bool _enabled = true;

    /// <summary>Construct an axis lock with the supplied options (or the defaults when null).</summary>
    public WheelAxisLock(WheelAxisLockOptions? options = null)
    {
        _options = options ?? new WheelAxisLockOptions();

        if (_options.BreakThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.BreakThreshold, "BreakThreshold must be positive.");
        }

        if (_options.GestureTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.GestureTimeout, "GestureTimeout must be positive.");
        }
    }

    /// <summary>
    /// Whether wheel events are filtered. When <see langword="false"/> the transformer passes
    /// every event through untouched. Safe to flip from any thread while the stream is running —
    /// this is the user-option toggle (<c>input.scrollDeadZone</c>), which must take effect
    /// without rebuilding the single-shot device chain.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<InputEvent> TransformAsync(
        IAsyncEnumerable<InputEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Gesture state, local to this enumeration so the transformer stays reusable and free of
        // cross-run interference. Axis: 0 = unlocked, 1 = vertical, -1 = horizontal.
        int lockAxis = 0;
        int charge = 0;
        var lastWheelTime = default(DateTimeOffset);

        await foreach (var evt in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (evt is not MouseEvent { Kind: MouseEventKind.Wheel } wheel)
            {
                yield return evt;
                continue;
            }

            if (!_enabled)
            {
                lockAxis = 0; // re-enabling starts a fresh gesture
                yield return evt;
                continue;
            }

            // A quiet gap ends the gesture: the next event re-seeds the lock, so scrolling one
            // axis, pausing, and scrolling the other is never penalized. Suppressed events also
            // refreshed this timestamp — a drift trickle must keep its gesture's lock alive, not
            // age it out and then seed a fresh gesture with more drift.
            if (lockAxis == 0 || wheel.Timestamp - lastWheelTime > _options.GestureTimeout)
            {
                lockAxis = Math.Abs(wheel.WheelDeltaY) >= Math.Abs(wheel.WheelDeltaX) ? 1 : -1;
                charge = 0;
            }

            lastWheelTime = wheel.Timestamp;

            int primary = lockAxis > 0 ? wheel.WheelDeltaY : wheel.WheelDeltaX;
            int secondary = lockAxis > 0 ? wheel.WheelDeltaX : wheel.WheelDeltaY;

            if (primary != 0)
            {
                // A locked-axis tick re-affirms the lock and pushes the challenger back. Terminal
                // wheel events are single-axis, so the mixed-event legs only run for synthesized
                // upstream sources.
                charge = Math.Max(0, charge + Math.Abs(secondary) - ChargeDecayFactor * Math.Abs(primary));

                // The break valve applies here too: a cross-axis push that out-accumulates a
                // GRAZING locked-axis component (|secondary| > 2·|primary|, sustained) is the same
                // real direction change as a pure cross run, and without this check it could
                // never re-rail — the charge would grow without bound while the dominant motion
                // stayed suppressed. Unreachable for single-axis input: there secondary == 0, so
                // the charge only ever discharges on this leg.
                if (charge >= _options.BreakThreshold)
                {
                    lockAxis = -lockAxis;
                    charge = 0;
                }

                yield return Rail(wheel, lockAxis);
                continue;
            }

            charge += Math.Abs(secondary);

            if (charge >= _options.BreakThreshold)
            {
                // A sustained cross-axis push is a real direction change: re-rail onto it. Only
                // the breaking event is delivered — replaying the absorbed ticks would turn the
                // dead zone's protection into a deferred jump.
                lockAxis = -lockAxis;
                charge = 0;
                yield return wheel;
            }

            // else: drift inside the dead zone — absorbed.
        }
    }

    /// <summary>The event with any component off the locked axis stripped (the original instance
    /// when nothing needs stripping — the whole-notch terminal case).</summary>
    private static MouseEvent Rail(MouseEvent wheel, int lockAxis)
        => lockAxis > 0
               ? wheel.WheelDeltaX == 0 ? wheel : wheel with { WheelDeltaX = 0 }
               : wheel.WheelDeltaY == 0 ? wheel : wheel with { WheelDeltaY = 0 };
}
