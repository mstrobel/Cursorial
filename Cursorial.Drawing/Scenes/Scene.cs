using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// A drawing surface backed by its own <see cref="CellBuffer"/>, cleared to
/// <see cref="Style.Transparent"/> so unpainted cells contribute nothing when the scene is later
/// composited (see <see cref="SceneCompositor"/>). A scene is the unit of <b>cached raster</b>:
/// its drawn cells persist, and <see cref="Draw"/> re-rasters only when the owner has marked it
/// dirty via <see cref="Invalidate"/>. The expensive work (gradient sampling, junction resolution,
/// text layout, curve interpolation) lives behind that gate, so a scene whose content hasn't
/// changed costs nothing to "re-draw".
/// </summary>
/// <remarks>
/// The drawing layer is memoryless — it does not record draw operations, so it cannot auto-detect
/// content change or re-flow on resize. Invalidation is the owner's responsibility, and coarse
/// (whole-scene). A scene composites onto a target; it does not know where it sits.
/// </remarks>
public sealed class Scene : IDisposable
{
    private readonly CellBuffer _buffer;
    private readonly ScenePool? _pool;
    private bool _dirty = true;
    private bool _disposed;
    private long _rasterVersion;

    internal Scene(CellBuffer buffer, ScenePool? pool)
    {
        _buffer = buffer;
        _pool = pool;
        ClearToTransparent();
    }

    /// <summary>Create a standalone scene of the given cell dimensions (not pooled).</summary>
    public static Scene Create(int columns, int rows) => new(new CellBuffer(columns, rows), null);

    /// <summary>Width of the scene in cells.</summary>
    public int Columns => _buffer.Columns;

    /// <summary>Height of the scene in cells.</summary>
    public int Rows => _buffer.Rows;

    /// <summary>The scene's bounds, anchored at its own (0, 0).</summary>
    public Rect Bounds => new(0, 0, Columns, Rows);

    /// <summary>True when the scene needs re-rastering on the next <see cref="Draw"/>.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Mark the scene for re-raster on the next <see cref="Draw"/>. Owner-driven, coarse.</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>The backing buffer (drawing-layer internal). Always the public <see cref="CellBuffer"/>.</summary>
    internal CellBuffer Buffer => _buffer;

    /// <summary>
    /// Monotonic counter bumped each time <see cref="Draw"/> actually re-rasters. The compositor
    /// compares it to detect a content change even though <see cref="IsDirty"/> is reset by
    /// <see cref="Draw"/> before compositing runs.
    /// </summary>
    internal long RasterVersion => _rasterVersion;

    /// <summary>
    /// Re-raster the scene if dirty: wipe to transparent, run <paramref name="draw"/>, mark clean,
    /// and bump <see cref="RasterVersion"/>. A no-op when not dirty (the cached raster is reused).
    /// </summary>
    public void Draw(Action<DrawingContext> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dirty) return;

        ClearToTransparent();
        var context = new DrawingContext(this);
        draw(context);
        context.FlushDeferredStrokes();   // resolve deferred pen strokes (junctions, glyphs) to cells
        _dirty = false;
        _rasterVersion++;
    }

    internal void ClearToTransparent() =>
        // CellBuffer.Clear() fills its default style (opaque); we need transparent so unpainted
        // cells composite to the backdrop. Default blend mode + an opaque-free fill hits the
        // Array.Fill fast path. Grapheme stays null = "no glyph contribution" for the compositor.
        _buffer.Fill(new Cell(null, CellKind.Single, Style.Transparent));

    /// <summary>
    /// Return a pooled scene's buffer to its pool (no-op for a standalone scene). Idempotent — a
    /// double dispose does not return the buffer twice (which would alias it across two future
    /// <see cref="ScenePool.Rent"/> callers).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool?.Return(this);
    }
}
