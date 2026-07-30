using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Tests.Input;

public class WheelAxisLockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MouseEvent Wheel(int dx, int dy, int atMs, KeyModifiers modifiers = KeyModifiers.None)
        => new()
           {
               Timestamp = T0.AddMilliseconds(atMs),
               Kind = MouseEventKind.Wheel,
               Position = new CellPosition(10, 5),
               Button = MouseButton.None,
               ButtonsHeld = MouseButtons.None,
               Modifiers = modifiers,
               WheelDeltaX = dx,
               WheelDeltaY = dy,
           };

    /// <summary>One vertical notch (away from the user).</summary>
    private static MouseEvent V(int atMs) => Wheel(0, 120, atMs);

    /// <summary>One horizontal notch (to the right).</summary>
    private static MouseEvent H(int atMs) => Wheel(120, 0, atMs);

    private static MouseEvent Move(int atMs)
        => Wheel(0, 0, atMs) with { Kind = MouseEventKind.Move };

    private static async IAsyncEnumerable<InputEvent> ToAsync(InputEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var e in events) yield return e;
    }

    private static async Task<List<MouseEvent>> Run(WheelAxisLock axisLock, params InputEvent[] events)
    {
        var result = new List<MouseEvent>();
        await foreach (var e in axisLock.TransformAsync(ToAsync(events)))
            result.Add((MouseEvent)e);
        return result;
    }

    private static Task<List<MouseEvent>> Run(params InputEvent[] events) => Run(new WheelAxisLock(), events);

    /// <summary>The (dx, dy) deltas of the surviving wheel events, for one-line stream assertions.</summary>
    private static (int X, int Y)[] Deltas(IEnumerable<MouseEvent> events)
        => events.Where(e => e.Kind == MouseEventKind.Wheel)
                 .Select(e => (e.WheelDeltaX, e.WheelDeltaY))
                 .ToArray();

    // ---- The dead zone ----

    [Fact]
    public async Task IsolatedDriftTick_IsSuppressed()
    {
        var outp = await Run(V(0), V(20), H(40), V(60), V(80));

        Assert.Equal([(0, 120), (0, 120), (0, 120), (0, 120)], Deltas(outp));
    }

    [Fact]
    public async Task RepeatedInterleavedDrift_NeverAccumulates()
    {
        // Alternating V H V H … — a diagonal-ish gesture rails fully onto its leading axis:
        // every primary tick discharges the challenger before it can reach the break threshold.
        var outp = await Run(V(0), H(20), V(40), H(60), V(80), H(100), V(120), H(140), V(160));

        Assert.All(Deltas(outp), d => Assert.Equal((0, 120), d));
        Assert.Equal(5, outp.Count);
    }

    [Fact]
    public async Task DriftBurstOfTwo_IsSuppressed()
    {
        var outp = await Run(V(0), V(20), H(40), H(60), V(80), H(100), H(120), V(140));

        Assert.All(Deltas(outp), d => Assert.Equal((0, 120), d));
        Assert.Equal(4, outp.Count);
    }

    // ---- The break valve ----

    [Fact]
    public async Task SustainedCrossAxisRun_BreaksThroughOnThirdTick()
    {
        var outp = await Run(V(0), V(20), H(40), H(60), H(80), H(100));

        // The first two cross ticks are absorbed; the third reaches the 360 threshold and
        // re-rails the gesture, so the fourth flows as the new primary.
        Assert.Equal([(0, 120), (0, 120), (120, 0), (120, 0)], Deltas(outp));
    }

    [Fact]
    public async Task AfterBreak_OldAxisIsTheOneSuppressed()
    {
        var outp = await Run(V(0), H(20), H(40), H(60), H(80), V(100), H(120));

        // Once re-railed onto X, a straggling V tick (momentum remnant) is the drift.
        Assert.Equal([(0, 120), (120, 0), (120, 0), (120, 0)], Deltas(outp));
    }

    [Fact]
    public async Task DriftLeadingAGesture_LocksWrong_ThenSelfCorrects()
    {
        // Accepted trade for zero-latency delivery: a gesture whose FIRST tick is drift locks
        // onto the wrong axis, and the real stream must break back through the valve.
        var outp = await Run(H(0), V(20), V(40), V(60), V(80));

        Assert.Equal([(120, 0), (0, 120), (0, 120)], Deltas(outp));
    }

    [Fact]
    public async Task MomentumTick_BetweenCrossTicks_DelaysButDoesNotStarveTheBreak()
    {
        // A decaying vertical momentum tail drips a V between deliberate H ticks: the charge
        // restarts, and the H run still breaks through on its third consecutive tick.
        var outp = await Run(V(0), V(20), V(40), H(60), H(80), V(100), H(120), H(140), H(160), H(180));

        Assert.Equal(
            [(0, 120), (0, 120), (0, 120), (0, 120), (120, 0), (120, 0)],
            Deltas(outp));
    }

    // ---- Gesture lifetime ----

    [Fact]
    public async Task QuietGap_EndsTheGesture_NextAxisWinsImmediately()
    {
        // Scroll vertically, pause past the timeout, scroll horizontally: no penalty at all.
        var outp = await Run(V(0), V(20), H(300));

        Assert.Equal([(0, 120), (0, 120), (120, 0)], Deltas(outp));
    }

    [Fact]
    public async Task SlowScroll_WithinTimeout_KeepsTheLock()
    {
        var outp = await Run(V(0), V(150), H(300), V(450));

        Assert.Equal([(0, 120), (0, 120), (0, 120)], Deltas(outp));
    }

    [Fact]
    public async Task SuppressedDrift_KeepsTheGestureAlive()
    {
        // Each event lands within the timeout of the PREVIOUS one, but only because suppressed
        // ticks refresh gesture liveness too — otherwise the second H would start a "fresh"
        // gesture mid-drift and be delivered.
        var outp = await Run(V(0), H(150), H(300), V(450));

        Assert.Equal([(0, 120), (0, 120)], Deltas(outp));
    }

    // ---- Contract edges ----

    [Fact]
    public async Task RawAxisIsWhatRails_ModifiersAreIgnored()
    {
        // Shift+vertical is the horizontal-scroll convention DOWNSTREAM; at the wire level it is
        // still a Y gesture, and its drift is still the stray raw-X tick.
        var outp = await Run(
            Wheel(0, 120, 0, KeyModifiers.Shift),
            Wheel(0, 120, 20, KeyModifiers.Shift),
            Wheel(120, 0, 40, KeyModifiers.Shift),
            Wheel(0, 120, 60, KeyModifiers.Shift));

        Assert.Equal([(0, 120), (0, 120), (0, 120)], Deltas(outp));
        Assert.All(outp, e => Assert.Equal(KeyModifiers.Shift, e.Modifiers));
    }

    [Fact]
    public async Task MixedAxesEvent_SecondaryComponentIsZeroed()
    {
        // Terminals never produce these, but a synthesized upstream may: the dominant component
        // seeds the lock and the cross component is stripped, not the whole event dropped.
        var outp = await Run(Wheel(40, 120, 0), Wheel(40, 120, 20));

        Assert.Equal([(0, 120), (0, 120)], Deltas(outp));
    }

    [Fact]
    public async Task CrossDominantMixedEvents_BreakTheLock_InsteadOfBeingStrippedForever()
    {
        // A deliberate horizontal push arriving as mixed events with a grazing vertical
        // component (dy = 6): each nets +108 charge (+120 − 2·6), so the fourth crosses the 360
        // threshold and re-rails — the break valve must live on the mixed leg too, or the
        // dominant motion would be suppressed indefinitely while the charge grew without bound.
        var outp = await Run(
            V(0), Wheel(120, 6, 20), Wheel(120, 6, 40), Wheel(120, 6, 60), Wheel(120, 6, 80),
            Wheel(120, 6, 100));

        Assert.Equal(
            [(0, 120), (0, 6), (0, 6), (0, 6), (120, 0), (120, 0)],
            Deltas(outp));
    }

    [Fact]
    public async Task SubNotchPrimaryTick_DischargesProportionally_NotAsAHardReset()
    {
        // Pins WHY the discharge is proportional (see ChargeDecayFactor): a grazing dy = 6 tick
        // discharges only 12 (240 → 228), so the cross run still breaks on its next-but-one tick
        // (348, then 468 ≥ 360). Under a hard reset the same stream would never break.
        var outp = await Run(V(0), H(20), H(40), Wheel(0, 6, 60), H(80), H(100));

        Assert.Equal([(0, 120), (0, 6), (120, 0)], Deltas(outp));
    }

    [Fact]
    public async Task NonWheelEvents_PassThrough_WithoutDisturbingTheGesture()
    {
        var outp = await Run(V(0), Move(20), H(40), Move(60), V(80));

        Assert.Equal(
            [MouseEventKind.Wheel, MouseEventKind.Move, MouseEventKind.Move, MouseEventKind.Wheel],
            outp.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task Disabled_IsAPurePassThrough()
    {
        var outp = await Run(new WheelAxisLock { Enabled = false }, V(0), V(20), H(40), V(60));

        Assert.Equal([(0, 120), (0, 120), (120, 0), (0, 120)], Deltas(outp));
    }

    [Fact]
    public async Task ReEnabling_StartsAFreshGesture()
    {
        var axisLock = new WheelAxisLock();
        var outp = new List<MouseEvent>();

        async IAsyncEnumerable<InputEvent> Source()
        {
            await Task.CompletedTask;
            yield return V(0);
            yield return V(20);

            axisLock.Enabled = false;
            yield return H(40); // passes through and clears the V lock

            axisLock.Enabled = true;
            yield return H(60); // fresh gesture: locks H, delivered immediately
            yield return V(80); // …and V is now the drift
        }

        await foreach (var e in axisLock.TransformAsync(Source()))
            outp.Add((MouseEvent)e);

        Assert.Equal([(0, 120), (0, 120), (120, 0), (120, 0)], Deltas(outp));
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WheelAxisLock(new WheelAxisLockOptions { BreakThreshold = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WheelAxisLock(new WheelAxisLockOptions { GestureTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void Capabilities_PassThroughUnchanged()
    {
        var caps = InputCapabilities.None;

        Assert.Same(caps, ((IInputTransformer)new WheelAxisLock()).TransformCapabilities(caps));
    }
}
