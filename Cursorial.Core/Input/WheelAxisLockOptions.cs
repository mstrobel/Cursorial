namespace Cursorial.Input;

/// <summary>
/// Configuration for the <see cref="WheelAxisLock"/>.
/// </summary>
public sealed record WheelAxisLockOptions
{
    /// <summary>
    /// The accumulated cross-axis magnitude (in 1/120-notch units) that breaks the current axis
    /// lock and re-rails the gesture onto the other axis. The default of <c>360</c> (three whole
    /// notches) means an isolated drift tick or two is absorbed, while a sustained push along the
    /// other axis re-rails on its third consecutive tick.
    /// </summary>
    public int BreakThreshold { get; init; } = 360;

    /// <summary>
    /// The quiet gap after which a wheel gesture is considered over. The first wheel event after
    /// the gap starts a fresh gesture and re-seeds the axis lock, so scrolling one axis, pausing,
    /// and scrolling the other is never penalized. The default is 200 ms — comfortably longer
    /// than the inter-tick gap of an active trackpad scroll, comfortably shorter than a
    /// deliberate pause.
    /// </summary>
    public TimeSpan GestureTimeout { get; init; } = TimeSpan.FromMilliseconds(200);
}
