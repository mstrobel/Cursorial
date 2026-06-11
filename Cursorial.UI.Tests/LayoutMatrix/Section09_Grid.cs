using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Layout matrix §9 — Grid (L130–L159). Star algorithm per the pinned integer Hamilton policy
/// (floors, leftover to largest fractional parts, ties to lowest index; clamp re-runs to fixpoint).
/// Grid arranged via <c>Root</c> at the stated width; single <c>c3</c> row unless noted;
/// <c>ActualWidth</c> readbacks are the assertion surface for column rows.
/// </summary>
public class Section09_Grid
{
    /// <summary>A grid with a single <c>c3</c> row and the given column widths (§9 preamble fixture).</summary>
    private static Grid GridWithColumns(params GridLength[] widths)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(3));
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(width));
        return grid;
    }

    private static LayoutManager Layout(Grid grid, int columns, int rows)
    {
        var manager = LayoutFixture.CreateRoot(grid);
        manager.Layout(columns, rows);
        return manager;
    }

    private static int[] ActualWidths(Grid grid)
    {
        var widths = new int[grid.ColumnDefinitions.Count];
        for (var i = 0; i < widths.Length; i++)
            widths[i] = grid.ColumnDefinitions[i].ActualWidth;
        return widths;
    }

    [Fact]
    public void L130_NoDefinitions_BehavesAsImplicitStar_ChildSlotIsWholeGrid()
    {
        var grid = new Grid();
        var child = new Probe(4, 2);
        grid.Children.Add(child);
        Layout(grid, 20, 10);

        Assert.Equal(new Rect(0, 0, 20, 10), child.Bounds);
    }

    [Fact]
    public void L131_FixedCells_PositionCumulatively()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(3));
        grid.ColumnDefinitions.Add(new ColumnDefinition(4));
        grid.RowDefinitions.Add(new RowDefinition(2));
        grid.RowDefinitions.Add(new RowDefinition(2));
        var child = new Probe(1, 1);
        Grid.SetRow(child, 1);
        Grid.SetColumn(child, 1);
        grid.Children.Add(child);
        Layout(grid, 20, 10);

        Assert.Equal(new Rect(3, 2, 4, 2), child.Bounds);
    }

    [Fact]
    public void L132_DefinitionMinMax_ClampsFixedColumns()
    {
        var grid = GridWithColumns(10);
        grid.ColumnDefinitions[0].MaxWidth = 6;
        Layout(grid, 20, 10);

        Assert.Equal(6, grid.ColumnDefinitions[0].ActualWidth);
    }

    [Fact]
    public void L133_SlotIsTheCell_AlignmentIsTheChilds()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(4));
        grid.RowDefinitions.Add(new RowDefinition(2));
        var child = new Probe(1, 1); // Stretch defaults
        grid.Children.Add(child);
        Layout(grid, 20, 10);

        Assert.Equal(new Rect(0, 0, 4, 2), child.Bounds);
    }

    [Fact]
    public void L134_Grid_EqualStars_LargestRemainder_TiesToLowestIndex()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star(), GridLength.Star());
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 4, 3, 3 }, ActualWidths(grid));
    }

    [Fact]
    public void L135_EqualStars_TwoLeftoverCells_TwoLowestIndices()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star(), GridLength.Star());
        Layout(grid, 11, 3);

        Assert.Equal(new[] { 4, 4, 3 }, ActualWidths(grid));
    }

    [Fact]
    public void L136_EqualStars_EightOverThree()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star(), GridLength.Star());
        Layout(grid, 8, 3);

        Assert.Equal(new[] { 3, 3, 2 }, ActualWidths(grid));
    }

    [Fact]
    public void L137_WeightedStars_LeftoverToLargerFraction()
    {
        var grid = GridWithColumns(GridLength.Star(2), GridLength.Star());
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 7, 3 }, ActualWidths(grid));
    }

    [Fact]
    public void L138_LeftoverFollowsFraction_NotIndex_WhenFractionsDiffer()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star(2));
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 3, 7 }, ActualWidths(grid));
    }

    [Fact]
    public void L139_ZeroWeightStar_GetsZero()
    {
        var grid = GridWithColumns(GridLength.Star(0), GridLength.Star());
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 0, 10 }, ActualWidths(grid));
    }

    [Fact]
    public void L140_FixedPlusWeightedStars()
    {
        var grid = GridWithColumns(4, GridLength.Star(), GridLength.Star(2));
        Layout(grid, 13, 3);

        Assert.Equal(new[] { 4, 3, 6 }, ActualWidths(grid));
    }

    [Fact]
    public void L141_MinClampViolation_PinsAndRerunsOverUnclampedStar()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star());
        grid.ColumnDefinitions[0].MinWidth = 8;
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 8, 2 }, ActualWidths(grid));
    }

    [Fact]
    public void L142_MaxClampViolation_PinsAndRerunsOverUnclampedStar()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star());
        grid.ColumnDefinitions[0].MaxWidth = 2;
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 2, 8 }, ActualWidths(grid));
    }

    [Fact]
    public void L143_AllStarsClamped_TrailingCellsStayUnassigned()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star());
        grid.ColumnDefinitions[0].MaxWidth = 3;
        grid.ColumnDefinitions[1].MaxWidth = 3;
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 3, 3 }, ActualWidths(grid));
    }

    [Fact]
    public void L144_MinWinsOverAvailable_ContentOverflowsGridWidth()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star());
        grid.ColumnDefinitions[0].MinWidth = 5;
        grid.ColumnDefinitions[1].MinWidth = 5;
        var child = new Probe(1, 1);
        Grid.SetColumn(child, 1);
        grid.Children.Add(child);
        Layout(grid, 6, 3);

        Assert.Equal(new[] { 5, 5 }, ActualWidths(grid));
        Assert.Equal(new Rect(5, 0, 5, 3), child.Bounds); // col1 starts at column 5 — overflow, zone clip's job
    }

    [Fact]
    public void L145_OneClamp_OneRerun_OverRemainingStars()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star(), GridLength.Star());
        grid.ColumnDefinitions[0].MinWidth = 6;
        Layout(grid, 12, 3);

        Assert.Equal(new[] { 6, 3, 3 }, ActualWidths(grid));
    }

    [Fact]
    public void L146_Auto_SizesToContentMax()
    {
        var grid = GridWithColumns(GridLength.Auto, GridLength.Star());
        var child = new Probe(4, 1);
        grid.Children.Add(child);
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 4, 6 }, ActualWidths(grid));
    }

    [Fact]
    public void L147_EmptyAuto_IsZero()
    {
        var grid = GridWithColumns(GridLength.Auto, GridLength.Star());
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 0, 10 }, ActualWidths(grid));
    }

    [Fact]
    public void L148_AutoMax_ClampsContent()
    {
        var grid = GridWithColumns(GridLength.Auto, GridLength.Star());
        grid.ColumnDefinitions[0].MaxWidth = 3;
        var child = new Probe(5, 1);
        grid.Children.Add(child);
        Layout(grid, 10, 3);

        Assert.Equal(3, grid.ColumnDefinitions[0].ActualWidth);
    }

    [Fact]
    public void L149_AutoMin_RaisesContent()
    {
        var grid = GridWithColumns(GridLength.Auto, GridLength.Star());
        grid.ColumnDefinitions[0].MinWidth = 6;
        var child = new Probe(4, 1);
        grid.Children.Add(child);
        Layout(grid, 10, 3);

        Assert.Equal(6, grid.ColumnDefinitions[0].ActualWidth);
    }

    [Fact]
    public void L150_AutoColumnChild_MeasuredWithUnboundedOnThatAxis()
    {
        var grid = GridWithColumns(GridLength.Auto, GridLength.Star());
        var child = new Probe(4, 1);
        grid.Children.Add(child);
        Layout(grid, 10, 3);

        Assert.Equal(LayoutMath.Unbounded, child.MeasureConstraints[^1].Columns);
    }

    [Fact]
    public void L151_StarUnderUnboundedConstraint_BehavesAsAuto()
    {
        var grid = GridWithColumns(GridLength.Star());
        var child = new Probe(7, 1);
        grid.Children.Add(child);
        grid.Measure(new Size(LayoutMath.Unbounded, 3));

        Assert.Equal(7, grid.DesiredSize.Columns);
    }

    [Fact]
    public void L152_StarDesiredContribution_IsContentDesired_NotAllocation()
    {
        var grid = GridWithColumns(4, GridLength.Star());
        var child = new Probe(5, 1);
        Grid.SetColumn(child, 1);
        grid.Children.Add(child);
        grid.Measure(new Size(20, 3));

        Assert.Equal(9, grid.DesiredSize.Columns); // 4 + min(16, 5) — LD8
    }

    [Fact]
    public void L153_ColumnSpan_SlotIsSumOfSpannedColumns()
    {
        var grid = GridWithColumns(4, 6);
        var child = new Probe(1, 1);
        Grid.SetColumnSpan(child, 2);
        grid.Children.Add(child);
        Layout(grid, 20, 10);

        Assert.Equal(new Rect(0, 0, 10, 3), child.Bounds);
    }

    [Fact]
    public void L154_Span_ClampsToRemainingDefinitions()
    {
        var grid = GridWithColumns(4, 6);
        var child = new Probe(1, 1);
        Grid.SetColumn(child, 1);
        Grid.SetColumnSpan(child, 5);
        grid.Children.Add(child);
        Layout(grid, 20, 10);

        Assert.Equal(new Rect(4, 0, 6, 3), child.Bounds); // effective span 1 — LD9
    }

    [Fact]
    public void L155_Placement_ClampsToLastColumn()
    {
        var grid = GridWithColumns(3, 3);
        var child = new Probe(1, 1);
        Grid.SetColumn(child, 5);
        grid.Children.Add(child);
        Layout(grid, 20, 10);

        Assert.Equal(new Rect(3, 0, 3, 3), child.Bounds); // clamped to index 1 — LD9
    }

    [Fact]
    public void L156_SpanningAutoDeficit_SpreadsEvenly_RemainderRightmost()
    {
        var grid = GridWithColumns(GridLength.Auto, GridLength.Auto);
        var a = new Probe(3, 1);
        var b = new Probe(10, 1);
        Grid.SetColumnSpan(b, 2);
        grid.Children.Add(a);
        grid.Children.Add(b);
        Layout(grid, 20, 3);

        Assert.Equal(new[] { 6, 4 }, ActualWidths(grid)); // deficit 7: 3 each + remainder to the rightmost
    }

    [Fact]
    public void L157_SpanningContent_NeverInflatesBoundedStars()
    {
        var grid = GridWithColumns(GridLength.Star(), GridLength.Star());
        var child = new Probe(20, 1);
        Grid.SetColumnSpan(child, 2);
        grid.Children.Add(child);
        Layout(grid, 10, 3);

        Assert.Equal(new[] { 5, 5 }, ActualWidths(grid));
        Assert.Equal(new Rect(0, 0, 10, 3), child.Bounds); // content overflows — zone clip's job
    }

    [Fact]
    public void L158_PlacementCoercion_RowNonNegative_SpanAtLeastOne()
    {
        var element = new Probe(1, 1);
        Grid.SetRow(element, -1);
        Grid.SetRowSpan(element, 0);

        Assert.Equal(0, Grid.GetRow(element));
        Assert.Equal(1, Grid.GetRowSpan(element));
    }

    [Fact]
    public void L159_DefinitionsAreOwnerWired_LiveMutation_SingleCollection_Readbacks()
    {
        var grid = GridWithColumns(4, GridLength.Star());
        var first = grid.ColumnDefinitions[0];
        var manager = Layout(grid, 20, 10);

        // ③ readbacks match the resolved sizes
        Assert.Equal(4, first.ActualWidth);
        Assert.Equal(16, grid.ColumnDefinitions[1].ActualWidth);

        // ① post-attach mutation is live: owner-wired definitions invalidate measure
        first.Width = GridLength.FromCells(8);
        Assert.False(grid.IsMeasureValid);
        Assert.True(manager.HasPendingWork);
        manager.Layout(20, 10);
        Assert.Equal(8, first.ActualWidth);
        Assert.Equal(12, grid.ColumnDefinitions[1].ActualWidth);

        // ② a definition belongs to at most one collection
        var other = new Grid();
        Assert.Throws<InvalidOperationException>(() => other.ColumnDefinitions.Add(first));
    }
}
