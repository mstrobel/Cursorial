// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The frame-frozen animation clock (design doc §9.1): <see cref="Now"/> is set once per frame at
/// <c>BeginFrame</c> (Phase 0, the frame's first statement) and never moves between frames, so
/// every animation and timer sampled in a frame sees one timestamp (invariant 1).
/// <c>TimeProvider</c>-derived through <see cref="FrameTime.Elapsed"/> — wall-clock monotonic in
/// production, <c>FakeTimeProvider</c>-deterministic in tests.
/// </summary>
public sealed class FrameClock
{
    internal FrameClock()
    {
    }

    /// <summary>Elapsed time since application start, frozen at the last <c>BeginFrame</c>.</summary>
    public TimeSpan Now { get; internal set; }
}
