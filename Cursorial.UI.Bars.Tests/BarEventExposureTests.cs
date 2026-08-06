using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Bars;

// The Bars event-exposure sweep: the newly-exposed dropdown / open-close events fire at their raise sites.
public sealed class BarEventExposureTests
{
    private static UIHeadlessHost NewHost(int w = 30, int h = 10) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static StackPanel DropContent()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new Button { Content = "One", Width = 8, Height = 1 });
        panel.Children.Add(new Button { Content = "Two", Width = 8, Height = 1 });
        return panel;
    }

    [Fact] // BarDropDownButton (via BarPopupButton): DropDownOpened / DropDownClosed fire once per transition
    public void BarPopupButton_DropDownOpenedClosed()
    {
        using var host = NewHost();
        var button = new BarPopupButton
        {
            Content = "Align", DropDownContent = DropContent(),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(button);
        host.RunUntilIdle();

        int opened = 0, closed = 0;
        button.DropDownOpened += (_, _) => opened++;
        button.DropDownClosed += (_, _) => closed++;

        button.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.Equal(1, opened);
        Assert.Equal(0, closed);

        button.IsDropDownOpen = true; // redundant — no second open
        host.RunUntilIdle();
        Assert.Equal(1, opened);

        button.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.Equal(1, closed);
    }

    [Fact] // BarSplitButton inherits the dropdown events from BarDropDownButton
    public void BarSplitButton_InheritsDropDownEvents()
    {
        using var host = NewHost();
        var split = new BarSplitButton
        {
            Content = "Paste", DropDownContent = DropContent(),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(split);
        host.RunUntilIdle();

        bool opened = false, closed = false;
        split.DropDownOpened += (_, _) => opened = true;
        split.DropDownClosed += (_, _) => closed = true;

        split.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.True(opened);

        split.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.True(closed);
    }

    [Fact] // MiniToolbar: Opened on Open(target), Closed on Close()
    public void MiniToolbar_OpenedClosed()
    {
        using var host = NewHost();
        var target = new Border { Width = 6, Height = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(target);
        host.RunUntilIdle();

        var bar = new MiniToolbar();
        bar.Items.Add(new BarButton { Content = "B" });

        bool opened = false, closed = false;
        bar.Opened += (_, _) => opened = true;
        bar.Closed += (_, _) => closed = true;

        bar.Open(target);
        host.RunUntilIdle();
        Assert.True(bar.IsOpen);
        Assert.True(opened);

        bar.Close();
        host.RunUntilIdle();
        Assert.True(closed);
    }

    [Fact] // MiniToolbar.Opening can veto the open
    public void MiniToolbar_OpeningVeto()
    {
        using var host = NewHost();
        var target = new Border { Width = 6, Height = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(target);
        host.RunUntilIdle();

        var bar = new MiniToolbar();
        bar.Items.Add(new BarButton { Content = "B" });

        bar.Opening += (_, e) => e.Cancel = true;
        var openedFired = false;
        bar.Opened += (_, _) => openedFired = true;

        bar.Open(target);
        host.RunUntilIdle();

        Assert.False(bar.IsOpen);
        Assert.False(openedFired);
    }
}
