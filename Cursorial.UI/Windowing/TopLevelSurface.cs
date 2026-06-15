using System;
using System.Collections.Generic;

using Cursorial.Drawing;
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
    private Size _size;
    private bool _needsLayout = true;

    internal TopLevelSurface(UIElement root, ScenePool scenePool, OutputCapabilities capabilities, IUserCodeGuard? guard)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(scenePool);
        ArgumentNullException.ThrowIfNull(capabilities);

        Root = root;
        _layout = new LayoutManager(root);
        RenderTree = new RenderTree(root, scenePool, capabilities) { UserCodeGuard = guard };
    }

    /// <summary>The element tree this surface hosts.</summary>
    public UIElement Root { get; }

    /// <summary>The surface's render tree (one <see cref="Scene"/> per render boundary, §8.5).</summary>
    public RenderTree RenderTree { get; }

    /// <summary>The shown <c>Window</c> hosting this surface, or <see langword="null"/> for the chrome-less application root (set at P7-W1).</summary>
    public object? HostWindow { get; internal set; }

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

    /// <summary>Re-rasters dirty zones and refreshes composite parameters (the per-surface render pass).</summary>
    internal void RunRenderPass() => RenderTree.RunRenderPass();

    /// <summary>Appends this surface's layers — translated to its screen offset, scaled by its opacity — to <paramref name="target"/>.</summary>
    internal void CollectLayers(List<SceneLayer> target) => RenderTree.CollectLayers(target, Left, Top, Opacity);

    /// <summary>Hit-tests a screen point against this surface, returning the element or <see langword="null"/> when outside the content rect.</summary>
    public UIElement? HitTest(int column, int row)
        => Contains(column, row) ? RenderTree.HitTest(column - Left, row - Top) : null;

    /// <summary>Re-rasters everything (the resize / renegotiate leg).</summary>
    internal void InvalidateAll() => RenderTree.InvalidateAll();

    /// <summary>Returns the surface's scenes to the pool (detach).</summary>
    internal void Detach() => RenderTree.Detach();
}
