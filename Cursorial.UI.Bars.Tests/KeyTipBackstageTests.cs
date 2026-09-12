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

// KeyTips through the Backstage (maintainer, 2026-09-12: "key tips don't play well with backstage — the ribbon doesn't
// know its backstage content, only whether the request was handled"). The File tab is a drill over WHATEVER its
// request opens: the controller diffs the surface stack across the reveal and builds the next level over the new
// window (FullScreen) or popup (Menu) — the Backstage's destinations badged by their header mnemonics. Esc closes the
// Backstage and returns to the ribbon level with the overlay still up; a destination's badge selects it and exits.
public sealed class KeyTipBackstageTests
{
    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(100, 24), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static KeyEvent Key_(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null, KeyEventKind kind = KeyEventKind.Down)
        => new() { Key = key, Modifiers = modifiers, Kind = kind, Text = (text ?? string.Empty).AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };

    private static void AltDown(UIHeadlessHost host) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.Alt));
    private static void TypeKeyTip(UIHeadlessHost host, char c) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.Alt, c.ToString()));
    private static void Escape(UIHeadlessHost host) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape));

    private static void SettleLevel(UIHeadlessHost host, KeyTipController controller, int depth)
    {
        for (var i = 0; i < 10 && controller.LevelDepthForTests != depth; i++)
            host.RunFrame();
        Assert.Equal(depth, controller.LevelDepthForTests);
    }

    // A ribbon whose File tab hosts a real Backstage through BackstageHost (the Gallery's wiring), in the given mode.
    private static (Ribbon Ribbon, Backstage Backstage, BackstageItem Open, BackstageItem Save) Build(UIHeadlessHost host, BackstageDisplayMode mode)
    {
        var ribbon = new Ribbon();
        var file = new RibbonTab { Header = "File", IsFileTab = true };
        ribbon.Items.Add(file);
        var home = new RibbonTab { Header = "Home" };
        home.Groups.Add(new RibbonGroup { Header = "Clipboard", Items = { new BarButton { Content = "Paste" } } });
        ribbon.Items.Add(home);

        var backstage = new Backstage { DisplayMode = mode };
        var open = new BackstageItem { Header = "_Open", Content = new TextBlock { Text = "Open a document." } };
        var save = new BackstageItem { Header = "_Save", Content = new TextBlock { Text = "Save the document." } };
        backstage.Items.Add(new BackstageItem { Header = "_New", Content = new TextBlock { Text = "New document." } });
        backstage.Items.Add(open);
        backstage.Items.Add(save);

        var root = new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, new TextBox() } };
        root.AddHandler(Ribbon.BackstageRequestedEvent, (_, e) =>
        {
            e.Handled = true;
            _ = BackstageHost.ShowAsync(backstage, e.Source as UIElement ?? ribbon);
        });
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (ribbon, backstage, open, save);
    }

    [Theory]
    [InlineData(BackstageDisplayMode.FullScreen)]
    [InlineData(BackstageDisplayMode.Menu)]
    public void FileTab_DrillsIntoTheBackstage_DestinationBadgeSelects(BackstageDisplayMode mode)
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();
        var (_, backstage, open, save) = Build(host, mode);

        AltDown(host);
        TypeKeyTip(host, 'F');                    // opens the Backstage (a window or a popup — the ribbon never knows which)
        SettleLevel(host, controller, 2);         // the level is built over whatever opened
        Assert.True(controller.IsActive);
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(open)!.Visibility);
        Assert.Equal("O", controller.BadgeForTargetForTests(open)!.KeyTipText);
        Assert.Equal("S", controller.BadgeForTargetForTests(save)!.KeyTipText);

        TypeKeyTip(host, 'S');                    // selects the destination and exits
        host.RunUntilIdle();
        Assert.Same(save, backstage.SelectedItem);
        Assert.False(controller.IsActive);
    }

    [Theory]
    [InlineData(BackstageDisplayMode.FullScreen)]
    [InlineData(BackstageDisplayMode.Menu)]
    public void EscInTheBackstageLevel_ClosesIt_AndReturnsToTheRibbonLevel(BackstageDisplayMode mode)
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, open, _) = Build(host, mode);
        var home = (RibbonTab)ribbon.Items[1];

        AltDown(host);
        TypeKeyTip(host, 'F');
        SettleLevel(host, controller, 2);
        Assert.NotNull(controller.BadgeForTargetForTests(open));

        Escape(host);                             // pops the Backstage level: its surface closes, the ribbon level is back
        host.RunUntilIdle();
        for (var i = 0; i < 4; i++)
            host.RunFrame();
        Assert.True(controller.IsActive);
        Assert.Equal(1, controller.LevelDepthForTests);
        Assert.Equal(1, host.Application.WindowManager!.Surfaces.Count(s => !s.IsHitTestTransparent)); // only the root remains
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(home)!.Visibility);

        TypeKeyTip(host, 'H');                    // the ribbon drills again as if nothing happened
        SettleLevel(host, controller, 2);
        Assert.Equal(1, ribbon.SelectedIndex);
    }

    [Fact] // The Backstage's own ◂ (BackRequested) closes it: the overlay follows — back at the ribbon level, still up.
    public void BackstagesOwnBack_PopsTheLevel()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();
        var (_, backstage, _, _) = Build(host, BackstageDisplayMode.FullScreen);

        AltDown(host);
        TypeKeyTip(host, 'F');
        SettleLevel(host, controller, 2);

        backstage.RaiseEvent(new RoutedEventArgs(Backstage.BackRequestedEvent, backstage));
        host.RunUntilIdle();
        for (var i = 0; i < 4; i++)
            host.RunFrame();

        Assert.True(controller.IsActive);
        Assert.Equal(1, controller.LevelDepthForTests);
    }
}
