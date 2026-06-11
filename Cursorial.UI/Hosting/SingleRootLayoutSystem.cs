using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The P1 <see cref="ILayoutSystem"/> facade: one <see cref="LayoutManager"/> over one root
/// element, laid out to the full viewport. At P7, S4's <c>WindowManager</c> supplies the
/// implementation that enumerates per-window managers; the frame loop consumes the seam unchanged.
/// </summary>
/// <remarks>
/// The frame-loop give-up (design doc §10.5): the manager owns convergence internally (16-pass cap
/// + one bounded <c>LayoutUpdated</c> retry); when a pass still leaves live queued work — a
/// non-converging layout — this facade calls <see cref="LayoutManager.AbandonPendingLayout"/> so
/// <see cref="HasPendingLayout"/> cannot pin <see langword="true"/> and spin the idle phase. The
/// parked entries re-arm with the next explicit invalidation.
/// </remarks>
internal sealed class SingleRootLayoutSystem : ILayoutSystem
{
    private LayoutManager? _manager;
    private Size _viewport;
    private bool _viewportChanged;

    public SingleRootLayoutSystem(Size viewport) => _viewport = viewport;

    /// <summary>The active manager (test observability; null until a root is set).</summary>
    public LayoutManager? Manager => _manager;

    /// <inheritdoc/>
    public bool HasPendingLayout => _manager is not null && (_viewportChanged || _manager.HasQueuedWork);

    /// <inheritdoc/>
    public void RunLayoutPass()
    {
        if (_manager is null)
            return;

        _viewportChanged = false;
        _manager.RunLayoutPass(_viewport);

        if (_manager.HasQueuedWork)
            _manager.AbandonPendingLayout(); // give up until the next explicit invalidation (doc §10.5)
    }

    /// <summary>Attaches <paramref name="root"/> under a fresh manager (the previous root must already be detached).</summary>
    public void SetRoot(UIElement? root)
    {
        _manager = root is null ? null : new LayoutManager(root);
        _viewportChanged = root is not null;
    }

    /// <summary>The resize leg: the next pass measures the root under the new constraint.</summary>
    public void OnViewportResized(Size newSize)
    {
        if (newSize == _viewport)
            return;

        _viewport = newSize;
        _viewportChanged = true;
    }
}
