using Cursorial.Output;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

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
    public static Scene Create(int columns, int rows)
    {
        // Bounds, dirty regions, and fragment footprints all flow through Rect, whose coordinates are
        // ushort — a scene wider/taller than ushort.MaxValue would silently truncate and corrupt layout.
        // Cap it explicitly rather than fail subtly downstream.
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue);
        return new(new CellBuffer(columns, rows), null);
    }

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
    /// Reads the composited cell at (<paramref name="column"/>, <paramref name="row"/>) in this scene's own
    /// local space — for inspection/diagnostics (e.g. sampling what a layer contributes at a screen cell).
    /// Bounds are the caller's responsibility (see <see cref="Columns"/> / <see cref="Rows"/>).
    /// </summary>
    public Cell GetCell(int column, int row) => _buffer[column, row];

    /// <summary>
    /// Monotonic counter bumped each time <see cref="Draw"/> actually re-rasters (never on a
    /// clean-scene no-op). Snapshot it before a compositing pass and compare after: a stable
    /// version means the raster content is unchanged and prior composite output remains valid —
    /// the change signal that survives <see cref="IsDirty"/> being reset by <see cref="Draw"/>
    /// before compositing runs (<see cref="SceneCompositor"/> relies on exactly this).
    /// </summary>
    public long RasterVersion => _rasterVersion;

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
        context.FlushDeferred();   // resolve deferred sub-cell layers (braille data, box strokes) to cells
        _dirty = false;
        _rasterVersion++;
    }

    internal void ClearToTransparent()
    {
        // CellBuffer.Clear() fills its default style (opaque); we need transparent so unpainted
        // cells composite to the backdrop. Default blend mode + an opaque-free fill hits the
        // Array.Fill fast path. Grapheme stays null = "no glyph contribution" for the compositor.
        _buffer.Fill(new Cell(null, CellKind.Single, Style.Transparent));

        // A wipe empties the FRAGMENT registry too: a raster fully re-registers what it draws, so
        // anything not re-added is gone. Leaving stale entries here kept a deleted sized-text
        // emission alive end to end — the compositor faithfully carried it to the target every
        // frame, so the renderer never saw it vanish and the glyphs lingered on the terminal
        // (deleting ALL text in a sized TextBox left every line's last emission standing).
        _buffer.ClearFragments();
    }

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
