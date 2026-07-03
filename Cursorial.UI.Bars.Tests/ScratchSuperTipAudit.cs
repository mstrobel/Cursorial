using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

public sealed class ScratchSuperTipAudit
{
    private static UITestHost NewHost() =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 20), Capabilities = TestCapabilities.KittyTruecolor });

    private static (Ribbon Ribbon, Button Bold) NewRibbon()
    {
        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var font = new RibbonGroup { Header = "Font" };
        var bold = new Button { Content = "Bold" };
        font.Items.Add(bold);
        home.Groups.Add(font);
        ribbon.Items.Add(home);

        var insert = new RibbonTab { Header = "Insert" };
        var tables = new RibbonGroup { Header = "Absble" };
        tables.Items.Add(new Button { Content = "Edd" });
        insert.Groups.Add(tables);
        ribbon.Items.Add(insert);
        return (ribbon, bold);
    }

    // Show the SuperTip via a real Popup (mirrors ToolTipController: one reusable ToolTip whose Content is the tip),
    // open/close/open. Between shows we change the ribbon's selected tab. Does the cached KeyTipSequence go stale?
    [Fact]
    public void Reused_SuperTip_StaleSequence_AcrossTabSwitch()
    {
        using var host = NewHost();
        _ = host.Application.EnableKeyTips();
        var (ribbon, bold) = NewRibbon();

        var tip = new SuperTip { Title = "Bold" };
        tip.Anchor = bold;

        // The reusable ToolTip host + Popup, exactly like ToolTipController.
        var toolTip = new ToolTip();
        var popup = new Popup { Child = toolTip, StaysOpen = true, IsHitTestTransparent = true, PlacementTarget = bold };

        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(0, ribbon.SelectedIndex); // Home selected -> Bold visible

        // Show #1 (Home selected).
        toolTip.Content = tip;
        popup.SetCurrentValue(Popup.IsOpenProperty, true);
        host.RunUntilIdle();
        var seq1 = tip.KeyTipSequence;

        // Close.
        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        host.RunUntilIdle();

        // Switch to Insert. (Bold is now NOT visible, but the SAME tip instance still lives on this button.)
        ribbon.SelectedIndex = 1;
        host.RunUntilIdle();

        // Switch back to Home, re-show.
        ribbon.SelectedIndex = 0;
        host.RunUntilIdle();
        popup.SetCurrentValue(Popup.IsOpenProperty, true);
        host.RunUntilIdle();
        var seq2 = tip.KeyTipSequence;

        System.Console.WriteLine($"[AUDIT] seq1={seq1 ?? "<null>"}  seq2={seq2 ?? "<null>"}");
        Assert.Equal("Alt, H, F, B", seq1);
    }

    // First show with KeyTips DISABLED -> GetHopSequence returns null -> SetCurrentValue(prop, null).
    // Then ENABLE KeyTips and re-show. Does OnApplyTemplate recompute, or is null stuck?
    [Fact]
    public void FirstShowNull_ThenEnable_DoesItRecompute()
    {
        using var host = NewHost();
        // KeyTips NOT enabled yet.
        var (ribbon, bold) = NewRibbon();

        var tip = new SuperTip { Title = "Bold" };
        tip.Anchor = bold;
        var toolTip = new ToolTip();
        var popup = new Popup { Child = toolTip, StaysOpen = true, IsHitTestTransparent = true, PlacementTarget = bold };

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        toolTip.Content = tip;
        popup.SetCurrentValue(Popup.IsOpenProperty, true);
        host.RunUntilIdle();
        var seq1 = tip.KeyTipSequence;

        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        host.RunUntilIdle();

        // Now enable KeyTips and re-show.
        _ = host.Application.EnableKeyTips();
        popup.SetCurrentValue(Popup.IsOpenProperty, true);
        host.RunUntilIdle();
        var seq2 = tip.KeyTipSequence;

        System.Console.WriteLine($"[AUDIT] first-null seq1={seq1 ?? "<null>"}  after-enable seq2={seq2 ?? "<null>"}");
    }

    // Anchor holds a strong ref to a detached button after a command swap? Check GetHopSequence when anchor detached.
    [Fact]
    public void GetHopSequence_DetachedAnchor_DoesNotThrow()
    {
        using var host = NewHost();
        _ = host.Application.EnableKeyTips();
        var (ribbon, bold) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        // Detach the button (remove from its group).
        var font = (RibbonGroup)((RibbonTab)ribbon.Items[0]).Groups[0];
        font.Items.Remove(bold);
        host.RunUntilIdle();

        var seq = KeyTip.GetHopSequence(bold);
        System.Console.WriteLine($"[AUDIT] detached-anchor hop = {seq ?? "<null>"}");
    }
}
