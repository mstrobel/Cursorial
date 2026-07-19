using System.Text;

using Cursorial.Gallery;
using Cursorial.Gallery.Pages;
using Cursorial.Gallery.ViewModels;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Gallery;

// The standalone gallery (#107) is a real TTY app, so it can't be run in CI — this headless canary loads its
// XAML-first MVVM shell through UITestHost and asserts the implicit-DataTemplate page resolution + navigation work
// (the manual harness still gets exercised on every run).
public sealed class GallerySmokeTests(ITestOutputHelper output)
{
    private static string Screen(UIHeadlessHost host, int rows)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
            sb.AppendLine(host.GetRowText(r));
        return sb.ToString();
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                if (FindDescendant<T>(child) is { } found)
                    return found;
        return null;
    }

    private static IEnumerable<T> AllDescendants<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            yield return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                foreach (var found in AllDescendants<T>(child))
                    yield return found;
    }

    [Fact] // the shell loads from embedded XAML, binds to the ShellViewModel, and the first page (ScrollViewer) resolves
    public void Shell_LoadsFromXaml_RendersFirstPage()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });

        UIElement root = null!;
        var ex = Record.Exception(() =>
        {
            root = GalleryApp.BuildRoot(host.Application);
            host.ShowRoot(root);

            if (root.DataContext is ShellViewModel shell)
                shell.SelectedPage = shell.Pages.OfType<ScrollViewerPageViewModel>().Single();

            host.RunUntilIdle();
        });
        Assert.Null(ex);

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Gallery", screen);        // the title bar
        Assert.Contains("ScrollViewer", screen);   // the nav entry (first page, selected)
        Assert.Contains("Inputs", screen);         // the second nav entry
        Assert.Contains("Cycle V-bar", screen);    // the ScrollViewer page's toggle bar (the implicit template resolved)
        Assert.Contains("row 000", screen);        // the scrollable content
        Assert.NotNull(FindDescendant<ScrollViewer>(root)); // the page view materialized
    }

    [Fact] // selecting a different page VM swaps the ContentControl's view via the implicit DataTemplate (the MVVM nav proof)
    public void Shell_Navigation_SwapsPageViaImplicitDataTemplate()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        Assert.IsType(shell.Pages[0].GetType(), shell.SelectedPage); // starts on the first page

        // Navigate to the Inputs page by moving the selection (the nav ListBox's SelectedItem is two-way bound to this).
        shell.SelectedPage = shell.Pages.OfType<InputsPageViewModel>().Single();
        host.RunUntilIdle();

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Password", screen);            // the Inputs view rendered
        Assert.Contains("Subscribe to updates", screen);
        Assert.DoesNotContain("Cycle V-bar", screen);   // the ScrollViewer page's chrome is gone
        Assert.NotNull(FindDescendant<PasswordBox>(root)); // the new control is in the showcase
        Assert.NotNull(FindDescendant<Slider>(root));
    }

    [Fact] // the Bars page resolves the Cursorial.UI.Bars Toolbar via the runtime XAML loader + renders bar controls,
           // auto-fills the BarButton labels from their BarCommands, and the Always item forces the overflow chevron
    public void BarsPage_RendersToolbar_WithOverflowChevron()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        shell.SelectedPage = shell.Pages.OfType<BarsViewModel>().Single();
        host.RunUntilIdle();

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.NotNull(FindDescendant<Toolbar>(root)); // the Cursorial.UI.Bars Toolbar materialized via the XAML loader
        Assert.Contains("Cut", screen);                // a BarButton label, auto-filled from the BarCommand's Text
        Assert.Contains("Copy", screen);
        Assert.Contains("Edit:", screen);              // the BarLabel caption
        Assert.Contains("»", screen);                  // the overflow chevron (forced by the Always-mode "Settings" item)

        var toolbar = FindDescendant<Toolbar>(root)!;
        var barsVm = shell.Pages.OfType<BarsViewModel>().Single();

        // Invoke Copy via its access key (Alt+C from "_Copy") while the bar is WIDE — Copy is on the visible row here
        // (at a narrow width it folds into the CLOSED » popup, where it is detached and its access key inactive). The
        // Alt-down + char go in one batch (the cue's sampled activation window). This proves the access-key →
        // BarCommand → bound status round-trip AND shortens SelectedPage.Status from the long seed to "Copy invoked.".
        host.Application.InputDispatcher.ProcessEvent(AltDown());
        host.Application.InputDispatcher.ProcessEvent(AltChar('C'));
        host.RunUntilIdle();
        Assert.Equal("Copy invoked.", barsVm.Status);

        // Narrowing folds more of the bar into the » popup — it must not throw and the chevron stays. Ordering matters:
        // the shell's bottom StatusBar wraps its SelectedPage.Status item in the ~22-col band right of the nav, growing
        // the bar vertically; with the long SEED text that squeezes this toolbar to zero rows at 44x24. Invoking a
        // command first (above) collapses the status to one line, so the FOLD — not the status squeeze — is what's
        // under test. A live user hits the same ordering: run a command, then narrow.
        host.SendResize(44, 24);
        host.RunUntilIdle();
        Assert.True(toolbar.Bounds.Rows > 0, $"toolbar should keep height once the status is short (was {toolbar.Bounds})");
        Assert.Contains("»", Screen(host, 24));
    }

    [Fact] // the Ribbon page (Surface B) resolves the Cursorial.UI.Bars Ribbon via the runtime XAML loader: its tabs +
           // group footers render, the RibbonTab Groups content + Ribbon.ButtonSize attached property resolve, and a
           // tab switch by click swaps the band
    public void RibbonPage_RendersRibbon_AndSwitchesTabs()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        shell.SelectedPage = shell.Pages.OfType<RibbonViewModel>().Single();
        host.RunUntilIdle();

        var ribbon = FindDescendant<Ribbon>(root);
        Assert.NotNull(ribbon); // the Ribbon materialized via the XAML loader (Groups content + Ribbon.ButtonSize)

        var screen = Screen(host, 24);
        output.WriteLine(screen);
        Assert.Contains("Home", screen);       // the RibbonTab strip
        Assert.Contains("Insert", screen);
        Assert.Contains("Clipboard", screen);  // a RibbonGroup footer (Home is auto-selected)
        Assert.Contains("Paste", screen);      // a Large bar button in the band

        // Click the Insert tab → the band swaps to Insert's group.
        var insert = AllDescendants<RibbonTab>(root).Single(t => t.Header as string == "Inse_rt");
        var origin = insert.TranslateToScreen(0, 0);
        host.SendClick(origin.Column + 1, origin.Row);
        host.RunUntilIdle();
        Assert.Contains("History", Screen(host, 24)); // the Insert band's group footer
    }

    [Fact] // the GalleryRibbon self-populates the QAT from the RibbonViewModel, and a described command auto-provisions a SuperTip
    public void RibbonPage_QuickAccessAndSuperTips_AreWired()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        shell.SelectedPage = shell.Pages.OfType<RibbonViewModel>().Single();
        host.RunUntilIdle();

        var ribbon = FindDescendant<Ribbon>(root)!;
        Assert.Equal(3, ribbon.QuickAccessCommands.Count);      // Undo/Redo/Paste populated by GalleryRibbon on DataContext set
        Assert.True(ribbon.QuickAccessCandidates.Count >= 3);   // the customize ▾ checklist candidates

        // A described command auto-provisioned a SuperTip on its bar control (hover help bound to the command).
        var paste = AllDescendants<BarButton>(root).First(b => b.Command is BarCommand { Text: "_Paste" });
        Assert.IsType<SuperTip>(ToolTipService.GetTip(paste));
    }

    [Fact] // KeyTips (#145): with EnableKeyTips armed (as Program.cs does), holding Alt over the gallery Ribbon shows the
           // badge overlay, and a tab letter drills into that tab's band — the live wiring, gated by the KittyTruecolor preset.
    public void RibbonPage_KeyTips_ArmOnAltAndDrill()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(80, 24),
            Capabilities = HeadlessCapabilities.KittyTruecolor, // satisfies the ND23 AltHeld gate
        });
        var controller = host.Application.EnableKeyTips(); // the same call Program.cs makes

        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        shell.SelectedPage = shell.Pages.OfType<RibbonViewModel>().Single();
        host.RunUntilIdle();

        var ribbon = FindDescendant<Ribbon>(root)!;
        Assert.False(controller.IsActive);

        // Alt down → the cue arms the KeyTip overlay over the ribbon (a dedicated hit-transparent surface appears).
        host.Application.InputDispatcher.ProcessEvent(AltDown());
        host.RunFrame();
        Assert.True(controller.IsActive);
        Assert.Contains(host.Application.WindowManager!.Surfaces, s => s.IsHitTestTransparent);

        // Drill the Insert tab (Alt+R, from "Inse_rt") → its band's group ("History") renders (the drill selected it).
        host.Application.InputDispatcher.ProcessEvent(AltChar('R'));
        host.RunFrame();
        Assert.Equal(ribbon.ItemContainerGenerator.IndexFromContainer(
            AllDescendants<RibbonTab>(root).Single(t => t.Header as string == "Inse_rt")), ribbon.SelectedIndex);
        Assert.True(controller.IsActive);            // still drilling (now at the group level)
        Assert.Contains("History", Screen(host, 24)); // the Insert band is shown

        // Esc twice backs out of the group level then dismisses the overlay.
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent { Key = Key.Escape, Modifiers = KeyModifiers.None, Kind = KeyEventKind.Down, Text = default, Timestamp = DateTimeOffset.UnixEpoch });
        host.RunFrame();
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent { Key = Key.Escape, Modifiers = KeyModifiers.None, Kind = KeyEventKind.Down, Text = default, Timestamp = DateTimeOffset.UnixEpoch });
        host.RunFrame();
        Assert.False(controller.IsActive);
    }

    private static KeyEvent AltDown() =>
        new() { Key = Key.LeftAlt, Modifiers = KeyModifiers.Alt, Kind = KeyEventKind.Down, Text = default, Timestamp = DateTimeOffset.UnixEpoch };

    private static KeyEvent AltChar(char c) =>
        new() { Key = Key.Character, Modifiers = KeyModifiers.Alt, Kind = KeyEventKind.Down, Text = c.ToString().AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };



    [Fact] // two-way bindings on the Inputs page round-trip VM <-> control (typing into the bound TextBox updates the VM)
    public void InputsPage_TwoWayBinding_RoundTrips()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        var inputs = shell.Pages.OfType<InputsPageViewModel>().Single();
        shell.SelectedPage = inputs;
        host.RunUntilIdle();

        var textBox = FindDescendant<TextBox>(root)!; // the first editable field is the Name TextBox
        textBox.Focus();
        host.RunUntilIdle();
        host.SendText("Ada");
        host.RunUntilIdle();

        Assert.Equal("Ada", inputs.Name);              // control -> VM (two-way)
        Assert.Contains("Name=\"Ada\"", inputs.Status); // and the live status reflects it
    }

    [Fact] // the Journal field is the undo/redo canary: the VM Undo/Redo commands (the button path, editor passed via
           // x:Reference) AND the field's own Ctrl+Z all drive the multi-line TextBox's history
    public void InputsPage_Journal_UndoRedo()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var shell = (ShellViewModel)root.DataContext!;
        var inputs = shell.Pages.OfType<InputsPageViewModel>().Single();
        shell.SelectedPage = inputs;
        host.RunUntilIdle();

        // The Journal is the one multi-line field (AcceptsReturn) — the Name/Permissions TextBoxes and the
        // PasswordBox (a TextBox subclass) are single-line.
        var journal = AllDescendants<TextBox>(root).Single(t => t.AcceptsReturn);
        var initial = inputs.Journal;
        Assert.Contains("\n", initial); // the seeded text is genuinely multi-line

        journal.Focus();
        journal.CaretIndex = journal.Text.Length; // seals coalescing; append after the seed text
        host.RunUntilIdle();
        host.SendText(" Extra.");
        host.RunUntilIdle();
        Assert.EndsWith(" Extra.", inputs.Journal); // typed text round-tripped to the VM (two-way)

        // Undo via the VM command — the editor arrives as the command parameter (the x:Reference button wiring).
        Assert.True(inputs.UndoCommand.CanExecute(journal));
        inputs.UndoCommand.Execute(journal);
        host.RunUntilIdle();
        Assert.Equal(initial, inputs.Journal); // the typed run was reverted in one undo

        inputs.RedoCommand.Execute(journal);
        host.RunUntilIdle();
        Assert.EndsWith(" Extra.", inputs.Journal); // and redone

        // The field's own keyboard chord drives the same history.
        host.SendKey(Key.Character, KeyModifiers.Control, "z");
        host.RunUntilIdle();
        Assert.Equal(initial, inputs.Journal);
    }

    [Fact] // The chessboard primitive (#107, a future page): content-assisted LEADING-EDGE snapping via IScrollContentHost.
           // The viewport height (24) is a whole number of 4-row tiles, so the vertical offsets are exact (4, then 0).
    public void Chessboard_SnapsScrollToWholeTiles()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(80, 24) });
        var board = new Chessboard();
        var sv = new ScrollViewer
        {
            Content = board,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // Hidden scrolls without a bar
            Focusable = true,
        };
        host.ShowRoot(sv);
        host.RunUntilIdle();
        sv.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(4, sv.VerticalOffset);   // bottom edge snapped onto a 4-row tile boundary (24 → 28 → offset 4)

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.InRange(sv.HorizontalOffset, 1, 8); // leading-edge: scroll right advances 1..8 cells to align the right edge

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(0, sv.VerticalOffset);

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(0, sv.HorizontalOffset);
    }

    [Fact] // Leading-edge snap math (the reported case), exercised directly with a controlled non-tile-multiple viewport.
    public void Chessboard_LeadingEdgeSnap_RevealsTheTrailingTile()
    {
        var board = new Chessboard();
        IScrollContentHost host = board;
        host.SetViewport(new Size(77, 24)); // 77 is NOT a multiple of the 8-wide tile → a tile would be cut

        Assert.Equal(3, host.LineStep(0, +1, vertical: false));
        Assert.Equal(8, host.LineStep(3, +1, vertical: false));
        Assert.Equal(3, host.LineStep(11, -1, vertical: false));
        Assert.Equal(8, host.LineStep(8, -1, vertical: false));
    }

    private static void Click(UIHeadlessHost host, int column, int row, MouseButton button = MouseButton.Left)
    {
        // Move first: a real pointer is over the cell before it presses. Under motion-reporting caps the
        // hover chain is maintained by Move events alone, and ButtonBase's release-click requires
        // IsPointerOver at the Up (C187/C188) — a bare Down/Up pair never clicks.
        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.Move, Position = new CellPosition(column, row), Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None, Modifiers = KeyModifiers.None, Timestamp = DateTimeOffset.UnixEpoch,
        });
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown, Position = new CellPosition(column, row), Button = button,
            ButtonsHeld = button == MouseButton.Left ? MouseButtons.Left : MouseButtons.Right,
            Modifiers = KeyModifiers.None, Timestamp = DateTimeOffset.UnixEpoch,
        });
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp, Position = new CellPosition(column, row), Button = button,
            ButtonsHeld = MouseButtons.None, Modifiers = KeyModifiers.None, Timestamp = DateTimeOffset.UnixEpoch,
        });
    }

    [Fact] // the Event Routing page: interactions render live route logs — a button click's route shows the
           // template chrome + boundary; a context-menu item click shows the popup→owner bridge hop (the menu
           // content lives on a separate surface); a key press logs the route from the focused element.
    public void EventRouting_Page_LogsMouseAndKeyRoutes()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(120, 40) });
        var root = GalleryApp.BuildRoot(host.Application);
        host.ShowRoot(root);
        var shell = (ShellViewModel)root.DataContext!;
        shell.SelectedPage = shell.Pages.OfType<EventRoutingViewModel>().Single();
        host.RunUntilIdle();

        var screen = Screen(host, 40);
        Assert.Contains("Event Routing", screen);
        Assert.Contains("Mouse-down route", screen);
        Assert.Contains("Key-down route", screen);

        // A left-click on "Click Me": the down's route passes the Button's template chrome (dimmed, badged)
        // and crosses the template boundary into the Button itself.
        // Route-content assertions read the LOG panels' TextBlocks directly — immune to the log ScrollViewer's
        // viewport clipping and to column wrapping on the rendered screen. Scoped to the two log ScrollViewers
        // (NOT the whole view): the static legend and scenario captions contain phrases like "template
        // boundary" and "logical bridge", which would satisfy loose assertions vacuously.
        var view = FindDescendant<EventRoutingView>(root)!;
        // Per-log readers (mouse first, key second — the two log ScrollViewers in grid-row order): post-
        // narrowing the two logs tell DIFFERENT stories, and the assertions pin exactly that.
        string LogText(int index) => string.Join("\n",
            AllDescendants<TextBlock>(AllDescendants<ScrollViewer>(view).ElementAt(index)).Select(t => t.Text));
        string MouseLog() => LogText(0);
        string KeyLog() => LogText(1);

        var clickMe = AllDescendants<Button>(root).Single(b => b.Name == "ClickMe");
        var buttonCell = clickMe.TranslateToScreen(1, 0);
        Click(host, buttonCell.Column, buttonCell.Row);
        host.RunUntilIdle();

        var log = MouseLog();
        Assert.Contains("MouseDown", log);
        Assert.Contains("chrome of Button", log);
        Assert.Contains("── template boundary ──", log); // the full rendered line — the legend's phrasing can't match

        // Scenario 2 — presented content: the route follows the VISUAL chain (through the ContentPresenter),
        // and the ⟨logical: …⟩ badge marks the divergent logical parent on the presented Button.
        var presented = AllDescendants<Button>(root).Single(b => b.Name == "Presented");
        var presentedCell = presented.TranslateToScreen(1, 0);
        Click(host, presentedCell.Column, presentedCell.Row);
        host.RunUntilIdle();

        log = MouseLog();
        Assert.Contains("Button \"Presented\"", log);
        Assert.Contains("⟨logical: ContentControl⟩", log);

        // A right-click release opens the context menu (router default); clicking an item then logs a route
        // that crosses the popup surface back to the owner — the ══ bridge line.
        var rightClickMe = AllDescendants<ContentControl>(root).Single(c => c.Name == "RightClickMe");
        var hostCell = rightClickMe.TranslateToScreen(1, 0);
        Click(host, hostCell.Column, hostCell.Row, MouseButton.Right);
        host.RunUntilIdle();

        var copyItem = host.Application.WindowManager!.Surfaces
            .SelectMany(s => AllDescendants<MenuItem>(s.Root))
            .Single(m => Equals(m.Header, "_Copy"));

        // KEY route first, while the menu is still open: from inside the menu it takes the LOGICAL seam
        // hop to the Popup element — and stops there (the placement leg is an ownership relation, never a
        // route).
        Assert.True(copyItem.Focus());
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.F1, Kind = KeyEventKind.Down, Modifiers = KeyModifiers.None,
            Text = default, Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        var keyLog = KeyLog();
        Assert.Contains("MenuItem", keyLog);
        Assert.Contains("══ surface → owner · logical bridge ══", keyLog);   // ContextMenu → its hosting Popup
        Assert.Contains("Popup", keyLog);                                    // the route's last node
        Assert.DoesNotContain("placement-target bridge", keyLog);            // never a route leg
        Assert.DoesNotContain("RightClickMe", keyLog);
        // The seam-hop lines must be VISIBLE at the default size: a bridged route auto-scrolls its log.
        Assert.Contains("══", Screen(host, 40));

        // The closing MOUSE click on the item: pointer routes are SURFACE-SCOPED — contained to the popup,
        // with the explicit containment marker where the ownership chain continues but the route does not.
        var itemCell = copyItem.TranslateToScreen(1, 0);
        Click(host, itemCell.Column, itemCell.Row);
        host.RunUntilIdle();

        log = MouseLog();
        Assert.Contains("MenuItem", log);
        Assert.Contains("ContextMenu", log);        // the route's LAST node — the popup surface root
        Assert.Contains("route ends here", log);    // the containment marker (flipped-assertion guard)
        Assert.DoesNotContain("══", log);           // no seam hop, ever
        Assert.DoesNotContain("RightClickMe", log); // and the owner is unreachable from a mouse route

        // Scenario 4 — the in-tree popup: toggle it open, click inside, and the route crosses the seam via
        // the LOGICAL bridge only (the Popup element sits in the host tree — contrast with scenario 3).
        var toggle = AllDescendants<ToggleButton>(root).Single(t => t.Name == "PopupToggle");
        var toggleCell = toggle.TranslateToScreen(1, 0);
        Click(host, toggleCell.Column, toggleCell.Row);
        host.RunUntilIdle();
        Assert.True(toggle.IsChecked == true, "first toggle click should open the popup");

        var inPopup = host.Application.WindowManager.Surfaces
            .SelectMany(s => AllDescendants<Button>(s.Root))
            .Single(b => b.Name == "InPopup");
        var inPopupCell = inPopup.TranslateToScreen(1, 0);
        Click(host, inPopupCell.Column, inPopupCell.Row);
        host.RunUntilIdle();

        log = MouseLog();
        Assert.Contains("Button \"InPopup\"", log);
        Assert.DoesNotContain("══", log); // pointer routes stop at the popup surface — no seam hop

        // The KEY route from popup content hops the LOGICAL seam to the in-tree Popup element and then
        // CONTINUES visually through the owner chain (contrast scenario 3, where the standalone popup's
        // route ends at the Popup — the placement leg is never a route).
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.F1, Kind = KeyEventKind.Down, Modifiers = KeyModifiers.None,
            Text = default, Timestamp = DateTimeOffset.UnixEpoch,
        }); // the popup focuses InPopup on open, so the key targets popup content
        host.RunUntilIdle();

        var keyLog2 = KeyLog();
        Assert.Contains("Popup \"TreePopup\"", keyLog2);
        Assert.Contains("══ surface → owner · logical bridge ══", keyLog2);
        Assert.DoesNotContain("placement-target bridge", keyLog2);
        Assert.Contains("EventRoutingView", keyLog2); // the in-tree route CONTINUES into the owner chain

        // Clicking the checked toggle CLOSES the popup — pins KeepOpenOnAnchorPress (without it, light
        // dismiss fires on the press and the release's Click re-opens: the dismiss/toggle race).
        Click(host, toggleCell.Column, toggleCell.Row);
        host.RunUntilIdle();
        Assert.True(toggle.IsChecked == false, "second toggle click should close the popup, not re-open it");
        Assert.DoesNotContain(host.Application.WindowManager.Surfaces,
            s => AllDescendants<Button>(s.Root).Any(b => b.Name == "InPopup"));

        // A key press routes from the focused element — focus the scenario TextBox and type.
        var input = AllDescendants<TextBox>(root).Single(t => t.Name == "Input");
        host.Application.FocusManager.SetFocus(input);
        host.Application.InputDispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.Character, Text = "x".AsMemory(), Kind = KeyEventKind.Down,
            Modifiers = KeyModifiers.None, Timestamp = DateTimeOffset.UnixEpoch,
        });
        host.RunUntilIdle();

        log = KeyLog();
        Assert.Contains("KeyDown", log);
        Assert.Contains("TextBox \"Input\"", log);
    }
}
