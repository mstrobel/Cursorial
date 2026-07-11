// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Output;            // ColorDepth
using Cursorial.Rendering;         // Size, Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;         // ThemeBase

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// W6 — the automated proxy for the control-gallery demo's visual acceptance: every P9 control composed in a
/// single tree (a menu bar, a TabControl whose tabs hold a TextBox / CheckBox / RadioButton / two ProgressBars /
/// Buttons / Separators and a ListBox with a ContextMenu + ToolTip) renders on the real frame loop without a
/// throw, and re-skins live across every color tier + a dark/light flip — the gallery's 't'/'d' acceptance. The
/// per-control behavior is covered by the ControlMatrix sections; this proves the cross-control COMPOSITION +
/// theme-flip-with-everything-present, which the demo exercises by hand.
/// </summary>
public sealed class P9ControlCompositionTests
{
    private static (UIElement Root, ListBox List) BuildGallery()
    {
        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "_New", InputGestureText = "Ctrl+N" });
        file.Items.Add(new Separator());
        file.Items.Add(new MenuItem { Header = "E_xit" });
        menu.Items.Add(file);

        var form = new StackPanel { Spacing = 1, Margin = new Margins(1) };
        var name = new TextBox { Width = 24, Placeholder = "username" };
        ToolTipService.SetTip(name, "your login name");
        form.Children.Add(new Label { Content = "_Name:" });
        form.Children.Add(name);
        form.Children.Add(new Separator());
        form.Children.Add(new CheckBox { Content = " Wi-Fi", IsChecked = true });
        form.Children.Add(new RadioButton { Content = " Low", GroupName = "q" });
        form.Children.Add(new RadioButton { Content = " High", GroupName = "q", IsChecked = true });
        form.Children.Add(new ProgressBar { Value = 60, Maximum = 100, Height = 1, Width = 24 });
        form.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 1, Width = 24 });
        form.Children.Add(new ComboBox { ItemsSource = new[] { "One", "Two", "Three" }, SelectedIndex = 0, Width = 16 });

        var treeRoot = new TreeViewItem { Header = "Root", IsExpanded = true };
        treeRoot.Items.Add(new TreeViewItem { Header = "Child 1" });
        treeRoot.Items.Add(new TreeViewItem { Header = "Child 2" });
        var tree = new TreeView { Width = 22, Height = 4 };
        tree.Items.Add(treeRoot);
        form.Children.Add(tree);

        form.Children.Add(new Calendar { Today = new DateOnly(2026, 6, 18), DisplayDate = new DateOnly(2026, 6, 1), SelectedDate = new DateOnly(2026, 6, 18) });
        form.Children.Add(new DatePicker { Width = 16, Watermark = "date", DisplayDate = new DateOnly(2026, 6, 1) });

        form.Children.Add(new Button { Content = "_OK", IsDefault = true });

        var list = new ListBox
        {
            Width = 26, Height = 6,
            ItemsSource = new[] { "alpha", "bravo", "charlie", "delta", "echo" },
        };
        var ctx = new ContextMenu();
        ctx.Items.Add(new MenuItem { Header = "_Copy" });
        ctx.Items.Add(new MenuItem { Header = "_Delete" });
        ContextMenu.SetMenu(list, ctx);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "_Form", Content = new Border { Padding = new Margins(1), Child = form } });
        tabs.Items.Add(new TabItem { Header = "_List", Content = new Border { Padding = new Margins(1), Child = list } });

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        root.Children.Add(tabs);
        return (root, list);
    }

    [Fact]
    public void P9Controls_ComposedTogether_RenderAndReskinAcrossTiers()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 24) });

        // A page striping rule so the ListBox exercises the :alternate pseudo-class under the composition.
        host.Application.Styles.Add(new Style(Selectors.OfType<ListBoxItem>().PseudoClass("alternate"))
            .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush));

        var (root, list) = BuildGallery();
        host.ShowRoot(root);
        // Builds + lays out + renders the whole P9 control set — no throw. (The gallery's indeterminate ProgressBar
        // animates perpetually, so the app never reaches idle; RunUntilIdle runs to its frame cap and that's
        // expected — layout/style still converge. The composition assertions below are the real acceptance.)
        host.RunUntilIdle();

        // The deepest composition realized: TabControl > TabItem > Border > Border > ListBox > containers.
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));

        // Re-skin across every color tier + a dark/light flip — the gallery's 't'/'d' acceptance, no throw.
        foreach (var tier in new ColorDepth?[] { ColorDepth.Ansi256, ColorDepth.Ansi16, ColorDepth.NoColor, ColorDepth.Truecolor })
        {
            host.Application.RequestedColorTier = tier;
            host.RunUntilIdle();
        }

        host.Application.RequestedThemeBase = ThemeBase.Light;
        host.RunUntilIdle();
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0)); // still intact after the re-skins
    }
}
