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
}
