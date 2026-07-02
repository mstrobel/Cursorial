using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// Ribbon minimize (the bars guide's "collapse the ribbon"): IsMinimized hides the body band, leaving a tabs-only
// strip; the pin/chevron (⌃/⌄) toggles it, a double-click on a tab toggles it, and a click on a content tab while
// minimized restores the ribbon (showing that tab's band). Robust — no re-parenting (the popup-float overlay is a
// deferred refinement).
public sealed class RibbonMinimizeTests
{
    private const int H = 12;

    private static UITestHost NewHost(int w = 64, int h = H) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static string AllRows(UITestHost host) =>
        string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));

    private static Ribbon NewRibbon()
    {
        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var clip = new RibbonGroup { Header = "Clipboard" };
        clip.Items.Add(new BarButton { Content = "Paste" });
        home.Groups.Add(clip);
        ribbon.Items.Add(home);

        var insert = new RibbonTab { Header = "Insert" };
        var tables = new RibbonGroup { Header = "Tables" };
        tables.Items.Add(new BarButton { Content = "Table" });
        insert.Groups.Add(tables);
        ribbon.Items.Add(insert);
        return ribbon;
    }

    [Fact] // IsMinimized hides the body band (the group controls) but keeps the tab strip; restoring shows it again
    public void Minimize_CollapsesBody_KeepsStrip()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Contains("Paste", AllRows(host));      // the Home band is shown
        Assert.Contains("Home", AllRows(host));

        ribbon.IsMinimized = true;
        host.RunUntilIdle();
        Assert.DoesNotContain("Paste", AllRows(host)); // the body band collapsed
        Assert.Contains("Home", AllRows(host));        // the tab strip remains

        ribbon.IsMinimized = false;
        host.RunUntilIdle();
        Assert.Contains("Paste", AllRows(host));       // restored
    }

    [Fact] // clicking the pin/chevron toggles the minimized state
    public void Minimize_PinButton_Toggles()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.False(ribbon.IsMinimized);

        var pin = ribbon.PinButtonForTests!;
        var origin = pin.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();
        Assert.True(ribbon.IsMinimized);

        // the glyph flipped to the "expand" chevron
        Assert.Equal("⌄", pin.Content);
    }

    [Fact] // a double-click on a content tab toggles the minimized state
    public void Minimize_DoubleClickTab_Toggles()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        var home = (RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;
        var origin = home.TranslateToScreen(1, 0);

        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row, clickCount: 2);
        host.RunUntilIdle();
        Assert.True(ribbon.IsMinimized);

        host.SendClick(origin.Column, origin.Row, clickCount: 2);
        host.RunUntilIdle();
        Assert.False(ribbon.IsMinimized);
    }

    [Fact] // minimize can be entered/exited REPEATEDLY (no stuck state) via BOTH the pin and tab double-clicks
    public void Minimize_RepeatedCycles_NoStuckState()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        var pin = ribbon.PinButtonForTests!;
        var home = (RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;
        var tab = home.TranslateToScreen(1, 0);

        // The ⌃ pin toggles repeatedly.
        for (var i = 0; i < 3; i++)
        {
            ClickAt(host, pin.TranslateToScreen(0, 0));
            Assert.True(ribbon.IsMinimized);
            ClickAt(host, pin.TranslateToScreen(0, 0));
            Assert.False(ribbon.IsMinimized);
        }

        // A real two-press double-click toggles repeatedly.
        for (var i = 0; i < 3; i++)
        {
            host.SendClick(tab.Column, tab.Row, clickCount: 1);
            host.SendClick(tab.Column, tab.Row, clickCount: 2);
            host.RunUntilIdle();
            Assert.True(ribbon.IsMinimized);
            host.SendClick(tab.Column, tab.Row, clickCount: 1);
            host.SendClick(tab.Column, tab.Row, clickCount: 2);
            host.RunUntilIdle();
            Assert.False(ribbon.IsMinimized);
        }
    }

    private static void ClickAt(UITestHost host, CellPosition origin)
    {
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();
    }

    [Fact] // audit: a REAL double-click (two presses, ClickCount 1 then 2) toggles correctly in BOTH directions —
           // the count==1 restore and the count>=2 toggle must not cancel out
    public void Minimize_RealDoubleClick_TogglesBothDirections()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        var home = (RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;
        var origin = home.TranslateToScreen(1, 0);

        // Expanded → real double-click → minimized. A real double-click is press(count=1) then press(count=2).
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row, clickCount: 1);
        host.SendClick(origin.Column, origin.Row, clickCount: 2);
        host.RunUntilIdle();
        Assert.True(ribbon.IsMinimized);

        // Minimized → real double-click → expanded (must NOT restore-then-re-minimize to a net no-op).
        host.SendClick(origin.Column, origin.Row, clickCount: 1);
        host.SendClick(origin.Column, origin.Row, clickCount: 2);
        host.RunUntilIdle();
        Assert.False(ribbon.IsMinimized);
    }

    [Fact] // clicking a content tab while minimized RESTORES the ribbon and selects that tab (its band shows)
    public void Minimize_ClickTabWhileMinimized_RestoresAndSelects()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        ribbon.IsMinimized = true;
        host.RunUntilIdle();
        Assert.DoesNotContain("Table", AllRows(host)); // Insert band not shown (minimized + Home selected)

        var insert = (RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(1)!;
        var origin = insert.TranslateToScreen(1, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.False(ribbon.IsMinimized);           // restored
        Assert.Equal(1, ribbon.SelectedIndex);       // Insert selected
        Assert.Contains("Table", AllRows(host));     // its band shows
    }
}
