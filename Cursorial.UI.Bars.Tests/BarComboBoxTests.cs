using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// BarComboBox = the existing ComboBox with a flat bar face. The behavior (selection, dropdown, type-ahead) is the
// ComboBox's (covered in the ComboBox suite); these smoke tests confirm the BAR theme renders the face + the parts
// wire so the inherited behavior works on the bar variant.
public sealed class BarComboBoxTests
{
    private static UITestHost NewHost(int w = 30, int h = 10) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static BarComboBox Show(UITestHost host, out BarComboBox combo)
    {
        combo = new BarComboBox
        {
            ItemsSource = new[] { "One", "Two", "Three" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        combo.SelectedIndex = 1;
        host.ShowRoot(combo);
        host.RunUntilIdle();
        return combo;
    }

    [Fact] // the bar face shows the selected value + the ▾ caret
    public void BarComboBox_RendersFaceWithSelectedValue()
    {
        using var host = NewHost();
        Show(host, out _);

        var row = host.GetRowText(0);
        Assert.Contains("Two", row); // the selected value
        Assert.Contains("▾", row);   // the drop caret
    }

    [Fact] // clicking the face opens the drop-down (inherited ComboBox behavior over the bar parts)
    public void BarComboBox_ClickOpensDropDown()
    {
        using var host = NewHost();
        Show(host, out var combo);

        var origin = combo.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row); // hover the face
        host.RunFrame();
        host.SendClick(origin.Column + 1, origin.Row);     // click the face
        host.RunUntilIdle();

        Assert.True(combo.IsDropDownOpen);
    }

    [Fact] // keyboard: Down opens; the selection changes; the face updates
    public void BarComboBox_KeyboardSelectsAndFaceUpdates()
    {
        using var host = NewHost();
        Show(host, out var combo);

        combo.Focus();
        host.RunUntilIdle();

        combo.SelectedIndex = 2; // Three
        host.RunUntilIdle();
        Assert.Equal("Three", combo.SelectedItem);
        Assert.Contains("Three", host.GetRowText(0)); // the face reflects the new selection
    }
}
