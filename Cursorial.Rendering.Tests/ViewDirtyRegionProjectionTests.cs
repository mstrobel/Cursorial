using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// <see cref="CellBufferView.DirtyRegions"/> projects the backing buffer's damage into view-local
/// coordinates. On a re-based view (<see cref="CellBufferView.WithOrigin"/>) part of the window maps
/// to NEGATIVE local coordinates.
/// </summary>
/// <remarks>
/// That used to be inexpressible — <c>Rect</c> forbade a negative origin — so the projection clamped
/// the intersection to zero, which silently truncated a straddling region and DROPPED one lying
/// wholly in the negative part. Damage that is dropped is damage that is never repainted, so the
/// clamp was a correctness bug wearing a representational excuse. With the origin restriction gone
/// the honest answer is expressible, and these pin it.
/// </remarks>
public class ViewDirtyRegionProjectionTests
{
    // A 10-wide window at backing column 4, re-based to origin 8: local coordinates run [-4, 6).
    private const int WindowColumn = 4, WindowColumns = 10, Origin = 8;

    private static CellBufferView Rebased(CellBuffer buffer) =>
        buffer.View(new Rect(WindowColumn, 0, WindowColumns, 2)).WithOrigin(Origin, 0);

    [Fact]
    public void RegionStraddlingTheLocalOrigin_ProjectsInFull_NotTruncatedAtZero()
    {
        var buffer = new CellBuffer(20, 2);
        var view = Rebased(buffer);

        buffer.MarkDirty(new Rect(WindowColumn, 0, WindowColumns, 2)); // the whole window

        // Local [-4, 6): the full 10 columns, starting 4 to the LEFT of the local origin.
        var projected = Assert.Single(view.DirtyRegions);
        Assert.Equal(new Rect(-4, 0, 10, 2), projected);
    }

    [Fact]
    public void RegionEntirelyLeftOfTheLocalOrigin_IsProjected_NotDropped()
    {
        var buffer = new CellBuffer(20, 2);
        var view = Rebased(buffer);

        // Backing [4, 7) maps to local [-4, -1) — wholly negative. The clamp dropped this outright,
        // which is the shape that loses a repaint rather than merely shrinking one.
        buffer.MarkDirty(new Rect(WindowColumn, 0, 3, 2));

        var projected = Assert.Single(view.DirtyRegions);
        Assert.Equal(new Rect(-4, 0, 3, 2), projected);
    }

    [Fact]
    public void RegionOutsideTheWindow_IsStillDropped()
    {
        var buffer = new CellBuffer(20, 2);
        var view = Rebased(buffer);

        // Backing [15, 18) is past the window's [4, 14) — genuinely nothing to project. The window
        // clip must survive; only the ORIGIN clamp was wrong.
        buffer.MarkDirty(new Rect(15, 0, 3, 2));

        Assert.Empty(view.DirtyRegions);
    }

    [Fact]
    public void OnAnUnrebasedView_ProjectionIsUnchanged()
    {
        var buffer = new CellBuffer(20, 2);
        var view = buffer.View(new Rect(WindowColumn, 0, WindowColumns, 2));

        buffer.MarkDirty(new Rect(6, 0, 4, 2));

        // No re-basing: local == window-relative, all non-negative, exactly as before.
        var projected = Assert.Single(view.DirtyRegions);
        Assert.Equal(new Rect(2, 0, 4, 2), projected);
    }
}
