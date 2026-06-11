using System.Runtime.InteropServices;

using Cursorial.Drawing;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The P1 <see cref="IRenderSystem"/>: renders <b>one</b> root element tree onto a single
/// full-screen surface. Owns the <see cref="SceneCompositor"/> + <see cref="ScenePool"/> (per the
/// §13.2 compositor-ownership resolution — S6 owns only the target <c>CellBuffer</c> and
/// <c>FrameRenderer</c>), the root's <see cref="RenderTree"/>, and the caret write.
/// </summary>
/// <remarks>
/// <b>This type is the P1 stand-in that S4's <c>WindowManager</c> replaces at P7.</b> Windowing
/// does not exist yet: there is exactly one layer stack from one <see cref="RenderTree"/>, no
/// window offsets, no per-window opacity, no z-ordered surfaces. At P7 one S4 object implements
/// <see cref="IRenderSystem"/> (plus <c>IWindowSystem</c>) wrapping one <c>TopLevelSurface</c>/
/// <c>RenderTree</c> per window and concatenating their layers in window z-order into the same
/// single <see cref="SceneCompositor.Composite"/> call; the frame loop consumes the
/// <see cref="IRenderSystem"/> seam unchanged.
/// </remarks>
public sealed class SingleRootRenderSystem : IRenderSystem
{
    private readonly ScenePool _scenePool = new();
    private readonly List<SceneLayer> _layers = [];
    private readonly TerminalCaretService _caretService;
    private readonly IUserCodeGuard? _guard;
    private SceneCompositor _compositor = new();
    private OutputCapabilities _capabilities;
    private RenderTree? _tree;
    private TerminalCaretState _lastCaret;
    private bool _caretEverApplied;

    /// <summary>
    /// Creates the render system. <paramref name="guard"/> is the user-code funnel routed to every
    /// element <c>Render</c> override (design doc §10.8); null means draw exceptions propagate raw
    /// (test harnesses that want the throw).
    /// </summary>
    public SingleRootRenderSystem(OutputCapabilities capabilities, TerminalCaretService caretService, IUserCodeGuard? guard = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(caretService);

        _capabilities = capabilities;
        _caretService = caretService;
        _guard = guard;
    }

    /// <summary>The caret registry consumed at frame assembly (S1's §5.9 service).</summary>
    public TerminalCaretService CaretService => _caretService;

    /// <summary>The root's render tree (test observability; null until a root is set).</summary>
    public RenderTree? Tree => _tree;

    /// <inheritdoc/>
    public bool HasDirtyVisuals => _tree?.HasPendingRenderWork ?? false;

    /// <inheritdoc/>
    public bool RenderFrame(CellBuffer target, in FrameTime time)
    {
        ArgumentNullException.ThrowIfNull(target);

        var changed = false;
        if (_tree is not null)
        {
            _tree.RunRenderPass();
            if (_guard is { IsFatal: true })
                return true; // unwind promptly; conservative changed — teardown is imminent anyway

            _layers.Clear();
            _tree.CollectLayers(_layers);
            changed = _compositor.Composite(CollectionsMarshal.AsSpan(_layers), new CellBufferView(target));
        }

        // The caret write (T4 contract): transform after layout + parameters are final, copy onto
        // the buffer's cursor fields; FrameRenderer owns emitting only what changed on the wire.
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
        // The resize transaction (design doc §10.6): fresh compositor (its retained per-layer state
        // is sized to the old target) + full re-raster; the same frame's Phase 5 re-lays-out the
        // root under the new constraint, which re-rents band/bounds-sized scenes as needed.
        _compositor = new SceneCompositor();
        _tree?.InvalidateAll();
    }

    /// <summary>
    /// Wires <paramref name="root"/> as the rendered tree (the P1 stand-in for S4's
    /// <c>Show(Window)</c>). The root must already be attached via its <see cref="LayoutManager"/>.
    /// Passing null detaches the current tree.
    /// </summary>
    internal void SetRoot(UIElement? root)
    {
        _tree?.Detach();
        _tree = null;
        if (root is not null)
        {
            _tree = new RenderTree(root, _scenePool, _capabilities) { UserCodeGuard = _guard };
        }

        // The layer stack changed wholesale — the compositor's count-change path full-recomposites,
        // but its retained state must not pair old scenes with new slots.
        _compositor = new SceneCompositor();
    }

    /// <summary>
    /// The renegotiation leg: re-stamps capabilities on the tree, resets the compositor, and marks
    /// everything for re-raster so the first post-renegotiate frame repaints fully.
    /// </summary>
    internal void OnCapabilitiesChanged(OutputCapabilities capabilities)
    {
        _capabilities = capabilities;
        if (_tree is not null)
        {
            _tree.Capabilities = capabilities;
            _tree.InvalidateAll();
        }

        _compositor = new SceneCompositor();
    }
}
