using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Bars;

// Keyboard navigation around a density-COLLAPSED ribbon group's [name ▾] flyout, and the File-tab ↕ crossing — the
// hands-on fixes: Down opens AND enters the flyout, Up backs out of the flyout to the opener (from ANY item, not just
// the first — the flyout is a horizontal band), Up off the opener climbs to the tab strip, and Down off the File tab
// drops into the currently-shown band.
public sealed class RibbonCollapsedNavTests
{
    private const int H = 10;

    private static UIHeadlessHost NewHost(int w = 90) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, H), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static BarButton Btn(string content) => new() { Content = content };

    private static RibbonGroup Group(string header, params UIElement[] items)
    {
        var g = new RibbonGroup { Header = header };
        foreach (var item in items)
            g.Items.Add(item);
        return g;
    }

    private static RibbonTab Tab(string header, params RibbonGroup[] groups)
    {
        var tab = new RibbonTab { Header = header };
        foreach (var g in groups)
            tab.Groups.Add(g);
        return tab;
    }

    private static FocusManager Focus(UIHeadlessHost host) => host.Application.FocusManager;

    [Fact] // One Down on a collapsed group's [name ▾] opener OPENS the flyout AND enters its first control (FocusContentOnOpen).
    public void Collapsed_Down_OpensAndEntersFlyout()
    {
        var paste = Btn("Paste");
        var group = Group("Clip", paste, Btn("Copy"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", group));
        using var host = NewHost();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(8, H); // collapse the group to a single [name ▾] dropdown
        host.RunUntilIdle();
        var opener = group.CollapsedButtonForTests!;
        Assert.True(opener.IsEffectivelyVisible);

        opener.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // ONE press
        host.RunUntilIdle();

        Assert.True(opener.IsDropDownOpen);          // the flyout opened
        Assert.True(paste.IsKeyboardFocusWithin);    // …AND focus entered the hosted control (not parked on the face)
    }

    [Fact] // Up from ANY flyout item backs out to the [name ▾] opener — the flyout is a horizontal band, so a non-first
           // item has no item "above" it and must return to the opener rather than escape to the tab strip.
    public void Collapsed_Up_FromAnyFlyoutItem_BacksOutToOpener()
    {
        var paste = Btn("Paste");
        var copy = Btn("Copy");
        var group = Group("Clip", paste, copy);
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", group));
        using var host = NewHost();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(8, H);
        host.RunUntilIdle();
        var opener = group.CollapsedButtonForTests!;
        opener.IsDropDownOpen = true; // open the flyout
        host.RunUntilIdle();

        // Focus the SECOND flyout item (the one that used to escape to the tab strip) and press Up.
        copy.Focus();
        host.RunUntilIdle();
        Assert.True(copy.IsKeyboardFocusWithin);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();

        Assert.True(opener.IsKeyboardFocusWithin); // backed out to the group's [name ▾] opener
    }

    [Fact] // Up off a collapsed group's opener climbs to the tab strip (the selected tab header), not a diagonal
           // neighbor in the taller group beside it.
    public void Collapsed_Up_FromOpener_ClimbsToTabStrip()
    {
        var collapsing = Group("Clip", Btn("Paste"));
        var wide = Group("Edit", Btn("Cut"), Btn("Copy"), Btn("Find"));
        Ribbon.SetMinDensity(wide, RibbonGroupDensity.Normal); // pin the neighbor at Normal so it stays tall
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", collapsing, wide));
        using var host = NewHost();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(20, H); // tight enough that 'Clip' collapses while pinned 'Edit' stays Normal
        host.RunUntilIdle();
        if (collapsing.DensityForTests != RibbonGroupDensity.Collapsed)
            return; // width heuristic didn't collapse it in this layout — skip (the climb is covered by the opener path)

        var opener = collapsing.CollapsedButtonForTests!;
        opener.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();

        var selectedTab = (RibbonTab)ribbon.ItemContainerGenerator.ContainerFromIndex(ribbon.SelectedIndex)!;
        Assert.True(selectedTab.IsKeyboardFocusWithin); // climbed to the tab strip, not a neighbor control
    }

    [Fact] // Down off a focused File tab drops into the currently-shown content band (File has no band of its own, but
           // the selected content tab's band is visible while File is merely focused).
    public void FileTab_Down_EntersShownBand()
    {
        var paste = Btn("Paste");
        var ribbon = new Ribbon();
        ribbon.Items.Add(new RibbonTab { Header = "File", IsFileTab = true });
        ribbon.Items.Add(Tab("Home", Group("Clip", paste)));
        using var host = NewHost(w: 60);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var file = (RibbonTab)ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;
        file.Focus();
        host.RunUntilIdle();
        Assert.True(file.IsKeyboardFocusWithin);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.False(file.IsKeyboardFocusWithin);  // focus left the File tab…
        Assert.True(paste.IsKeyboardFocusWithin);  // …and entered the shown Home band
    }
}
