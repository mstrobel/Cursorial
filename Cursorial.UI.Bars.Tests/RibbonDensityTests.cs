using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// Density collapse (#144, Checkpoint 1 — the Normal↔Compact fold): as the band tightens, groups demote from full
// (large glyph-over-label) to Compact (small inline faces) in discrete steps, widest-first; a widen restores them.
// Compact demotes a control to icon-only ONLY when it has an icon — a label-only button keeps its label (never blanks).
public sealed class RibbonDensityTests
{
    private const int H = 10;

    private static UITestHost NewHost(int w) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, H), Capabilities = TestCapabilities.KittyTruecolor });

    private static string AllRows(UITestHost host) => string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));

    private static BarButton LargeIcon(string content, string icon)
    {
        var b = new BarButton { Content = content, Icon = icon };
        Ribbon.SetButtonSize(b, RibbonButtonSize.Large);
        return b;
    }

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

    [Fact] // the :density-compact cascade (driven directly, bypassing the band fold): an ICON-bearing button goes
           // icon-only, a LABEL-ONLY button keeps its label (the "don't blank label-only buttons" smarts)
    public void Density_Compact_IconOnlyForIconButtons_KeepsLabelOnlyLabels()
    {
        using var host = NewHost(w: 80);
        var iconBtn = LargeIcon("Paste", "▪");
        var labelBtn = new BarButton { Content = "Cut" }; // no icon
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; // no band ⇒ nothing promotes it back
        panel.Children.Add(iconBtn);
        panel.Children.Add(labelBtn);
        host.ShowRoot(panel);
        host.RunUntilIdle();
        Assert.Contains("Paste", AllRows(host)); // Normal: the large button shows its label

        Ribbon.SetIsDensityCompact(iconBtn, true);
        Ribbon.SetIsDensityCompact(labelBtn, true);
        host.RunUntilIdle();

        var all = AllRows(host);
        Assert.DoesNotContain("Paste", all); // icon button → icon-only (label hidden)
        Assert.Contains("▪", all);            // …its icon renders
        Assert.Contains("Cut", all);          // label-only button KEEPS its label (the "don't blank" smarts)
    }

    [Fact] // Compact never writes ButtonSize — an authored Large face restores exactly when Compact clears
    public void Density_Compact_PreservesAuthoredButtonSize()
    {
        using var host = NewHost(w: 80);
        var btn = LargeIcon("Paste", "▪");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(btn);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Ribbon.SetIsDensityCompact(btn, true);
        host.RunUntilIdle();
        Assert.Equal(RibbonButtonSize.Large, Ribbon.GetButtonSize(btn)); // authored size untouched under Compact
        Assert.DoesNotContain("Paste", AllRows(host));                   // …icon-only face

        Ribbon.SetIsDensityCompact(btn, false);
        host.RunUntilIdle();
        Assert.Equal(RibbonButtonSize.Large, Ribbon.GetButtonSize(btn));
        Assert.Contains("Paste", AllRows(host)); // the large face (label) restored byte-identically
    }

    [Fact] // the fold + inheritance + face together: at a tight width the group demotes and its ICON-bearing large
           // button actually renders compact (icon-only) — the band's inherited signal reaches the hosted control
    public void Density_Compact_TightWidth_DemotesAndRendersIconOnly()
    {
        var iconBtn = LargeIcon("Paste", "▪");
        var group = Group("Clip", iconBtn, LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", group));
        using var host = NewHost(w: 90);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Contains("Paste", AllRows(host)); // wide: full faces

        host.SendResize(24, H); // tight ⇒ the fold demotes the group to Compact
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Compact, group.DensityForTests);
        Assert.True(Ribbon.GetIsDensityCompact(iconBtn), "the group's Compact signal must inherit to the hosted button");
        Assert.DoesNotContain("Paste", AllRows(host)); // …and the button actually renders icon-only
    }

    [Fact] // the band fold: wide ⇒ all Normal; tight ⇒ the WIDEST group demotes to Compact first; widen ⇒ restores
    public void Density_Band_DemotesWidestFirst_ThenRestoresOnWiden()
    {
        var wide = Group("Alpha", LargeIcon("Paste", "▪"), LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        var narrow = Group("Beta", LargeIcon("Cut", "▪"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", wide, narrow));
        using var host = NewHost(w: 90);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Normal, wide.DensityForTests);
        Assert.Equal(RibbonGroupDensity.Normal, narrow.DensityForTests);

        host.SendResize(24, H); // tight ⇒ the fold demotes
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Compact, wide.DensityForTests);   // the widest group demotes first

        host.SendResize(90, H); // widen ⇒ the reverse staircase restores
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Normal, wide.DensityForTests);
    }

    [Fact] // Ribbon.MinDensity=Normal pins a signature group at full size — it never demotes even under width pressure
    public void Density_MinDensityNormal_PinsGroupFullSize()
    {
        var pinned = Group("Pin", LargeIcon("Paste", "▪"), LargeIcon("Copy", "▪"));
        Ribbon.SetMinDensity(pinned, RibbonGroupDensity.Normal);
        var other = Group("Other", LargeIcon("Cut", "▪"), LargeIcon("Bold", "▪"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", pinned, other));
        using var host = NewHost(w: 90);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(16, H); // very tight
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Normal, pinned.DensityForTests); // pinned holds full size (others demote first)
    }
}
