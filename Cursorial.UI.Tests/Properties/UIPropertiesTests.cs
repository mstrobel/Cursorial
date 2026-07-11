using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Properties;

/// <summary>
/// The public enumeration surface (<see cref="UIProperties"/>) over the property registry —
/// what inspectors/designers use instead of reflecting over <c>*Property</c> field conventions.
/// </summary>
public class UIPropertiesTests
{
    [Fact]
    public void ForType_includes_declared_and_inherited_properties_but_not_attached()
    {
        // Force registration (the XAML loader normally does this via static ctors).
        _ = Button.ContentProperty;
        _ = UIElement.VisibilityProperty;
        _ = Grid.RowProperty;

        var properties = UIProperties.ForType(typeof(Button));

        Assert.Contains(properties, p => p.Name == "Content");
        Assert.Contains(properties, p => p.Name == "Visibility"); // inherited from UIElement
        Assert.DoesNotContain(properties, p => p is { Name: "Row", IsAttached: true });
        Assert.All(properties, p => Assert.False(p.IsAttached));
    }

    [Fact]
    public void AttachedBy_returns_the_owners_attached_properties()
    {
        _ = Grid.RowProperty;
        _ = DockPanel.DockProperty;

        var grid = UIProperties.AttachedBy(typeof(Grid));
        Assert.Contains(grid, p => p.Name == "Row");
        Assert.Contains(grid, p => p.Name == "Column");
        Assert.All(grid, p => Assert.True(p.IsAttached));

        var dock = UIProperties.AttachedBy(typeof(DockPanel));
        Assert.Contains(dock, p => p.Name == "Dock");
    }

    [Fact]
    public void Find_resolves_through_base_types()
    {
        _ = UIElement.VisibilityProperty;

        var property = UIProperties.Find(typeof(Button), "Visibility");
        Assert.NotNull(property);
        Assert.Equal(typeof(UIElement), property!.OwnerType);
        Assert.Null(UIProperties.Find(typeof(Button), "NoSuchProperty"));
    }

    [Fact]
    public void Enumeration_composes_with_value_sources_for_inherited_reads()
    {
        // The InspectorDemo snag: finding inherited contributions that have no local store
        // entry. Enumerate applicable properties, then ask each for its source.
        _ = UIElement.VisibilityProperty;

        var button = new Button();
        var sources = UIProperties.ForType(typeof(Button))
            .ToDictionary(p => p, button.GetValueSource);

        Assert.NotEmpty(sources);
        Assert.All(sources.Values, _ => { }); // every applicable property answers without throwing
    }
}
