using System.Collections.ObjectModel;
using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §46 — <see cref="BreadcrumbBar"/>: the chip trail (BC1), focus-only arrow navigation (BC2/BC3), activation
/// through the <see cref="ButtonBase"/> path (BC4), the LEFT overflow fold behind the ellipsis chip and its
/// drop-down (BC5/BC6), one-tab-stop entry/exit (BC7), the chips ↔ edit-box hand-off (BC8/BC9/BC10), the
/// deliberately-unhandled chip-mode Escape (BC11), and the separator's sibling drop-down (BC12).
/// </summary>
public sealed class Section46_BreadcrumbBar
{
    private static readonly string[] Trail = ["Home", "Projects", "assets"];

    private static (UIHeadlessHost Host, BreadcrumbBar Bar) Show(int width = 40, string[]? items = null)
    {
        var host = UIHeadlessHost.Create(
            new UIHeadlessHostOptions
            {
                InitialSize = new Size(60, 10),
                Capabilities = HeadlessCapabilities.KittyTruecolor,
                CaptureFrameBytes = true
            });

        var bar = new BreadcrumbBar
        {
            ItemsSource = items ?? Trail,
            Width = width,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        host.ShowRoot(bar);
        host.RunUntilIdle();
        return (host, bar);
    }

    private static BreadcrumbBarItem Chip(BreadcrumbBar bar, int index)
        => Assert.IsType<BreadcrumbBarItem>(bar.ItemContainerGenerator.ContainerFromIndex(index));

    private static int Popups(UIHeadlessHost host) => host.Application.WindowManager!.Popups.Count;

    // ───────────────────────────── BC1 — the trail ─────────────────────────────

    [Fact] // BC1: items generate BreadcrumbBarItem chips, the trailing chip is :current, and the row renders the trail
    public void BC1_ChipsRenderAsATrail()
    {
        var (host, bar) = Show();
        using var _ = host;

        Assert.Equal(3, bar.ItemContainerGenerator.ContainerCount);
        Assert.False(Chip(bar, 0).IsCurrent);
        Assert.False(Chip(bar, 1).IsCurrent);
        Assert.True(Chip(bar, 2).IsCurrent); // :current is POSITIONAL — the deepest segment

        // …and the pseudo-class reaches the theme: ancestors are dim ink, the current segment is full strength.
        Assert.NotEqual(Chip(bar, 0).Foreground, Chip(bar, 2).Foreground);

        var row = host.GetRowText(0);
        Assert.Contains("Home", row);
        Assert.Contains("Projects", row);
        Assert.Contains("assets", row);
        Assert.Contains("▸", row);                     // the drill affordance sits on every chip
        Assert.EndsWith("▸", row.TrimEnd());           // including the current one — see BC1b
        Assert.False(bar.HasOverflow);
    }

    [Fact] // BC1b: EVERY chip keeps its "▸", the current one included — it is the drill affordance, not a divider
    public void BC1b_EveryChipKeepsItsSeparator()
    {
        var (host, bar) = Show();
        using var _ = host;

        // The separator opens THIS segment's children, so collapsing it on the deepest chip removed the one
        // drop-down a user most wants (drill deeper from where you already are) and left a single-segment trail
        // with no drop-down at all. Explorer shows the trailing chevron for the same reason.
        Assert.Equal(Visibility.Visible, Chip(bar, 0).SeparatorPart!.Visibility);
        Assert.Equal(Visibility.Visible, Chip(bar, 2).SeparatorPart!.Visibility);
        Assert.Equal(3, host.GetRowText(0).Count(c => c == '▸'));
    }

    [Fact] // BC1c: ↓ opens the ACTIVE segment's drop-down — the keyboard twin of pressing its "▸"
    public void BC1c_DownArrowOpensTheActiveSegmentsDropDown()
    {
        var (host, bar) = Show();
        using var _ = host;

        var asked = -1;
        bar.DropDownOpening += (_, e) =>
        {
            asked = e.Index;
            e.Children.Add("child-a");
            e.Children.Add("child-b");
        };

        bar.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);   // active := segment 1
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.Equal(1, asked);         // the ACTIVE segment — not the first, not the current
        Assert.Equal(1, Popups(host));  // without this gesture the child list was pointer-only
    }

    // ───────────────────────────── BC2/BC3 — arrow navigation is FOCUS ONLY ─────────────────────────────

    [Fact] // BC2: ←/→ move the active chip and raise NO activation (moving the highlight never navigates)
    public void BC2_ArrowsMoveFocusOnly()
    {
        var (host, bar) = Show();
        using var _ = host;

        var activations = 0;
        bar.ItemActivated += (_, _) => activations++;

        Chip(bar, 0).Focus();
        host.RunUntilIdle();
        Assert.Equal(0, bar.ActiveIndex);

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(1, bar.ActiveIndex);
        Assert.True(Chip(bar, 1).IsFocused);

        host.SendKey(Key.RightArrow);
        host.SendKey(Key.RightArrow); // at the end: the edge holds, no wrap
        host.RunUntilIdle();
        Assert.Equal(2, bar.ActiveIndex);

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(1, bar.ActiveIndex);

        Assert.Equal(0, activations); // focus only — nothing was navigated
    }

    [Fact] // BC3: Home/End jump to the first/last chip
    public void BC3_HomeEnd()
    {
        var (host, bar) = Show();
        using var _ = host;

        Chip(bar, 1).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(2, bar.ActiveIndex);

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, bar.ActiveIndex);
    }

    // ───────────────────────────── BC4 — activation ─────────────────────────────

    [Fact] // BC4: Enter/Space/click activate the ACTIVE chip — the routed ItemActivated plus the chip's Command
    public void BC4_EnterActivatesActiveChip()
    {
        var (host, bar) = Show();
        using var _ = host;

        var activated = new List<(object? Item, int Index)>();
        bar.ItemActivated += (_, e) => activated.Add((e.Item, e.Index));

        var commanded = 0;
        Chip(bar, 1).Command = new RelayCommand(_ => commanded++);

        Chip(bar, 1).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal([("Projects", 1)], activated);
        Assert.Equal(1, commanded); // ButtonBase's Command coupling, unchanged

        host.SendKey(Key.Character, text: " "); // the spacebar is (Character, " ") on every wire (ND10)
        host.RunUntilIdle();
        Assert.Equal(2, activated.Count);
        Assert.Equal(2, commanded);
    }

    [Fact] // BC4b: a chip whose Command cannot execute is disabled by IsEnabledCore and never activates
    public void BC4b_CommandGatesTheChip()
    {
        var (host, bar) = Show();
        using var _ = host;

        var activated = 0;
        bar.ItemActivated += (_, _) => activated++;

        var chip = Chip(bar, 0);
        chip.Command = new RelayCommand(_ => { }, _ => false);
        host.RunUntilIdle();

        Assert.False(chip.IsEffectivelyEnabled); // CanExecute folds into IsEnabledCore (CD25)
        Assert.False(chip.Focus());              // …so it is not even a focus target

        var origin = chip.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();
        Assert.Equal(0, activated); // a disabled chip does not navigate

        // …and arrow navigation steps over it (the ring filters on IsEffectivelyEnabled).
        Chip(bar, 1).Focus();
        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(1, bar.ActiveIndex);
    }

    // ───────────────────────────── BC5/BC6 — the LEFT overflow fold ─────────────────────────────

    [Fact] // BC5: too narrow ⇒ the ANCESTORS fold away behind a leading ellipsis chip; the deepest context survives
    public void BC5_OverflowFoldsFromTheLeft()
    {
        var (host, bar) = Show(width: 18);
        using var _ = host;

        Assert.True(bar.HasOverflow);
        Assert.Equal(2, bar.OverflowCount);

        // The elided ancestors are Collapsed (they paint nothing); the current segment always survives.
        Assert.Equal(Visibility.Collapsed, Chip(bar, 0).Visibility);
        Assert.Equal(Visibility.Collapsed, Chip(bar, 1).Visibility);
        Assert.Equal(Visibility.Visible, Chip(bar, 2).Visibility);

        var row = host.GetRowText(0);
        Assert.Contains("…", row);        // the leading ellipsis chip is up
        Assert.Contains("assets", row);   // the deepest context is what a breadcrumb is for
        Assert.DoesNotContain("Home", row);
        Assert.DoesNotContain("Projects", row);
        Assert.Equal(Visibility.Visible, bar.OverflowChipPart!.Visibility);
    }

    [Fact] // BC5b: widening un-folds — the elided chips come back and the ellipsis chip stands down
    public void BC5b_WideningUnfolds()
    {
        var (host, bar) = Show(width: 18);
        using var _ = host;
        Assert.True(bar.HasOverflow);

        bar.Width = 40;
        host.RunUntilIdle();

        Assert.False(bar.HasOverflow);
        Assert.Equal(0, bar.OverflowCount);
        Assert.Equal(Visibility.Visible, Chip(bar, 0).Visibility);
        Assert.Equal(Visibility.Collapsed, bar.OverflowChipPart!.Visibility);
        Assert.Contains("Home", host.GetRowText(0));
    }

    [Fact] // BC6: the ellipsis chip drops the ELIDED ancestors; picking one activates that ancestor
    public void BC6_EllipsisDropsTheElidedAncestors()
    {
        var (host, bar) = Show(width: 18);
        using var _ = host;

        var activated = new List<int>();
        bar.ItemActivated += (_, e) => activated.Add(e.Index);

        bar.OverflowChipPart!.Focus();
        host.SendKey(Key.Enter); // activating the "…" chip opens its list instead of navigating
        host.RunUntilIdle();

        Assert.Equal(1, Popups(host));
        var menu = bar.DropDownPart!;
        Assert.Equal(2, menu.Items.Count); // exactly the two elided ancestors
        Assert.Equal("Home", Assert.IsType<MenuItem>(menu.Items[0]).Header);
        Assert.Equal("Projects", Assert.IsType<MenuItem>(menu.Items[1]).Header);

        host.SendKey(Key.Enter); // the menu focused its first item on open
        host.RunUntilIdle();

        Assert.Equal([0], activated);
        Assert.Equal(0, Popups(host)); // a leaf invoke dismisses the menu
    }

    // ───────────────────────────── BC7 — one tab stop ─────────────────────────────

    [Fact] // BC7: the whole trail is ONE tab stop — Tab enters once, the next Tab leaves past every chip
    public void BC7_OneTabStop()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 10) });
        using var _ = host;

        var before = new Button { Content = "before" };
        var after = new Button { Content = "after" };
        var bar = new BreadcrumbBar { ItemsSource = Trail };
        var root = new StackPanel { Children = { before, bar, after } };

        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(bar));

        before.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        var entered = host.Application.FocusManager.FocusedElement;
        Assert.IsType<BreadcrumbBarItem>(entered); // Tab lands on a chip …

        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        Assert.Same(after, host.Application.FocusManager.FocusedElement); // … and the next Tab exits the WHOLE bar
    }

    // ───────────────────────────── BC8–BC10 — the edit hand-off ─────────────────────────────

    [Fact] // BC8: F2 flips to the edit box with the text PRE-SELECTED; the host seeds the text on EditingStarted
    public void BC8_F2EntersEditModeWithTextSelected()
    {
        var (host, bar) = Show();
        using var _ = host;
        bar.IsEditable = true;
        bar.EditingStarted += (_, e) => e.Text = "/home/projects/assets";
        host.RunUntilIdle();

        Assert.Null(bar.Background); // resting: the trail sits on the page, no fill

        Chip(bar, 2).Focus();
        host.SendKey(Key.F2);
        host.RunUntilIdle();

        Assert.True(bar.IsEditing);
        Assert.NotNull(bar.Background); // the :editing theme rule armed — the bar reads as a field
        var box = bar.EditBoxPart!;
        Assert.Equal("/home/projects/assets", box.Text);
        Assert.Equal("/home/projects/assets", box.SelectedText); // pre-selected: type to replace
        Assert.True(box.IsFocused);
        Assert.Equal(Visibility.Visible, box.Visibility);
        Assert.DoesNotContain("Projects", host.GetRowText(0)); // the chips gave way to the box
    }

    [Fact] // BC9: Escape in EDIT mode reverts to the chips and DISCARDS the edit (Text keeps its committed value)
    public void BC9_EscapeRevertsDiscardingEdits()
    {
        var (host, bar) = Show();
        using var _ = host;
        bar.IsEditable = true;
        bar.Text = "/home";
        host.RunUntilIdle();

        var canceled = new List<string>();
        bar.EditCanceled += (_, e) => canceled.Add(e.Text);

        Chip(bar, 0).Focus();
        host.SendKey(Key.F2);
        host.RunUntilIdle();

        host.SendText("/etc/passwd");
        host.RunUntilIdle();
        Assert.Equal("/etc/passwd", bar.EditText);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.False(bar.IsEditing);
        Assert.Equal("/home", bar.Text);          // DISCARDED — the committed value never moved
        Assert.Equal(["/etc/passwd"], canceled);  // …but the host is told what was abandoned
        Assert.Contains("Projects", host.GetRowText(0)); // the chips are back
    }

    [Fact] // BC10: Enter commits — the host validates and may keep the box open by setting KeepEditing
    public void BC10_EnterCommitsAndValidationCanKeepEditing()
    {
        var (host, bar) = Show();
        using var _ = host;
        bar.IsEditable = true;

        var committed = new List<string>();
        var reject = true;
        bar.EditCommitted += (_, e) =>
        {
            committed.Add(e.Text);
            e.KeepEditing = reject; // "that path does not exist" — stay in the box
        };

        bar.BeginEdit();
        host.RunUntilIdle();
        host.SendText("/nope");
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.True(bar.IsEditing);   // held open by the host
        Assert.Equal("", bar.Text);   // nothing adopted
        Assert.Equal(["/nope"], committed);

        reject = false;
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.False(bar.IsEditing);
        Assert.Equal("/nope", bar.Text); // adopted on the accepted commit
        Assert.True(host.Application.FocusManager.FocusedElement is BreadcrumbBarItem); // focus returns to the chips
    }

    // ───────────────────────────── BC11 — Escape in CHIP mode is not ours ─────────────────────────────

    [Fact] // BC11: Escape in chip mode is left UNHANDLED so the host (a dialog) decides what it means
    public void BC11_EscapeInChipModeIsLeftUnhandled()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 10) });
        using var _ = host;

        var bar = new BreadcrumbBar { ItemsSource = Trail, IsEditable = true }; // editable, but NOT editing
        var root = new StackPanel { Children = { bar } };
        host.ShowRoot(root);
        host.RunUntilIdle();

        var sawEscape = 0;
        root.AddHandler(UIElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
                sawEscape++;
        }, handledEventsToo: true);

        Chip(bar, 1).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.Equal(1, sawEscape);        // it bubbled OUT of the bar unhandled — the host owns the gesture
        Assert.Equal(1, bar.ActiveIndex);  // and the bar did not quietly move focus either
    }

    // ───────────────────────────── BC12 — the separator's sibling list ─────────────────────────────

    [Fact] // BC12: pressing a chip's "▸" asks the host for that segment's children and reports the pick
    public void BC12_SeparatorDropsTheSiblingList()
    {
        var (host, bar) = Show();
        using var _ = host;

        var openedFor = new List<int>();
        bar.DropDownOpening += (_, e) =>
        {
            openedFor.Add(e.Index);
            e.Children.Add("docs");
            e.Children.Add("src");
        };

        var picked = new List<(object? Parent, object? Child)>();
        bar.ChildActivated += (_, e) => picked.Add((e.Item, e.SelectedChild));

        var separator = Chip(bar, 0).SeparatorPart!;
        var origin = separator.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Equal([0], openedFor);
        Assert.Equal(1, Popups(host));
        Assert.Equal(2, bar.DropDownPart!.Items.Count);

        host.SendKey(Key.Enter); // the menu opened focused on its first entry
        host.RunUntilIdle();

        Assert.Equal([("Home", (object?) "docs")], picked);
        Assert.Equal(0, Popups(host));
    }

    [Fact] // BC12c: picking a child focuses the PICKED chip, even when the host rebuilds the trail around it
    public void BC12c_PickingAChildFocusesTheNewSegment()
    {
        // The host answers the pick by drilling — truncate to the anchor, then append the pick — which DETACHES
        // the chip the drop-down hung from. Popup.CloseCore's focus restore is guarded on IsAttachedToTree, so it
        // silently skips, and focus used to fall to the first chip in the ring (the leftmost visible one) rather
        // than to what the user just chose.
        var trail = new ObservableCollection<string> { "Home", "Projects", "assets" };
        var (host, bar) = Show();
        using var _ = host;
        bar.SetCurrentValue(ItemsControl.ItemsSourceProperty, trail);
        host.RunUntilIdle();

        bar.DropDownOpening += (_, e) =>
        {
            e.Children.Add("docs");
            e.Children.Add("src");
        };
        bar.ChildActivated += (_, e) =>
        {
            while (trail.Count > e.Index + 1)
                trail.RemoveAt(trail.Count - 1);
            trail.Add((string) e.SelectedChild!);
        };

        var separator = Chip(bar, 0).SeparatorPart!;   // the "Home" chip's ▸
        var origin = separator.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        host.SendKey(Key.Enter);                        // pick "docs"
        host.RunUntilIdle();

        Assert.Equal(["Home", "docs"], trail);

        // Focus is on the PICK — not on "Home" (the anchor, which survived) and not on the first chip in the ring.
        var focused = UIApplication.Current!.FocusManager.FocusedElement;
        Assert.Same(Chip(bar, 1), focused);

        // …and ←/→ continue from where focus actually landed rather than from a stale active index.
        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Same(Chip(bar, 0), UIApplication.Current!.FocusManager.FocusedElement);
    }

    [Fact] // BC12e: an ASYNC host still lands focus on the pick — the arm survives until the new trail arrives
    public void BC12e_PickFocusSurvivesAnAsynchronousNavigation()
    {
        // The file dialogs enumerate off the UI thread (a slow share must not stall the frame loop), so nothing is
        // rebuilt by the time the drop-down closes. A synchronous lookup finds no chip to focus; the arm has to
        // outlive the close and land when the trail actually arrives.
        var trail = new ObservableCollection<string> { "Home", "Projects", "assets" };
        var (host, bar) = Show();
        using var _ = host;
        bar.SetCurrentValue(ItemsControl.ItemsSourceProperty, trail);
        host.RunUntilIdle();

        Action? pendingNavigation = null;
        bar.DropDownOpening += (_, e) => e.Children.Add("docs");
        bar.ChildActivated += (_, e) =>
        {
            var index = e.Index;
            var child = (string) e.SelectedChild!;
            pendingNavigation = () =>          // deferred: stands in for the awaited enumeration
            {
                while (trail.Count > index + 1)
                    trail.RemoveAt(trail.Count - 1);
                trail.Add(child);
            };
        };

        var separator = Chip(bar, 0).SeparatorPart!;
        var origin = separator.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(["Home", "Projects", "assets"], trail); // nothing has happened yet — the "await" is in flight

        pendingNavigation!();                                // the navigation completes
        host.RunUntilIdle();

        Assert.Equal(["Home", "docs"], trail);
        Assert.Same(Chip(bar, 1), UIApplication.Current!.FocusManager.FocusedElement);
    }

    [Fact] // BC12f: an armed pick NEVER yanks focus back if the user moved on while the navigation was in flight
    public void BC12f_PickFocusIsAbandonedWhenFocusLeftTheBar()
    {
        var trail = new ObservableCollection<string> { "Home", "Projects", "assets" };
        var outside = new Button { Content = "elsewhere", Width = 12, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };

        using var host = UIHeadlessHost.Create(
            new UIHeadlessHostOptions { InitialSize = new Size(60, 10), Capabilities = HeadlessCapabilities.KittyTruecolor });

        var bar = new BreadcrumbBar { ItemsSource = trail, Width = 40, Height = 1 };
        root.Children.Add(bar);
        root.Children.Add(outside);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Action? pendingNavigation = null;
        bar.DropDownOpening += (_, e) => e.Children.Add("docs");
        bar.ChildActivated += (_, e) =>
        {
            var index = e.Index;
            var child = (string) e.SelectedChild!;
            pendingNavigation = () =>
            {
                while (trail.Count > index + 1)
                    trail.RemoveAt(trail.Count - 1);
                trail.Add(child);
            };
        };

        var separator = Chip(bar, 0).SeparatorPart!;
        var origin = separator.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        outside.Focus(FocusNavigationMethod.Programmatic); // the user moves on mid-flight
        host.RunUntilIdle();

        pendingNavigation!();
        host.RunUntilIdle();

        // Focus stays where the user put it — stealing it back into the bar would be worse than not restoring.
        Assert.Same(outside, UIApplication.Current!.FocusManager.FocusedElement);
    }

    [Fact] // BC12d: a plain dismiss (Esc) leaves the popup's own focus restore alone — back to the anchor chip
    public void BC12d_DismissingTheDropDownRestoresTheAnchorChip()
    {
        var (host, bar) = Show();
        using var _ = host;

        bar.DropDownOpening += (_, e) => e.Children.Add("docs");

        var separator = Chip(bar, 0).SeparatorPart!;
        var origin = separator.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.Equal(0, Popups(host));
        Assert.Same(Chip(bar, 0), UIApplication.Current!.FocusManager.FocusedElement);
    }

    [Fact] // BC12b: with no handler (or an empty child list) the separator press is inert — no popup, no navigation
    public void BC12b_SeparatorIsInertWithoutAHandler()
    {
        var (host, bar) = Show();
        using var _ = host;

        var activated = 0;
        bar.ItemActivated += (_, _) => activated++;

        var separator = Chip(bar, 0).SeparatorPart!;
        var origin = separator.TranslateToWindow(0, 0);
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(0, Popups(host));
        Assert.Equal(0, activated); // and it did NOT fall through to activating the chip
    }

    private sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
