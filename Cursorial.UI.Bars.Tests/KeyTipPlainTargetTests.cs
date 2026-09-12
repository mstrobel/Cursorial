using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Bars.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Bars;

// Compose-both (keytips-design §12's deferral, built 2026-09-12): the active scope's PLAIN access-key targets — a page's
// check boxes, buttons, labels — join the overlay's root level with their mnemonic as the badge, placed inline where
// their cue would be (the cue itself stays suspended), so a content access key remains reachable while the overlay owns
// Alt. Collisions go through the one policy: an explicit key wins, colliding auto letters are suffixed — including a
// content letter beside a tab letter, and content targets that would multi-match under the access-key manager.
public sealed class KeyTipPlainTargetTests
{
    private static UIHeadlessHost NewHost(int w = 100, int h = 20) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static KeyEvent Key_(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null, KeyEventKind kind = KeyEventKind.Down)
        => new() { Key = key, Modifiers = modifiers, Kind = kind, Text = (text ?? string.Empty).AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };

    private static void AltDown(UIHeadlessHost host) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.Alt));
    private static void TypeKeyTip(UIHeadlessHost host, char c) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.Alt, c.ToString()));

    private static Ribbon RibbonWith(params string[] tabHeaders)
    {
        var ribbon = new Ribbon();
        foreach (var header in tabHeaders)
        {
            var tab = new RibbonTab { Header = header };
            tab.Groups.Add(new RibbonGroup { Header = "Group", Items = { new BarButton { Content = "Item" } } });
            ribbon.Items.Add(tab);
        }

        return ribbon;
    }

    private static AccessTextPresenter? FindPresenter(UIElement root)
    {
        if (root is AccessTextPresenter p) return p;
        for (var i = 0; i < root.VisualChildrenCount; i++)
            if (FindPresenter(root.GetVisualChild(i)) is { } found) return found;
        return null;
    }

    [Fact] // A page check box and button get their mnemonic badges beside the ribbon's tab badges; the badge toggles / clicks.
    public void PlainTargets_JoinTheRootLevel_AndActivate()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = RibbonWith("Home");
        var links = new CheckBox { Content = "Include _Links" };
        var go = new Button { Content = "_Go" };
        var page = new StackPanel { Orientation = Orientation.Vertical, Children = { links, go } };
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, page } });
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();
        Assert.True(controller.IsActive);
        Assert.NotNull(controller.BadgeForTargetForTests((RibbonTab)ribbon.Items[0]));             // the bar's badge…
        var linksBadge = controller.BadgeForTargetForTests(links)!;
        Assert.Equal(Visibility.Visible, linksBadge.Visibility);                                   // …and the page's
        Assert.Equal("L", linksBadge.KeyTipText);
        Assert.Equal("G", controller.BadgeForTargetForTests(go)!.KeyTipText);

        // Inline: over the mnemonic's cell ("Include _Links" → cluster 8), and the underline cue stays suspended.
        var presenter = FindPresenter(links)!;
        Assert.Equal(presenter.TranslateToScreen(8, 0), (Canvas.GetLeft(linksBadge) ?? -1, Canvas.GetTop(linksBadge) ?? -1));
        Assert.False(AccessKeyManager.GetShowUnderline(presenter));

        TypeKeyTip(host, 'L');                    // toggles the check box (a badge names it outright) and exits
        host.RunUntilIdle();
        Assert.True(links.IsChecked == true);
        Assert.False(controller.IsActive);

        var clicked = false;
        go.Click += (_, _) => clicked = true;
        AltDown(host);
        host.RunFrame();
        TypeKeyTip(host, 'G');
        host.RunUntilIdle();
        Assert.True(clicked);
    }

    [Fact] // A content letter colliding with a tab letter: both suffixed in document order (the tab first), both reachable.
    public void PlainTarget_CollidingWithATab_BothSuffixed()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = RibbonWith("Blocks", "View");
        var buttons = new CheckBox { Content = "Include _Buttons" };
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, buttons } });
        host.RunUntilIdle();
        var blocks = (RibbonTab)ribbon.Items[0];

        AltDown(host);
        host.RunFrame();
        Assert.Equal("B0", controller.BadgeForTargetForTests(blocks)!.KeyTipText);
        Assert.Equal("B1", controller.BadgeForTargetForTests(buttons)!.KeyTipText);

        TypeKeyTip(host, 'B');                    // shared prefix — both stay viable
        TypeKeyTip(host, '1');
        host.RunUntilIdle();
        Assert.True(buttons.IsChecked == true);
        Assert.False(controller.IsActive);

        AltDown(host);
        host.RunFrame();
        TypeKeyTip(host, 'B');
        TypeKeyTip(host, '0');                    // drills the Blocks tab
        host.RunFrame();
        Assert.Equal(0, ribbon.SelectedIndex);
        Assert.Equal(2, controller.LevelDepthForTests);
    }

    [Fact] // Content targets that would MULTI-MATCH under the access-key manager (cycling focus) get suffixes and activate directly.
    public void PlainTargets_ThatWouldMultiMatch_GetSuffixes_AndActivateDirectly()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = RibbonWith("Home");
        var alpha = new CheckBox { Content = "_Alpha" };
        var amber = new CheckBox { Content = "_Amber" };
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, alpha, amber } });
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();
        Assert.Equal("A0", controller.BadgeForTargetForTests(alpha)!.KeyTipText);
        Assert.Equal("A1", controller.BadgeForTargetForTests(amber)!.KeyTipText);

        TypeKeyTip(host, 'A');
        TypeKeyTip(host, '1');
        host.RunUntilIdle();
        Assert.True(amber.IsChecked == true);     // toggled outright — no focus cycling
        Assert.False(alpha.IsChecked == true);
    }

    [Fact] // A label's badge forwards to its target, like its access key does.
    public void LabelBadge_FocusesItsTarget()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = RibbonWith("Home");
        var name = new TextBox();
        var label = new Label { Content = "_Name", Target = name };
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, label, name } });
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();
        Assert.Equal("N", controller.BadgeForTargetForTests(label)!.KeyTipText);

        TypeKeyTip(host, 'N');
        host.RunUntilIdle();
        Assert.True(name.IsFocused);
        Assert.False(controller.IsActive);
    }

    [Fact] // A control INSIDE a badged bar is that bar's business — it is not badged again at the root level.
    public void BarControls_AreNotBadgedAtTheRootLevel()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var paste = new BarButton { Content = "_Paste" };
        home.Groups.Add(new RibbonGroup { Header = "Clipboard", Items = { paste } });
        ribbon.Items.Add(home);
        var toolbar = new Toolbar { Items = { new BarButton { Content = "_Cut" } } };
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, toolbar, new TextBox() } });
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();
        Assert.Null(controller.BadgeForTargetForTests(paste));                                    // level 2 material, not level 0
        Assert.NotNull(controller.BadgeForTargetForTests((UIElement)toolbar.Items[0]!));          // the toolbar host's own badge, once
        Assert.Equal(1, controller.LevelDepthForTests);                                           // still at the root
    }
}
