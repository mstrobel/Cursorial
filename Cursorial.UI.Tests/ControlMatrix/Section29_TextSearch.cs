using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C16 — TextSearch (type-ahead) for ItemsControls. A shared ItemsControl facility: printable keys
// accumulate a prefix (idle-reset) and move the current item to the first match; a re-pressed single character cycles.
// Wired into ListBox / ComboBox (SelectingItemsControl) + TreeView (top-level). Pure matcher/controller + end-to-end.
public sealed class Section29_TextSearch
{
    private static readonly string[] Fruits = { "alpha", "apple", "ant", "bravo", "charlie" };
    private static string? Get(int i) => Fruits[i];

    // ── the pure matcher ──────────────────────────────────────────────────────────────────────────────

    [Fact] // C16.1: prefix match from the current item, wrapping
    public void C16_1_Matcher_Prefix()
    {
        Assert.Equal(4, TextSearchMatcher.FindMatchIndex(5, Get, -1, "c", repeat: false, caseSensitive: false)); // charlie
        Assert.Equal(0, TextSearchMatcher.FindMatchIndex(5, Get, 0, "a", repeat: false, caseSensitive: false));  // current alpha stays
        Assert.Equal(3, TextSearchMatcher.FindMatchIndex(5, Get, -1, "br", repeat: false, caseSensitive: false));// bravo
        Assert.Equal(-1, TextSearchMatcher.FindMatchIndex(5, Get, -1, "z", repeat: false, caseSensitive: false));
    }

    [Fact] // C16.2: a repeat (single char re-pressed) cycles from AFTER the current item, wrapping
    public void C16_2_Matcher_RepeatCycles()
    {
        Assert.Equal(1, TextSearchMatcher.FindMatchIndex(5, Get, 0, "a", repeat: true, caseSensitive: false)); // apple
        Assert.Equal(2, TextSearchMatcher.FindMatchIndex(5, Get, 1, "a", repeat: true, caseSensitive: false)); // ant
        Assert.Equal(0, TextSearchMatcher.FindMatchIndex(5, Get, 2, "a", repeat: true, caseSensitive: false)); // wrap → alpha
    }

    [Fact] // C16.3: case sensitivity
    public void C16_3_Matcher_CaseSensitivity()
    {
        string?[] mixed = { "Alpha", "beta" };
        Assert.Equal(0, TextSearchMatcher.FindMatchIndex(2, i => mixed[i], -1, "a", repeat: false, caseSensitive: false));
        Assert.Equal(-1, TextSearchMatcher.FindMatchIndex(2, i => mixed[i], -1, "a", repeat: false, caseSensitive: true));
        Assert.Equal(0, TextSearchMatcher.FindMatchIndex(2, i => mixed[i], -1, "A", repeat: false, caseSensitive: true));
    }

    // ── the controller (prefix accumulation + repeat detection; no clock needed) ─────────────────────────

    [Fact] // C16.4: typing extends the prefix (re-searching from the current item)
    public void C16_4_Controller_ExtendsPrefix()
    {
        string?[] words = { "can", "car", "cat", "dog" };
        var c = new TextSearchController();
        Assert.Equal(0, c.Handle("c", 4, i => words[i], -1, caseSensitive: false)); // can
        Assert.Equal(0, c.Handle("a", 4, i => words[i], 0, caseSensitive: false));  // "ca" → can stays
        Assert.Equal(2, c.Handle("t", 4, i => words[i], 0, caseSensitive: false));  // "cat"
    }

    [Fact] // C16.5: re-pressing the same letter cycles; a leading space is ignored (stays a command)
    public void C16_5_Controller_RepeatAndLeadingSpace()
    {
        var c = new TextSearchController();
        Assert.Equal(-1, c.Handle(" ", 5, Get, -1, caseSensitive: false)); // leading space: no search
        Assert.Equal(0, c.Handle("a", 5, Get, -1, caseSensitive: false));  // alpha
        Assert.Equal(1, c.Handle("a", 5, Get, 0, caseSensitive: false));   // apple (cycle)
        Assert.Equal(2, c.Handle("a", 5, Get, 1, caseSensitive: false));   // ant (cycle)
    }

    // ── end-to-end through the controls ─────────────────────────────────────────────────────────────────

    [Fact] // C16.6: ListBox type-ahead selects the matching item
    public void C16_6_ListBox()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var list = new ListBox { Width = 20, Height = 8, ItemsSource = Fruits };
        host.ShowRoot(list);
        host.RunUntilIdle();
        list.ItemContainerGenerator.ContainerFromIndex(0)!.Focus(); // a focused item puts the ListBox on the input route
        host.RunUntilIdle();

        host.SendText("c");
        host.RunUntilIdle();
        Assert.Equal("charlie", list.SelectedItem);

        // Rapid keystrokes EXTEND the prefix: "c"+"h" = "ch" → still charlie (no re-search).
        host.SendText("h");
        host.RunUntilIdle();
        Assert.Equal("charlie", list.SelectedItem);

        // After the idle reset, a fresh keystroke re-searches: "b" → bravo.
        host.AdvanceTime(TimeSpan.FromSeconds(1.5));
        host.RunFrame();
        host.SendText("b");
        host.RunUntilIdle();
        Assert.Equal("bravo", list.SelectedItem);
    }

    [Fact] // C16.7: ComboBox type-ahead (closed) selects without opening
    public void C16_7_ComboBox()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var box = new ComboBox { Width = 14, ItemsSource = new[] { "alpha", "beta", "gamma" } };
        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();

        host.SendText("g");
        host.RunUntilIdle();
        Assert.Equal("gamma", box.SelectedItem);
        Assert.False(box.IsDropDownOpen); // closed type-ahead doesn't open the drop-down
    }

    [Fact] // C16.8: TreeView type-ahead selects a matching top-level node (matched against its Header)
    public void C16_8_TreeView()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var tree = new TreeView { Width = 20, Height = 8 };
        tree.Items.Add(new TreeViewItem { Header = "apple" });
        tree.Items.Add(new TreeViewItem { Header = "banana" });
        tree.Items.Add(new TreeViewItem { Header = "cherry" });
        host.ShowRoot(tree);
        host.RunUntilIdle();
        tree.ItemContainerGenerator.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();

        host.SendText("c");
        host.RunUntilIdle();
        Assert.Same(tree.Items[2], tree.SelectedContainer);
    }

    [Fact] // C16.9: TextPath matches against an item property; a plain ItemsControl never engages (no swallow)
    public void C16_9_TextPathAndOptIn()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var people = new[] { new Person("Ada"), new Person("Grace"), new Person("Alan") };
        var list = new ListBox { Width = 20, Height = 8, ItemsSource = people };
        TextSearch.SetTextPath(list, nameof(Person.Name));
        host.ShowRoot(list);
        host.RunUntilIdle();
        list.ItemContainerGenerator.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();

        host.SendText("g");
        host.RunUntilIdle();
        Assert.Same(people[1], list.SelectedItem); // matched "Grace" via TextPath
    }

    private sealed record Person(string Name)
    {
        public override string ToString() => "wrong"; // ensures the match used TextPath, not ToString()
    }

    // ── audit regressions (CD-P2F-1 audit) ──────────────────────────────────────────────────────────────

    [Fact] // C16.10: repeat-detection uses the matcher's folding — long-s 'ſ' then ASCII 's' is NOT a repeat
    public void C16_10_RepeatFoldingMatchesMatcher()
    {
        string?[] items = { "ſun", "sea", "ſky" }; // ſun, sea, ſky
        var c = new TextSearchController();
        Assert.Equal(0, c.Handle("ſ", 3, i => items[i], -1, caseSensitive: false)); // ſun
        // 's' differs from 'ſ' under OrdinalIgnoreCase ⇒ extend to "ſs" (no match), NOT a repeat that cycles to "sea".
        Assert.Equal(-1, c.Handle("s", 3, i => items[i], 0, caseSensitive: false));
    }

    [Fact] // C16.11: ComboBox open type-ahead moves keyboard focus to the matched item (the :focus-visible cue tracks it)
    public void C16_11_ComboBoxOpenFocusesMatch()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var box = new ComboBox { Width = 14, ItemsSource = new[] { "alpha", "beta", "gamma" } };
        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendText("g");
        host.RunUntilIdle();
        Assert.Equal("gamma", box.SelectedItem);
        Assert.True(box.ItemContainerGenerator.ContainerFromIndex(2)!.IsFocused); // focus tracks the match, not a stale row
    }

    [Fact] // C16.12: leaving the control resets the type-ahead buffer (a fresh keystroke re-searches)
    public void C16_12_FocusLossResetsBuffer()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var list = new ListBox { Width = 20, Height = 6, ItemsSource = Fruits };
        var other = new Button { Content = "x" };
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(other);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        list.ItemContainerGenerator.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();
        host.SendText("c");
        host.SendText("h"); // prefix "ch" → charlie
        host.RunUntilIdle();
        Assert.Equal("charlie", list.SelectedItem);

        other.Focus(); // focus leaves the ListBox ⇒ buffer reset
        host.RunUntilIdle();
        list.ItemContainerGenerator.ContainerFromIndex(4)!.Focus(); // re-enter on charlie
        host.RunUntilIdle();
        host.SendText("a"); // a FRESH "a" (not "cha") ⇒ alpha
        host.RunUntilIdle();
        Assert.Equal("alpha", list.SelectedItem);
    }

    [Fact] // C16.13: TreeView type-ahead with a nested selection anchors at the top-level ancestor (not index 0)
    public void C16_13_TreeViewNestedAnchor()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 10) });
        var carrot = new TreeViewItem { Header = "carrot" };
        var car = new TreeViewItem { Header = "car", IsExpanded = true };
        car.Items.Add(carrot);
        var tree = new TreeView { Width = 18, Height = 8 };
        tree.Items.Add(new TreeViewItem { Header = "cat" }); // index 0
        tree.Items.Add(car);                                  // index 1 (ancestor of the selection)
        tree.Items.Add(new TreeViewItem { Header = "cab" });  // index 2
        host.ShowRoot(tree);
        host.RunUntilIdle();

        carrot.IsSelected = true; // a NESTED selection
        host.RunUntilIdle();
        carrot.Focus();
        host.RunUntilIdle();

        host.SendText("c"); // anchored at car (the ancestor, index 1) ⇒ car, NOT cat (index 0)
        host.RunUntilIdle();
        Assert.Same(car, tree.SelectedContainer);
    }

    // ── own-container items: match the displayed Content/Header, not the container's type name ────────────

    [Fact] // C16.14: ListBox type-ahead over ListBoxItem containers matches the item's Content, not "ListBoxItem"
    public void C16_14_ListBox_OwnContainerItems_MatchContent()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var list = new ListBox { Width = 20, Height = 8 };
        list.Items.Add(new ListBoxItem { Content = "apple" });
        list.Items.Add(new ListBoxItem { Content = "banana" });
        list.Items.Add(new ListBoxItem { Content = "cherry" });
        host.ShowRoot(list);
        host.RunUntilIdle();
        list.ItemContainerGenerator.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();

        host.SendText("b");
        host.RunUntilIdle();
        Assert.Same(list.Items[1], list.SelectedItem); // banana — matched via Content (was the type name → no match)
    }

    [Fact] // C16.15: ComboBox (closed) type-ahead over ComboBoxItem containers matches the item's Content
    public void C16_15_ComboBox_OwnContainerItems_MatchContent()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var box = new ComboBox { Width = 14 };
        box.Items.Add(new ComboBoxItem { Content = "alpha" });
        box.Items.Add(new ComboBoxItem { Content = "beta" });
        box.Items.Add(new ComboBoxItem { Content = "gamma" });
        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();

        host.SendText("g");
        host.RunUntilIdle();
        Assert.Same(box.Items[2], box.SelectedItem); // gamma
        Assert.False(box.IsDropDownOpen);
    }
}
