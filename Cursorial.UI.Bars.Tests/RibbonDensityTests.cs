using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Bars;

// Density collapse (#144, Checkpoint 1 — the Normal↔Compact fold): as the band tightens, groups demote from full
// (large glyph-over-label) to Compact (small inline faces) in discrete steps, widest-first; a widen restores them.
// Compact demotes a control to icon-only ONLY when it has an icon — a label-only button keeps its label (never blanks).
public sealed class RibbonDensityTests
{
    private const int H = 10;

    private static UIHeadlessHost NewHost(int w) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, H), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static string AllRows(UIHeadlessHost host) => string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));

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

    [Fact] // the fold + inheritance + face together: at a tight width a Compact-capped group demotes and its ICON-
           // bearing large button actually renders compact (icon-only) — the band's inherited signal reaches the control
    public void Density_Compact_TightWidth_DemotesAndRendersIconOnly()
    {
        var iconBtn = LargeIcon("Paste", "▪");
        var group = Group("Clip", iconBtn, LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        Ribbon.SetMinDensity(group, RibbonGroupDensity.Compact); // cap at Compact so the fold stops there (no collapse)
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

    [Fact] // the Collapsed tier: at a very tight width the group becomes a single [name ▾] dropdown; its controls move
           // into the flyout (still logical children), render at AUTHORED size when opened, and return inline on widen
    public void Density_Collapsed_BecomesDropdown_HostsControlsInFlyout()
    {
        var paste = LargeIcon("Paste", "▪");
        var group = Group("Clip", paste, LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", group));
        using var host = NewHost(w: 90);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(8, H); // very tight ⇒ even Compact doesn't fit ⇒ the group collapses to a dropdown
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Collapsed, group.DensityForTests);
        Assert.True(group.CollapsedButtonForTests!.IsEffectivelyVisible);       // the [name ▾] dropdown shows
        Assert.Same(group, paste.LogicalParent);                                // controls stay LOGICAL children of the group
        Assert.True(group.CollapsedPopupHostForTests!.IsAncestorOf(paste));     // …but VISUALLY moved into the flyout host

        group.CollapsedButtonForTests!.IsDropDownOpen = true; // open the flyout
        host.RunUntilIdle();
        Assert.False(Ribbon.GetIsDensityCompact(paste)); // flyout controls render at AUTHORED size, NOT compacted

        host.SendResize(90, H); // widen ⇒ the group restores inline
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Normal, group.DensityForTests);
        Assert.False(group.CollapsedButtonForTests!.IsEffectivelyVisible);
    }

    [Fact] // the band fold: wide ⇒ all Normal; tight ⇒ the WIDEST group demotes to Compact first; widen ⇒ restores
    public void Density_Band_DemotesWidestFirst_ThenRestoresOnWiden()
    {
        var wide = Group("Alpha", LargeIcon("Paste", "▪"), LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        var narrow = Group("Beta", LargeIcon("Cut", "▪"));
        Ribbon.SetMinDensity(wide, RibbonGroupDensity.Compact);   // cap at Compact so the fold stops there (no collapse)
        Ribbon.SetMinDensity(narrow, RibbonGroupDensity.Compact);
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

    [Fact] // audit: collapsing a group while one of its controls is focused repairs focus to the [name ▾] opener
           // (deferred until the opener's :density-collapsed visibility flip lands) — focus is never stranded
    public void Density_Collapse_WhileFocused_RepairsFocusToOpener()
    {
        var paste = LargeIcon("Paste", "▪");
        var group = Group("Clip", paste, LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", group));
        using var host = NewHost(w: 90);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        paste.Focus();
        host.RunUntilIdle();
        Assert.True(paste.IsFocused);

        host.SendResize(8, H); // collapse ⇒ paste moves into the closed flyout
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Collapsed, group.DensityForTests);
        Assert.True(group.CollapsedButtonForTests!.IsKeyboardFocusWithin); // focus repaired to the opener, not stranded
    }

    [Fact] // audit gap: an UNCAPPED group must actually REST at Compact through a middle width range — not skip from
           // Normal straight to Collapsed (the deferred-restyle bug the analytic fold fixes). Sweep the width down.
    public void Density_Uncapped_RestsAtCompact_ThroughAMiddleRange()
    {
        var group = Group("Clip", LargeIcon("Paste", "▪"), LargeIcon("Copy", "▪"), LargeIcon("Format", "▪"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", group)); // NO MinDensity cap ⇒ the fold owns all three tiers
        using var host = NewHost(w: 90);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Normal, group.DensityForTests); // wide ⇒ Normal

        var sawCompact = false;
        for (var w = 40; w >= 6; w--)
        {
            host.SendResize(w, H);
            host.RunUntilIdle();
            if (group.DensityForTests == RibbonGroupDensity.Compact)
                sawCompact = true;
        }
        Assert.True(sawCompact, "an uncapped group must rest at Compact for some width range, not skip Normal→Collapsed");
        Assert.Equal(RibbonGroupDensity.Collapsed, group.DensityForTests); // …and collapse at the tightest end
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

    // ─────────────── the two-phase improving fold (the gallery Home-tab cascade regression) ───────────────

    private static BarButton MediumIcon(string content, string icon) => new() { Content = content, Icon = icon };

    private static BarToggleButton ToggleIcon(string content, string icon) => new() { Content = content, Icon = icon };

    // The gallery Home-tab replica whose tier widths expose the cascade: Clipboard [N=18 C=9 X=14] and
    // Editing [N=9 C=7 X=12] both collapse WIDER than they compact; Format [N=30 C=13 X=11] is floored at Compact.
    private static (Ribbon Ribbon, RibbonGroup Clipboard, RibbonGroup Format, RibbonGroup Editing) HomeTabReplica()
    {
        var clipboard = Group("Clipboard", LargeIcon("Paste", "▣"));
        clipboard.HasDialogLauncher = true;
        var cutCopy = new RibbonControlGroup();
        cutCopy.Items.Add(MediumIcon("Cut", "✂"));
        cutCopy.Items.Add(MediumIcon("Copy", "⧉"));
        clipboard.Items.Add(cutCopy);

        var format = Group("Format");
        Ribbon.SetMinDensity(format, RibbonGroupDensity.Compact);
        var stack = new RibbonControlGroup();
        stack.Items.Add(ToggleIcon("Bold", "𝐁"));
        stack.Items.Add(ToggleIcon("Italic", "𝐼"));
        stack.Items.Add(ToggleIcon("Code", "‹›"));
        var left = ToggleIcon("Left", "⟸");
        RibbonControlGroup.SetRowBreak(left, true);
        stack.Items.Add(left);
        stack.Items.Add(ToggleIcon("Center", "≡"));
        stack.Items.Add(ToggleIcon("Right", "⟹"));
        format.Items.Add(stack);

        var editing = Group("Editing", LargeIcon("Find", "🔍"));

        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", clipboard, format, editing));
        return (ribbon, clipboard, format, editing);
    }

    [Fact] // the headline regression: the old width-blind fold collapsed Clipboard+Editing here (39 wide, clipping,
           // both WIDER collapsed than compacted) when compacting everyone (29) fit outright
    public void Fold_CompactsEveryone_InsteadOfCascadingToCollapsed()
    {
        var (ribbon, clipboard, format, editing) = HomeTabReplica();
        using var host = NewHost(w: 120);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(30, H); // all-Compact = 29 fits; any collapse is wider
        host.RunUntilIdle();
        host.RunUntilIdle();

        Assert.Equal(RibbonGroupDensity.Compact, clipboard.DensityForTests);
        Assert.Equal(RibbonGroupDensity.Compact, format.DensityForTests);
        Assert.Equal(RibbonGroupDensity.Compact, editing.DensityForTests);
    }

    [Fact] // even when all-Compact still overflows, a collapse that WIDENS a group is never taken — clipping a
           // little beats clipping more behind [name ▾] faces that cost more than the compact rows they replace
    public void Fold_RefusesAWideningCollapse()
    {
        var (ribbon, clipboard, format, editing) = HomeTabReplica();
        using var host = NewHost(w: 120);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        host.SendResize(22, H); // all-Compact = 29 > 22 — but every collapse on offer is wider than Compact
        host.RunUntilIdle();
        host.RunUntilIdle();

        Assert.Equal(RibbonGroupDensity.Compact, clipboard.DensityForTests);
        Assert.Equal(RibbonGroupDensity.Compact, format.DensityForTests);
        Assert.Equal(RibbonGroupDensity.Compact, editing.DensityForTests);
    }

    [Fact] // phase ordering: a legitimately-collapsible group (short header, wide content) still collapses — but
           // only after every other group has compacted, and a long-header group never collapses at all
    public void Fold_CollapsesAShortHeaderGroup_OnlyAfterEveryoneCompacted()
    {
        // Formatting is NARROW (one button) with a long header ([Formatting ▾] = 15: never collapses); Go is WIDE
        // (five buttons) whose compact row STILL exceeds Formatting's normal width — the regime where a width-blind
        // single-phase fold would collapse Go while Formatting sits untouched at Normal.
        var formatting = Group("Formatting", MediumIcon("Emphasis", "◆"));
        var go = Group("Go", // [Go ▾] = 7 cells: narrower than its compact row — the collapse candidate
            MediumIcon("Alpha", "α"), MediumIcon("Beta", "β"), MediumIcon("Gamma", "γ"),
            MediumIcon("Delta", "δ"), MediumIcon("Epsilon", "ε"));
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", formatting, go));

        using var host = NewHost(w: 120);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var goCollapsed = false;
        for (var w = 60; w >= 5; w--)
        {
            host.SendResize(w, H);
            host.RunUntilIdle();

            if (go.DensityForTests == RibbonGroupDensity.Collapsed)
            {
                goCollapsed = true;
                Assert.NotEqual(RibbonGroupDensity.Normal, formatting.DensityForTests); // everyone compacted FIRST
            }

            Assert.NotEqual(RibbonGroupDensity.Collapsed, formatting.DensityForTests); // the guard: never wider
        }

        Assert.True(goCollapsed, "expected the short-header group to collapse at the tightest widths");
    }
}
