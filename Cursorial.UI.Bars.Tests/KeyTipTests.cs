using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Bars.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// KeyTips (#145 / keytips-design): the Alt-overlay accelerator. Alt arms an amber badge overlay riding
// AccessKeyManager's cue; letters walk a multi-level prefix drill (ribbon tab → group → control), Esc backs out.
// Driven headlessly through UITestHost on the KittyTruecolor preset (which satisfies the ND23 AltHeld gate).
public sealed class KeyTipTests
{
    private static UITestHost NewHost(TerminalCapabilities caps, int w = 80, int h = 14) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = caps });

    private static KeyEvent Key_(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null,
                                 KeyEventKind kind = KeyEventKind.Down)
        => new() { Key = key, Modifiers = modifiers, Kind = kind, Text = (text ?? string.Empty).AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };

    // Alt down → the cue turns on → the controller enters (in AltHeld mode).
    private static void AltDown(UITestHost host) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.Alt));

    // A drill/activate letter while Alt is held (Alt+letter is the drill gesture).
    private static void TypeKeyTip(UITestHost host, char c)
        => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.Alt, c.ToString()));

    // Home(H)[Font(F): Bold(B), Italic(T)]  Insert(I)[Tables(A): Table(E)] — deliberately collision-free letters.
    private static (Ribbon Ribbon, Button Bold, Button TableButton) NewRibbon()
    {
        var ribbon = new Ribbon();

        var home = new RibbonTab { Header = "Home" };
        var font = new RibbonGroup { Header = "Font" };
        var bold = new Button { Content = "Bold" };
        var italic = new Button { Content = "Talic" };   // 'T' — a distinct letter from Bold/Font
        font.Items.Add(bold);
        font.Items.Add(italic);
        home.Groups.Add(font);
        ribbon.Items.Add(home);

        var insert = new RibbonTab { Header = "Insert" };
        var tables = new RibbonGroup { Header = "Absble" };   // 'A' — distinct from Insert/Table
        var table = new Button { Content = "Edd" };           // 'E' — distinct
        tables.Items.Add(table);
        insert.Groups.Add(tables);
        ribbon.Items.Add(insert);

        return (ribbon, bold, table);
    }

    [Fact] // Gate: KittyTruecolor satisfies the ND23 AltHeld gate — Alt-down arms KeyTips.
    public void Gate_KittyTruecolor_AltArmsKeyTips()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(controller.IsActive);
        AltDown(host);
        Assert.True(controller.IsActive);

        host.RunFrame();
        Assert.Contains(host.Application.WindowManager!.Surfaces, s => s.IsHitTestTransparent); // the KeyTip overlay
    }

    [Fact] // Gate: a preset that fails the AltHeld gate (no Kitty keyboard) → KeyTips never arms.
    public void Gate_LegacyPreset_NeverArms()
    {
        using var host = NewHost(TestCapabilities.Ansi16Legacy);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();
        Assert.False(controller.IsActive);
    }

    [Fact] // Full ribbon drill: Alt → tab letter (selects + L1) → group letter (L2) → control letter (activates + exits).
    public void Ribbon_MultiLevelDrill_ActivatesLeaf()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, table) = NewRibbon();
        var clicked = false;
        table.Click += (_, _) => clicked = true;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Equal(0, ribbon.SelectedIndex);

        AltDown(host);
        TypeKeyTip(host, 'I');            // drill Insert (index 1)
        Assert.Equal(1, ribbon.SelectedIndex);
        host.RunFrame();                  // park builds L1 (the selected tab's groups)

        TypeKeyTip(host, 'A');            // drill the "Absble" group
        host.RunFrame();                  // park builds L2 (the group's controls)

        TypeKeyTip(host, 'E');            // activate "Edd"
        host.RunUntilIdle();

        Assert.True(clicked);
        Assert.False(controller.IsActive); // a leaf activation exits the overlay
    }

    [Fact] // The File tab activates Backstage instead of drilling.
    public void Ribbon_FileTab_RaisesBackstage()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var file = new RibbonTab { Header = "File", IsFileTab = true };
        ribbon.Items.Add(file);
        var home = new RibbonTab { Header = "Home" };
        home.Groups.Add(new RibbonGroup { Header = "Group" });
        ribbon.Items.Add(home);

        var backstage = false;
        ribbon.BackstageRequested += (_, _) => backstage = true;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'F');            // File
        host.RunUntilIdle();

        Assert.True(backstage);
        Assert.False(controller.IsActive);
    }

    [Fact] // Toolbar single level: Alt → a control letter activates it, no tab step.
    public void Toolbar_SingleLevel_ActivatesControl()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var cut = new BarButton { Content = "Xut" };   // 'X' distinct
        var clicked = false;
        cut.Click += (_, _) => clicked = true;
        var toolbar = new Toolbar();
        toolbar.Items.Add(cut);
        toolbar.Items.Add(new BarButton { Content = "Yopy" }); // 'Y'
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'X');
        host.RunUntilIdle();

        Assert.True(clicked);
        Assert.False(controller.IsActive);
    }

    [Fact] // Esc backs out one level; at the top it exits.
    public void Esc_PopsLevel_ThenExits()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'I');            // drill Insert → L1
        host.RunFrame();
        Assert.True(controller.IsActive);

        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape)); // pop L1 → back to L0
        host.RunFrame();
        Assert.True(controller.IsActive);

        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape)); // exit at L0
        host.RunFrame();
        Assert.False(controller.IsActive);
    }

    [Fact] // A non-matching letter bonks: the char is consumed (never leaks to a focused TextBox) and the overlay stays.
    public void Bonk_ConsumesChar_NoLeak()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var box = new TextBox();
        var (ribbon, _, _) = NewRibbon();
        var root = new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, box } };
        host.ShowRoot(root);
        host.RunUntilIdle();
        host.Application.FocusManager.SetFocus(box);

        AltDown(host);
        TypeKeyTip(host, 'Z');            // no badge is 'Z'
        host.RunFrame();

        Assert.True(controller.IsActive); // still armed (bonk, not exit)
        Assert.Equal("", box.Text ?? "");  // the char never reached the focused TextBox
    }

    [Fact] // A global gesture survives while KeyTips is active (a Ctrl chord falls through PreProcessInput).
    public void GlobalGesture_SurvivesWhileActive()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var (ribbon, _, _) = NewRibbon();
        var fired = false;
        ribbon.InputBindings.Add(new KeyBinding(new KeyGesture(Key.Character, KeyModifiers.Control, "S"), new BarCommand(() => fired = true)));
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        host.Application.FocusManager.SetFocus(ribbon);

        AltDown(host);
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.Control, "S")); // Ctrl+S
        host.RunUntilIdle();

        Assert.True(fired); // the Ctrl chord was NOT consumed by the KeyTip controller
    }

    [Fact] // Badges stay glued to their targets when the ribbon SCROLLS inside a ScrollViewer (an in-band composite
           // slide moves the tabs; the badges must follow via TranslateToScreen's scroll-offset walk).
    public void Badges_TrackTarget_WhenRibbonScrolls()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor, w: 80, h: 12);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();

        // [header][ribbon][tall filler] in a ScrollViewer: the header keeps the ribbon a few rows DOWN, so a small
        // scroll moves it up but leaves it on-screen — the badge must track the moved tab.
        var header = new Border { Height = 4 };
        var filler = new Border { Height = 40 };
        var stack = new StackPanel { Orientation = Orientation.Vertical, Children = { header, ribbon, filler } };
        var scroll = new ScrollViewer { Content = stack };
        host.ShowRoot(scroll);
        host.RunUntilIdle();

        var homeTab = (RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;

        AltDown(host);
        host.RunFrame();
        var badge = controller.BadgeForTargetForTests(homeTab)!;
        Assert.NotNull(badge);

        var before = homeTab.TranslateToScreen(0, 0);
        Assert.True(before.Row > 0);                        // the ribbon sits below the header
        Assert.Equal(before.Row, Canvas.GetTop(badge));     // badge glued to the tab initially

        scroll.VerticalOffset = 3;                          // an in-band composite slide — moves the ribbon up
        host.RunFrame();
        host.RunFrame();

        var after = homeTab.TranslateToScreen(0, 0);
        Assert.True(after.Row >= 0 && after.Row < before.Row); // the tab moved up but stayed on-screen
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal(after.Row, Canvas.GetTop(badge));      // …and the badge tracked it (no misalignment)
    }

    [Fact] // A target scrolled OFF the top of the viewport hides its badge (never stranded at the screen edge).
    public void Badge_HidesWhenTargetScrollsOffViewport()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor, w: 80, h: 8);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();

        var filler = new Border { Height = 60 };
        var stack = new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, filler } };
        var scroll = new ScrollViewer { Content = stack };
        host.ShowRoot(scroll);
        host.RunUntilIdle();

        var homeTab = (RibbonTab) ribbon.ItemContainerGenerator.ContainerFromIndex(0)!;

        AltDown(host);
        host.RunFrame();
        var badge = controller.BadgeForTargetForTests(homeTab)!;
        Assert.Equal(Visibility.Visible, badge.Visibility);

        scroll.VerticalOffset = 30;                          // scroll the ribbon far above the viewport top
        host.RunFrame();
        host.RunFrame();

        Assert.True(homeTab.TranslateToScreen(0, 0).Row < 0); // the tab is above the screen
        Assert.Equal(Visibility.Collapsed, badge.Visibility); // its badge is hidden, not clamped to the edge
    }

    [Fact] // Audit #8 (HIGH): after drilling a tab then Esc-ing back to L0, a DIFFERENT sibling tab is still drillable
           // (the popped-to level's typed prefix must be cleared, else every later letter bonks — the level bricks).
    public void EscBack_ThenReDrillSibling_Works()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'I');            // drill Insert → L1
        host.RunFrame();
        Assert.Equal(2, controller.LevelDepthForTests);

        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape)); // pop back to L0
        host.RunFrame();
        Assert.Equal(1, controller.LevelDepthForTests);

        TypeKeyTip(host, 'H');            // re-drill Home — would BONK (stay at depth 1) if L0.Typed weren't cleared
        host.RunFrame();
        Assert.Equal(2, controller.LevelDepthForTests);
    }

    [Fact] // Audit #1 (MEDIUM): Esc at the top level dismisses the overlay even when Alt is PHYSICALLY HELD — the real
           // wire carries the Alt bit (Alt+Esc), so the stale-Alt inference never fires; the overlay must still exit.
    public void EscAtTop_WithAltHeld_Exits()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);                    // Alt physically down (never released)
        host.RunFrame();
        Assert.True(controller.IsActive);

        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape, KeyModifiers.Alt)); // real held-mode Alt+Esc
        host.RunFrame();
        Assert.False(controller.IsActive); // dismissed despite Alt still held
    }

    [Fact] // Audit #9 (MEDIUM): drilling a tab that has NO groups doesn't brick the overlay — after the parked build
           // gives up it re-shows the root level, and a sibling with content is still drillable.
    public void DrillEmptyTab_RecoversAndReDrills()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var font = new RibbonGroup { Header = "Font" };
        font.Items.Add(new Button { Content = "Bold" });
        home.Groups.Add(font);
        ribbon.Items.Add(home);
        var empty = new RibbonTab { Header = "Umpty" };   // 'U' — a tab with no groups
        ribbon.Items.Add(empty);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'U');            // drill the empty tab → parked build returns null → gives up
        for (var i = 0; i < 12; i++)      // exceed MaxParkRetries (8)
            host.RunFrame();

        Assert.True(controller.IsActive);                    // not stuck / not exited
        Assert.Equal(1, controller.LevelDepthForTests);      // re-showed the root level

        TypeKeyTip(host, 'H');            // re-drill Home (has a group) — proves L0 is not bricked
        host.RunFrame();
        Assert.Equal(2, controller.LevelDepthForTests);
    }

    [Fact] // Audit #10 (LOW): a digit-leading tab header does NOT auto-derive a digit badge (which would collide with
           // the QAT digit badges); digits are excluded from first-letter derivation.
    public void DigitLeadingHeader_DerivesNoDigitBadge()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        _ = host.Application.EnableKeyTips();

        var tab = new RibbonTab { Header = "3D Tools" };
        var (keyTip, _) = KeyTipModel.Resolve(tab);
        Assert.Equal("D", keyTip);        // the first LETTER ('D'), never the leading digit '3'
    }

    [Fact] // A COLLAPSED tab (e.g. a contextual tab that's hidden) gets NO badge — otherwise its badge would derive a
           // letter and, having no arranged position, land at the ribbon origin over the first tab (the reported bug).
    public void CollapsedTab_GetsNoBadge()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        home.Groups.Add(new RibbonGroup { Header = "Font" });
        ribbon.Items.Add(home);
        var contextual = new RibbonTab { Header = "Table", Visibility = Visibility.Collapsed }; // hidden contextual tab
        contextual.Groups.Add(new RibbonGroup { Header = "Cells" });
        ribbon.Items.Add(contextual);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();

        Assert.True(controller.IsActive);
        Assert.Null(controller.BadgeForTargetForTests(contextual)); // no 'T' badge for the collapsed tab
        Assert.NotNull(controller.BadgeForTargetForTests(home));    // the visible tab still gets its badge

        // Typing the collapsed tab's would-be letter bonks (no such badge) instead of drilling it.
        TypeKeyTip(host, 'T');
        host.RunFrame();
        Assert.Equal(0, ribbon.SelectedIndex); // Home stays selected; the hidden tab was not drilled
    }

    [Fact] // QAT digit badges are activatable via the NUMPAD too (keyboard-first users), not only the number row.
    public void NumpadDigit_ActivatesDigitBadge()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var one = new BarButton { Content = "first" };
        KeyTip.SetKey(one, "1"); // an explicit digit badge (as the QAT assigns)
        var clicked = false;
        one.Click += (_, _) => clicked = true;
        var toolbar = new Toolbar();
        toolbar.Items.Add(one);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        AltDown(host);
        // Numpad "1" (Key.Numpad1) — not a Key.Character; the controller maps it to '1'.
        host.Application.InputDispatcher.ProcessEvent(
            new KeyEvent { Key = Key.Numpad1, Modifiers = KeyModifiers.Alt, Kind = KeyEventKind.Down, Text = default, Timestamp = DateTimeOffset.UnixEpoch });
        host.RunUntilIdle();

        Assert.True(clicked);
        Assert.False(controller.IsActive);
    }

    [Fact] // Layering: the KeyTip overlay sits just above the ROOT surface — below windows/popups — so a Backstage
           // window opened over the ribbon occludes the badges instead of the badges bleeding on top of it.
    public void Overlay_SitsAboveRoot_BelowWindows()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();
        var (ribbon, _, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        AltDown(host);
        host.RunFrame();
        var surfaces = host.Application.WindowManager!.Surfaces;

        var keyTipIndex = -1;
        for (var i = 0; i < surfaces.Count; i++)
        {
            if (surfaces[i].IsHitTestTransparent)
                keyTipIndex = i;
        }
        Assert.Equal(1, keyTipIndex); // index 0 is the root surface; the KeyTip overlay is directly above it

        // Open a window over the ribbon → it stacks ABOVE the KeyTip overlay (so its content occludes the badges).
        var window = new Window { Content = new Button { Content = "Modal" } };
        window.Show(host.Application.WindowManager);
        host.RunUntilIdle();

        var stack = host.Application.WindowManager.Surfaces.ToList();
        var keyTipPos = stack.FindIndex(s => s.IsHitTestTransparent);
        var windowPos = stack.FindIndex(s => ReferenceEquals(s.HostWindow, window));
        Assert.True(windowPos >= 0 && keyTipPos < windowPos); // KeyTip overlay is below the window
    }

    [Fact] // An OVERFLOWED toolbar control (in the closed overflow popup — off any live surface) gets NO visible badge,
           // rather than a stranded badge at the ribbon origin (the reported 'S'-for-Settings in the top-left corner).
    public void OverflowedControl_HasNoVisibleBadge()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var visible = new BarButton { Content = "Xut" };       // 'X' — stays in the row
        var overflowed = new BarButton { Content = "Zettings" }; // 'Z' — forced into the overflow popup
        Toolbar.SetOverflowMode(overflowed, ToolbarOverflowMode.Always);
        var toolbar = new Toolbar();
        toolbar.Items.Add(visible);
        toolbar.Items.Add(overflowed);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        Assert.True(toolbar.HasOverflow); // 'Zettings' is in the (closed) overflow popup

        AltDown(host);
        host.RunFrame();

        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(visible)!.Visibility);
        var overflowBadge = controller.BadgeForTargetForTests(overflowed);
        Assert.True(overflowBadge is null || overflowBadge.Visibility == Visibility.Collapsed); // never shown at the origin
    }

    [Fact] // After a KeyTip leaf activation in STICKY mode (Alt-tap), the Alt cue is fully DISMISSED — so the first Esc
           // is not eaten by the sticky-cue consume (otherwise it takes two Escs to close a just-opened Backstage).
    public void ActivationInStickyMode_DoesNotEatNextEscape()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var cut = new BarButton { Content = "Xut" };
        var toolbar = new Toolbar();
        toolbar.Items.Add(cut);
        var escFired = false;
        toolbar.InputBindings.Add(new KeyBinding(new KeyGesture(Key.Escape), new BarCommand(() => escFired = true)));
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        host.Application.FocusManager.SetFocus(toolbar);

        // Sticky mode: an Alt tap (down then chordless up) arms KeyTips AND sets the sticky cue.
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.Alt));
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.None, kind: KeyEventKind.Up));
        host.RunFrame();
        Assert.True(controller.IsActive);

        // Activate the leaf (unmodified letter in sticky mode) → KeyTips exits and DismissCue clears the sticky cue.
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.None, "X"));
        host.RunUntilIdle();
        Assert.False(controller.IsActive);

        // The FIRST Esc now reaches the InputBinding (a lingering sticky cue would have consumed it).
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape));
        host.RunUntilIdle();
        Assert.True(escFired);
    }

    [Fact] // The KeyTip hop sequence for a ribbon BAND control is Alt → tab → group → control (for SuperTips).
    public void HopSequence_RibbonBandControl_IsTabGroupControl()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        _ = host.Application.EnableKeyTips();
        var (ribbon, bold, _) = NewRibbon(); // Home(H) → Font(F) → Bold(B)
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal("Alt, H, F, B", KeyTip.GetHopSequence(bold));
    }

    [Fact] // A flat toolbar control's hop sequence is Alt → control.
    public void HopSequence_ToolbarControl_IsAltPlusControl()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        _ = host.Application.EnableKeyTips();
        var cut = new BarButton { Content = "Xut" };
        var toolbar = new Toolbar();
        toolbar.Items.Add(cut);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        Assert.Equal("Alt, X", KeyTip.GetHopSequence(cut));
    }

    [Fact] // No hop sequence when KeyTips isn't enabled on the app (the hint would be misleading).
    public void HopSequence_Null_WhenKeyTipsDisabled()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor); // note: EnableKeyTips NOT called
        var (ribbon, bold, _) = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Null(KeyTip.GetHopSequence(bold));
    }

    [Fact] // A described BarCommand auto-provisions a SuperTip whose Anchor is the control — so the tip can compute
           // its own hop sequence at show time.
    public void SuperTip_ProvisionedWithAnchor_ComputesHops()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        _ = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var font = new RibbonGroup { Header = "Font" };
        var boldButton = new BarButton { Command = new BarCommand(() => { }) { Text = "_Bold", Description = "Embolden." } };
        font.Items.Add(boldButton);
        home.Groups.Add(font);
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var tip = Assert.IsType<SuperTip>(ToolTipService.GetTip(boldButton));
        Assert.Same(boldButton, tip.Anchor);
        Assert.Equal("Alt, H, F, B", KeyTip.GetHopSequence(boldButton)); // _Bold folds to 'B'
    }

    [Fact] // Audit: a DISABLED bar control gets no hop hint — the overlay never badges it, so the hop would lie.
    public void HopSequence_Null_ForDisabledControl()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        _ = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var font = new RibbonGroup { Header = "Font" };
        var disabled = new BarButton { Content = "Bold", Command = new BarCommand(() => { }, () => false) };
        font.Items.Add(disabled);
        home.Groups.Add(font);
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(disabled.IsEffectivelyEnabled);  // CanExecute=false disables it
        Assert.Null(KeyTip.GetHopSequence(disabled)); // …so no (unreachable) hop hint
    }

    [Fact] // Audit: a control whose badge was DROPPED by a same-letter collision gets no hop; the survivor keeps it.
    public void HopSequence_CollisionDropped_NullForLoser_SurvivorKeeps()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        _ = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var font = new RibbonGroup { Header = "Font" };
        var bold = new BarButton { Content = "Bold" };     // 'B' — first-in-order wins
        var border = new BarButton { Content = "Border" }; // 'B' — collides → dropped
        font.Items.Add(bold);
        font.Items.Add(border);
        home.Groups.Add(font);
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.Equal("Alt, H, F, B", KeyTip.GetHopSequence(bold)); // the survivor's real hop
        Assert.Null(KeyTip.GetHopSequence(border));                // the dropped collider — no hop
    }

    [Fact] // Multi-char keytips: typing the shared prefix dims the matched letters + keeps only the viable badges.
    public void MatchedPrefix_MultiChar_FiltersToViable()
    {
        using var host = NewHost(TestCapabilities.KittyTruecolor);
        var controller = host.Application.EnableKeyTips();

        var fp = new BarButton { Content = "one" };
        var ff = new BarButton { Content = "two" };
        KeyTip.SetKey(fp, "FP");
        KeyTip.SetKey(ff, "FF");
        var fpClicked = false;
        fp.Click += (_, _) => fpClicked = true;

        var toolbar = new Toolbar();
        toolbar.Items.Add(fp);
        toolbar.Items.Add(ff);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'F');    // both FP/FF still viable — no commit yet
        host.RunFrame();
        Assert.True(controller.IsActive);

        TypeKeyTip(host, 'P');    // FP completes
        host.RunUntilIdle();
        Assert.True(fpClicked);
        Assert.False(controller.IsActive);
    }
}
