using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Cursorial.Drawing;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// S4's window manager (design doc §8.5): it turns one terminal screen into a stack of
/// <see cref="TopLevelSurface"/>s and is itself the window system — there are no OS HWNDs. It owns the
/// <see cref="SceneCompositor"/> + <see cref="ScenePool"/> (S6 owns only the screen <c>CellBuffer</c> +
/// <c>FrameRenderer</c> and hands the target per frame) and implements the frame-loop seams
/// (<see cref="ILayoutSystem"/>, <see cref="IRenderSystem"/>, <see cref="IWindowSystem"/>), replacing the
/// P1 single-root stand-ins with no frame-loop change. Each frame: lay out every surface's root, raster
/// each surface's dirty zones, concatenate their <see cref="TopLevelSurface.CollectLayers"/> output — at
/// each surface's screen offset — into <b>one</b> <see cref="SceneCompositor.Composite"/> call, then write
/// the caret.
/// </summary>
/// <remarks>
/// <b>P7-W0 scope:</b> the surface stack holds only the chrome-less application-root surface
/// (<c>HostWindow == null</c>) set through <see cref="SetRootSurface"/> — the existing
/// <c>RootElement</c>/<c>ShowRoot</c> path, behaviourally identical to the old single-root systems, and
/// the future inline / non-alt-screen primitive. Shown <c>Window</c>s (W1), modality + z-order (W2),
/// <c>IWindowTopology</c> gating + light dismiss (W3), <c>Popup</c>s (W4), and drag/resize/shutdown
/// (W5) layer on top. The S3 topology stays <c>SingleRootWindowTopology</c> until W3.
/// </remarks>
public sealed class WindowManager : ILayoutSystem, IRenderSystem, IWindowSystem
{
    private readonly ScenePool _scenePool = new();
    private readonly List<TopLevelSurface> _surfaces = [];   // z-order, bottom→top; [0] is the root surface when present
    private readonly List<SceneLayer> _layers = [];          // per-frame scratch (the one concatenated layer span)
    private readonly TerminalCaretService _caretService;
    private readonly IUserCodeGuard? _guard;
    private SceneCompositor _compositor = new();
    private OutputCapabilities _capabilities;
    private Size _viewport;
    private TopLevelSurface? _rootSurface;
    private TerminalCaretState _lastCaret;
    private bool _caretEverApplied;

    /// <summary>
    /// Creates the window manager. <paramref name="guard"/> is the user-code funnel routed to every
    /// element <c>Render</c> override (design doc §10.8); null means draw exceptions propagate raw.
    /// </summary>
    public WindowManager(OutputCapabilities capabilities, TerminalCaretService caretService, IUserCodeGuard? guard = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(caretService);

        _capabilities = capabilities;
        _caretService = caretService;
        _guard = guard;
    }

    /// <summary>The z-ordered surface stack (bottom→top); the root surface, when present, is the bottom.</summary>
    public IReadOnlyList<TopLevelSurface> Surfaces => _surfaces;

    /// <summary>The chrome-less application-root surface, or <see langword="null"/> until a root is set.</summary>
    public TopLevelSurface? RootSurface => _rootSurface;

    /// <summary>The root surface's render tree — the W0-compat single-tree accessor (null until a root is set).</summary>
    internal RenderTree? Tree => _rootSurface?.RenderTree;

    // ── ILayoutSystem ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool HasPendingLayout
    {
        get
        {
            foreach (var surface in _surfaces)
                if (surface.HasPendingLayout)
                    return true;
            return false;
        }
    }

    /// <inheritdoc/>
    public void RunLayoutPass()
    {
        // Snapshot count: a surface's layout must not see the list mutate mid-pass (topology mutations
        // are deferred to DrainDeferredTopology — §8.8). W0 has at most the root surface.
        for (var i = 0; i < _surfaces.Count; i++)
            _surfaces[i].RunLayoutPass();
    }

    // ── IWindowSystem ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void DrainDeferredTopology()
    {
        // W0: no deferred topology queue yet — Show/Close/popup mutations land at W1/W4 and the queue at W5.
    }

    /// <inheritdoc/>
    public void OnLayoutCompleted()
    {
        // W0: SizeToContent resolution (W1) and popup anchor reposition (W4) land with those surfaces.
    }

    // ── IRenderSystem ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool HasDirtyVisuals
    {
        get
        {
            foreach (var surface in _surfaces)
                if (surface.HasDirtyVisuals)
                    return true;
            return false;
        }
    }

    /// <inheritdoc/>
    public bool RenderFrame(CellBuffer target, in FrameTime time)
    {
        ArgumentNullException.ThrowIfNull(target);

        var changed = false;

        // ① raster each surface's dirty zones (z order doesn't matter for rastering — it's per-surface).
        for (var i = 0; i < _surfaces.Count; i++)
        {
            _surfaces[i].RunRenderPass();
            if (_guard is { IsFatal: true })
                return true; // unwind promptly; conservative changed — teardown is imminent
        }

        // ② concatenate every surface's layers, bottom→top, each at its screen offset, into ONE composite.
        _layers.Clear();
        for (var i = 0; i < _surfaces.Count; i++)
            _surfaces[i].CollectLayers(_layers);

        if (_surfaces.Count > 0)
            changed = _compositor.Composite(CollectionsMarshal.AsSpan(_layers), new CellBufferView(target));

        // ③ caret write (T4 contract). W0: the root surface sits at the origin, so the published caret
        // state passes through unchanged; the focused-surface offset fold lands with Window/Popup (W1+).
        var caret = _caretService.GetCaretState();
        if (!_caretEverApplied || caret != _lastCaret)
        {
            _caretEverApplied = true;
            _lastCaret = caret;
            changed = true;
        }

        target.CursorVisible = caret.Visible;
        if (caret.Visible)
        {
            target.CursorColumn = caret.Column;
            target.CursorRow = caret.Row;
            target.CursorShape = caret.Shape;
        }

        return changed;
    }

    /// <inheritdoc/>
    public void OnViewportResized(Size newSize)
    {
        // The resize transaction (design doc §10.6 / punch 4): fresh compositor (its retained per-layer
        // state is sized to the old target) + the root surface re-fits the screen + full re-raster; the
        // same frame's Phase 5 re-lays-out under the new constraint. Windows re-clamp/re-size at W5.
        _viewport = newSize;
        _compositor = new SceneCompositor();

        if (_rootSurface is not null)
            _rootSurface.Size = newSize;

        for (var i = 0; i < _surfaces.Count; i++)
            _surfaces[i].InvalidateAll();
    }

    // ── Root surface (the RootElement/ShowRoot path; HostWindow == null) ─────────────────────────────

    /// <summary>
    /// Sets (or, with <see langword="null"/>, clears) the chrome-less application-root surface — the
    /// W0 replacement for the single-root <c>SetRoot</c>. The root must already be attached via its tree;
    /// the surface fills the screen. The layer set changes wholesale, so the compositor is reset.
    /// </summary>
    internal void SetRootSurface(UIElement? root)
    {
        if (_rootSurface is not null)
        {
            _surfaces.Remove(_rootSurface);
            _rootSurface.Detach(); // RenderTree.Detach — scenes back to the pool
            _rootSurface = null;
        }

        if (root is not null)
        {
            _rootSurface = new TopLevelSurface(root, _scenePool, _capabilities, _guard) { Size = _viewport };
            _surfaces.Insert(0, _rootSurface); // bottom of the stack
        }

        _compositor = new SceneCompositor();
    }

    // ── Renegotiation leg (UIApplication calls; mirrors the single-root render system) ───────────────

    /// <summary>
    /// Re-stamps capabilities on every surface's tree, resets the compositor, and marks everything for
    /// re-raster so the first post-renegotiate frame repaints fully (design doc §10.6).
    /// </summary>
    internal void OnCapabilitiesChanged(OutputCapabilities capabilities)
    {
        _capabilities = capabilities;

        for (var i = 0; i < _surfaces.Count; i++)
        {
            _surfaces[i].RenderTree.Capabilities = capabilities;
            _surfaces[i].InvalidateAll();
        }

        _compositor = new SceneCompositor();
    }
}
