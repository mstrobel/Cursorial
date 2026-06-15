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
    private readonly List<Window> _windows = [];            // shown windows, bottom→top z-order (above the root)
    private readonly List<Window> _modalStack = [];         // active modals, bottom→top; the topmost is the gate
    private readonly HashSet<Window> _blocked = [];         // windows currently disabled by a modal (the `obscured` set)
    private SceneCompositor _compositor = new();
    private OutputCapabilities _capabilities;
    private Size _viewport;
    private TopLevelSurface? _rootSurface;
    private Window? _activeWindow;
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

    /// <summary>The shown windows, bottom→top in z-order (above the root surface).</summary>
    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>The active (focused) top-level window, or <see langword="null"/> when none (the root has focus).</summary>
    public Window? ActiveWindow => _activeWindow;

    /// <summary>Raised after <see cref="ActiveWindow"/> changes.</summary>
    public event EventHandler? ActiveWindowChanged;

    /// <summary>The topmost modal window (the modal gate), or <see langword="null"/> when no modal is active.</summary>
    public Window? TopmostModal => _modalStack.Count > 0 ? _modalStack[^1] : null;

    /// <summary>Whether <paramref name="window"/> is in the enabled set (not blocked by a modal). The W3 input gate.</summary>
    public bool IsInputEnabled(Window window) => !_blocked.Contains(window);

    /// <summary>Invoked when surfaces/z/modality change so S3 re-evaluates hover against the new stack (wired by the host).</summary>
    internal Action? SurfacesChanged { get; set; }

    /// <summary>Invoked when a window becomes blocked so S3 can release pointer capture held inside it (wired at W3).</summary>
    internal Action<Window>? WindowBlocked { get; set; }

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
        // Keep each window's surface anchored to its Left/Top so a programmatic move re-composites
        // (AffectsComposite on Window.Left/Top). Full SizeToContent re-fit on content change is W5.
        for (var i = 0; i < _windows.Count; i++)
        {
            var window = _windows[i];
            if (window.HostSurface is { } surface)
            {
                surface.Left = window.Left;
                surface.Top = window.Top;
            }
        }
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
            _rootSurface = new TopLevelSurface(root, _scenePool, _capabilities, _guard) { Size = _viewport };

        RebuildSurfaceStack();
        _compositor = new SceneCompositor();
    }

    // ── Window hosting + modality (P7-W1 show/close; P7-W2 modal stack + blocked set + handoff) ───────

    /// <summary>Shows <paramref name="window"/> modelessly on its own surface above the root, then activates it
    /// (or, if a modal blocks it, leaves it below the gate and activates the gate).</summary>
    internal void ShowWindow(Window window)
    {
        AddWindowSurface(window);
        ComputeBlockedSet();
        FinishShow(_blocked.Contains(window) ? TopmostModal : window);
    }

    /// <summary>Shows <paramref name="window"/> modally: pushes the modal stack, blocks the rest, and activates it.</summary>
    internal void ShowDialog(Window window)
    {
        AddWindowSurface(window);
        _modalStack.Add(window);
        ComputeBlockedSet(); // blocks every window except the modal + its transitively owned subtree
        FinishShow(window);
    }

    /// <summary>Brings <paramref name="window"/> to the top of the window band and activates it. Returns false when
    /// it is not shown here or is modal-blocked (a blocked activate silently redirects to the gate — no pulse).</summary>
    internal bool ActivateWindow(Window window)
    {
        if (!_windows.Contains(window))
            return false;

        if (_blocked.Contains(window))
        {
            if (TopmostModal is { } gate && !ReferenceEquals(gate, window))
                ActivateWindow(gate); // programmatic redirect, no attention pulse (§8.6)
            return false;
        }

        MoveToTop(window);
        RebuildSurfaceStack();
        _compositor = new SceneCompositor();
        SetActive(window);
        SurfacesChanged?.Invoke();
        return true;
    }

    /// <summary>Removes <paramref name="window"/>'s surface, pops it from the modal stack, recomputes the blocked
    /// set, and — only if it was active — hands activation off (owner → gate → topmost enabled → null, §8.6).</summary>
    internal void CloseWindow(Window window)
    {
        if (!_windows.Remove(window))
            return;

        var wasActive = ReferenceEquals(_activeWindow, window);
        _modalStack.Remove(window);
        _blocked.Remove(window);
        window.HostSurface?.Detach();
        window.HostSurface = null;

        RebuildSurfaceStack();
        _compositor = new SceneCompositor();
        ComputeBlockedSet(); // a closed modal unblocks the windows it was gating

        if (wasActive)
            SetActive(ResolveHandoff(window));

        SurfacesChanged?.Invoke();
    }

    private void AddWindowSurface(Window window)
    {
        var surface = new TopLevelSurface(window, _scenePool, _capabilities, _guard) { HostWindow = window };
        window.HostSurface = surface;
        _windows.Add(window);
        SizeAndPositionWindow(window, surface);
    }

    private void FinishShow(Window? active)
    {
        if (active is not null)
            MoveToTop(active); // the active window is the top of its band (W2 owner-DFS banding refines this)

        RebuildSurfaceStack();
        _compositor = new SceneCompositor(); // the layer set changed wholesale
        SetActive(active);
        SurfacesChanged?.Invoke();
    }

    private void MoveToTop(Window window)
    {
        _windows.Remove(window);
        _windows.Add(window);
    }

    private void ComputeBlockedSet()
    {
        var enabled = new HashSet<Window>();
        if (TopmostModal is { } gate)
        {
            enabled.Add(gate);
            foreach (var window in _windows) // the gate's transitively owned subtree stays enabled
                for (var owner = window.Owner; owner is not null; owner = owner.Owner)
                    if (ReferenceEquals(owner, gate))
                    {
                        enabled.Add(window);
                        break;
                    }
        }
        else
        {
            foreach (var window in _windows)
                enabled.Add(window);
        }

        foreach (var window in _windows)
            SetBlocked(window, !enabled.Contains(window));
    }

    private void SetBlocked(Window window, bool blocked)
    {
        if (blocked == _blocked.Contains(window))
            return;

        if (blocked)
        {
            _blocked.Add(window);
            window.Classes.Add("obscured");   // Fork B composite-dim recipe (Window.obscured { Opacity: 0.7 })
            WindowBlocked?.Invoke(window);     // S3 releases capture held inside (wired at W3)
        }
        else
        {
            _blocked.Remove(window);
            window.Classes.Remove("obscured");
        }
    }

    private Window? ResolveHandoff(Window closed)
    {
        if (closed.Owner is { } owner && _windows.Contains(owner) && !_blocked.Contains(owner))
            return owner;
        if (TopmostModal is { } gate)
            return gate;
        for (var i = _windows.Count - 1; i >= 0; i--)
            if (!_blocked.Contains(_windows[i]))
                return _windows[i];
        return null;
    }

    private void SetActive(Window? window)
    {
        if (ReferenceEquals(_activeWindow, window))
            return;

        var previous = _activeWindow;
        _activeWindow = window;
        previous?.SetActiveInternal(false);
        window?.SetActiveInternal(true);
        ActiveWindowChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SizeAndPositionWindow(Window window, TopLevelSurface surface)
    {
        // Provisional measure at the screen constraint to read the window's content-driven desired size,
        // then fit per SizeToContent / explicit Width-Height. (W5 adds frame-converged re-fit on changes.)
        surface.Size = _viewport;
        surface.RunLayoutPass();

        var desired = window.DesiredSize;
        var stc = window.SizeToContent;
        var width = window.Width ?? (stc is SizeToContent.Width or SizeToContent.WidthAndHeight ? desired.Columns : _viewport.Columns);
        var height = window.Height ?? (stc is SizeToContent.Height or SizeToContent.WidthAndHeight ? desired.Rows : _viewport.Rows);

        var size = new Size(Math.Clamp(width, 0, _viewport.Columns), Math.Clamp(height, 0, _viewport.Rows));
        surface.Size = size;
        window.ActualSize = size;

        var (left, top) = window.WindowStartupLocation switch
        {
            WindowStartupLocation.CenterScreen =>
                ((_viewport.Columns - size.Columns) / 2, (_viewport.Rows - size.Rows) / 2),
            WindowStartupLocation.CenterOwner when window.Owner?.HostSurface is { } owner =>
                (owner.Left + (owner.Size.Columns - size.Columns) / 2, owner.Top + (owner.Size.Rows - size.Rows) / 2),
            _ => (window.Left, window.Top),
        };

        window.SetCurrentValue(Window.LeftProperty, left); // user-gesture-style write: bindings survive (§8.2)
        window.SetCurrentValue(Window.TopProperty, top);
        surface.Left = left;
        surface.Top = top;
        surface.Opacity = window.Opacity;
    }

    private void RebuildSurfaceStack()
    {
        _surfaces.Clear();
        if (_rootSurface is not null)
            _surfaces.Add(_rootSurface);
        for (var i = 0; i < _windows.Count; i++)
            if (_windows[i].HostSurface is { } surface)
                _surfaces.Add(surface);
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
