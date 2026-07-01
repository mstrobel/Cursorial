using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Bars;

// The Ribbon (Surface B): a TabControl-shaped host of RibbonTab tabs over a band of RibbonGroups, hosting the SAME
// bar controls a Toolbar hosts, bound to the SAME BarCommands. P2 core: docked, single-density render + commands.
public sealed class RibbonTests
{
    private static UITestHost NewHost(int w = 64, int h = 10) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

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

    [Fact] // arrowing onto the File tab from the strip keeps focus AND selection on the content tab (no divergence
           // between :selected and :focus-visible — the redirect is honored by the focus target)
    public void Ribbon_ArrowOntoFileTabTracksSelection()
    {
        using var host = NewHost();
        var ribbon = new Ribbon();
        var file = new RibbonTab { Header = "File", IsFileTab = true };
        var home = Tab("Home", Group("G", new BarButton { Content = "A" }));
        ribbon.Items.Add(file);
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(1, ribbon.SelectedIndex); // Home (File skipped)

        home.Focus(); // focus the Home tab header (on the strip)
        host.RunUntilIdle();
        host.SendKey(Key.LeftArrow); // targets index 0 = the File tab

        host.RunUntilIdle();
        Assert.Equal(1, ribbon.SelectedIndex); // selection did NOT stick on File
        Assert.True(home.IsFocused);           // focus tracks the settled selection…
        Assert.False(file.IsFocused);          // …and is NOT stranded on the un-selectable File tab
    }
}
