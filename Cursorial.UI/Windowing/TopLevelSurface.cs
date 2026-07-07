using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// A top-level rendering surface (design doc §8.5): one root element tree wrapped in an S1
/// <see cref="RenderTree"/> + <see cref="LayoutManager"/>, positioned at a screen offset. There is one
/// <see cref="Scene"/> per render boundary — never a whole-surface rasterizer; raster scheduling, zone
/// scenes, and intra-surface hit testing are S1's. The window manager owns the stack of surfaces and
/// concatenates each surface's <see cref="CollectLayers"/> output — translated to its screen offset and
/// scaled by its opacity — into one <see cref="SceneCompositor.Composite"/> call (so window movement is
/// a parameters-only change).
/// </summary>
/// <remarks>
/// A surface is one of three kinds, distinguished without subclassing:
/// <list type="bullet">
/// <item>the chrome-less <b>application root</b> (<see cref="HostWindow"/> <c>== null</c>, not a popup) —
/// also the inline / non-alt-screen case, which is just "render this tree into a region" with no window
/// semantics;</item>
/// <item>a shown <c>Window</c>'s content (<see cref="HostWindow"/> set — P7-W1);</item>
/// <item>an open <c>Popup</c>'s child (<see cref="IsPopup"/> — P7-W4).</item>
/// </list>
/// </remarks>
public sealed class TopLevelSurface
{
    private readonly LayoutManager _layout;
    private readonly ScenePool _scenePool;
    private Size _size;
    private bool _mightNeedRaiseContentRendered = true;
    private bool _needsLayout = true;
    private bool _detached;

    // The pooled scene the drop-shadow fringe rasters into — the surface's LOWEST layer (painted beneath its own
    // content, over whatever is behind it). Kept across frames and re-rastered only when the shadow or content
    // size changes (a window MOVE is composite-only — just its offset shifts). Released to the pool on detach or
    // when the shadow turns off. Sized to the content rect grown by Shadow.GetMargins().
    private Scene? _shadowScene;
    private WindowShadow _shadowSignature; // the shadow the live _shadowScene was rastered for
    private Size _shadowContentSize;       // the content size the live _shadowScene was rastered for

    internal TopLevelSurface(UIElement root, ScenePool scenePool, OutputCapabilities capabilities, IUserCodeGuard? guard)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(scenePool);
        ArgumentNullException.ThrowIfNull(capabilities);

        Root = root;
        _scenePool = scenePool;
        _layout = new LayoutManager(root);
        RenderTree = new RenderTree(root, scenePool, capabilities) { UserCodeGuard = guard };
    }

    /// <summary>The element tree this surface hosts.</summary>
    public UIElement Root { get; }

    /// <summary>The surface's render tree (one <see cref="Scene"/> per render boundary, §8.5).</summary>
    public RenderTree RenderTree { get; }

    /// <summary>The shown <see cref="Window"/> hosting this surface, or <see langword="null"/> for the chrome-less application root.</summary>
    public Window? HostWindow { get; internal set; }

    /// <summary>The surface's screen-space left column (0 for the application root).</summary>
    public int Left { get; internal set; }

    /// <summary>The surface's screen-space top row (0 for the application root).</summary>
    public int Top { get; internal set; }

    /// <summary>The surface's size — both the layout constraint and the content rect used for hit testing.</summary>
    public Size Size
    {
        get => _size;
        internal set
        {
            if (_size == value)
                return;
            _size = value;
            _needsLayout = true;
        }
    }

    /// <summary>The composite opacity applied to every layer of this surface (host window / popup opacity).</summary>
    public double Opacity { get; internal set; } = 1.0;

    /// <summary>
    /// The surface's drop shadow (design doc §8.2/§8.7); <see cref="WindowShadow.None"/> casts nothing (the
    /// default). The window manager syncs it from the host <c>Window</c>/<c>Popup</c>; it paints into a fringe
    /// band beyond the content rect, composited <b>below</b> the surface's own layers.
    /// </summary>
    public WindowShadow Shadow { get; internal set; }

    /// <summary>True when this surface is an open popup's child — a light-dismiss participant (P7-W4).</summary>
    public bool IsPopup { get; internal set; }

    /// <summary>True when point hit-testing and the light-dismiss "outside" tests skip this surface (P7-W4).</summary>
    public bool IsHitTestTransparent { get; internal set; }

    internal bool HasPendingLayout => _needsLayout || _layout.HasQueuedWork;

    internal bool HasDirtyVisuals => RenderTree.HasPendingRenderWork;

    /// <summary>True when the screen point (<paramref name="column"/>,<paramref name="row"/>) lies in the surface's content rect.</summary>
    public bool Contains(int column, int row)
        => column >= Left && row >= Top && column < Left + _size.Columns && row < Top + _size.Rows;

    /// <summary>Runs the surface's layout pass at its allotted size; abandons a non-converging pass (doc §10.5).</summary>
    internal void RunLayoutPass()
    {
        _needsLayout = false;
        _layout.RunLayoutPass(_size);
        if (_layout.HasQueuedWork)
            _layout.AbandonPendingLayout();
    }

    /// <summary>Re-rasters dirty zones and refreshes composite parameters (the per-surface render pass), then
    /// reconciles the drop-shadow fringe scene (§8.2).</summary>
    internal void RunRenderPass()
    {
        RenderTree.RunRenderPass();
        UpdateShadowScene();
        MaybeRaiseContentRendered();
    }

    private void MaybeRaiseContentRendered()
    {
        if (_mightNeedRaiseContentRendered is false)
            return;

        _mightNeedRaiseContentRendered = false;

        if (Root is Window window)
            window.RaiseContentRendered();
        else if (Root is { UIParent: Popup popup })
            popup.RaiseContentRendered();
    }

    /// <summary>Appends this surface's layers — translated to its screen offset, scaled by its opacity — to <paramref name="target"/>.</summary>
    /// <param name="target">The shared layer list the compositor concatenates across surfaces.</param>
    /// <param name="surfaceZ">This surface's z-index in the stack (stamped on every layer for the compositor's fragment-occlusion).</param>
    /// <param name="isOccluder">Whether this surface is an opaque occluder (a window/popup/badge — not the root).</param>
    /// <param name="boundaryDescriptions">An optional list into which descriptions of the surface's boundaries are written.</param>
    internal void CollectLayers(List<SceneLayer> target, int surfaceZ = 0, bool isOccluder = false, List<string>? boundaryDescriptions = null)
    {
        // The shadow fringe is the LOWEST layer of the surface — emit it first (earlier == composited lower),
        // at the content offset pulled back by the cast margins so the fringe lands just outside the content
        // rect. It is never an occluder: a translucent tint must let a lower surface's graphics-protocol image
        // show through (dimmed), not crop it. Opacity tracks the surface so a fading window fades its shadow.
        if (_shadowScene is {} shadow)
        {
            var margins = Shadow.GetMargins();

            var parameters = new CompositeParameters(
                Left - margins.Left, Top - margins.Top,
                (byte) Math.Round(Math.Clamp(Opacity, 0.0, 1.0) * 255.0));

            target.Add(new SceneLayer(shadow, parameters) { SurfaceZ = surfaceZ, IsOccluder = false });

            if (boundaryDescriptions is not null)
            {
                var description = Root.GetType().Name;

                if (Root.Name is { Length: > 0 } name)
                    description += $"#{name}";

                boundaryDescriptions.Add(description);
            }
        }

        RenderTree.CollectLayers(target, Left, Top, Opacity, surfaceZ, isOccluder, boundaryDescriptions);
    }

    // Shadows read as a soft tint only through RGB-on-RGB alpha compositing — a non-RGB backdrop short-circuits
    // to the source (the translucent shadow would paint SOLID). So they emit only when the EFFECTIVE color tier
    // is truecolor — the same gate RenderTree applies to surface opacity. (A non-RGB shadow Color is its own
    // no-op inside DrawDropShadow, so a palette accent never leaks through.)
    private static bool ShadowsEnabled => UIApplication.Current?.ActualThemeVariant is not { Tier: not ColorDepth.Truecolor };

    /// <summary>
    /// Reconciles the pooled shadow-fringe scene with the current <see cref="Shadow"/> + content size: rents and
    /// sizes it to the content rect grown by <see cref="WindowShadow.GetMargins"/>, re-rastering the soft drop
    /// only when the shadow or size changed (a window MOVE leaves the raster untouched — only its composite
    /// offset shifts). Releases the scene when there is no shadow to paint or the effective tier can't render one.
    /// </summary>
    private void UpdateShadowScene()
    {
        if (Shadow.IsNone || !ShadowsEnabled || _size.Columns <= 0 || _size.Rows <= 0)
        {
            ReleaseShadowScene();
            return;
        }

        var margins = Shadow.GetMargins();

        if (margins == Margins.Zero)
        {
            ReleaseShadowScene(); // a degenerate geometry (no fringe to grow into) casts nothing
            return;
        }

        int columns = _size.Columns + margins.Horizontal;
        int rows = _size.Rows + margins.Vertical;

        // A size change re-rents from the exact-size-bucketed pool; the signature drives the re-raster gate.
        if (_shadowScene is {} existing && (existing.Columns != columns || existing.Rows != rows))
            ReleaseShadowScene();

        if (_shadowScene is null)
        {
            _shadowScene = _scenePool.Rent(columns, rows);
            _shadowSignature = default;
            _shadowContentSize = default;
        }

        if (_shadowSignature == Shadow && _shadowContentSize == _size)
            return; // unchanged — reuse the cached raster (the move-only case costs nothing here)

        var element = new Rect(margins.Left, margins.Top, _size.Columns, _size.Rows);
        var geometry = Shadow.Geometry;
        var color = Shadow.Color;

        _shadowScene.Invalidate();
        _shadowScene.Draw(context => context.DrawDropShadow(element, geometry, color));

        _shadowSignature = Shadow;
        _shadowContentSize = _size;
    }

    private void ReleaseShadowScene()
    {
        _shadowScene?.Dispose(); // returns the buffer to the pool
        _shadowScene = null;
        _shadowSignature = default;
        _shadowContentSize = default;
    }

    /// <summary>Hit-tests a screen point against this surface, returning the element or <see langword="null"/> when outside the content rect.</summary>
    public UIElement? HitTest(int column, int row)
        => Contains(column, row) ? RenderTree.HitTest(column - Left, row - Top) : null;

    /// <summary>Re-rasters everything (the resize / renegotiate leg).</summary>
    internal void InvalidateAll() => RenderTree.InvalidateAll();

    /// <summary>
    /// Fully detaches the surface's root: returns its scenes to the pool (<see cref="RenderTree.Detach"/>)
    /// AND reverses <c>AttachAsRoot</c> (detach walk → clears the root's <c>VisualRoot</c>/LayoutManager), so
    /// the root is no longer attached and can be re-hosted on a fresh surface — a popup reopen. Dropping only
    /// the render tree (the prior behavior) left the root half-attached, and re-hosting it threw "already
    /// attached to a tree". Idempotent.
    /// </summary>
    internal void Detach()
    {
        if (_detached)
            return;

        _detached = true;
        ReleaseShadowScene();
        Root.DetachRoot(); // DetachRoot calls RenderTree.Detach itself, then detaches the element
    }
}
