using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Bars;

// The Ribbon (Surface B): a TabControl-shaped host of RibbonTab tabs over a band of RibbonGroups, hosting the SAME
// bar controls a Toolbar hosts, bound to the SAME BarCommands. P2 core: docked, single-density render + commands.
public sealed class RibbonTests
{
    private static UIHeadlessHost NewHost(int w = 64, int h = 10) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static RibbonGroup Group(string header, params UIElement[] items)
    {
        var group = new RibbonGroup { Header = header };
        foreach (var item in items)
            group.Items.Add(item);
        return group;
    }

    private static RibbonTab Tab(string header, params RibbonGroup[] groups)
    {
        var tab = new RibbonTab { Header = header };
        foreach (var group in groups)
            tab.Groups.Add(group);
        return tab;
    }

    private static BarButton Large(BarButton b) { Ribbon.SetButtonSize(b, RibbonButtonSize.Large); return b; }

    [Fact] // mixed button sizes: every group's NAME sits on the SAME (band bottom) row — a large 3-row group and a
           // medium 2-row group must not paint their names on different rows (they bottom-align to a common baseline)
    public void Ribbon_GroupNames_BottomAlign_AcrossMixedButtonSizes()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home",
            Group("Clip", Large(new BarButton { Content = "Paste" })), // large ⇒ 3-row group (glyph over label + name)
            Group("Fnt", new BarToggleButton { Content = "B" })));     // medium ⇒ 2-row group (single button + name)
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var clipRow = RowContaining(host, "Clip");
        var fntRow = RowContaining(host, "Fnt");
        Assert.True(clipRow >= 0 && fntRow >= 0, $"group names not found (Clip={clipRow}, Fnt={fntRow})");
        Assert.Equal(clipRow, fntRow); // same row ⇒ names share the band's bottom baseline
    }

    private static int RowContaining(UIHeadlessHost host, string text)
    {
        for (var r = 0; r < 10; r++)
            if (host.GetRowText(r).Contains(text))
                return r;
        return -1;
    }

    [Fact] // a docked ribbon renders: the tab strip, the selected tab's groups, their buttons, and the group labels
    public void Ribbon_DockedRenders()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home",
            Group("Clipboard", Large(new BarButton { Content = "Paste" }), new BarButton { Content = "Cut" }),
            Group("Font", new BarToggleButton { Content = "B" }, new BarToggleButton { Content = "I" })));
        ribbon.Items.Add(Tab("Insert", Group("Tables", new BarButton { Content = "Table" })));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var all = string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText));
        Assert.Contains("Home", all);
        Assert.Contains("Insert", all);   // both tabs in the strip
        Assert.Contains("Paste", all);    // the Home band's controls
        Assert.Contains("Cut", all);
        Assert.Contains("Clipboard", all); // the group-name footers
        Assert.Contains("Font", all);
        Assert.DoesNotContain("Table", all); // the Insert band is NOT shown (Home is selected)
    }

    [Fact] // the first content tab is auto-selected (a ribbon always shows a band, TabControl parity)
    public void Ribbon_AutoSelectsFirstTab()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("G", new BarButton { Content = "A" })));
        ribbon.Items.Add(Tab("Insert", Group("H", new BarButton { Content = "B" })));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal(0, ribbon.SelectedIndex);
    }

    [Fact] // clicking the Insert tab switches the band to Insert's groups
    public void Ribbon_TabSwitchByClick()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("G", new BarButton { Content = "Alpha" })));
        var insert = Tab("Insert", Group("H", new BarButton { Content = "Beta" }));
        ribbon.Items.Add(insert);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var insertHeader = insert.TranslateToScreen(0, 0);
        host.SendClick(insertHeader.Column + 1, insertHeader.Row);
        host.RunUntilIdle();

        Assert.Equal(1, ribbon.SelectedIndex);
        var all = string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText));
        Assert.Contains("Beta", all);
        Assert.DoesNotContain("Alpha", all);
    }

    [Fact] // arrow-key nav on the focused strip moves + selects the next tab
    public void Ribbon_TabSwitchByKeyboard()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var home = Tab("Home", Group("G", new BarButton { Content = "Alpha" }));
        ribbon.Items.Add(home);
        ribbon.Items.Add(Tab("Insert", Group("H", new BarButton { Content = "Beta" })));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        home.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();

        Assert.Equal(1, ribbon.SelectedIndex);
    }

    [Fact] // ↕: Down off a focused tab header drops focus into the ribbon body (the selected tab's first control)
    public void Ribbon_DownFromTab_EntersBody()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var home = Tab("Home", Group("Clipboard", button));
        var ribbon = new Ribbon();
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        home.Focus();
        host.RunUntilIdle();
        Assert.True(home.IsFocused);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.True(button.IsFocused);   // focus crossed the strip→body boundary
        Assert.False(home.IsFocused);
    }

    [Fact] // ↕: Up from the ribbon body's top row climbs back to the selected tab header
    public void Ribbon_UpFromBody_ReturnsToTab()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var home = Tab("Home", Group("Clipboard", button));
        var ribbon = new Ribbon();
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        button.Focus();
        host.RunUntilIdle();
        Assert.True(button.IsFocused);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();

        Assert.True(home.IsFocused);      // climbed body→strip back to the active tab
        Assert.False(button.IsFocused);
    }

    [Fact] // ↕ round trip: Down into the body then Up returns to the same tab
    public void Ribbon_DownThenUp_RoundTripsTabAndBody()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var home = Tab("Home", Group("Clipboard", button));
        var ribbon = new Ribbon();
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        home.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.True(button.IsFocused);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.True(home.IsFocused);
    }

    [Fact] // ↕: Down off the FILE tab drops into the currently-SHOWN band — while File is merely focused (not activated
           // into Backstage) the selected content tab's band is visible, so Down enters it like any other tab.
    public void Ribbon_DownFromFileTab_EntersShownBand()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var ribbon = new Ribbon();
        ribbon.Items.Add(new RibbonTab { Header = "File", IsFileTab = true });
        ribbon.Items.Add(Tab("Home", Group("Clipboard", button)));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var file = (RibbonTab)ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;
        file.Focus();
        host.RunUntilIdle();
        Assert.True(file.IsFocused);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.True(button.IsKeyboardFocusWithin); // Down off File entered the shown Home band
    }

    [Fact] // Ctrl+F1 toggles minimize from within the ribbon (the keyboard route — the pin chevron is mouse-only)
    public void Ribbon_CtrlF1_TogglesMinimize()
    {
        using var host = NewHost();
        var home = Tab("Home", Group("Clipboard", new BarButton { Content = "Paste" }));
        var ribbon = new Ribbon();
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.False(ribbon.IsMinimized);

        home.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.F1, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.True(ribbon.IsMinimized);

        host.SendKey(Key.F1, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.False(ribbon.IsMinimized);
    }

    [Fact] // ↕ on a MINIMIZED ribbon: Down FLOATS the band (transient reveal) and enters it; Esc re-collapses + returns
           // to the tab; the ribbon stays minimized throughout (the Office peek-the-band model).
    public void Ribbon_DownFromTab_Minimized_FloatsBandAndEnters_EscCollapses()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var home = Tab("Home", Group("Clipboard", button));
        var ribbon = new Ribbon { IsMinimized = true };
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        home.Focus();
        host.RunUntilIdle();
        Assert.False(button.IsFocused); // minimized: nothing entered yet

        host.SendKey(Key.DownArrow); // float + enter
        host.RunUntilIdle();
        Assert.True(button.IsEffectivelyVisible);  // the band floated (revealed)
        Assert.True(button.IsFocused);             // focus entered the floated band
        Assert.True(ribbon.IsMinimized);           // still minimized — the float is transient

        host.SendKey(Key.Escape); // dismiss the float
        host.RunUntilIdle();
        Assert.False(button.IsEffectivelyVisible); // re-collapsed
        Assert.True(home.IsFocused);               // focus returned to the tab
        Assert.True(ribbon.IsMinimized);
    }

    [Fact] // a floated band auto-collapses when keyboard focus LEAVES the ribbon (Office peek-then-dismiss)
    public void Ribbon_FloatedBand_CollapsesOnFocusLeavingRibbon()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var outside = new BarButton { Content = "Outside" };
        var ribbon = new Ribbon { IsMinimized = true };
        ribbon.Items.Add(Tab("Home", Group("Clipboard", button)));
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(outside);
        root.Children.Add(ribbon);
        host.ShowRoot(root);
        host.RunUntilIdle();

        ((RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(0)!).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // float + enter the band
        host.RunUntilIdle();
        Assert.True(button.IsEffectivelyVisible);

        outside.Focus(); // focus leaves the ribbon → the float auto-collapses
        host.RunUntilIdle();
        Assert.False(button.IsEffectivelyVisible);
        Assert.True(ribbon.IsMinimized);
    }

    [Fact] // a float dismissed then RE-floated within the same interaction enters the band cleanly (the retry chain is
           // generation-scoped, so a stale chain from the first float can't interfere with the second)
    public void Ribbon_Minimized_RefloatAfterCollapse_EntersBand()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste" };
        var home = Tab("Home", Group("Clipboard", button));
        var ribbon = new Ribbon { IsMinimized = true };
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        home.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // float #1 + enter
        host.RunUntilIdle();
        Assert.True(button.IsFocused);

        host.SendKey(Key.Escape); // dismiss float #1
        host.RunUntilIdle();
        Assert.False(button.IsEffectivelyVisible);
        Assert.True(home.IsFocused);

        host.SendKey(Key.DownArrow); // float #2 + enter — must land cleanly despite float #1's spent chain
        host.RunUntilIdle();
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsFocused);
        Assert.True(ribbon.IsMinimized);
    }

    [Fact] // a Large button renders glyph-over-label (2 rows); a Medium button is a single row
    public void Ribbon_LargeButtonIsTallerThanMedium()
    {
        using var host = NewHost();
        var large = Large(new BarButton { Content = "Paste", Icon = "P" });
        var medium = new BarButton { Content = "Cut", Icon = "C" };
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("Clipboard", large, medium)));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.True(large.DesiredSize.Rows >= 2, $"Large should span ≥2 rows, was {large.DesiredSize.Rows}");
        Assert.Equal(1, medium.DesiredSize.Rows); // the Medium face is a single [icon][label] row
        Assert.True(large.DesiredSize.Rows > medium.DesiredSize.Rows);
    }

    [Fact] // flipping a button's size at runtime re-measures it (Medium → Large grows its rows)
    public void Ribbon_SizeFlipReMeasures()
    {
        using var host = NewHost();
        var button = new BarButton { Content = "Paste", Icon = "P" };
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("Clipboard", button)));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(1, button.DesiredSize.Rows);

        Ribbon.SetButtonSize(button, RibbonButtonSize.Large);
        host.RunUntilIdle();

        Assert.True(button.DesiredSize.Rows >= 2, $"after Large flip should be ≥2 rows, was {button.DesiredSize.Rows}");
    }

    [Fact] // a Medium ribbon button renders the SAME single-row [icon][label] face as a Toolbar button (byte-identical
           // Medium — the size-aware template must not perturb the existing toolbar look)
    public void Ribbon_MediumFaceMatchesToolbar()
    {
        static string FaceRow(Func<UIElement> makeHost)
        {
            using var host = NewHost(20, 4);
            var root = makeHost();
            (root as UIElement)!.HorizontalAlignment = HorizontalAlignment.Left;
            (root as UIElement)!.VerticalAlignment = VerticalAlignment.Top;
            host.ShowRoot(root);
            host.RunUntilIdle();
            return host.GetRowText(0).TrimEnd();
        }

        var toolbarRow = FaceRow(() => { var t = new Toolbar(); t.Items.Add(new BarButton { Content = "Cut", Icon = "C" }); return t; });
        var ribbonMediumRow = FaceRow(() =>
        {
            var group = new RibbonGroupPanel(); // the group's own layout, isolated from the tab chrome
            group.Children.Add(new BarButton { Content = "Cut", Icon = "C" });
            return group;
        });

        Assert.Contains("Cut", toolbarRow);        // the [icon][label] face
        Assert.Equal(toolbarRow, ribbonMediumRow); // identical in the ribbon (Medium = the toolbar face, byte-identical)
    }

    [Fact] // the SAME BarCommand instance drives a Toolbar button AND a ribbon button — auto-fill + enabled together
    public void Ribbon_SharesBarCommandWithToolbar()
    {
        using var host = NewHost();
        var canRun = true;
        // ReSharper disable once AccessToModifiedClosure
        var cmd = new BarCommand(() => { }, () => canRun) { Text = "Save", InputGestureText = "Ctrl+S" };

        var toolbarButton = new BarButton { Command = cmd };
        var ribbonButton = new BarButton { Command = cmd };

        var toolbar = new Toolbar();
        toolbar.Items.Add(toolbarButton);
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("File", ribbonButton)));

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);
        root.Children.Add(ribbon);
        host.ShowRoot(root);
        host.RunUntilIdle();

        // Both surfaces auto-filled Content from the ONE command.
        Assert.Equal("Save", toolbarButton.Content);
        Assert.Equal("Save", ribbonButton.Content);

        // Flip CanExecute → both disable together (no per-surface code).
        canRun = false;
        cmd.RaiseCanExecuteChanged();
        host.RunUntilIdle();
        Assert.False(toolbarButton.IsEffectivelyEnabled);
        Assert.False(ribbonButton.IsEffectivelyEnabled);
    }

    [Fact] // a group with a dialog launcher raises DialogLauncherRequested when its ⋰ is clicked
    public void Ribbon_DialogLauncherRaisesEvent()
    {
        using var host = NewHost();
        var group = Group("Font", new BarButton { Content = "B" });
        group.HasDialogLauncher = true;
        group.HorizontalAlignment = HorizontalAlignment.Left;
        group.VerticalAlignment = VerticalAlignment.Top;
        RibbonGroup? raisedFor = null;
        group.DialogLauncherRequested += (s, _) => raisedFor = s as RibbonGroup;

        host.ShowRoot(group); // the launcher wiring is a group concern — test it directly
        host.RunUntilIdle();

        var launcher = group.DialogLauncherForTests;
        Assert.NotNull(launcher);
        var origin = launcher!.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column, origin.Row); // hover arms the release-click gate
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Same(group, raisedFor);
    }

    [Fact] // the File tab is a command, not a band: clicking it raises BackstageRequested and does NOT select it
    public void Ribbon_FileTabRaisesBackstage()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var file = new RibbonTab { Header = "File", IsFileTab = true };
        ribbon.Items.Add(file);
        ribbon.Items.Add(Tab("Home", Group("G", new BarButton { Content = "A" })));
        var raised = 0;
        ribbon.BackstageRequested += (_, _) => raised++;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal(1, ribbon.SelectedIndex); // auto-selection skipped the File tab → Home (index 1)

        var origin = file.TranslateToScreen(0, 0);
        host.SendClick(origin.Column + 1, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(1, raised);                // Backstage requested
        Assert.Equal(1, ribbon.SelectedIndex);  // selection did NOT move to File
    }

    [Fact] // a dark/light flip re-skins the ribbon live with the same text layout (the DynamicResource R2 spine)
    public void Ribbon_ThemeFlipReSkins()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("Clipboard", new BarButton { Content = "Paste" })));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        var before = string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText));

        host.Application.RequestedThemeBase = host.Application.ActualThemeVariant.Base == ThemeBase.Dark ? ThemeBase.Light : ThemeBase.Dark;
        host.RunUntilIdle();
        var after = string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText));

        Assert.Equal(before, after); // same text/layout after the flip (only colors changed)
        Assert.Contains("Paste", after);
    }

    [Fact] // arrowing while a group control is focused does NOT switch tabs — arrows are the group's own directional
           // navigation, not the tab strip's (the bug: tab into a group, arrow, and the tab changes)
    public void Ribbon_DirectionalNavInGroupDoesNotSwitchTabs()
    {
        using var host = NewHost();
        var b1 = new BarButton { Content = "One" };
        var b2 = new BarButton { Content = "Two" };
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("G", b1, b2)));
        ribbon.Items.Add(Tab("Insert", Group("H", new BarButton { Content = "X" })));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        b1.Focus();
        host.RunUntilIdle();
        Assert.True(b1.IsFocused);

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();

        Assert.Equal(0, ribbon.SelectedIndex); // STILL on Home — the arrow did not hijack tab switching
    }

    [Fact] // the File tab is a command, not a band: arrowing onto it FOCUSES it (reachable, highlights) WITHOUT
           // selecting it (the shown band stays on the content tab), and you can arrow back off it — not a one-way trap
    public void Ribbon_FileTabIsFocusableButNotSelectable()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var file = new RibbonTab { Header = "File", IsFileTab = true };
        var home = Tab("Home", Group("G", new BarButton { Content = "A" }));
        var insert = Tab("Insert", Group("H", new BarButton { Content = "B" }));
        ribbon.Items.Add(file);
        ribbon.Items.Add(home);
        ribbon.Items.Add(insert);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(1, ribbon.SelectedIndex); // Home auto-selected (File skipped)

        home.Focus(); // focus the Home tab header (on the strip)
        host.RunUntilIdle();
        host.SendKey(Key.LeftArrow); // arrow onto the File tab
        host.RunUntilIdle();

        Assert.True(file.IsFocused);           // File is now REACHABLE and focused (highlights)…
        Assert.Equal(1, ribbon.SelectedIndex); // …but it is NOT selected — the shown band stays on Home
        Assert.False(file.IsSelected);

        host.SendKey(Key.RightArrow); // arrow back OFF File → Home (selects it) — File is not a trap
        host.RunUntilIdle();
        Assert.True(home.IsFocused);
        Assert.Equal(1, ribbon.SelectedIndex);
        Assert.False(file.IsFocused);
    }

    [Fact] // Enter (and click) on the focused File tab opens Backstage; arrow-reached File is keyboard-activatable
    public void Ribbon_FileTabEnterOpensBackstage()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var file = new RibbonTab { Header = "File", IsFileTab = true };
        ribbon.Items.Add(file);
        ribbon.Items.Add(Tab("Home", Group("G", new BarButton { Content = "A" })));
        var raised = 0;
        ribbon.BackstageRequested += (_, _) => raised++;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        file.Focus(); // focus the File tab (as an arrow-onto would)
        host.RunUntilIdle();
        Assert.True(file.IsFocused);

        host.SendKey(Key.Enter); // activate it
        host.RunUntilIdle();
        Assert.Equal(1, raised);               // Backstage requested
        Assert.False(file.IsSelected);         // …still not selected
    }

    [Fact] // Escape from within a ribbon group returns focus to where it came from before entering the ribbon (the
           // Ribbon — not the group — is the single non-retaining returning scope for the whole surface)
    public void Ribbon_EscapeInGroupRestoresFocus()
    {
        using var host = NewHost();
        var groupButton = new BarButton { Content = "Paste" };
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("Clipboard", groupButton)));
        var outside = new Button { Content = "Editor", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(ribbon);
        root.Children.Add(outside);
        host.ShowRoot(root);
        host.RunUntilIdle();

        outside.Focus();
        host.RunUntilIdle();
        groupButton.Focus(FocusNavigationMethod.Tab); // enter the ribbon via Tab (captures the return target)
        host.RunUntilIdle();
        Assert.True(groupButton.IsFocused);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.True(outside.IsFocused);      // Escape returned focus to where it came from
        Assert.False(groupButton.IsFocused);
    }

    [Fact] // #138: Escape from the TAB STRIP (a tab header, not group content) returns focus to the pre-entry element —
           // the whole ribbon is the returning scope, so the strip is covered, not just the content.
    public void Ribbon_EscapeOnTabStripRestoresFocus()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("G", new BarButton { Content = "A" })));
        ribbon.Items.Add(Tab("Insert", Group("H", new BarButton { Content = "B" })));
        var outside = new Button { Content = "Editor", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(ribbon);
        root.Children.Add(outside);
        host.ShowRoot(root);
        host.RunUntilIdle();

        outside.Focus();
        host.RunUntilIdle();
        var home = ribbon.ItemContainerGenerator.ContainerFromIndex(0) as RibbonTab;
        home!.Focus(FocusNavigationMethod.Tab); // Tab onto the tab strip (a header)
        host.RunUntilIdle();
        Assert.True(home.IsFocused);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.True(outside.IsFocused);   // Escape from the STRIP returned focus to before the ribbon was entered
        Assert.False(home.IsFocused);
    }

    [Fact] // #139: arrow (directional) navigation flows ACROSS RibbonGroup boundaries — from the last control of one
           // group, Right moves to the first control of the NEXT group (the band is one directional continuum, no
           // Tab-mode switch needed). It still does not switch tabs.
    public void Ribbon_DirectionalNavCrossesGroupBoundaries()
    {
        using var host = NewHost();
        var a = new BarButton { Content = "A" };
        var b = new BarButton { Content = "B" }; // last control of group G
        var c = new BarButton { Content = "C" }; // first control of the NEXT group H
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("G", a, b), Group("H", c)));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        b.Focus(); // last control of the first group
        host.RunUntilIdle();
        Assert.True(b.IsFocused);

        host.SendKey(Key.RightArrow); // crosses the G→H group boundary to the first control of the next group
        host.RunUntilIdle();
        Assert.True(c.IsFocused);              // reached the next group's control by arrow alone
        Assert.Equal(0, ribbon.SelectedIndex); // …and did NOT switch tabs

        host.SendKey(Key.LeftArrow); // and back across the boundary
        host.RunUntilIdle();
        Assert.True(b.IsFocused);
    }

    [Fact] // the ribbon content is a SINGLE Tab stop (aligning with the Toolbar / tab strip): Tab enters the content
           // once (on the first control) and the next Tab exits past the WHOLE content — you don't tab through every
           // control to leave. Arrow keys do the within-content navigation.
    public void Ribbon_ContentIsSingleTabStop()
    {
        using var host = NewHost();
        var a = new BarButton { Content = "A" };
        var b = new BarButton { Content = "B" };
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", Group("G", a, b)));
        var outside = new Button { Content = "Out", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(ribbon);
        root.Children.Add(outside);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var home = ribbon.ItemContainerGenerator.ContainerFromIndex(0) as RibbonTab;
        home!.Focus(); // on the tab strip
        host.RunUntilIdle();

        host.SendKey(Key.Tab); // strip → content: enters the content on its first control
        host.RunUntilIdle();
        Assert.True(a.IsFocused);

        host.SendKey(Key.Tab); // content → out in ONE step (past the whole content — not to b)
        host.RunUntilIdle();
        Assert.True(outside.IsFocused);
        Assert.False(b.IsFocused);

        a.Focus(); // within the content, arrows still cross controls
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.True(b.IsFocused);
    }

    // ───────────────────────── P3a: contextual tabs (the purple, conditional band tab) ─────────────────────────

    private static Color Resolve(UIElement el, string key)
        => ((SolidColorBrush) el.FindResource(key)!).Color;

    private static Color PenColor(UIElement el, string key)
        => ((SolidColorBrush) ((Pen) el.FindResource(key)!).Brush!).Color;

    // A ribbon with a File tab, two content tabs (Home, Insert), and a trailing contextual "Table" tab.
    private static (Ribbon ribbon, RibbonTab home, RibbonTab table) ContextualRibbon()
    {
        var ribbon = new Ribbon();
        ribbon.Items.Add(new RibbonTab { Header = "File", IsFileTab = true });
        var home = Tab("Home", Group("Clipboard", new BarButton { Content = "Paste" }));
        ribbon.Items.Add(home);
        ribbon.Items.Add(Tab("Insert", Group("Tables", new BarButton { Content = "Ins" })));
        var table = Tab("Table", Group("Layout", new BarButton { Content = "Merge" }));
        table.IsContextual = true;
        ribbon.Items.Add(table);
        return (ribbon, home, table);
    }

    // The column of the first occurrence of `needle` on `row` (the strip is row 0; bands are on lower rows).
    private static int ColumnOf(UIHeadlessHost host, int row, string needle)
        => host.GetRowText(row).IndexOf(needle, StringComparison.Ordinal);

    [Fact] // a shown-inactive contextual tab renders in purple ink (--ctx) — distinct from a normal tab's dim ink
    public void Contextual_RestingSkinIsPurple()
    {
        using var host = NewHost();
        var (ribbon, _, _) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var purple = Resolve(ribbon, ThemeKeys.PurpleBrush);
        var tableCol = ColumnOf(host, 0, "Table");
        Assert.True(tableCol >= 0, "the contextual tab label is on the strip");
        Assert.Equal(purple, host.GetCell(tableCol, 0).Style.Foreground); // purple ink, resting (not selected)

        var homeCol = ColumnOf(host, 0, "Home");
        Assert.NotEqual(purple, host.GetCell(homeCol, 0).Style.Foreground); // a normal tab is NOT purple
    }

    [Fact] // the │ divider and ▾ caret cells render for a contextual tab, WITH a separating space before the caret
    public void Contextual_DividerAndCaretCells()
    {
        using var host = NewHost();
        var (ribbon, _, _) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        // divider + space + label(5) + gap + caret, read cell-by-cell. The caret lands at tableCol+6 — one cell PAST
        // the label's last glyph (tableCol+4) — proving the audit-#5 separating gap (pre-fix it rendered adjacent
        // "Table▾" at tableCol+5, because a TextBlock drops a leading " ▾" space; a left Margin survives).
        var tableCol = ColumnOf(host, 0, "Table");
        Assert.True(tableCol >= 2);
        Assert.Equal("│", host.GetCell(tableCol - 2, 0).Grapheme);      // divider before the label
        Assert.Equal("▾", host.GetCell(tableCol + 6, 0).Grapheme);      // caret after a 1-cell gap
        Assert.NotEqual("▾", host.GetCell(tableCol + 5, 0).Grapheme);   // NOT adjacent to the label
    }

    [Fact] // selecting the contextual tab KEEPS its purple ink (the :ribbon-contextual:selected rule out-specifies :selected)
    public void Contextual_ActiveKeepsPurpleInk()
    {
        using var host = NewHost();
        var (ribbon, _, table) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var tableCol = ColumnOf(host, 0, "Table");
        table.IsSelected = true; // select the contextual tab (avoid pointer :pointerover masking the resting ink)
        host.RunUntilIdle();
        Assert.True(table.IsSelected);

        var purple = Resolve(ribbon, ThemeKeys.PurpleBrush);
        var text = Resolve(ribbon, ThemeKeys.TextBrush);
        var activeCol = ColumnOf(host, 0, "Table");
        Assert.Equal(purple, host.GetCell(activeCol, 0).Style.Foreground); // still purple, NOT the normal --text ink
        Assert.NotEqual(text, host.GetCell(activeCol, 0).Style.Foreground);
        Assert.Contains("Merge", string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText))); // its band shows
    }

    [Fact] // the active contextual tab's underline is purple; a normal active tab's underline is accent blue
    public void Contextual_ActiveUnderlineIsPurple()
    {
        using var host = NewHost();
        var (ribbon, _, table) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var accentPen = PenColor(ribbon, ThemeKeys.TabUnderlinePen);
        var ctxPen = PenColor(ribbon, ThemeKeys.RibbonContextualUnderlinePen);
        Assert.NotEqual(accentPen, ctxPen); // the two pens differ in color (purple vs accent)

        var homeCol = ColumnOf(host, 0, "Home");
        Assert.Equal(accentPen, host.GetCell(homeCol, 1).Style.Foreground); // Home (auto-selected) underline = accent

        table.IsSelected = true;
        host.RunUntilIdle();
        var ctxCol = ColumnOf(host, 0, "Table");
        Assert.Equal(ctxPen, host.GetCell(ctxCol, 1).Style.Foreground); // contextual active underline = purple
    }

    [Fact] // THE SHARP EDGE: hiding the ACTIVE contextual tab redirects selection to the first content tab (band never blanks)
    public void Contextual_HideActiveRedirectsSelection()
    {
        using var host = NewHost();
        var (ribbon, home, table) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        table.IsSelected = true;
        host.RunUntilIdle();
        Assert.True(table.IsSelected);
        Assert.Contains("Merge", string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText)));

        table.Visibility = Visibility.Collapsed; // the table was deselected in the app → hide its tab
        host.RunUntilIdle();

        Assert.True(home.IsSelected);            // selection fell back to the first content tab
        Assert.False(table.IsSelected);
        var all = string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText));
        Assert.Contains("Paste", all);           // Home's band renders — never a blank band
        Assert.DoesNotContain("Merge", all);     // the hidden tab's band is gone
        Assert.DoesNotContain("Table", host.GetRowText(0)); // the strip no longer shows the hidden tab
    }

    [Fact] // a hidden-then-shown contextual tab is an ordinary selectable band tab (select shows its band)
    public void Contextual_ShowThenSelectShowsBand()
    {
        using var host = NewHost();
        var (ribbon, _, table) = ContextualRibbon();
        table.Visibility = Visibility.Collapsed; // start hidden
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.DoesNotContain("Table", host.GetRowText(0));

        table.Visibility = Visibility.Visible; // the table got selected in the app → show its tab
        host.RunUntilIdle();
        Assert.True(ColumnOf(host, 0, "Table") >= 0);

        table.IsSelected = true;
        host.RunUntilIdle();
        Assert.True(table.IsSelected);
        Assert.Contains("Merge", string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText)));
    }

    [Fact] // selecting a HIDDEN contextual tab redirects to a content tab (a Collapsed tab is not a content tab)
    public void Contextual_SelectHiddenTab_Redirects()
    {
        using var host = NewHost();
        var (ribbon, home, table) = ContextualRibbon();
        table.Visibility = Visibility.Collapsed; // hidden from the start
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.SelectedIndex = ribbon.Items.IndexOf(table); // try to select the hidden tab
        host.RunUntilIdle();

        Assert.True(home.IsSelected);               // redirected to the first content tab
        Assert.False(table.IsSelected);
        Assert.Contains("Paste", string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText))); // never blank
    }

    [Fact] // hiding a NON-selected contextual tab does not disturb selection (only the ACTIVE-tab hide redirects)
    public void Contextual_HideInactiveTab_KeepsSelection()
    {
        using var host = NewHost();
        var (ribbon, home, table) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.True(home.IsSelected); // Home is the auto-selected content tab; the contextual Table is NOT selected

        table.Visibility = Visibility.Collapsed;
        host.RunUntilIdle();

        Assert.True(home.IsSelected); // selection unchanged — the hide of a non-active tab is a no-op for selection
    }

    [Fact] // audit #4: an all-contextual ribbon (File + one contextual tab) recovers its band when the tab re-shows
    public void Contextual_AllContextualRibbon_RecoversOnReshow()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        ribbon.Items.Add(new RibbonTab { Header = "File", IsFileTab = true });
        var table = Tab("Table", Group("Layout", new BarButton { Content = "Merge" }));
        table.IsContextual = true;
        ribbon.Items.Add(table);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.True(table.IsSelected); // the sole content tab is selected
        table.Visibility = Visibility.Collapsed; // hide the only content tab → nothing to select (band blank)
        host.RunUntilIdle();
        Assert.True(ribbon.SelectedIndex < 0);

        table.Visibility = Visibility.Visible; // re-show it → selection recovers onto it (band returns)
        host.RunUntilIdle();
        Assert.True(table.IsSelected);
        Assert.Contains("Merge", string.Join("\n", Enumerable.Range(0, 10).Select(host.GetRowText)));
    }

    [Fact] // audit #5 (dark/light flip re-skins): the contextual fill + underline are DynamicResource-wired, same layout
    public void Contextual_ThemeFlipReSkins()
    {
        using var host = NewHost();
        var (ribbon, _, _) = ContextualRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var beforePurple = Resolve(ribbon, ThemeKeys.PurpleBrush);
        var beforeCol = ColumnOf(host, 0, "Table");
        Assert.Equal(beforePurple, host.GetCell(beforeCol, 0).Style.Foreground);

        host.Application.RequestedThemeBase = host.Application.ActualThemeVariant.Base == ThemeBase.Dark ? ThemeBase.Light : ThemeBase.Dark;
        host.RunUntilIdle();

        var afterPurple = Resolve(ribbon, ThemeKeys.PurpleBrush);
        Assert.NotEqual(beforePurple, afterPurple); // the tier's purple changed
        var afterCol = ColumnOf(host, 0, "Table");
        Assert.Equal(afterPurple, host.GetCell(afterCol, 0).Style.Foreground); // the tab tracked the new purple
    }

    [Fact] // audit #1/#2: Left/Right arrow nav SKIPS a Collapsed contextual tab and continues in the pressed direction
    public void Contextual_ArrowNavSkipsHiddenTab()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var home = Tab("Home", Group("G", new BarButton { Content = "A" }));
        var table = Tab("Table", Group("L", new BarButton { Content = "M" }));
        table.IsContextual = true;
        var insert = Tab("Insert", Group("H", new BarButton { Content = "B" }));
        ribbon.Items.Add(home);   // 0
        ribbon.Items.Add(table);  // 1 (contextual, INTERIOR)
        ribbon.Items.Add(insert); // 2
        table.Visibility = Visibility.Collapsed; // hide the interior contextual tab
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.True(home.IsSelected);

        home.Focus(); // focus the Home header on the strip
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow); // Right skips the hidden Table and lands on Insert (not trapped, not redirected)
        host.RunUntilIdle();

        Assert.True(insert.IsSelected); // reached Insert past the hidden interior tab
        Assert.False(table.IsSelected);
    }

    [Fact] // audit #2: Right from the last visible tab, past a hidden TRAILING contextual tab, stays put (no backward jump)
    public void Contextual_ArrowNavPastTrailingHidden_StaysPut()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var home = Tab("Home", Group("G", new BarButton { Content = "A" }));
        var insert = Tab("Insert", Group("H", new BarButton { Content = "B" }));
        var table = Tab("Table", Group("L", new BarButton { Content = "M" }));
        table.IsContextual = true;
        ribbon.Items.Add(home);   // 0
        ribbon.Items.Add(insert); // 1
        ribbon.Items.Add(table);  // 2 (contextual, TRAILING)
        table.Visibility = Visibility.Collapsed;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        insert.IsSelected = true;
        host.RunUntilIdle();
        insert.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow); // nowhere visible to the right → stays on Insert (NOT a backward jump to Home)

        host.RunUntilIdle();
        Assert.True(insert.IsSelected);
        Assert.False(home.IsSelected);
    }

}
