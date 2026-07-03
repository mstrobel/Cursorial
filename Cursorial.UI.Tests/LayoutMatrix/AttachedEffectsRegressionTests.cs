using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

/// <summary>
/// Regression coverage for the property-global effects lane carrying attached layout properties
/// (doc §5.5, ledger A1): a host type's per-type effects table can freeze before the declaring
/// panel's static constructor runs, so <c>Grid.SetRow(child, …)</c> must invalidate the hosting
/// grid through <c>GlobalEffects</c> — without the global lane it would invalidate nothing.
/// </summary>
public class AttachedEffectsRegressionTests
{
    [Fact]
    public void GlobalLane_GridPlacementWrite_InvalidatesHostingGridMeasure_EndToEnd()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(4));
        grid.ColumnDefinitions.Add(new ColumnDefinition(6));
        var child = new Probe(1, 1); // host type Probe never appears in Grid's per-type lane
        grid.Children.Add(child);
        var manager = LayoutFixture.CreateRoot(grid);
        manager.Layout(20, 10);
        Assert.Equal(new Rect(0, 0, 4, 10), child.Bounds);

        Grid.SetColumn(child, 1);

        Assert.False(grid.IsMeasureValid); // AffectsParentMeasure routed to the hosting grid
        Assert.True(manager.HasPendingWork);

        manager.Layout(20, 10);
        Assert.Equal(new Rect(4, 0, 6, 10), child.Bounds);
    }

    [Fact]
    public void GlobalLane_AllFourGridPlacementProperties_SurfaceAffectsParentMeasure_OnForeignHostTypes()
    {
        // GetEffects for a type the registrations never named must still include the flag — the
        // global lane is what makes attached-property invalidation host-type-agnostic.
        UIProperty[] properties = [Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty];
        foreach (var property in properties)
        {
            Assert.NotEqual(
                PropertyEffects.None,
                property.GetEffects(typeof(Probe)) & PropertyEffects.AffectsParentMeasure);
        }
    }
}
