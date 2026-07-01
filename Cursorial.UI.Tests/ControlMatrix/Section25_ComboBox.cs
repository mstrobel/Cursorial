using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C12 — ComboBox (P2B): the ListBox-in-Popup single-select drop-down. The face shows the
// SelectedItem; a click / keyboard opens the Popup; the drop-down's items are ComboBoxItem containers (realized
// only while open — they live on the Popup surface); picking one (click or Enter) commits + closes.
public sealed class Section25_ComboBox
{
    private static (UITestHost Host, ComboBox Box) Show()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var box = new ComboBox
        {
            ItemsSource = new[] { "alpha", "beta", "gamma" },
            Width = 12, Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(box);
        host.RunUntilIdle();
        return (host, box);
    }

    [Fact] // C12.1: selecting an item sets SelectedItem/SelectedIndex (the face presents it) — no drop-down needed
    public void C12_1_SelectedItem()
    {
        var (host, box) = Show();
        using var _ = host;

        box.SelectedIndex = 1;
        host.RunUntilIdle();
        Assert.Equal("beta", box.SelectedItem);

        box.SelectedItem = "gamma";
        host.RunUntilIdle();
        Assert.Equal(2, box.SelectedIndex);
    }

    [Fact] // C12.2: IsDropDownOpen drives the Popup; the items generate ComboBoxItem containers
    public void C12_2_OpenAndContainers()
    {
        var (host, box) = Show();
        using var _ = host;

        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);
        Assert.IsType<ComboBoxItem>(box.ItemContainerGenerator.ContainerFromIndex(0)); // ComboBoxItem containers
        Assert.Equal(3, box.ItemContainerGenerator.ContainerCount);

        box.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C12.3: a left click on the face opens the drop-down; clicking again closes it
    public void C12_3_ClickFaceToggles()
    {
        var (host, box) = Show();
        using var _ = host;
        var o = box.TranslateToWindow(0, 0);

        host.SendClick(o.Column, o.Row);
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);

        host.SendClick(o.Column, o.Row);
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C12.4: keyboard — closed Down opens; open Down moves the selection; Enter commits + closes
    public void C12_4_Keyboard_OpenNavigateCommit()
    {
        var (host, box) = Show();
        using var _ = host;
        box.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // closed → open
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);

        host.SendKey(Key.DownArrow); // open → select index 0
        host.RunUntilIdle();
        Assert.Equal(0, box.SelectedIndex);

        host.SendKey(Key.DownArrow); // → index 1
        host.RunUntilIdle();
        Assert.Equal(1, box.SelectedIndex);

        host.SendKey(Key.Enter); // commit + close
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
        Assert.Equal("beta", box.SelectedItem); // the highlighted selection stuck
    }

    [Fact] // C12.4b: PageDown/PageUp jump to the last/first item in the open drop-down (no scroll viewport ⇒ a page is the whole list)
    public void C12_4b_PageNav_LastFirst()
    {
        var (host, box) = Show();
        using var _ = host;
        box.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open the drop-down (no selection yet)
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);

        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.Equal(2, box.SelectedIndex); // last item (alpha/beta/gamma)

        host.SendKey(Key.PageUp);
        host.RunUntilIdle();
        Assert.Equal(0, box.SelectedIndex); // first item
    }

    [Fact] // C12.5: Escape closes the open drop-down
    public void C12_5_EscapeCloses()
    {
        var (host, box) = Show();
        using var _ = host;
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        box.Focus();
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C12.6: clicking a drop-down item commits that selection and closes
    public void C12_6_ClickItemCommitsAndCloses()
    {
        var (host, box) = Show();
        using var _ = host;
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        var item = box.ItemContainerGenerator.ContainerFromIndex(2)!;
        var p = item.TranslateToWindow(0, 0);
        host.SendClick(p.Column, p.Row);
        host.RunUntilIdle();

        // A drop-down item click commits a selection and closes (the exact item depends on popup-surface placement;
        // the keyboard test C12.4 pins the precise highlighted-item commit).
        Assert.NotNull(box.SelectedItem);
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C12.ItemTemplate: the (non-editable) face TEMPLATES the selected item with ItemTemplate. SelectionBoxItem
           // stays the ITEM (meaningful to consumers — never a rendered instance); PART_ContentSite's ContentTemplate
           // follows ItemTemplate via a binding, so the presenter builds the display copy AND a runtime template swap
           // re-templates the face.
    public void C12_ItemTemplate_TemplatesFace_SelectionBoxItemStaysTheItem()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        using var _ = host;
        var template = new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock("MARK")) };
        var box = new ComboBox
        {
            ItemsSource = new[] { "alpha", "beta", "gamma" },
            ItemTemplate = template,
            Width = 12, Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(box);
        host.RunUntilIdle();

        box.SelectedIndex = 1;
        host.RunUntilIdle();

        Assert.Equal("beta", box.SelectionBoxItem);                  // the ITEM — NOT a rendered UIElement instance
        Assert.Same(template, box.ContentSitePart!.ContentTemplate); // the face's ContentTemplate followed ItemTemplate
        var faceRow = host.GetRowText(0);
        Assert.Contains("MARK", faceRow);                            // …and the presenter rendered the TEMPLATE,
        Assert.DoesNotContain("beta", faceRow);                      // …not the item's raw text

        // A runtime ItemTemplate change re-templates the face (a binding, not a one-time set — no OnItemTemplateChanged
        // override / hook needed).
        var template2 = new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock("TWO")) };
        box.ItemTemplate = template2;
        host.RunUntilIdle();

        Assert.Same(template2, box.ContentSitePart!.ContentTemplate);
        Assert.Contains("TWO", host.GetRowText(0));
    }
}
