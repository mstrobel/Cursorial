using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// BarGallery = a ComboBox whose drop-down wraps its choices into a grid (WrapPanel items host). Behavior is the
// ComboBox's (selection/drop-down); these smoke tests confirm the bar face renders + the grid drop-down realizes.
public sealed class BarGalleryTests
{
    private static UITestHost NewHost(int w = 30, int h = 12) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    [Fact] // the bar face shows the selection; the drop-down opens and realizes its grid cells
    public void BarGallery_RendersFace_AndOpensGrid()
    {
        using var host = NewHost();
        var gallery = new BarGallery
        {
            ItemsSource = new[] { "Sm", "Md", "Lg" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        gallery.SelectedIndex = 1;
        host.ShowRoot(gallery);
        host.RunUntilIdle();

        Assert.Contains("Md", host.GetRowText(0)); // the face shows the selected value

        gallery.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.True(gallery.IsDropDownOpen);
        Assert.Equal(3, gallery.ItemContainerGenerator.ContainerCount); // the grid realized its cells
        Assert.NotNull(gallery.ItemsPanel); // a WrapPanel items-panel template is configured (the grid layout)
    }
}
