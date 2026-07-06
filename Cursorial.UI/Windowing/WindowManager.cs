using System.Runtime.InteropServices;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

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
public sealed class WindowManager : ILayoutSystem, IRenderSystem, IWindowSystem, IWindowTopology
{
    private const int MinVisible = 4; // cells of a window that must stay on-screen so the title bar is grabbable (§8.7)

    private readonly ScenePool _scenePool = new();
    private readonly List<TopLevelSurface> _surfaces = [];   // z-order, bottom→top; [0] is the root surface when present
    private readonly List<SceneLayer> _layers = [];          // per-frame scratch (the one concatenated layer span)
    private readonly TerminalCaretService _caretService;
    private readonly IUserCodeGuard? _guard;
    private readonly List<Window> _windows = [];            // shown windows, bottom→top z-order (above the root)
    private readonly List<Popup> _popups = [];              // open popups, in open order (the band above all windows)
    private readonly List<Window> _modalStack = [];         // active modals, bottom→top; the topmost is the gate
    private readonly HashSet<Window> _blocked = [];         // windows currently disabled by a modal (the `obscured` set)
    private SceneCompositor _compositor = new();
    private bool _needsComposite;                           // a stack change reset the compositor → force one render so vacated cells repaint
    private OutputCapabilities _capabilities;
    private Size _viewport;
    private TopLevelSurface? _rootSurface;
    private Window? _activeWindow;
    private UITimer? _modalAttentionTimer;
    private Window? _modalAttentionGate;
    private TerminalCaretState _lastCaret;
    private bool _caretEverApplied;
    private (int Column, int Row) _lastPointer;           // last screen pointer position (for PlacementMode.Pointer)
    private readonly List<Action> _deferredTopology = []; // §8.8 — mutations requested mid-frame, drained next frame
    private bool _topologyLocked;                         // true while S6 iterates surfaces (layout / OnLayoutCompleted / render)
    private string? _lastEmittedTitle;                    // the last title mirrored to the terminal (OSC 2 change detector)
    private TopLevelSurface? _fitBadgeSurface;            // the WM-owned fit-to-viewport badge (top-right; §8.7)
    private bool _fitBadgeVisible;
    private TopLevelSurface? _keyTipSurface;              // the Bars KeyTip badge overlay (topmost; keytips-design §8)

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

    /// <summary>
    /// Inspector/diagnostic helper: samples what every composited layer contributes at screen cell
    /// (<paramref name="column"/>, <paramref name="row"/>), ordered bottom→top. Each entry translates the
    /// screen coordinate into the layer's local space (its composite offset) and returns the cell there, or a
    /// null <see cref="LayerCellSample.Cell"/> when the coordinate falls outside that layer's footprint. Useful
    /// for diagnosing overlay compositing — e.g. a wide glyph on a lower layer whose continuation a higher layer
    /// covers, the mechanism behind an overlay bleed. Reflects the surfaces' last-rendered scenes (it re-collects
    /// their layers) and does not run a fresh render pass, so call it after a settled frame.
    /// </summary>
    internal IReadOnlyList<LayerCellSample> SampleCell(int column, int row)
    {
        var scratch = new List<SceneLayer>();
        var samples = new List<LayerCellSample>();
        for (var i = 0; i < _surfaces.Count; i++)
        {
            scratch.Clear();
            var isOccluder = !ReferenceEquals(_surfaces[i], _rootSurface) && !ReferenceEquals(_surfaces[i], _keyTipSurface);
            _surfaces[i].CollectLayers(scratch, surfaceZ: i, isOccluder: isOccluder);
            foreach (var layer in scratch)
            {
                int localColumn = column - layer.Parameters.OffsetColumn;
                int localRow = row - layer.Parameters.OffsetRow;
                bool inside = localColumn >= 0 && localColumn < layer.Scene.Columns
                              && localRow >= 0 && localRow < layer.Scene.Rows;
                samples.Add(new LayerCellSample(i, layer.Parameters, inside ? layer.Scene.GetCell(localColumn, localRow) : null));
            }
        }

        return samples;
    }

    /// <summary>The chrome-less application-root surface, or <see langword="null"/> until a root is set.</summary>
    public TopLevelSurface? RootSurface => _rootSurface;

    /// <summary>The shown windows, bottom→top in z-order (above the root surface).</summary>
    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>The open popups, in open order; their surfaces form the band above all windows (§8.4).</summary>
    public IReadOnlyList<Popup> Popups => _popups;

    /// <summary>The active (focused) top-level window, or <see langword="null"/> when none (the root has focus).</summary>
    public Window? ActiveWindow => _activeWindow;

    /// <summary>The current screen (viewport) size in cells (§8.5).</summary>
    public Size ScreenSize => _viewport;

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

    /// <summary>Invoked with the active window's title to mirror it to the terminal (OSC 2 — §8.8); wired by the host.</summary>
    internal Action<string>? SetTerminalTitle { get; set; }

    /// <summary>The root surface's render tree — the W0-compat single-tree accessor (null until a root is set).</summary>
    internal RenderTree? Tree => _rootSurface?.RenderTree;

    // ── ILayoutSystem ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool HasPendingLayout
    {
        get
        {
            foreach (var surface in _surfaces)
            {
                if (surface.HasPendingLayout)
                    return true;
            }

            // A parked one-shot re-fit (restore-from-maximize / viewport re-probe) must arm the layout
            // phase even when nothing else invalidated — the flag is only consumed inside RunLayoutPass,
            // and e.g. a chrome-less window's restore may otherwise leave every surface clean.
            foreach (var window in _windows)
            {
                if (window.FitToContentPending)
                    return true;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public void RunLayoutPass()
    {
        // Snapshot count: a surface's layout must not see the list mutate mid-pass — topology mutations
        // requested while we iterate (e.g. a Close() from a MeasureOverride) are deferred (§8.8).
        _topologyLocked = true;

        try
        {
            foreach (var surface in _surfaces)
            {
                // The SizeToContentMode.Always frame-converged re-fit: a window whose content changed
                // re-derives its size BEFORE this frame renders, so the resized frame is the first one
                // emitted (no transitional flicker). Gated on pending layout — an idle surface pays
                // nothing — except when a one-shot re-fit was requested (restore-from-maximize /
                // viewport-resize re-probe), which must run even on an otherwise-clean surface.
                if (surface.HostWindow is {} window && (surface.HasPendingLayout || window.FitToContentPending))
                    FitWindowToContent(window, surface);

                surface.RunLayoutPass();
            }
        }
        finally
        {
            _topologyLocked = false;
        }
    }

    /// <summary>
    /// The <see cref="SizeToContentMode.Always"/> re-fit (§8.5): a <b>measure-only probe</b> of the
    /// window element at the open constraint — the full viewport on each tracked axis, the same
    /// constraint the first-show fit measures at — re-derives the surface size from the resulting
    /// <see cref="UIElement.DesiredSize"/>. The window element is what gets measured, chrome template
    /// included, so chrome insets participate like any other template part and no inset arithmetic
    /// exists anywhere. Nothing is arranged at the probe size: when the fit is unchanged (the common
    /// dirty-frame case — a keystroke in a dialog TextBox) the caller's pass right after re-measures at
    /// the real constraint and arranges at unchanged bounds, so no zone re-rasters from the probe
    /// (invariant 3 holds inside Always windows). When the fit changed, the surface resize drives a
    /// normal full pass at the new constraint — still inside this frame's layout phase (no flicker).
    /// The unclamped fit is remembered per axis (<see cref="Window.FittedWidth"/>/<c>…Height</c>) so a
    /// transient viewport shrink caps the surface without losing it. A content-driven <b>grow</b> that
    /// pushes the window past the viewport follows the §8.7 badge policy by default; a window that
    /// opted into <see cref="Window.AutoFitToViewport"/> is instead shifted back into view (never past
    /// the origin), and only by the grown extent — a user-dragged overhang is respected.
    /// </summary>
    private void FitWindowToContent(Window window, TopLevelSurface surface)
    {
        var requested = window.FitToContentPending;
        window.FitToContentPending = false; // one-shot: consumed even when nothing tracks below

        if (window.WindowState == WindowState.Maximized)
            return;

        if (!requested && window.SizeToContentMode != SizeToContentMode.Always)
            return; // Once-mode windows re-fit only on request (restore-from-maximize / FitToContent())

        var stc = window.SizeToContent;
        var tracksWidth = window.Width is null && stc is SizeToContent.Width or SizeToContent.WidthAndHeight;
        var tracksHeight = window.Height is null && stc is SizeToContent.Height or SizeToContent.WidthAndHeight;

        if (!tracksWidth && !tracksHeight)
            return; // every axis is explicit (e.g. pinned by a resize drag) — nothing tracks

        var current = surface.Size;

        var probe = new Size(
            tracksWidth ? _viewport.Columns : Math.Clamp(window.Width ?? current.Columns, 0, _viewport.Columns),
            tracksHeight ? _viewport.Rows : Math.Clamp(window.Height ?? current.Rows, 0, _viewport.Rows));

        window.Measure(probe);

        var desired = window.DesiredSize;

        // A Collapsed window measures empty — a real fit, never zero, is what the memory must hold
        // (the reveal path re-fits via FitToContentPending, stamping the true fit then).
        if (window.Visibility != Visibility.Collapsed)
        {
            if (tracksWidth)
                window.FittedWidth = desired.Columns;

            if (tracksHeight)
                window.FittedHeight = desired.Rows;
        }

        var target = new Size(
            tracksWidth ? Math.Clamp(desired.Columns, 0, _viewport.Columns) : probe.Columns,
            tracksHeight ? Math.Clamp(desired.Rows, 0, _viewport.Rows) : probe.Rows);

        if (target == current)
        {
            // The fit held — but the tree is now measure-stamped at the probe constraint, and the
            // caller's queued-work drain re-measures non-root elements at their LAST constraint. Force
            // the root back through the real constraint so the pass re-stamps the tree at the arranged
            // size; unchanged bounds arrange without visual invalidation.
            surface.Root.InvalidateMeasure();
            surface.Root.InvalidateArrange();
        }
        else
        {
            surface.Size = target; // constraint change ⇒ the caller's pass right after re-lays-out fully
            window.ActualSize = target;
        }

        if (!window.AutoFitToViewport)
            return; // §8.7 default policy: an overhanging grow raises the fit affordance instead

        // Opt-in keep-visible: only a grow beyond the PREVIOUS size may shift, and only enough to fit —
        // a user-dragged overhang on an axis the content did not grow is respected.
        if (target.Columns > current.Columns && window.Left + target.Columns > _viewport.Columns)
            window.SetCurrentValue(Window.LeftProperty, Math.Max(0, _viewport.Columns - target.Columns));

        if (target.Rows > current.Rows && window.Top + target.Rows > _viewport.Rows)
            window.SetCurrentValue(Window.TopProperty, Math.Max(0, _viewport.Rows - target.Rows));
    }

    // ── IWindowSystem ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether topology mutations are queued for the next <see cref="DrainDeferredTopology"/> — the app is
    /// NOT idle until they drain (a popup/window close requested mid-layout applies one frame later, §8.8).</summary>
    internal bool HasDeferredTopology => _deferredTopology.Count > 0;

    /// <inheritdoc/>
    public void DrainDeferredTopology()
    {
        // §8.8 — apply mutations queued while the previous frame iterated its surfaces, before this
        // frame's layout. Snapshot + clear first: the lock is off now, so each replayed mutation applies
        // immediately and must not re-enqueue into the list we are draining.
        if (_deferredTopology.Count == 0)
            return;

        var pending = _deferredTopology.ToArray();

        _deferredTopology.Clear();

        foreach (var mutation in pending)
            mutation();
    }

    /// <inheritdoc/>
    public void OnLayoutCompleted()
    {
        _topologyLocked = true;

        try
        {
            // Keep each window's surface anchored to its Left/Top so a programmatic move re-composites
            // (AffectsComposite on Window.Left/Top), and re-fit SizeToContent windows whose content grew/shrank.
            foreach (var window in _windows)
            {
                if (window.HostSurface is {} surface)
                {
                    SyncSurfaceSize(window, surface);
                    surface.Left = window.Left;
                    surface.Top = window.Top;
                    surface.Shadow = window.Shadow;                                // picks up a runtime Shadow change (AffectsRender forces the frame)
                    window.SetClippedByViewport(IsWindowClipped(window, surface)); // drives the fit affordance
                }
            }

            UpdateTerminalTitle();
        }
        finally
        {
            _topologyLocked = false;
        }
    }

    /// <summary>Mirrors the active window's title to the terminal (OSC 2 — §8.8) on change. Cheap string compare
    /// per frame; emits only non-null titles (the root surface / a null-title window leaves the last title).</summary>
    private void UpdateTerminalTitle()
    {
        var title = _activeWindow?.Title;

        if (title == _lastEmittedTitle)
            return;

        _lastEmittedTitle = title;

        if (title is not null)
            SetTerminalTitle?.Invoke(title);
    }

    // ── IRenderSystem ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool HasDirtyVisuals
    {
        get
        {
            if (_needsComposite) // a surface-stack change is pending a full recomposite (e.g., a closed popup)
                return true;

            foreach (var surface in _surfaces)
            {
                if (surface.HasDirtyVisuals)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Replaces the compositor (its retained per-slot state is invalid after a layer-set/z change) and
    /// flags a pending full recomposite so the frame loop renders even when no surface has dirty raster work —
    /// the cells a closed/moved surface vacated repaint from the layers behind.</summary>
    private void ResetCompositor()
    {
        var previous = _compositor;
        _compositor = new SceneCompositor();
        // Carry the ghost-footprint set across the reset. A Cells-layer image (iTerm2/Sixel) has no protocol
        // erase, so its pixels linger on the terminal until overwritten — and when occluded by a popup it isn't
        // a registered target fragment, so a fresh compositor would have no record of it. Without this hand-off,
        // switching tabs WHILE an occluding popup is open would strand the (now-unloaded) image's pixels on screen.
        _compositor.AdoptGhostFootprints(previous);
        _needsComposite = true;
    }

    /// <inheritdoc/>
    public bool RenderFrame(CellBuffer target, in FrameTime time)
    {
        ArgumentNullException.ThrowIfNull(target);
        _topologyLocked = true; // a topology mutation from a Render override defers (§8.8)

        try
        {
            return RenderFrameCore(target);
        }
        finally
        {
            _topologyLocked = false;
        }
    }

    private bool RenderFrameCore(CellBuffer target)
    {
        var changed = false;

        // ① raster each surface's dirty zones (z order doesn't matter for rastering — it's per-surface).
        foreach (var surface in _surfaces)
        {
            surface.RunRenderPass();

            if (_guard is { IsFatal: true })
                return true; // unwind promptly; conservative changed — teardown is imminent
        }

        // ② concatenate every surface's layers, bottom→top, each at its screen offset, into ONE composite.
        // Stamp each surface's z-index + occluder flag so the compositor can crop/suppress a lower surface's
        // graphics-protocol image where a higher OPAQUE surface (a window/popup/badge — anything but the root)
        // overlaps it. (The terminal draws such images above the cell grid, so they'd otherwise show through.)
        _layers.Clear();

        for (var i = 0; i < _surfaces.Count; i++)
        {
            // The root never occludes; the KeyTip overlay never occludes either — it is a sparse, screen-sized
            // badge layer, so marking its full footprint an occluder would wrongly crop every inline image beneath
            // it (occlusion is image-fragment-only; keytips-design §8). The accepted tradeoff: an inline image
            // directly under a badge shows through the amber chip (rare — bar-control images are out of v1 scope).
            // Every window/popup/badge does occlude.
            var isOccluder = !ReferenceEquals(_surfaces[i], _rootSurface) && !ReferenceEquals(_surfaces[i], _keyTipSurface);
            _surfaces[i].CollectLayers(_layers, surfaceZ: i, isOccluder: isOccluder);
        }

        // Composite unconditionally — even with zero surfaces. An empty layer set is the signal that the surface
        // stack just emptied (null root / last surface closed): the compositor's full-reset path then erases any
        // ghost footprint (a removed Cells-layer image whose pixels linger with no protocol erase). When the stack
        // was already empty across frames it early-returns with no work, so there's no per-frame churn.
        changed |= _compositor.Composite(CollectionsMarshal.AsSpan(_layers), new CellBufferView(target));

        // A surface-stack change (open/close/z/resize) reset the compositor to force a full recomposite; that
        // recomposite has now run, so clear the pending flag. (When a popup/window closes, the remaining
        // surfaces have no dirty raster work — only this flag makes the frame loop render, so the cells the
        // closed surface vacated get repainted from the layers behind. Without it the closed popup's pixels
        // would linger though it is no longer hit-testable.)
        _needsComposite = false;

        // ③ caret write (T4 contract): the caret service transforms the winning publication to its owning
        // surface's WINDOW-LOCAL cell; here we fold that surface's screen offset so a caret published inside
        // a window/popup lands at the right ABSOLUTE cell (a centered dialog's caret is at Left+col, not col).
        var caret = _caretService.GetCaretState(_resolveSurfaceOffset ??= ResolveSurfaceOffset);

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

    private Func<UIElement, (int Column, int Row)>? _resolveSurfaceOffset;

    // Maps a caret owner to its top-level surface's screen offset (③). An element's VisualRoot is the
    // surface's Root (the root element / a Window / a Popup child — see the TopLevelSurface ctor sites), so
    // a reference match finds the owning surface. Cached as a field so the per-frame caret call allocates no
    // delegate, and the body itself is allocation-free (a value tuple over the surface list) — a blinking
    // caret keeps the frame clean. Falls back to the origin for a detached owner / no matching surface.
    private (int Column, int Row) ResolveSurfaceOffset(UIElement owner)
    {
        var root = owner.VisualRoot;

        foreach (var surface in _surfaces)
        {
            if (ReferenceEquals(surface.Root, root))
                return (surface.Left, surface.Top);
        }

        return (0, 0);
    }

    /// <inheritdoc/>
    public void OnViewportResized(Size newSize)
    {
        // The resize transaction (design doc §10.6 / punch 4): fresh compositor (its retained per-layer
        // state is sized to the old target) + the root surface re-fits the screen + full re-raster; the
        // same frame's Phase 5 re-lays-out under the new constraint. Windows re-clamp/re-size at W5.
        _viewport = newSize;

        ResetCompositor();

        // A resize invalidates popup placement: light-dismiss popups close, StaysOpen popups re-place below.
        for (var i = _popups.Count - 1; i >= 0; i--)
        {
            if (!_popups[i].StaysOpen)
                _popups[i].CloseCore(PopupCloseReason.ScreenResized);
        }

        _rootSurface?.Size = newSize;
        _keyTipSurface?.Size = newSize; // the KeyTip overlay tracks the screen too, or a GROW clips its badges

        ReclampWindowsForViewport();

        foreach (var popup in _popups)
        {
            if (popup.PopupSurface is {} surface)
                PlacePopup(popup, surface);
        }

        // The fit badge appears (or re-evaluates) on a terminal resize: shown only while a non-maximized
        // window now overhangs the viewport (§8.7 — the user moves+shrinks on demand, no auto-shrink).
        _fitBadgeVisible = AnyWindowClipped();

        SyncFitBadge();

        foreach (var surface in _surfaces)
            surface.InvalidateAll();
    }

    /// <summary>
    /// The §8.7 screen-resize policy: <see cref="WindowState.Maximized"/> windows re-fill; every Normal
    /// window re-clamps so at least <see cref="MinVisible"/> cells stay visible on <b>both</b> axes (the
    /// title bar stays grabbable). It deliberately does <b>not</b> auto-shrink — a temporary terminal resize
    /// must not lose the user's chosen window size; the user moves+shrinks on demand via
    /// <see cref="Window.FitToViewport"/> (surfaced by the clipped-state affordance). The per-window clipped
    /// flag is recomputed every frame in <see cref="OnLayoutCompleted"/> (so it tracks drags too).
    /// </summary>
    private void ReclampWindowsForViewport()
    {
        foreach (var window in _windows)
        {
            if (window.HostSurface is not {} surface)
                continue;

            if (window.WindowState == WindowState.Maximized)
            {
                FillScreen(window, surface);
                continue;
            }

            var (left, top) = ClampPosition(window.Left, window.Top, surface.Size);

            if (left != window.Left)
                window.SetCurrentValue(Window.LeftProperty, left);

            if (top != window.Top)
                window.SetCurrentValue(Window.TopProperty, top);

            surface.Left = left;
            surface.Top = top;

            // An Always window re-probes at the new viewport: a grow releases a viewport-capped tracked
            // axis back toward its content size (no ratchet), a shrink re-caps it. Once windows keep the
            // no-auto-shrink policy — their size is the user's (or the first fit's) to keep.
            if (window.SizeToContentMode == SizeToContentMode.Always)
                window.FitToContentPending = true;
        }
    }

    /// <summary>
    /// Clamps a window position so at least <see cref="MinVisible"/> cells stay visible on each axis and the
    /// title bar (top row) is never pushed above the screen — §8.7's "pull MinVisible into view" rule, shared
    /// by the live move-drag and the screen-resize re-clamp. Never shrinks; see <see cref="Window.FitToViewport"/>.
    /// </summary>
    internal (int Left, int Top) ClampPosition(int left, int top, Size windowSize)
        => (Math.Clamp(left, MinVisible - windowSize.Columns, Math.Max(0, _viewport.Columns - MinVisible)),
            Math.Clamp(top, 0, Math.Max(0, _viewport.Rows - MinVisible)));

    /// <summary>Whether <paramref name="window"/> overhangs the viewport on any edge (drives the fit affordance).</summary>
    internal bool IsWindowClipped(Window window, TopLevelSurface surface)
    {
        if (window.WindowState == WindowState.Maximized)
            return false; // fills the viewport exactly

        var size = surface.Size;

        return surface.Left < 0 ||
               surface.Top < 0 ||
               surface.Left + size.Columns > _viewport.Columns ||
               surface.Top + size.Rows > _viewport.Rows;
    }

    /// <summary>Clamps a resize-drag size into <c>[<see cref="MinVisible"/>, viewport]</c> width and <c>[2, viewport]</c> height.</summary>
    internal Size ClampSize(int width, int height)
        => new(Math.Clamp(width, MinVisible, _viewport.Columns), Math.Clamp(height, 2, _viewport.Rows));

    /// <summary>Sizes <paramref name="window"/>'s surface to the full viewport at the origin (the maximized fill).</summary>
    private void FillScreen(Window window, TopLevelSurface surface)
    {
        surface.Size = _viewport;
        surface.Left = 0;
        surface.Top = 0;
        window.ActualSize = _viewport;
        window.SetCurrentValue(Window.LeftProperty, 0);
        window.SetCurrentValue(Window.TopProperty, 0);
    }

    // ── Root surface (the RootElement/ShowRoot path; HostWindow == null) ─────────────────────────────

    /// <summary>
    /// Sets (or, with <see langword="null"/>, clears) the chrome-less application-root surface — the
    /// W0 replacement for the single-root <c>SetRoot</c>. The root must already be attached via its tree;
    /// the surface fills the screen. The layer set changes wholesale, so the compositor is reset.
    /// </summary>
    internal void SetRootSurface(UIElement? root)
    {
        if (DeferIfLocked(() => SetRootSurface(root)))
            return;

        if (_rootSurface is not null)
        {
            _surfaces.Remove(_rootSurface);
            _rootSurface.Detach(); // RenderTree.Detach — scenes back to the pool
            _rootSurface = null;
        }

        if (root is not null)
            _rootSurface = new TopLevelSurface(root, _scenePool, _capabilities, _guard) { Size = _viewport };

        RebuildSurfaceStack();
        ResetCompositor();
    }

    // ── Window hosting + modality (P7-W1 show/close; P7-W2 modal stack + blocked set + handoff) ───────

    /// <summary>Shows <paramref name="window"/> modelessly on its own surface above the root, then activates it
    /// (or, if a modal blocks it, leaves it below the gate and activates the gate).</summary>
    internal void ShowWindow(Window window)
    {
        if (DeferIfLocked(() => ShowWindow(window)))
            return;

        AddWindowSurface(window);
        ComputeBlockedSet();
        FinishShow(_blocked.Contains(window) ? TopmostModal : window);
    }

    /// <summary>Shows <paramref name="window"/> modally: pushes the modal stack, blocks the rest, and activates it.</summary>
    internal void ShowDialog(Window window)
    {
        if (DeferIfLocked(() => ShowDialog(window)))
            return;

        AddWindowSurface(window);
        _modalStack.Add(window);
        ComputeBlockedSet(); // blocks every window except the modal + its transitively owned subtree
        FinishShow(window);
    }

    /// <summary>Brings <paramref name="window"/> to the top of the window band and activates it. Returns false when
    /// it is not shown here or is modal-blocked (a blocked activate silently redirects to the gate — no pulse).</summary>
    internal bool ActivateWindow(Window window)
    {
        // A mid-frame activate defers and reports false now (it lands next frame — §8.8).
        if (DeferIfLocked(() => ActivateWindow(window)))
            return false;

        if (!_windows.Contains(window))
            return false;

        if (_blocked.Contains(window))
        {
            if (TopmostModal is {} gate && !ReferenceEquals(gate, window))
                ActivateWindow(gate); // programmatic redirect, no attention pulse (§8.6)

            return false;
        }

        MoveToTop(window);
        RebuildSurfaceStack();
        ResetCompositor();
        SetActive(window);
        SurfacesChanged?.Invoke();
        return true;
    }

    /// <summary>Removes <paramref name="window"/>'s surface, pops it from the modal stack, recomputes the blocked
    /// set, and — only if it was active — hands activation off (owner → gate → topmost enabled → null, §8.6).</summary>
    internal void CloseWindow(Window window)
    {
        if (DeferIfLocked(() => CloseWindow(window)))
            return;

        if (!_windows.Remove(window))
            return;

        var wasActive = ReferenceEquals(_activeWindow, window);
        _modalStack.Remove(window);
        _blocked.Remove(window);
        window.HostSurface?.Detach();
        window.HostSurface = null;

        RebuildSurfaceStack();
        ResetCompositor();
        ComputeBlockedSet(); // a closed modal unblocks the windows it was gating

        if (wasActive)
            SetActive(ResolveHandoff(window));

        SurfacesChanged?.Invoke();
    }

    /// <summary>
    /// Closes the windows owned by <paramref name="window"/> (recursively, <see cref="WindowCloseReason.OwnerClosed"/>
    /// — not cancelable) and the popups hosted in its surface (<see cref="PopupCloseReason.HostClosed"/>), before the
    /// window itself closes (§8.8). Called by <see cref="Window.Close(WindowCloseReason)"/>.
    /// </summary>
    internal void CloseOwnedAndHostedOf(Window window)
    {
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            var owned = _windows[i];

            if (ReferenceEquals(owned.Owner, window))
                owned.Close(WindowCloseReason.OwnerClosed); // recurses into the owned window's own owned/popups
        }

        if (window.HostSurface is {} surface)
        {
            for (var i = _popups.Count - 1; i >= 0; i--)
            {
                if (_popups[i].EffectiveTarget is {} target && ReferenceEquals(SurfaceForElement(target), surface))
                    _popups[i].CloseCore(PopupCloseReason.HostClosed);
            }
        }
    }

    /// <summary>
    /// The shutdown sweep (§8.8): closes every popup (<see cref="PopupCloseReason.HostClosed"/>) then every window
    /// top→bottom with the non-cancelable <see cref="WindowCloseReason.ManagerShutdown"/> reason. Pending
    /// <see cref="Window.ShowDialogAsync(System.Threading.CancellationToken)"/> tasks complete as their windows close.
    /// </summary>
    public Task CloseAllAsync()
    {
        for (var i = _popups.Count - 1; i >= 0; i--)
            _popups[i].CloseCore(PopupCloseReason.HostClosed);

        var windows = _windows.ToArray(); // snapshot — Close mutates _windows (and cascades to owned)

        for (var i = windows.Length - 1; i >= 0; i--)
            windows[i].Close(WindowCloseReason.ManagerShutdown);

        _fitBadgeVisible = false;
        SyncFitBadge();

        return Task.CompletedTask;
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
        ResetCompositor(); // the layer set changed wholesale
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

        if (TopmostModal is {} gate)
        {
            enabled.Add(gate);

            foreach (var window in _windows) // the gate's transitively owned subtree stays enabled
            {
                for (var owner = window.Owner; owner is not null; owner = owner.Owner)
                {
                    if (ReferenceEquals(owner, gate))
                    {
                        enabled.Add(window);
                        break;
                    }
                }
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
            window.Classes.Add("obscured"); // Fork B composite-dim recipe (Window.obscured { Opacity: 0.7 })
            WindowBlocked?.Invoke(window);        // S3 releases capture held inside (wired at W3)
        }
        else
        {
            _blocked.Remove(window);
            window.Classes.Remove("obscured");
        }
    }

    private Window? ResolveHandoff(Window closed)
    {
        if (closed.Owner is {} owner && _windows.Contains(owner) && !_blocked.Contains(owner))
            return owner;

        if (TopmostModal is {} gate)
            return gate;

        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            if (!_blocked.Contains(_windows[i]))
                return _windows[i];
        }

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
        // A window shown already maximized fills the viewport from the origin; ApplyMaximizeState snapshots
        // the restore bounds and clears its explicit Width/Height so the element stretches to the surface.
        if (window.WindowState == WindowState.Maximized)
        {
            window.ApplyMaximizeState(WindowState.Maximized);
            surface.Size = _viewport;
            window.ActualSize = _viewport;
            window.SetCurrentValue(Window.LeftProperty, 0);
            window.SetCurrentValue(Window.TopProperty, 0);
            surface.Left = 0;
            surface.Top = 0;
            return;
        }

        // Provisional measure at the screen constraint to read the window's content-driven desired size,
        // then fit per SizeToContent / explicit Width-Height. (W5 adds frame-converged re-fit on changes.)
        surface.Size = _viewport;
        surface.RunLayoutPass();

        var desired = window.DesiredSize;
        var stc = window.SizeToContent;
        var tracksWidth = stc is SizeToContent.Width or SizeToContent.WidthAndHeight;
        var tracksHeight = stc is SizeToContent.Height or SizeToContent.WidthAndHeight;

        // Remember the unclamped fit (§8.5): SyncSurfaceSize re-derives held axes from it every frame,
        // so a transient viewport shrink caps the surface without losing the fit. A Collapsed window
        // measures empty — never stamp zero; the reveal path re-fits via FitToContentPending.
        if (window.Visibility != Visibility.Collapsed)
        {
            if (tracksWidth && window.Width is null)
                window.FittedWidth = desired.Columns;

            if (tracksHeight && window.Height is null)
                window.FittedHeight = desired.Rows;
        }

        var width = window.Width ?? (tracksWidth ? desired.Columns : _viewport.Columns);
        var height = window.Height ?? (tracksHeight ? desired.Rows : _viewport.Rows);

        var size = new Size(Math.Clamp(width, 0, _viewport.Columns), Math.Clamp(height, 0, _viewport.Rows));
        surface.Size = size;
        window.ActualSize = size;

        var (left, top) = window.WindowStartupLocation switch
                          {
                              WindowStartupLocation.CenterScreen =>
                                  ((_viewport.Columns - size.Columns) / 2, (_viewport.Rows - size.Rows) / 2),
                              WindowStartupLocation.CenterOwner when window.Owner?.HostSurface is {} owner =>
                                  (owner.Left + (owner.Size.Columns - size.Columns) / 2, owner.Top + (owner.Size.Rows - size.Rows) / 2),
                              _ => (window.Left, window.Top)
                          };

        window.SetCurrentValue(Window.LeftProperty, left); // user-gesture-style write: bindings survive (§8.2)
        window.SetCurrentValue(Window.TopProperty, top);
        surface.Left = left;
        surface.Top = top;
        // surface.Opacity is intentionally NOT set from window.Opacity: the Window IS the surface's render-tree
        // root, so its Opacity is already applied (live, via AffectsComposite) by the root opacity boundary.
        // Folding it into surface.Opacity too would DOUBLE it — and worse, this runs before FinishShow activates
        // the window, so a `When IsActive==false` opacity setter would freeze the surface at the inactive value
        // and never re-sync. surface.Opacity stays 1.0 for windows (popups, whose Child ≠ the Popup element, do
        // carry popup.Opacity here). Per the design note: window opacity is a render-tree boundary, not surface-level.
    }

    /// <summary>
    /// Re-derives a window's surface size from its current state (§8.5/§8.7):
    /// <see cref="WindowState.Maximized"/> ⇒ the full viewport; else, per axis, an explicit
    /// <c>Width</c>/<c>Height</c> wins, a remembered content fit (<see cref="Window.FittedWidth"/>/<c>…Height</c>,
    /// kept unclamped so a transient viewport shrink only caps the surface and a re-grow recovers the
    /// fit) comes next, and an axis with neither holds the surface size — each viewport-capped. This is
    /// where a resize-drag and a maximize toggle land. Content tracking deliberately does <b>not</b> happen
    /// here: a post-layout resize could only render one mismatched frame (the pass already ran) —
    /// <see cref="SizeToContentMode.Always"/> windows converge INSIDE the layout phase
    /// (<see cref="FitWindowToContent"/>) and <see cref="SizeToContentMode.Once"/> windows keep their
    /// first-show fit. Compare-and-skip keeps a stable window idle (no thrash): at steady state the
    /// derived size equals the surface size, so it is a no-op every frame.
    /// </summary>
    private void SyncSurfaceSize(Window window, TopLevelSurface surface)
    {
        var size = surface.Size;

        // The fitted memory only speaks for an axis that CURRENTLY tracks content — stale memory from
        // a previous incarnation (re-shown after a SizeToContent change to Manual) must not override
        // Manual/hold semantics.
        var stc = window.SizeToContent;
        var fittedWidth = stc is SizeToContent.Width or SizeToContent.WidthAndHeight ? window.FittedWidth : null;
        var fittedHeight = stc is SizeToContent.Height or SizeToContent.WidthAndHeight ? window.FittedHeight : null;

        var target =
            window.WindowState == WindowState.Maximized
                ? _viewport
                : new Size(
                    Math.Clamp(window.Width ?? fittedWidth ?? size.Columns, 0, _viewport.Columns),
                    Math.Clamp(window.Height ?? fittedHeight ?? size.Rows, 0, _viewport.Rows));

        if (target == size)
            return; // steady state — idle holds

        surface.Size = target; // flips HasPendingLayout → re-layout next frame at the new constraint
        window.ActualSize = target;
    }

    /// <summary>
    /// §8.8 reentrancy guard: while S6 iterates surfaces (layout / OnLayoutCompleted / render) a topology
    /// mutation is queued for the next frame's <see cref="DrainDeferredTopology"/> instead of mutating the
    /// stack mid-iteration. Returns true when <paramref name="mutation"/> was deferred (the caller returns).
    /// </summary>
    private bool DeferIfLocked(Action mutation)
    {
        if (!_topologyLocked)
            return false;

        _deferredTopology.Add(mutation);
        return true;
    }

    private void RebuildSurfaceStack()
    {
        _surfaces.Clear();

        if (_rootSurface is not null)
            _surfaces.Add(_rootSurface);

        // The KeyTip overlay sits directly ABOVE the root surface (its badges annotate the root's bars) but BELOW
        // windows and popups — so a modal Backstage window / File-anchored popup opened over the ribbon OCCLUDES the
        // badges rather than the badges bleeding on top of it (keytips-design §8). v1 badges only root/window bar
        // surfaces; a v2 dropdown-drill (badging an opened popup's items) will need a per-popup badge layer.
        if (_keyTipSurface is {} keyTips)
            _surfaces.Add(keyTips);

        foreach (var window in _windows)
        {
            if (window.HostSurface is {} surface)
                _surfaces.Add(surface);
        }

        foreach (var popup in _popups)
        {
            if (popup.PopupSurface is {} surface)
                _surfaces.Add(surface);
        }

        if (_fitBadgeSurface is {} badge) // the fit badge sits above everything (§8.7)
            _surfaces.Add(badge);
    }

    // ── Popup hosting (P7-W4: light-dismiss surfaces in the band above every window) ──────────────────

    /// <summary>Hosts <paramref name="popup"/>'s child on a new popup surface, places it relative to the target
    /// (clamped to the screen), and pushes it onto the popup band. A null <c>Child</c> is a no-op (the popup
    /// reopens when one is set). Called by <c>Popup.OpenCore</c>.</summary>
    internal void OpenPopup(Popup popup)
    {
        if (DeferIfLocked(() => OpenPopup(popup)))
            return;

        if (popup.Child is not {} child)
            return;

        var surface = new TopLevelSurface(child, _scenePool, _capabilities, _guard)
                      {
                          IsPopup = true,
                          IsHitTestTransparent = popup.IsHitTestTransparent, // a ToolTip's surface never steals hover/clicks
                          Shadow = popup.Shadow                              // the popup's drop shadow (default WindowShadow.Default)
                      };

        popup.PopupSurface = surface;
        _popups.Add(popup);

        PlacePopup(popup, surface);

        RebuildSurfaceStack();
        ResetCompositor(); // the layer set changed wholesale
        SurfacesChanged?.Invoke();
    }

    /// <summary>Removes <paramref name="popup"/>'s surface from the band and detaches it. Idempotent (a popup not
    /// currently hosted is a no-op). Called by <c>Popup.CloseCore</c> / the content-swap path.</summary>
    internal void ClosePopup(Popup popup)
    {
        if (DeferIfLocked(() => ClosePopup(popup)))
            return;

        if (!_popups.Remove(popup))
            return;

        popup.PopupSurface?.Detach();
        popup.PopupSurface = null;

        RebuildSurfaceStack();
        ResetCompositor();
        SurfacesChanged?.Invoke();
    }

    /// <summary>Measures the popup child at the screen, then positions its surface per <see cref="Popup.Placement"/>
    /// + offsets relative to the effective target's screen rect, clamped into the viewport (§8.4).</summary>
    private void PlacePopup(Popup popup, TopLevelSurface surface)
    {
        // Provisional measure at the screen constraint to read the child's content-driven desired size.
        surface.Size = _viewport;
        surface.RunLayoutPass();

        var desired = surface.Root.DesiredSize;

        var size = new Size(
            Math.Clamp(desired.Columns, 0, _viewport.Columns),
            Math.Clamp(desired.Rows, 0, _viewport.Rows));

        surface.Size = size;

        var anchor = AnchorRect(popup);

        var (left, top) =
            popup.Placement switch
            {
                PlacementMode.Top     => (anchor.Column, anchor.Row - size.Rows),
                PlacementMode.Right   => (anchor.ColumnEnd, anchor.Row),
                PlacementMode.Left    => (anchor.Column - size.Columns, anchor.Row),
                PlacementMode.Center  => (anchor.Column + (anchor.Columns - size.Columns) / 2, anchor.Row + (anchor.Rows - size.Rows) / 2),
                PlacementMode.Pointer => (_lastPointer.Column, _lastPointer.Row),
                _                     => (anchor.Column, anchor.RowEnd) // Bottom (default)
            };

        left += popup.HorizontalOffset;
        top += popup.VerticalOffset;

        // Clamp so the whole surface stays on-screen (a content larger than the viewport pins to the origin).
        surface.Left = Math.Clamp(left, 0, Math.Max(0, _viewport.Columns - size.Columns));
        surface.Top = Math.Clamp(top, 0, Math.Max(0, _viewport.Rows - size.Rows));
        surface.Opacity = popup.Opacity;
    }

    /// <summary>The effective target's bounds in screen space (its surface offset + a live parent-chain walk), or
    /// a zero rect at the origin when the popup has no resolvable on-surface target.</summary>
    private LayoutRect AnchorRect(Popup popup)
    {
        if (popup.EffectiveTarget is {} target && SurfaceForElement(target) is {} host)
        {
            var (wx, wy) = target.TranslateToWindow(0, 0);
            return new LayoutRect(host.Left + wx, host.Top + wy, target.Bounds.Size);
        }

        return LayoutRect.Empty;
    }

    /// <summary>The surface whose render tree owns <paramref name="element"/>, or <see langword="null"/> when it is
    /// detached / not hosted here.</summary>
    internal TopLevelSurface? SurfaceForElement(UIElement element)
    {
        if (element.GetRenderTree() is not {} tree)
            return null;

        foreach (var surface in _surfaces)
        {
            if (ReferenceEquals(surface.RenderTree, tree))
                return surface;
        }

        return null;
    }

    /// <summary>Light-dismiss (§8.4): an uncaptured press closes every open non-<c>StaysOpen</c> popup except the
    /// one pressed (<paramref name="hit"/> null = a press in dead space — all dismiss). Full chain semantics is
    /// W4-b. A popup's <c>Closed</c> handler may close OTHER popups (a menu's whole-chain collapse dismisses every
    /// submenu at once), so <see cref="_popups"/> can shrink by more than one per close — snapshot the dismiss set
    /// first, then close each (<see cref="Popup.CloseCore"/> is idempotent, so an already-cascaded-closed popup is a
    /// no-op).</summary>
    private void LightDismissPopups(TopLevelSurface? hit, int pressColumn, int pressRow)
    {
        if (_popups.Count == 0)
            return;

        var toDismiss = new List<Popup>(_popups.Count);
        foreach (var popup in _popups)
            if (!(popup.StaysOpen || ReferenceEquals(popup.PopupSurface, hit) || PressOnAnchor(popup, pressColumn, pressRow)))
                toDismiss.Add(popup);

        foreach (var popup in toDismiss)
            popup.CloseCore(PopupCloseReason.LightDismiss);
    }

    // A KeepOpenOnAnchorPress popup is not dismissed by a press on its anchor (PlacementTarget) — the anchor owns
    // the toggle (ComboBox/DatePicker face). Screen-bounds containment of the press against the anchor.
    private static bool PressOnAnchor(Popup popup, int pressColumn, int pressRow)
    {
        if (!popup.KeepOpenOnAnchorPress || popup.EffectiveTarget is not { IsAttachedToTree: true } anchor)
            return false;

        var origin = anchor.TranslateToWindow(0, 0);
        return new LayoutRect(origin.Column, origin.Row, anchor.Bounds.Size).Contains(pressColumn, pressRow);
    }

    // ── Fit-to-viewport badge (P7-W5b: the WM-owned affordance for clipped windows after a resize, §8.7) ──

    /// <summary>Whether the fit badge is currently shown (test/diagnostic observability).</summary>
    public bool IsFitBadgeVisible => _fitBadgeSurface is not null;

    /// <summary>Whether any Normal (non-maximized) window currently overhangs the viewport.</summary>
    private bool AnyWindowClipped()
    {
        foreach (var window in _windows)
        {
            if (window.HostSurface is {} surface && IsWindowClipped(window, surface))
                return true;
        }

        return false;
    }

    /// <summary>The fit badge's "Fit windows" action: moves+shrinks every clipped window into view, then hides
    /// the badge (nothing remains to fit). Public so the demo / code-behind can trigger the same sweep.</summary>
    public void FitAllWindowsToViewport()
    {
        foreach (var window in _windows)
        {
            if (window.HostSurface is {} surface && IsWindowClipped(window, surface))
                window.FitToViewport();
        }

        _fitBadgeVisible = false;
        SyncFitBadge();
    }

    /// <summary>The fit badge's dismiss (✕) action: hides the badge until the next clipping resize.</summary>
    public void DismissFitBadge()
    {
        _fitBadgeVisible = false;
        SyncFitBadge();
    }

    /// <summary>Reconciles the badge surface with <see cref="_fitBadgeVisible"/> (create+place / detach).</summary>
    private void SyncFitBadge()
    {
        if (_fitBadgeVisible)
        {
            _fitBadgeSurface ??= new TopLevelSurface(BuildFitBadge(), _scenePool, _capabilities, _guard);
            PlaceFitBadge(_fitBadgeSurface);
        }
        else if (_fitBadgeSurface is {} badge)
        {
            badge.Detach();
            _fitBadgeSurface = null;
        }
        else
        {
            return; // already hidden — nothing changed
        }

        RebuildSurfaceStack();
        ResetCompositor();
        SurfacesChanged?.Invoke();
    }

    /// <summary>Measures the badge against the screen and pins it to the top-right corner.</summary>
    private void PlaceFitBadge(TopLevelSurface surface)
    {
        surface.Size = _viewport;
        surface.RunLayoutPass();

        var desired = surface.Root.DesiredSize;

        var size = new Size(
            Math.Clamp(desired.Columns, 0, _viewport.Columns),
            Math.Clamp(desired.Rows, 0, _viewport.Rows));

        surface.Size = size;
        surface.Left = Math.Max(0, _viewport.Columns - size.Columns - 1);
        surface.Top = 1;
    }

    /// <summary>Builds the interim badge: an occluding bar with a "Fit windows" button + a dismiss ✕ (C4 themes it).</summary>
    private UIElement BuildFitBadge()
    {
        var fit = new Button { Content = "Fit windows", Focusable = false, IsTabStop = false };

        fit.Classes.Add("info");
        fit.Click += (_, _) => FitAllWindowsToViewport();

        var dismiss = new Button { Content = "✕", Focusable = false, IsTabStop = false };

        dismiss.Click += (_, _) => DismissFitBadge();

        var row = new StackPanel
                  {
                      Orientation = Orientation.Horizontal,
                      Children = { fit, dismiss }
                  };

        var root = new Border { Occludes = false, Child = row, Padding = new Margins(1, 0) };

        root.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        root.SetResourceReference(Border.BorderPenProperty, ThemeKeys.MenuBorderPen);

        return root;
    }

    // ── KeyTip overlay (Cursorial.UI.Bars; keytips-design §8: a dedicated topmost hit-transparent surface) ──────

    /// <summary>Shows <paramref name="badgeRoot"/> (the Bars <c>KeyTipLayer</c>) on a dedicated surface just above the
    /// root surface — above the ribbon it badges, but below windows/popups so a modal Backstage occludes the badges
    /// (keytips-design §8; see <see cref="RebuildSurfaceStack"/>). The surface is hit-test transparent (mouse never
    /// routes to it) and screen-sized; the layer places each badge by absolute screen coordinates. Replaces any
    /// existing overlay. Called by the KeyTip controller's <c>Enter()</c>.</summary>
    internal void ShowKeyTipOverlay(UIElement badgeRoot)
    {
        ArgumentNullException.ThrowIfNull(badgeRoot);

        _keyTipSurface?.Detach();
        _keyTipSurface = new TopLevelSurface(badgeRoot, _scenePool, _capabilities, _guard)
                         {
                             IsHitTestTransparent = true,
                             Size = _viewport
                         };

        RebuildSurfaceStack();
        ResetCompositor(); // the layer set changed wholesale
        SurfacesChanged?.Invoke();
    }

    /// <summary>Re-arranges the KeyTip overlay surface within the current frame (a no-op when none is showing). The
    /// controller calls this right after re-anchoring badges in the post-layout hook so a scroll/move that shifts a
    /// target's screen cell repositions its badge THIS frame — the main layout pass already ran, so without this the
    /// badge would trail its target by one frame (keytips-design §9). Cheap: a clean overlay re-arranges nothing.</summary>
    internal void RunKeyTipOverlayLayout() => _keyTipSurface?.RunLayoutPass();

    /// <summary>Tears the KeyTip overlay down (idempotent — a no-op when none is showing). Called by the controller's
    /// <c>Exit()</c>.</summary>
    internal void HideKeyTipOverlay()
    {
        if (_keyTipSurface is not {} surface)
            return;

        surface.Detach();
        _keyTipSurface = null;

        RebuildSurfaceStack();
        ResetCompositor();
        SurfacesChanged?.Invoke();
    }

    // ── Renegotiation leg (UIApplication calls; mirrors the single-root render system) ───────────────

    /// <summary>
    /// Re-stamps capabilities on every surface's tree, resets the compositor, and marks everything for
    /// re-raster so the first post-renegotiate frame repaints fully (design doc §10.6).
    /// </summary>
    internal void OnCapabilitiesChanged(OutputCapabilities capabilities)
    {
        _capabilities = capabilities;

        foreach (var surface in _surfaces)
        {
            surface.RenderTree.Capabilities = capabilities;
            surface.InvalidateAll();
        }

        ResetCompositor();
    }

    // ── IWindowTopology (S3's surface-level gate; replaces SingleRootWindowTopology at P7-W3) ─────────

    /// <summary>The topmost non-hit-test-transparent surface containing the screen point, or <see langword="null"/>.</summary>
    public TopLevelSurface? SurfaceFromPoint(int column, int row)
    {
        for (var i = _surfaces.Count - 1; i >= 0; i--) // top→bottom in z-order
        {
            var surface = _surfaces[i];

            if (!surface.IsHitTestTransparent && surface.Contains(column, row))
                return surface;
        }

        return null;
    }

    /// <inheritdoc/>
    public bool FilterMouseEvent(MouseEvent mouse, out UIElement? surfaceRoot, out int surfaceColumn, out int surfaceRow, bool hitTestOnly = false)
    {
        surfaceColumn = mouse.Position.Column;
        surfaceRow = mouse.Position.Row;
        surfaceRoot = null;
        _lastPointer = (mouse.Position.Column, mouse.Position.Row); // feeds PlacementMode.Pointer

        var surface = SurfaceFromPoint(mouse.Position.Column, mouse.Position.Row);

        if (surface is null)
        {
            // A press over no surface still light-dismisses open popups (a click in dead space, §8.4).
            if (hitTestOnly is false && mouse.Kind == MouseEventKind.ButtonDown && _popups.Count > 0)
                LightDismissPopups(hit: null!, mouse.Position.Column, mouse.Position.Row);

            return true; // routes nowhere (ND5: dropped, no throw)
        }

        if (hitTestOnly is false)
        {
            var window = surface.HostWindow;
            var isPress = mouse.Kind == MouseEventKind.ButtonDown;

            // Light dismiss before activation/routing: a press closes every open light-dismiss popup except the one
            // pressed (§8.4). Pressing inside a popup keeps it (and routes into it); pressing a window/root dismisses.
            if (isPress && _popups.Count > 0)
                LightDismissPopups(surface, mouse.Position.Column, mouse.Position.Row);

            // A blocked window swallows everything (no hover/routing); a press redirects activation to the gate
            // and pulses the gate's :modal-attention cue (§8.6 — the *one* source of the pulse).
            if (window is not null && _blocked.Contains(window))
            {
                if (isPress && TopmostModal is {} gate)
                {
                    ActivateWindow(gate);
                    PulseModalAttention(gate);
                }

                return false;
            }

            // Activation-on-press for an inactive but enabled window (§8.6). Topology mutations during the input
            // drain apply immediately (§8.8); the z-reorder doesn't move the surface, so the local coords below hold.
            if (isPress && window is not null && !ReferenceEquals(_activeWindow, window))
                ActivateWindow(window);
        }

        surfaceRoot = surface.Root;
        surfaceColumn = mouse.Position.Column - surface.Left;
        surfaceRow = mouse.Position.Row - surface.Top;
        return true;
    }

    /// <summary>
    /// Pulses the transient <c>:modal-attention</c> pseudo-class on the gate's root and raises
    /// <see cref="Window.ModalAttentionEvent"/> for code-behind; a frame-aligned <see cref="UITimer"/>
    /// clears the cue ~600 ms later (§8.6). A re-pulse re-arms the timer; the prior gate is cleared first.
    /// </summary>
    private void PulseModalAttention(Window gate)
    {
        if (_modalAttentionGate is {} previous && !ReferenceEquals(previous, gate))
            previous.SetModalAttention(false);

        _modalAttentionGate = gate;
        gate.SetModalAttention(true);
        gate.RaiseEvent(new RoutedEventArgs(Window.ModalAttentionEvent, gate));

        _modalAttentionTimer?.Stop();

        _modalAttentionTimer = UITimer.Start(TimeSpan.FromMilliseconds(600),
                                             () =>
                                             {
                                                 _modalAttentionGate?.SetModalAttention(false);
                                                 _modalAttentionGate = null;
                                             });
    }
}

/// <summary>
/// One composited layer's contribution at a sampled screen cell (see <see cref="WindowManager.SampleCell"/>),
/// carrying the layer's z-order (bottom→top) and its composite <see cref="Parameters"/>. <see cref="Cell"/> is
/// <see langword="null"/> when the sampled screen coordinate falls outside this layer's footprint.
/// </summary>
internal readonly record struct LayerCellSample(int SurfaceZ, CompositeParameters Parameters, Cell? Cell);