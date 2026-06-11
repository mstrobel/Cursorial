using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.UI;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The frame loop's cross-subsystem seams (design doc §10.9). At P1 only ILayoutSystem and
// IRenderSystem have implementations; the rest are declared now so the loop's phase slots are
// named and the later phases (S3 at P2, Fork B at P3, S5 at P8, S4 at P7) plug in without
// touching the loop. All calls happen on the UI thread.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The result of routing one input event through S3's dispatcher (design doc §7.4).
/// </summary>
public enum InputDispatchResult
{
    /// <summary>The event was not UI input at all (no routing occurred). Default gestures still apply.</summary>
    NotUIInput,

    /// <summary>The event routed but no handler claimed it. Default gestures still apply.</summary>
    DispatchedUnhandled,

    /// <summary>A handler claimed the event — default gestures (e.g. Ctrl+C exit) are suppressed.</summary>
    DispatchedHandled,
}

/// <summary>
/// S3's input-routing seam (design doc §10.9; lands at P2). The frame loop calls
/// <see cref="Dispatch"/> in Phase 1 — one event at a time, in arrival order, never re-entrantly —
/// and <see cref="UpdateHover"/> once per rendered frame in Phase 6. Until P2 the loop treats every
/// event as <see cref="InputDispatchResult.NotUIInput"/> and applies default gestures only.
/// </summary>
public interface IInputDispatchTarget
{
    /// <summary>
    /// Routes one event. <b>Coordinate caveat:</b> resize coalescing means positions may lie
    /// outside the current viewport — implementations must clamp, never throw.
    /// <see cref="ResizeEvent"/> and <see cref="DeviceResponseEvent"/> never arrive here.
    /// </summary>
    InputDispatchResult Dispatch(InputEvent inputEvent);

    /// <summary>Phase 6, once per rendered frame, after layout and composite parameters are final.</summary>
    void UpdateHover();

    /// <summary>The capability fan-out leg (startup and renegotiation).</summary>
    void OnCapabilitiesChanged(TerminalCapabilities capabilities);
}

/// <summary>
/// Fork B's frame hooks (design doc §10.9; lands at P3). Phase 3 flushes queued pseudo/class flips
/// to fixpoint before animation/layout/render (invariant 1); a second flush after the animation
/// tick catches animation-driven flips. <see cref="HasPendingActivations"/> participates in the
/// Phase-7 idle guard so flips queued during layout/render trigger another frame instead of
/// sitting until the next input event.
/// </summary>
public interface IStyleFrameHooks
{
    /// <summary>Drains the queued pseudo-flip fixpoint. Must be cheap when empty.</summary>
    void FlushPendingActivations();

    /// <summary>O(1) — whether the flip queue is non-empty (Phase-7 guard + <c>RunUntilIdle</c>).</summary>
    bool HasPendingActivations { get; }

    /// <summary>
    /// Records the capability snapshot. Stamping of <c>caps-*</c> classes happens at visual-root
    /// attachment (and immediately for already-attached roots on renegotiation).
    /// </summary>
    void OnCapabilitiesChanged(TerminalCapabilities capabilities);
}

/// <summary>
/// S5's frame driver (design doc §10.9; lands at P8 — <c>AnimationScheduler</c> implements).
/// <see cref="BeginFrame"/> freezes the animation clock as the frame's first statement so a
/// <c>BeginAnimation</c> during the input drain stamps <c>StartTime = T_N</c>; <see cref="Tick"/>
/// samples every active storyboard once at the frozen clock; <see cref="TickNewlyStarted"/> samples
/// storyboards ignited this frame (including by the post-tick styling flush) at elapsed-zero so the
/// frame renders <c>From</c>, not the pre-animation value.
/// </summary>
public interface IAnimationFrameDriver
{
    /// <summary>Phase 0, first statement — the single time source, frozen for the frame.</summary>
    void BeginFrame(in FrameTime time);

    /// <summary>Phase 4: sample every active storyboard once; apply via <c>AnimatedValueHandle</c>.</summary>
    void Tick();

    /// <summary>Post-flush ignitions sample at elapsed-zero. Must be a cheap no-op when none started.</summary>
    void TickNewlyStarted();

    /// <summary>Drives paced-vs-event-driven idle (Phase 7). Includes pending UITimers; perpetual repeats count.</summary>
    bool HasActiveAnimations { get; }

    /// <summary>Teardown step: release handles; values revert to base.</summary>
    void Shutdown();
}

/// <summary>
/// The S1 layout facade the frame loop consumes (design doc §10.9). Phase 5 makes <b>one</b>
/// <see cref="RunLayoutPass"/> call per frame; the convergence cap and
/// <see cref="LayoutManager.AbandonPendingLayout"/> give-up are owned behind this seam.
/// </summary>
public interface ILayoutSystem
{
    /// <summary>Consulted only by the Phase-7 idle guard (and Phase 5's should-run check).</summary>
    bool HasPendingLayout { get; }

    /// <summary>One per-frame pass: each root's <see cref="LayoutManager"/> runs its internal fixpoint.</summary>
    void RunLayoutPass();
}

/// <summary>
/// The render-system seam (design doc §10.9) — owns the <c>SceneCompositor</c> + <c>ScenePool</c>
/// and everything between "dirty visuals" and "cells in the target buffer". At P1
/// <see cref="SingleRootRenderSystem"/> implements it over one root tree; at P7, S4's
/// <c>WindowManager</c> replaces that implementation (one object implementing this plus
/// <c>IWindowSystem</c>) with zero frame-loop change.
/// </summary>
public interface IRenderSystem
{
    /// <summary>Whether any raster/composite work is pending — the Phase-6 gate and Phase-7 guard.</summary>
    bool HasDirtyVisuals { get; }

    /// <summary>
    /// Re-rasters invalidated scenes (z order), refreshes composite parameters, composites the
    /// layer stack onto the <b>retained</b> <paramref name="target"/> (never cleared), and writes
    /// the caret cursor state. Returns <see langword="true"/> when the target changed. App draw
    /// delegates run through the <see cref="IUserCodeGuard"/> supplied at composition: a handled
    /// draw exception is reported as changed (conservative emit — the compositing invariant keeps
    /// the buffer safe); on <see cref="IUserCodeGuard.IsFatal"/> the implementation returns
    /// immediately.
    /// </summary>
    bool RenderFrame(CellBuffer target, in FrameTime time);

    /// <summary>
    /// The resize transaction leg: construct a fresh compositor and invalidate all surfaces so the
    /// same frame's Phase 5/6 produce a full relayout + redraw.
    /// </summary>
    void OnViewportResized(Size newSize);
}

/// <summary>
/// The user-code exception funnel S6 hands to S1/S4 at composition (design doc §10.8): S6 cannot
/// guard code it never calls, so draw delegates run through <see cref="Run{TState}"/>, which
/// applies the <see cref="UIApplication.DispatcherUnhandledException"/> policy.
/// </summary>
public interface IUserCodeGuard
{
    /// <summary>
    /// Runs app code under the funnel policy. Returns <see langword="true"/> when the code
    /// completed or its exception was handled; <see langword="false"/> when an unhandled exception
    /// was recorded as fatal — the caller must unwind promptly.
    /// </summary>
    bool Run<TState>(TState state, Action<TState> userCode);

    /// <summary>Whether a fatal (unhandled) exception has been recorded.</summary>
    bool IsFatal { get; }
}
