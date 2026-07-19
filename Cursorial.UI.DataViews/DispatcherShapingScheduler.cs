using Cursorial.UI.DataViews.Shaping;

namespace Cursorial.UI.DataViews;

/// <summary>
/// The UI-thread <see cref="IShapingScheduler"/> (final-audit fix 2026-07-18): bridges the engine's
/// owner-thread publishes onto the <see cref="UIDispatcher"/>. Without it the grid's controller
/// defaulted to the inline scheduler, and past <c>BackgroundThreshold</c> a background reshape
/// completed ON the ThreadPool thread — cross-thread snapshot/layout mutation with the resulting
/// <c>VerifyAccess</c> throw swallowed by the background catch (a silently frozen grid).
/// </summary>
internal sealed class DispatcherShapingScheduler(UIDispatcher dispatcher) : IShapingScheduler
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.Post(action);
}
