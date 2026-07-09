// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

/// <summary>
/// <see cref="RibbonControlGroup"/> — the within-group stacking construct: the 2-row min-width packer, RowBreak pins,
/// ItemSize stamping, and the authored band-height contract (Auto stacks only when something authored makes the band
/// 2 rows tall; density never re-flows rows).
/// </summary>
public sealed class RibbonControlGroupTests
{
    private const int H = 12;

    private static UITestHost NewHost(int w = 100) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, H), Capabilities = TestCapabilities.KittyTruecolor });

    private static BarButton Btn(string content, string? icon = null) => new() { Content = content, Icon = icon };

    private static BarButton LargeIcon(string content, string icon)
    {
        var b = Btn(content, icon);
        Ribbon.SetButtonSize(b, RibbonButtonSize.Large);
        return b;
    }

    private static RibbonControlGroup ControlGroup(params UIElement[] items)
    {
        var group = new RibbonControlGroup();
        foreach (var item in items)
            group.Items.Add(item);
        return group;
    }

    private static Ribbon RibbonWith(params UIElement[] groupItems)
    {
        var group = new RibbonGroup { Header = "Clipboard" };
        foreach (var item in groupItems)
            group.Items.Add(item);

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);

        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);
        return ribbon;
    }

    private static (int Column, int Row) ScreenPos(UIElement e)
    {
        var p = e.TranslateToScreen(0, 0);
        return (p.Column, p.Row);
    }

    // ───────────────────────────── the packer (unit level) ─────────────────────────────

    [Fact] // contiguous definition-order split minimizing the wider row
    public void PackedWidth_MinimizesTheWiderRow()
    {
        // [7,8,9]: split k=2 → max(15, 9) = 15; k=1 → max(7, 17) = 17 ⇒ picks k=2.
        Assert.Equal(15, RibbonControlGroupPanel.PackedWidth([7, 8, 9], pinnedBreak: -1, out var k));
        Assert.Equal(2, k);

        // A wide leading child claims row 0 alone: [20, 4, 5] → k=1, max(20, 9) = 20.
        Assert.Equal(20, RibbonControlGroupPanel.PackedWidth([20, 4, 5], pinnedBreak: -1, out k));
        Assert.Equal(1, k);
    }

    [Fact] // a pinned break wins over the heuristic
    public void PackedWidth_HonorsPinnedBreak()
    {
        Assert.Equal(17, RibbonControlGroupPanel.PackedWidth([7, 8, 9], pinnedBreak: 1, out var k));
        Assert.Equal(1, k);
    }

    [Fact]
    public void PackedWidth_DegenerateCounts()
    {
        Assert.Equal(0, RibbonControlGroupPanel.PackedWidth([], pinnedBreak: -1, out _));
        Assert.Equal(9, RibbonControlGroupPanel.PackedWidth([9], pinnedBreak: -1, out _));
    }

    // ───────────────────────────── stacking beside a Large hero ─────────────────────────────

    [Fact] // the headline scenario: Cut/Copy stack into the two rows a Large Paste already forced
    public void AutoStacks_BesideALargeHero()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), ControlGroup(cut, copy));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var (cutCol, cutRow) = ScreenPos(cut);
        var (copyCol, copyRow) = ScreenPos(copy);

        Assert.Equal(cutCol, copyCol);          // one column…
        Assert.Equal(cutRow + 1, copyRow);      // …Cut stacked directly over Copy
    }

    [Fact] // Auto with a 1-row band (no Large anywhere): flat single row, byte-compatible with ungrouped authoring
    public void AutoFlattens_InAOneRowBand()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var ribbon = RibbonWith(Btn("Paste", "▣"), ControlGroup(cut, copy)); // Medium hero — band stays 1-row

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var (cutCol, cutRow) = ScreenPos(cut);
        var (copyCol, copyRow) = ScreenPos(copy);

        Assert.Equal(cutRow, copyRow);          // one row…
        Assert.True(copyCol > cutCol, $"expected Copy right of Cut, got Cut@{cutCol} Copy@{copyCol}");
    }

    [Fact] // a TwoRow pin is an authored vote that MAKES the band tall — no Large hero needed
    public void TwoRowPin_ForcesTheTallBand()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var group = ControlGroup(cut, copy);
        group.RowBehavior = RibbonControlGroupRowBehavior.TwoRow;
        var ribbon = RibbonWith(Btn("Paste", "▣"), group);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var (_, cutRow) = ScreenPos(cut);
        var (_, copyRow) = ScreenPos(copy);
        Assert.Equal(cutRow + 1, copyRow);
    }

    [Fact] // SingleRow pins flat even in a tall band (centers like any 1-row control)
    public void SingleRowPin_StaysFlatInATallBand()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var group = ControlGroup(cut, copy);
        group.RowBehavior = RibbonControlGroupRowBehavior.SingleRow;
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), group);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var (_, cutRow) = ScreenPos(cut);
        var (_, copyRow) = ScreenPos(copy);
        Assert.Equal(cutRow, copyRow);
    }

    [Fact] // three children pack 2+1 (min-width split), definition order preserved
    public void ThreeChildren_PackTwoOverOne()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var format = Btn("Format", "🖌");
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), ControlGroup(cut, copy, format));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var (_, cutRow) = ScreenPos(cut);
        var (_, copyRow) = ScreenPos(copy);
        var (formatCol, formatRow) = ScreenPos(format);

        Assert.Equal(cutRow, copyRow);              // Cut, Copy share row 0 (their sum beats any other split)
        Assert.Equal(cutRow + 1, formatRow);        // Format alone on row 1
        Assert.Equal(ScreenPos(cut).Column, formatCol); // rows left-align
    }

    [Fact] // RowBreak pins the split where the width heuristic would choose otherwise
    public void RowBreak_PinsTheSplit()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var format = Btn("Format", "🖌");
        RibbonControlGroup.SetRowBreak(copy, true); // force Copy to START row 1
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), ControlGroup(cut, copy, format));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var (_, cutRow) = ScreenPos(cut);
        var (_, copyRow) = ScreenPos(copy);
        var (_, formatRow) = ScreenPos(format);

        Assert.Equal(cutRow + 1, copyRow);
        Assert.Equal(copyRow, formatRow); // Copy and Format share row 1
    }

    // ───────────────────────────── LayoutMode (Classic / Simplified / Compact) ─────────────────────────────

    [Fact] // Simplified: one row — the Large hero demotes to its [icon][label] face, the stack flattens, footer hides
    public void Simplified_OneLabeledRow_ThenClassicRestoresExactly()
    {
        using var host = NewHost(120);
        var paste = LargeIcon("Paste", "▣");
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var ribbon = RibbonWith(paste, ControlGroup(cut, copy));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var classicRows = string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));
        Assert.Contains("Clipboard", classicRows); // the group-name footer renders in Classic
        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row); // stacked beside the hero

        ribbon.LayoutMode = RibbonLayoutMode.Simplified;
        host.RunUntilIdle();
        host.RunUntilIdle(); // the pseudo-class restyle is deferred a frame

        Assert.Equal(ScreenPos(cut).Row, ScreenPos(copy).Row);    // flattened
        Assert.Equal(ScreenPos(paste).Row, ScreenPos(cut).Row);   // the hero shares the single row
        Assert.Equal(1, paste.Bounds.Rows);                        // demoted to the 1-row face
        Assert.Contains("Paste", string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText))); // label KEPT
        Assert.DoesNotContain("Clipboard", string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText))); // footer gone

        ribbon.LayoutMode = RibbonLayoutMode.Classic;
        host.RunUntilIdle();
        host.RunUntilIdle();

        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row); // authored layout restores exactly
        Assert.Equal(2, paste.Bounds.Rows);
        Assert.Contains("Clipboard", string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText)));
    }

    [Fact] // Compact: Simplified's row PLUS icon-only faces ribbon-wide — and the fold's un-compact never unpins it
    public void Compact_IconOnlyRow_SurvivesTheFoldsNormalAssignment()
    {
        using var host = NewHost(120);
        var paste = LargeIcon("Paste", "▣");
        var cut = Btn("Cut", "✂");
        var ribbon = RibbonWith(paste, ControlGroup(cut));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.LayoutMode = RibbonLayoutMode.Compact;
        host.RunUntilIdle();
        host.RunUntilIdle();

        var rows = string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));
        Assert.DoesNotContain("Paste", rows); // icon-only: labels gone (icon-bearing controls)
        Assert.DoesNotContain("Cut", rows);
        Assert.Equal(ScreenPos(paste).Row, ScreenPos(cut).Row); // single row

        // The width fold keeps running and assigns Normal at this width — its un-compact must CLEAR (not write
        // false), so the ribbon-root Compact pin keeps inheriting through to every control.
        Assert.True(cut.GetValue(Ribbon.IsDensityCompactProperty), "the root Compact pin was shadowed by the fold");

        ribbon.LayoutMode = RibbonLayoutMode.Classic;
        host.RunUntilIdle();
        host.RunUntilIdle();

        rows = string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));
        Assert.Contains("Paste", rows); // labels restore
        Assert.Contains("Cut", rows);
    }

    // ───────────────────────────── ItemSize stamping ─────────────────────────────

    [Fact] // children inherit the group's ItemSize (default Medium) — even against a Large default on the OWNING group
    public void ItemSize_ShadowsTheOwningGroupsLargeDefault()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var controlGroup = ControlGroup(cut);

        var group = new RibbonGroup { Header = "Clipboard" };
        Ribbon.SetButtonSize(group, RibbonButtonSize.Large); // group-wide Large default
        group.Items.Add(LargeIcon("Paste", "▣"));
        group.Items.Add(controlGroup);

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);
        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal(RibbonButtonSize.Medium, cut.GetValue(Ribbon.ButtonSizeProperty)); // the stack's stamp wins

        controlGroup.ItemSize = RibbonButtonSize.Small;
        host.RunUntilIdle();
        Assert.Equal(RibbonButtonSize.Small, cut.GetValue(Ribbon.ButtonSizeProperty));
    }

    // ───────────────────────────── fold analytics ─────────────────────────────

    [Fact] // the group's Compact tier width is stack-aware: the recursed estimate runs the SAME packer icon-only
    public void CompactEstimate_RecursesIntoTheStack()
    {
        using var host = NewHost();
        var group = new RibbonGroup { Header = "Clipboard" };
        group.Items.Add(LargeIcon("Paste", "▣"));
        group.Items.Add(ControlGroup(Btn("Cut", "✂"), Btn("Copy", "⧉")));

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);
        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var normal = group.TierWidth(RibbonGroupDensity.Normal);
        var compact = group.TierWidth(RibbonGroupDensity.Compact);

        Assert.InRange(compact, 1, normal - 1); // icon-only labels shed real cells through the stack
    }

    [Fact] // squeezing the terminal folds the group to Compact WITHOUT re-flowing the stack's rows
    public void Fold_CompactsTheStack_WithoutReflow()
    {
        using var host = NewHost(120);
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var group = new RibbonGroup { Header = "Clipboard" };
        group.Items.Add(LargeIcon("Paste", "▣"));
        group.Items.Add(ControlGroup(cut, copy));

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);
        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(RibbonGroupDensity.Normal, group.DensityForTests);

        // Walk the width down until the fold demotes the group to Compact (never Collapsed — stop first).
        for (var w = 60; w >= 16 && group.DensityForTests == RibbonGroupDensity.Normal; w -= 2)
        {
            host.SendResize(w, H);
            host.RunUntilIdle();
        }

        Assert.Equal(RibbonGroupDensity.Compact, group.DensityForTests);
        Assert.Equal(ScreenPos(cut).Column, ScreenPos(copy).Column); // STILL one column…
        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row);   // …still stacked: density never re-flows rows
    }

    // ───────────────────────────── audit regressions ─────────────────────────────

    [Fact] // audit F1: a RowBreak flipped at RUNTIME re-packs immediately (effect registration, not luck)
    public void RowBreak_SetAtRuntime_Repacks()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var format = Btn("Format", "🖌");
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), ControlGroup(cut, copy, format));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(ScreenPos(cut).Row, ScreenPos(copy).Row); // heuristic: Cut+Copy row 0

        RibbonControlGroup.SetRowBreak(copy, true); // pin the split at Copy — live
        host.RunUntilIdle();

        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row);
        Assert.Equal(ScreenPos(copy).Row, ScreenPos(format).Row);
    }

    [Fact] // audit F2: split/popup buttons never compact their faces — the estimate must claim NO savings for them
    public void CompactEstimate_ClaimsNoSavingsForSplitButtons()
    {
        using var host = NewHost();
        var split = new BarSplitButton { Content = "Paste", Icon = "▣" };
        var group = new RibbonGroup { Header = "Clipboard" };
        group.Items.Add(split);

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);
        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        // The fold's "errs high" contract: since the split button's face keeps its width under :density-compact,
        // the Compact tier must not promise a narrower group than Normal.
        Assert.Equal(group.TierWidth(RibbonGroupDensity.Normal), group.TierWidth(RibbonGroupDensity.Compact));
    }

    [Fact] // audit F3: Simplified is the USER's word — it flattens even an authored TwoRow pin
    public void Simplified_FlattensATwoRowPin()
    {
        using var host = NewHost(120);
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var pinned = ControlGroup(cut, copy);
        pinned.RowBehavior = RibbonControlGroupRowBehavior.TwoRow;
        var ribbon = RibbonWith(Btn("Paste", "▣"), pinned);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row); // the pin makes the band tall in Classic

        ribbon.LayoutMode = RibbonLayoutMode.Simplified;
        host.RunUntilIdle();
        host.RunUntilIdle();

        Assert.Equal(ScreenPos(cut).Row, ScreenPos(copy).Row); // one row — the mode wins over the pin
    }

    // ───────────────────────────── keytips + collapse ─────────────────────────────

    [Fact] // keytips flatten through the stack: every command keeps its own L2 target; the grouping is pure layout
    public void KeyTipContainers_FlattenControlGroups()
    {
        using var host = NewHost();
        var paste = LargeIcon("Paste", "▣");
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");

        var group = new RibbonGroup { Header = "Clipboard" };
        group.Items.Add(paste);
        group.Items.Add(ControlGroup(cut, copy));

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);
        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal([paste, cut, copy], group.KeyTipContainers);   // flattened, document order
        Assert.Equal(2, group.Containers.Count);                    // the band walk still sees the group itself
        Assert.IsType<RibbonControlGroup>(group.Containers[1]);
    }

    [Fact] // the collapsed flyout carries the stack whole — "the popup is the group's normal layout, just relocated"
    public void CollapsedFlyout_CarriesTheStackIntact()
    {
        using var host = NewHost(120);
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        // A SHORT header: the fold's improvement guard only collapses a group whose [name ▾] face is narrower than
        // its compact row — a "Clipboard"-length header (9 + 5 = 14 cells collapsed) would floor at Compact forever.
        var group = new RibbonGroup { Header = "CB" };
        group.Items.Add(LargeIcon("Paste", "▣"));
        group.Items.Add(ControlGroup(cut, copy));

        var tab = new RibbonTab { Header = "Home" };
        tab.Groups.Add(group);
        var ribbon = new Ribbon();
        ribbon.Items.Add(tab);

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        for (var w = 60; w >= 5 && group.DensityForTests != RibbonGroupDensity.Collapsed; w -= 1)
        {
            host.SendResize(w, H);
            host.RunUntilIdle();
        }

        Assert.Equal(RibbonGroupDensity.Collapsed, group.DensityForTests);

        group.CollapsedButtonForTests!.IsDropDownOpen = true; // open the [Clipboard ▾] flyout
        host.RunUntilIdle();

        Assert.Equal(ScreenPos(cut).Column, ScreenPos(copy).Column); // the stack renders inside the flyout…
        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row);   // …still two rows: authored layout, relocated
    }

    // ───────────────────────────── density orthogonality ─────────────────────────────

    [Fact] // :density-compact narrows the stack (labels drop) but never re-flows rows
    public void DensityCompact_NarrowsWithoutReflow()
    {
        using var host = NewHost();
        var cut = Btn("Cut", "✂");
        var copy = Btn("Copy", "⧉");
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), ControlGroup(cut, copy));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var wideCut = cut.Bounds.Columns;
        Assert.Equal(ScreenPos(cut).Column, ScreenPos(copy).Column); // stacked

        Ribbon.SetIsDensityCompact(cut, true);
        Ribbon.SetIsDensityCompact(copy, true);
        host.RunUntilIdle();

        Assert.Equal(ScreenPos(cut).Row + 1, ScreenPos(copy).Row); // STILL stacked (rows never re-flow)
        Assert.True(cut.Bounds.Columns < wideCut, $"expected icon-only narrower than {wideCut}, got {cut.Bounds.Columns}");
    }

    // ───────────────────────────── BarSeparator band height ─────────────────────────────

    [Fact] // an upright separator fills the band's authored height, not its own ButtonSize face
    public void Separator_FillsATallBand()
    {
        using var host = NewHost();
        var separator = new BarSeparator();
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), separator, Btn("Cut", "✂"));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal(2, separator.Bounds.Rows);

        var (col, row) = ScreenPos(separator);
        Assert.Equal('│', host.GetRowText(row)[col]);     // the rule spans both band rows
        Assert.Equal('│', host.GetRowText(row + 1)[col]);
    }

    [Fact] // no Large hero, no TwoRow pin: the band is 1 row and so is the rule
    public void Separator_StaysOneRow_InAFlatBand()
    {
        using var host = NewHost();
        var separator = new BarSeparator();
        var ribbon = RibbonWith(Btn("Paste", "▣"), separator, Btn("Cut", "✂"));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal(1, separator.Bounds.Rows);
    }

    [Fact] // the band's re-stamp reaches the separator (BandContentRows' GLOBAL effects lane): Simplified flattens, Classic restores
    public void Separator_ReflattensUnderSimplified_AndRestores()
    {
        using var host = NewHost(120);
        var separator = new BarSeparator();
        var ribbon = RibbonWith(LargeIcon("Paste", "▣"), separator, Btn("Cut", "✂"));

        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal(2, separator.Bounds.Rows);

        ribbon.LayoutMode = RibbonLayoutMode.Simplified;
        host.RunUntilIdle();
        host.RunUntilIdle(); // the pseudo-class restyle is deferred a frame

        Assert.Equal(1, separator.Bounds.Rows);

        ribbon.LayoutMode = RibbonLayoutMode.Classic;
        host.RunUntilIdle();
        host.RunUntilIdle();

        Assert.Equal(2, separator.Bounds.Rows);
    }
}
