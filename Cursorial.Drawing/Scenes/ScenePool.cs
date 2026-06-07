using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// Recycles <see cref="Scene"/> backing buffers so transient (per-frame) scenes don't churn
/// allocations. <see cref="Rent"/> reuses a freed buffer (resizing it) when one is available;
/// <see cref="Scene.Dispose"/> returns the buffer here. Persistent (cached) scenes are owner-held
/// and created via <see cref="Scene.Create"/> instead.
/// </summary>
/// <remarks>
/// Phase 1 keeps the pool deliberately simple (a single free list, resized on rent). Size-bucketing
/// to avoid reallocation on every rent is a later refinement. Not thread-safe — rent/return from a
/// single render loop.
/// </remarks>
public sealed class ScenePool
{
    private readonly Stack<CellBuffer> _free = new();

    /// <summary>Rent a transparent scene of the given dimensions, reusing a pooled buffer if available.</summary>
    public Scene Rent(int columns, int rows)
    {
        if (_free.TryPop(out var buffer))
        {
            if (buffer.Columns != columns || buffer.Rows != rows)
                buffer.Resize(columns, rows);

            return new Scene(buffer, this);   // ctor re-clears to transparent
        }

        return new Scene(new CellBuffer(columns, rows), this);
    }

    internal void Return(Scene scene) => _free.Push(scene.Buffer);
}
