using System.Collections.ObjectModel;
using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Output;
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

    /// <summary>Finds <paramref name="needle"/> in the composited frame, or <c>(-1, -1)</c>.</summary>
    private static (int Column, int Row) FindOnScreen(UIHeadlessHost host, string needle)
    {
        for (var row = 0; row < host.FrameBuffer.Rows; row++)
        {
            var column = host.GetRowText(row).IndexOf(needle, StringComparison.Ordinal);

            if (column >= 0)
                return (column, row);
        }

        return (-1, -1);
    }

    private static bool IsBold(UIHeadlessHost host, int column, int row)
        => (host.GetCell(column, row).Style.Attributes & TextAttributes.Bold) != 0;

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

    [Fact] // BC5c: an ItemsSource reset while folded re-applies the elision to the regenerated containers
    public void BC5c_ItemsResetWhileFolded_ReappliesTheElision()
    {
        // Gallery repro (Breadcrumb page, Deeper/Narrower/Reset): the anti-thrash latch's width/count/hash
        // inputs all coincidentally match when a reset re-adds equal items, so the fold — and with it
        // ApplyElision — was skipped, leaving the REGENERATED ancestor chips Visible with the stale bounds
        // of their initial un-elided arrange. Their item-template content then painted straight over the
        // surviving chips ("Orion Arm" stamped on top of its neighbors, stray cells bleeding through).
        string[] chain = ["Universe", "Laniakea", "Milky Way", "Orion Arm", "Solar System"];
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(80, 6),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;

        var trail = new ObservableCollection<string>(chain);
        var bar = new BreadcrumbBar
        {
            ItemsSource = trail,
            Width = 34,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(bar);
        host.RunUntilIdle();

        var folded = host.GetRowText(0).TrimEnd();
        Assert.True(bar.HasOverflow); // the precondition: the trail folds at this width

        trail.Clear();
        foreach (var label in chain)
            trail.Add(label);
        host.RunUntilIdle();

        // The reset bar must be indistinguishable from the never-reset one: same row, same fold, and the
        // regenerated ancestor containers actually COLLAPSED (not just arranged empty — a Visible chip with
        // stale bounds paints its template content wherever those bounds point).
        Assert.Equal(folded, host.GetRowText(0).TrimEnd());
        for (var i = 0; i < trail.Count; i++)
        {
            var chip = Chip(bar, i);
            Assert.Equal(i < bar.OverflowCount ? Visibility.Collapsed : Visibility.Visible, chip.Visibility);
        }
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
        var picker = bar.DropDownPart!;
        Assert.Equal(2, picker.MatchCount); // exactly the two elided ancestors …
        Assert.Equal(["Home", "Projects"], picker.Entries.Select(e => e.Item.Display)); // … in TRAIL order

        // The picker never takes focus: the "…" chip keeps it and drives the list through its PreviewKeyDown.
        Assert.Same(bar.OverflowChipPart, UIApplication.Current!.FocusManager.FocusedElement);

        host.SendKey(Key.Enter); // the picker opened with its first row highlighted
        host.RunUntilIdle();

        Assert.Equal([0], activated);
        Assert.Equal(0, Popups(host)); // a pick dismisses the list
    }

    [Fact] // BC6b: the overflow list keeps TRAIL order, not alphabetical order — it is a path prefix, not a sibling set
    public void BC6b_OverflowListKeepsTrailOrder()
    {
        // "zulu" before "alpha" is the whole point: sorting an ordered path by name would destroy the one
        // thing it means. The bar pins it by stamping SortGroup with the segment's DEPTH, which the picker
        // ranks on before anything else — so the order survives a fuzzy filter too.
        var (host, bar) = Show(width: 14, items: ["zulu", "alpha", "mike", "leaf"]);
        using var _ = host;

        Assert.True(bar.HasOverflow);
        bar.OverflowChipPart!.Focus();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        var picker = bar.DropDownPart!;
        Assert.Equal(["zulu", "alpha", "mike"], picker.Entries.Select(e => e.Item.Display));

        host.SendText("l"); // "zulu" and "alpha" both carry an 'l'; "mike" drops out — and depth still leads
        host.RunUntilIdle();
        Assert.Equal(["zulu", "alpha"], picker.Entries.Select(e => e.Item.Display));
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
        Assert.Equal(["docs", "src"], bar.DropDownPart!.Entries.Select(e => e.Item.Display));

        host.SendKey(Key.Enter); // the list opened with its first row highlighted
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

        host.SendKey(Key.Tab); // the USER moves on mid-flight — a real gesture, not a programmatic move
        host.RunUntilIdle();
        Assert.Same(outside, UIApplication.Current!.FocusManager.FocusedElement);

        pendingNavigation!();
        host.RunUntilIdle();

        // Focus stays where the user put it — stealing it back into the bar would be worse than not restoring.
        Assert.Same(outside, UIApplication.Current!.FocusManager.FocusedElement);
    }

    // The real dialog toolbar shape: focusable controls on BOTH sides of the bar
    // ([◂ ▸ ▴ ↻] · path bar · [▤▥▦] · search), which is what makes a lost focus land on a BUTTON rather than
    // merely on the wrong chip. Driven by keyboard only — the pointer path never focuses the anchor chip the
    // same way, so it does not exercise the same restore.
    private static (UIHeadlessHost Host, BreadcrumbBar Bar, Button Before, Button After, ObservableCollection<string> Trail)
        ShowToolbar()
    {
        var host = UIHeadlessHost.Create(
            new UIHeadlessHostOptions { InitialSize = new Size(80, 10), Capabilities = HeadlessCapabilities.KittyTruecolor });

        var trail = new ObservableCollection<string> { "Home", "Projects", "assets" };
        var before = new Button { Content = "◂", Width = 3, Height = 1 };
        var after = new Button { Content = "▤", Width = 3, Height = 1 };
        var bar = new BreadcrumbBar { ItemsSource = trail, Width = 40, Height = 1 };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(before);
        toolbar.Children.Add(bar);
        toolbar.Children.Add(after);

        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        return (host, bar, before, after, trail);
    }

    [Fact] // BC12h: ACTIVATING an ancestor keeps focus on it — the truncation must not strand focus on a doomed chip
    public void BC12h_ActivatingAnAncestorKeepsFocusOnTheActivatedChip()
    {
        var (host, bar, before, after, trail) = ShowToolbar();
        using var _ = host;

        // The reported trail shape: activating a MIDDLE segment removes every chip after it — including the
        // one focus tends to be repaired onto. Rebuilt with Clear()+Add(), as a view model recomputing a path
        // from scratch does, so every container is recycled and nothing survives by luck.
        bar.ItemActivated += (_, e) =>
        {
            var kept = trail.Take(e.Index + 1).ToArray();
            trail.Clear();
            foreach (var segment in kept)
                trail.Add(segment);
        };

        before.Focus(FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();
        host.SendKey(Key.Tab);          // into the bar — the active chip is the first
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);   // …move to "Projects", the middle segment
        host.RunUntilIdle();

        host.SendKey(Key.Enter);        // activate it
        host.RunUntilIdle();

        Assert.Equal(["Home", "Projects"], trail);

        // Focus is ON the activated chip. Before the fix it landed there, slid to the doomed trailing chip as
        // the truncation detached it, and ended up nowhere visible at all.
        var focused = UIApplication.Current!.FocusManager.FocusedElement;
        Assert.NotNull(focused);
        Assert.Same(Chip(bar, 1), focused);
        Assert.True(bar.IsKeyboardFocusWithin);
        Assert.NotSame(before, focused);
        Assert.NotSame(after, focused);
    }

    [Fact] // BC12g: keyboard-only, in a real toolbar — the pick keeps focus in the bar, never on a neighbouring button
    public void BC12g_KeyboardPickInAToolbarDoesNotFallToANeighbouringButton()
    {
        var (host, bar, before, after, trail) = ShowToolbar();
        using var _ = host;

        Action? pendingNavigation = null;
        bar.DropDownOpening += (_, e) => e.Children.Add("docs");
        bar.ChildActivated += (_, e) =>
        {
            var index = e.Index;
            var child = (string) e.SelectedChild!;

            // The REAL shape: FileDialogViewModel.RebuildSegments does Segments.Clear() then Add(...) because it
            // recomputes the trail from the new path. That Reset recycles EVERY container — including the focused
            // chip — where an incremental RemoveAt would have left the surviving ones (and the focus) alone.
            pendingNavigation = () =>
            {
                var kept = trail.Take(index + 1).Append(child).ToArray();
                trail.Clear();
                foreach (var segment in kept)
                    trail.Add(segment);
            };
        };

        // Keyboard only, from the button to the LEFT of the bar.
        before.Focus(FocusNavigationMethod.Programmatic);
        host.RunUntilIdle();
        host.SendKey(Key.Tab);                 // into the bar (one tab stop)
        host.RunUntilIdle();
        Assert.True(bar.IsKeyboardFocusWithin);

        host.SendKey(Key.DownArrow);           // open the active segment's drop-down
        host.RunUntilIdle();
        Assert.Equal(1, Popups(host));

        host.SendKey(Key.Enter);               // pick "docs"
        host.RunUntilIdle();

        pendingNavigation!();                  // the awaited enumeration completes
        host.RunUntilIdle();

        var focused = UIApplication.Current!.FocusManager.FocusedElement;
        Assert.NotSame(before, focused);       // the reported symptom: focus fell to the first toolbar button
        Assert.NotSame(after, focused);
        Assert.True(bar.IsKeyboardFocusWithin);
        Assert.Same(FocusableContainerFor(bar, "docs"), focused);
    }

    private static UIElement? FocusableContainerFor(BreadcrumbBar bar, object item)
    {
        for (var i = 0; i < bar.ItemContainerGenerator.ContainerCount; i++)
        {
            if (Equals(bar.ItemContainerGenerator.ItemFromIndex(i), item))
                return bar.ItemContainerGenerator.ContainerFromIndex(i);
        }

        return null;
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

    // ───────────────────────────── BC13–BC16 — the drop-down IS a picker ─────────────────────────────

    [Fact] // BC13: the child list SCROLLS — the whole reason the drop-downs left ContextMenu behind
    public void BC13_LargeChildListScrolls()
    {
        // A ContextMenu is not scrollable, so a directory with five hundred folders produced a menu taller
        // than the terminal with no way to reach the bottom of it. A CompletionList is a ListBox: the picker
        // caps at MaxVisibleItems and the highlight drags the viewport along behind it.
        var host = UIHeadlessHost.Create(
            new UIHeadlessHostOptions
            {
                InitialSize = new Size(60, 24),
                Capabilities = HeadlessCapabilities.KittyTruecolor,
                CaptureFrameBytes = true
            });

        using var _ = host;

        var bar = new BreadcrumbBar
        {
            ItemsSource = Trail,
            Width = 40,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        host.ShowRoot(bar);
        host.RunUntilIdle();

        bar.DropDownOpening += (_, e) =>
        {
            for (var i = 0; i < 500; i++)
                e.Children.Add($"folder-{i:000}");
        };

        Chip(bar, 0).Focus();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        var picker = bar.DropDownPart!;
        Assert.Equal(500, picker.MatchCount);
        Assert.Equal(12, picker.MaxVisibleItems); // the theme's cap, in ROWS — one row is one terminal cell

        // The window opens on the first rows and the rest of the list is genuinely off-screen.
        Assert.True(FindOnScreen(host, "folder-000").Row >= 0);
        Assert.Equal(-1, FindOnScreen(host, "folder-020").Row);

        for (var i = 0; i < 20; i++)
            host.SendKey(Key.DownArrow);

        host.RunUntilIdle();

        Assert.Equal(20, picker.SelectedIndex);
        Assert.True(FindOnScreen(host, "folder-020").Row >= 0); // driven past the window, the list scrolled …
        Assert.Equal(-1, FindOnScreen(host, "folder-000").Row); // … and the rows it started on are gone
    }

    [Fact] // BC14: typing while the drop-down is open filters it FUZZILY, with the matched cells bold
    public void BC14_TypingFiltersTheDropDownWithHighlight()
    {
        var (host, bar) = Show();
        using var _ = host;

        bar.DropDownOpening += (_, e) =>
        {
            e.Children.Add("source-images");
            e.Children.Add("src");
            e.Children.Add("tests");
        };

        Chip(bar, 0).Focus();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(3, bar.DropDownPart!.MatchCount);

        host.SendText("src");
        host.RunUntilIdle();

        // "src" is a literal prefix of itself and a scattered subsequence of "source-images" (s…r,c);
        // "tests" is gone. The prefix tier leads — this is the completion popup's ranking, not a StartsWith.
        Assert.Equal(["src", "source-images"], bar.DropDownPart!.Entries.Select(e => e.Item.Display));

        var (column, row) = FindOnScreen(host, "source-images");
        Assert.True(column >= 0, "the filtered row never rendered");
        Assert.True(IsBold(host, column, row));      // the matched 's' …
        Assert.False(IsBold(host, column + 1, row)); // … and the unmatched 'o' beside it
    }

    [Fact] // BC15: entries are SORTED — by Display, ordinal-ignore-case, with SortGroup ahead of it
    public void BC15_DropDownEntriesAreSorted()
    {
        var (host, bar) = Show();
        using var _ = host;

        bar.DropDownOpening += (_, e) =>
        {
            // Deliberately out of order and deliberately mixed-case: a host adds whatever its enumeration
            // produced, and "Apple" must not sort miles away from "mango" just for being capitalized.
            e.Children.Add("zebra");
            e.Children.Add("Apple");
            e.Children.Add("mango");
            e.Children.Add(new CompletionItem("bin") { SortGroup = -1 });
        };

        Chip(bar, 0).Focus();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        // The bar sorts BEFORE handing the list over, because the picker deliberately preserves provider
        // order for an empty pattern — "sorted with nothing typed" is arranged here, never assumed.
        Assert.Equal(["bin", "Apple", "mango", "zebra"], bar.DropDownPart!.Entries.Select(e => e.Item.Display));
    }

    [Fact] // BC16: a host MAY fill Children with real CompletionItems — and gets that same instance back
    public void BC16_HostSuppliedCompletionItemsKeepTheirChromeAndIdentity()
    {
        var (host, bar) = Show();
        using var _ = host;

        var folder = new CompletionItem("bin") { KindLabel = "folder", SortGroup = 0 };
        var file = new CompletionItem("readme.md") { KindLabel = "file", SortGroup = 1 };

        bar.DropDownOpening += (_, e) =>
        {
            e.Children.Add(file);     // out of order on purpose
            e.Children.Add(folder);
            e.Children.Add("assets"); // …and a PLAIN object in the same list still works, through ToString()
        };

        object? selected = null;
        bar.ChildActivated += (_, e) => selected = e.SelectedChild;

        Chip(bar, 0).Focus();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        // SortGroup leads, so the host's grouping survives the bar's by-display sort: the file falls below
        // both group-0 rows even though "readme.md" would otherwise sort between "assets" and "bin".
        Assert.Equal(["assets", "bin", "readme.md"], bar.DropDownPart!.Entries.Select(e => e.Item.Display));
        Assert.True(FindOnScreen(host, "folder").Row >= 0); // the item's own kind label is rendered, not swallowed

        host.SendKey(Key.DownArrow); // onto "bin"
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        // The SAME instance the host added — the bar never reports a wrapper it built (BC12 pins the plain
        // object case, which the file dialog and the gallery both depend on).
        Assert.Same(folder, selected);
    }

    [Fact] // BC17: pressing a second chip's "▸" RE-ANCHORS the list under it, with that chip's children
    public void BC17_OpeningASecondDropDownReAnchorsIt()
    {
        // The list is a Popup, and a Popup only PLACES on the closed→open edge — so moving it means closing
        // and re-opening, which raises Closed, which drops the open list's state. Building the new entries
        // before that close ran wiped them and left the second press showing nothing at all.
        var (host, bar) = Show();
        using var _ = host;

        bar.DropDownOpening += (_, e) => e.Children.Add($"child-of-{e.Index}");

        var first = Chip(bar, 0).SeparatorPart!.TranslateToWindow(0, 0);
        host.SendClick(first.Column, first.Row);
        host.RunUntilIdle();

        Assert.Equal(1, Popups(host));
        Assert.Equal("child-of-0", Assert.Single(bar.DropDownPart!.Entries).Item.Display);

        var second = Chip(bar, 1).SeparatorPart!.TranslateToWindow(0, 0);
        host.SendClick(second.Column, second.Row);
        host.RunUntilIdle();

        Assert.Equal(1, Popups(host)); // one list, moved — not two stacked on each other
        Assert.Equal("child-of-1", Assert.Single(bar.DropDownPart!.Entries).Item.Display);
        Assert.Same(Chip(bar, 1), UIApplication.Current!.FocusManager.FocusedElement);

        // …and it really drives the NEW anchor: Enter picks out of the second chip's list.
        var picked = new List<object?>();
        bar.ChildActivated += (_, e) => picked.Add(e.SelectedChild);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(["child-of-1"], picked);
        Assert.Equal(0, Popups(host));
    }

    [Fact] // BC18: an OUTSIDE press dismisses the list, even on dead space that takes no focus
    public void BC18_AnOutsidePressDismissesTheDropDown()
    {
        // The picker is a chooser, not a hint over a field, so it light-dismisses like every other chooser in
        // the framework. Only a FOCUS-taking press reaches the anchor's LostFocus, so without this a press on
        // a label, a panel background or a dialog's chrome left the list floating with the keyboard still
        // pointed at it — the one thing the ContextMenu it replaced always got right.
        var host = UIHeadlessHost.Create(
            new UIHeadlessHostOptions
            {
                InitialSize = new Size(60, 10),
                Capabilities = HeadlessCapabilities.KittyTruecolor,
                CaptureFrameBytes = true
            });

        using var _ = host;

        var bar = new BreadcrumbBar
        {
            ItemsSource = Trail,
            Width = 40,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var root = new Grid();
        root.Children.Add(bar);
        host.ShowRoot(root);
        host.RunUntilIdle();

        bar.DropDownOpening += (_, e) => e.Children.Add("docs");

        Chip(bar, 0).Focus();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, Popups(host));

        host.SendClick(55, 9); // dead space: nothing focusable there, and nothing below the list either
        host.RunUntilIdle();

        Assert.Equal(0, Popups(host));
        Assert.False(bar.DropDownPart!.IsOpen);
        Assert.Null(bar.DropDownPart!.Anchor); // …and the picker let go of the chip it was driving
    }

    [Fact] // BC19: a drop-down requested before the bar's first measure still comes up
    public void BC19_DropDownWorksBeforeTheFirstLayoutPass()
    {
        // PART_DropDown reaches its Popup surface through its OWN template, and CompletionPopup returns
        // silently when that part is missing — so realizing the bar's template is only half the job. The
        // affordance used to come up empty and never recover, because the later template expansion only
        // re-opens a popup that already believes it is open. (The ContextMenu it replaced built its surface
        // on demand and needed no template at all, which is how the gap survived the swap.)
        using var host = UIHeadlessHost.Create(
            new UIHeadlessHostOptions { InitialSize = new Size(60, 10), Capabilities = HeadlessCapabilities.KittyTruecolor });

        var bar = new BreadcrumbBar { ItemsSource = Trail, Width = 40, Height = 1 };
        bar.DropDownOpening += (_, e) => e.Children.Add("docs");

        host.ShowRoot(bar);
        bar.RequestDropDownAt(0); // deliberately BEFORE the first measure/arrange pass
        host.RunUntilIdle();

        Assert.Equal(1, Popups(host));
        Assert.True(bar.DropDownPart!.IsOpen);
        Assert.Equal("docs", Assert.Single(bar.DropDownPart!.Entries).Item.Display);
    }

    private sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
