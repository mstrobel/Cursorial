using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI;

/// <summary>
/// The <see cref="GridSplitter"/> (#111): a <see cref="Thumb"/>-derived divider that redistributes cells between two
/// adjacent <see cref="Grid"/> tracks. Covers column + row drag (PreviousAndNext), Min/Max clamping with size
/// conservation, star-unit preservation, keyboard resize, the resolved resize cursor, and the not-in-a-grid no-op.
/// </summary>
public class GridSplitterTests
{
    // A 3-column grid [left | splitter(1) | right] exactly filling a `width`-wide host; the splitter resizes the
    // outer two columns (PreviousAndNext). Returns the grid + splitter for driving the drag.
    private static (UITestHost Host, Grid Grid, GridSplitter Splitter) MakeColumns(
        int width = 21, int left = 10, int right = 10, int rightMin = 0, int rightMax = LayoutMathUnbounded)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(width, 6) });
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = width, Height = 6,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(left)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(1)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(right)) { MinWidth = rightMin, MaxWidth = rightMax });

        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        host.ShowRoot(grid);
        host.RunUntilIdle();
        return (host, grid, splitter);
    }

    private const int LayoutMathUnbounded = int.MaxValue; // mirrors LayoutMath.Unbounded (internal)

    private static MouseEvent Mouse(MouseEventKind kind, int c, int r, MouseButton button, MouseButtons held) => new()
    {
        Kind = kind, Position = new CellPosition(c, r), Button = button, ButtonsHeld = held,
        Modifiers = KeyModifiers.None, Timestamp = default,
    };

    private static void Drag(UITestHost host, GridSplitter splitter, int dx, int dy)
    {
        var (col, row) = splitter.TranslateToWindow(0, 0);
        host.SendInput(Mouse(MouseEventKind.ButtonDown, col, row, MouseButton.Left, MouseButtons.None));
        host.RunUntilIdle();
        host.SendInput(Mouse(MouseEventKind.Move, col + dx, row + dy, MouseButton.None, MouseButtons.Left));
        host.RunUntilIdle();
        host.SendInput(Mouse(MouseEventKind.ButtonUp, col + dx, row + dy, MouseButton.Left, MouseButtons.None));
        host.RunUntilIdle();
    }

    [Fact]
    public void ColumnDrag_RedistributesBetweenNeighbors_ConservingTotal()
    {
        var (host, grid, splitter) = MakeColumns();
        using var _ = host;

        Assert.Equal(10, grid.ColumnDefinitions[0].ActualWidth);
        Assert.Equal(10, grid.ColumnDefinitions[2].ActualWidth);

        Drag(host, splitter, dx: 3, dy: 0); // pull the divider right 3 cells

        Assert.Equal(13, grid.ColumnDefinitions[0].ActualWidth); // left grows
        Assert.Equal(7, grid.ColumnDefinitions[2].ActualWidth);  // right shrinks; 13 + 7 == 20 conserved
    }

    [Fact]
    public void ColumnDrag_HonorsNextMin_PrevPinnedSoPartnerStaysAboveMin()
    {
        var (host, grid, splitter) = MakeColumns(rightMin: 5);
        using var _ = host;

        Drag(host, splitter, dx: 100, dy: 0); // drag far right — the right column would collapse past its Min

        Assert.Equal(5, grid.ColumnDefinitions[2].ActualWidth);  // clamped at MinWidth
        Assert.Equal(15, grid.ColumnDefinitions[0].ActualWidth); // left pinned so the pair total (20) holds
    }

    [Fact]
    public void ColumnDrag_HonorsNextMax()
    {
        var (host, grid, splitter) = MakeColumns(rightMax: 12);
        using var _ = host;

        Drag(host, splitter, dx: -100, dy: 0); // drag far left — the right column would exceed its Max

        Assert.Equal(12, grid.ColumnDefinitions[2].ActualWidth); // clamped at MaxWidth
        Assert.Equal(8, grid.ColumnDefinitions[0].ActualWidth);
    }

    [Fact]
    public void RowDrag_RedistributesRows()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(8, 21) });
        using var _ = host;
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = 8, Height = 21,
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.FromCells(10)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.FromCells(1)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.FromCells(10)));

        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        grid.Children.Add(splitter);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Drag(host, splitter, dx: 0, dy: 4); // pull the divider down 4 rows

        Assert.Equal(14, grid.RowDefinitions[0].ActualHeight);
        Assert.Equal(6, grid.RowDefinitions[2].ActualHeight);
    }

    [Fact]
    public void StarColumns_StayStar_RatioReflectsTheDrag()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(21, 6) });
        using var _ = host;
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = 21, Height = 6,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star())); // 1* — 10 cells
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(1)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star())); // 1* — 10 cells
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Drag(host, splitter, dx: 4, dy: 0);

        Assert.True(grid.ColumnDefinitions[0].Width.IsStar); // unit type preserved
        Assert.True(grid.ColumnDefinitions[2].Width.IsStar);
        Assert.Equal(14, grid.ColumnDefinitions[0].ActualWidth);
        Assert.Equal(6, grid.ColumnDefinitions[2].ActualWidth);
    }

    [Fact]
    public void Keyboard_RightArrow_GrowsPreviousByIncrement()
    {
        var (host, grid, splitter) = MakeColumns();
        using var _ = host;
        splitter.KeyboardIncrement = 2;
        splitter.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();

        Assert.Equal(12, grid.ColumnDefinitions[0].ActualWidth); // +2
        Assert.Equal(8, grid.ColumnDefinitions[2].ActualWidth);
    }

    [Fact]
    public void ResolvedCursor_ReflectsAxis()
    {
        var (hostC, _, splitterC) = MakeColumns();
        using (hostC)
            Assert.Equal(MouseCursorShape.ColResize, splitterC.Cursor);

        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(8, 21) });
        using var _ = host;
        var grid = new Grid { Width = 8, Height = 21, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.FromCells(10)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.FromCells(1)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.FromCells(10)));
        var splitterR = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitterR, 1);
        grid.Children.Add(splitterR);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Assert.Equal(MouseCursorShape.RowResize, splitterR.Cursor);
    }

    [Fact]
    public void NotInAGrid_DragIsHarmlessNoOp()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(10, 4) });
        using var _ = host;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = 4, Height = 2 };
        panel.Children.Add(splitter);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Drag(host, splitter, dx: 3, dy: 0); // no grid to resize — must not throw
    }

    [Fact] // the PreviousAndNext pair at column 0 has no "previous" (prevIndex -1) — the resolve aborts, drag no-ops
    public void EdgeIndex_NoPreviousColumn_DragIsNoOp()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(21, 6) });
        using var _ = host;
        var grid = new Grid { Width = 21, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(10)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(11)));
        // Splitter at column 0 with the default PreviousAndNext behavior ⇒ prevIndex -1 ⇒ no valid pair.
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 0);
        grid.Children.Add(splitter);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Drag(host, splitter, dx: 5, dy: 0);

        Assert.Equal(10, grid.ColumnDefinitions[0].ActualWidth); // unchanged — no valid pair to resize
        Assert.Equal(11, grid.ColumnDefinitions[1].ActualWidth);
    }

    [Fact] // CurrentAndNext: the splitter's OWN column + the next one (the edge-docked idiom)
    public void CurrentAndNext_ResizesOwnAndNext()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(20, 6) });
        using var _ = host;
        var grid = new Grid { Width = 20, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(8)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(12)));
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
        Grid.SetColumn(splitter, 0); // the splitter shares column 0 with content (edge-docked); resizes col0 + col1
        grid.Children.Add(splitter);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Drag(host, splitter, dx: 3, dy: 0);

        Assert.Equal(11, grid.ColumnDefinitions[0].ActualWidth); // own grows
        Assert.Equal(9, grid.ColumnDefinitions[1].ActualWidth);  // next shrinks; 11 + 9 == 20 conserved
    }

    [Fact] // ResizeDirection.Auto with no alignment resolves to Columns for a tall-thin splitter (aspect tiebreak)
    public void AutoDirection_TallThinSplitter_ResolvesToColumns()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(21, 6) });
        using var _ = host;
        var grid = new Grid { Width = 21, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(10)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(1)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.FromCells(10)));
        var splitter = new GridSplitter(); // ResizeDirection defaults to Auto; alignment defaults to Stretch
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        host.ShowRoot(grid);
        host.RunUntilIdle();

        Assert.Equal(MouseCursorShape.ColResize, splitter.Cursor); // Auto → Columns (1 cell wide ≤ 6 rows tall)

        Drag(host, splitter, dx: 3, dy: 0);

        Assert.Equal(13, grid.ColumnDefinitions[0].ActualWidth);
        Assert.Equal(7, grid.ColumnDefinitions[2].ActualWidth);
    }
}
