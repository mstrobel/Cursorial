using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C17 — ComboBox v2 (editable mode + WPF/Avalonia parity). IsEditable swaps the read-only face
// for a PART_EditableTextBox; typing edits Text as free text, Enter/blur commits (exact item match selects, else
// free text is kept + selection clears), navigating the open list updates the text, and focus delegates to the box.
public sealed class Section30_ComboBoxV2
{
    private static readonly string[] Fruits = { "apple", "banana", "cherry" };

    private static (UIHeadlessHost Host, ComboBox Box) Show(bool editable = true)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(28, 12) });
        var box = new ComboBox
        {
            ItemsSource = Fruits,
            IsEditable = editable,
            Width = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(box);
        host.RunUntilIdle();
        return (host, box);
    }

    [Fact] // C17.1: IsEditable swaps the face — the text box is shown, but not the tab stop; the content site is collapsed
    public void C17_1_EditableSwapsFace()
    {
        var (host, box) = Show(editable: false);
        using var _ = host;
        Assert.Equal(Visibility.Visible, box.ContentSitePart!.Visibility);
        Assert.Equal(Visibility.Collapsed, box.EditableTextBoxPart!.Visibility);
        Assert.True(box.IsTabStop);

        box.IsEditable = true;
        host.RunUntilIdle();
        Assert.Equal(Visibility.Collapsed, box.ContentSitePart!.Visibility);
        Assert.Equal(Visibility.Visible, box.EditableTextBoxPart!.Visibility);
        Assert.False(box.EditableTextBoxPart.IsTabStop); // the text box is never a tab stop
        Assert.True(box.IsTabStop); // the combo box remains the tab stop
    }

    [Fact] // C17.2: typing into the editable box updates Text as free text (no type-ahead selection jump)
    public void C17_2_TypingUpdatesText()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        host.RunUntilIdle();

        host.SendText("ch"); // a non-editable combo would type-ahead to "cherry"; editable just edits Text
        host.RunUntilIdle();
        Assert.Equal("ch", box.Text);
        Assert.Null(box.SelectedItem); // no selection jump in editable mode
    }

    [Fact] // C17.3: Enter commits an exact item match (selects it + closes)
    public void C17_3_EnterCommitsMatch()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        host.RunUntilIdle();

        host.SendText("banana");
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem);
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C17.4: non-matching text is kept as free text and clears the selection
    public void C17_4_FreeTextClearsSelection()
    {
        var (host, box) = Show();
        using var _ = host;
        box.SelectedItem = "apple";
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        box.EditableTextBoxPart!.SelectAll();
        host.SendText("xyzzy"); // overwrites; no item matches
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Null(box.SelectedItem);
        Assert.Equal("xyzzy", box.Text);
    }

    [Fact] // C17.5: setting SelectedItem syncs the editable Text
    public void C17_5_SelectionSyncsText()
    {
        var (host, box) = Show();
        using var _ = host;
        box.SelectedItem = "cherry";
        host.RunUntilIdle();
        Assert.Equal("cherry", box.Text);
        Assert.Equal("cherry", box.EditableTextBoxPart!.Text);
    }

    [Fact] // C17.6: IsReadOnly blocks typing (the box still shows Text, the list still drives selection)
    public void C17_6_ReadOnlyBlocksTyping()
    {
        var (host, box) = Show();
        using var _ = host;
        box.IsReadOnly = true;
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        host.SendText("abc");
        host.RunUntilIdle();
        Assert.Equal("", box.Text ?? "");
    }

    [Fact] // C17.7: focusing the editable ComboBox delegates focus to the text box (so the caret publishes)
    public void C17_7_FocusDelegation()
    {
        var (host, box) = Show();
        using var _ = host;
        box.Focus();
        host.RunUntilIdle();
        Assert.True(box.EditableTextBoxPart!.IsFocused);
    }

    [Fact] // C17.8: StaysOpenOnEdit opens the drop-down when the user types
    public void C17_8_StaysOpenOnEditOpensOnType()
    {
        var (host, box) = Show();
        using var _ = host;
        box.StaysOpenOnEdit = true;
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        host.SendText("a");
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);
    }

    [Fact] // C17.9: Escape reverts the edit to the current selection
    public void C17_9_EscapeReverts()
    {
        var (host, box) = Show();
        using var _ = host;
        box.SelectedItem = "apple";
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        box.EditableTextBoxPart!.SelectAll();
        host.SendText("zzz");
        host.RunUntilIdle();
        Assert.Equal("zzz", box.Text);

        box.IsDropDownOpen = true; // Escape path runs while open
        host.RunUntilIdle();
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.Equal("apple", box.Text); // reverted to the selection
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C17.10: SelectionBoxItem reflects the selection; non-editable type-ahead still works (parity unbroken)
    public void C17_10_SelectionBoxAndNonEditableTypeAhead()
    {
        var (host, box) = Show(editable: false);
        using var _ = host;
        box.Focus();
        host.RunUntilIdle();
        host.SendText("c"); // non-editable type-ahead
        host.RunUntilIdle();
        Assert.Equal("cherry", box.SelectedItem);
        Assert.Equal("cherry", box.SelectionBoxItem);
    }

    // ── audit regressions (CD-P2G-1 audit) ──────────────────────────────────────────────────────────────

    [Fact] // C17.11: a model selection-drop (removing the selected item) does NOT wipe the user's uncommitted free text
    public void C17_11_ModelDropPreservesFreeText()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(28, 12) });
        using var _ = host;
        var items = new System.Collections.ObjectModel.ObservableCollection<string> { "apple", "banana", "cherry" };
        var box = new ComboBox { ItemsSource = items, IsEditable = true, Width = 16 };
        host.ShowRoot(box);
        host.RunUntilIdle();
        box.SelectedItem = "apple";
        host.RunUntilIdle();

        box.EditableTextBoxPart!.Focus();
        box.EditableTextBoxPart!.SelectAll();
        host.SendText("inprogress"); // uncommitted free text
        host.RunUntilIdle();
        Assert.Equal("inprogress", box.Text);

        items.RemoveAt(0); // the model drops the selected item — must NOT clobber the edit
        host.RunUntilIdle();
        Assert.Equal("inprogress", box.Text);
    }

    [Fact] // C17.12: turning on IsEditable at runtime does not spuriously open the drop-down (the box sync is guarded)
    public void C17_12_RuntimeEditableNoSpuriousOpen()
    {
        var (host, box) = Show(editable: false);
        using var _ = host;
        box.StaysOpenOnEdit = true;
        box.SelectedItem = "cherry";
        host.RunUntilIdle();

        box.IsEditable = true; // the cherry → box sync must not be mistaken for typing
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
        Assert.Equal("cherry", box.Text);
    }

    [Fact] // C17.13: a runtime IsEditable flip while focused moves the caret into the text box
    public void C17_13_RuntimeEditableDelegatesFocus()
    {
        var (host, box) = Show(editable: false);
        using var _ = host;
        box.Focus();
        host.RunUntilIdle();
        Assert.True(box.IsFocused);

        box.IsEditable = true;
        host.RunUntilIdle();
        Assert.True(box.EditableTextBoxPart!.IsFocused); // focus delegated, not stranded on the combo
    }

    [Fact] // C17.14: committing a case-insensitive match normalizes the text to the item's display casing
    public void C17_14_CommitNormalizesCasing()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        host.SendText("BANANA");
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem);
        Assert.Equal("banana", box.Text); // normalized, not "BANANA"
    }

    [Fact] // C17.15: a fresh editable ComboBox reports Text as "" (not null), matching the empty text box
    public void C17_15_TextNeverNull()
    {
        var (host, box) = Show();
        using var _ = host;
        Assert.Equal("", box.Text);
        Assert.Equal("", box.EditableTextBoxPart!.Text);
    }

    // ── ComboBoxItem-as-item: the face must not host the live container (the displayed selection) ─────────

    [Fact] // C17.16: when items are ComboBoxItems, the face shows the item's CONTENT, never the live container
    public void C17_16_ComboBoxItemFaceUnwrapsToContent()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(28, 12) });
        using var _ = host;
        var apple = new ComboBoxItem { Content = "apple" };
        var box = new ComboBox
        {
            IsEditable = false,
            Width = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        box.Items.Add(apple);
        box.Items.Add(new ComboBoxItem { Content = "banana" });
        host.ShowRoot(box);
        host.RunUntilIdle();

        box.SelectedItem = apple;
        host.RunUntilIdle();

        // The face shows the item's unwrapped CONTENT, not the live ComboBoxItem element (a UIElement cannot live
        // in both the face and the drop-down; hosting the container there corrupts mouse interaction with the face).
        Assert.Equal("apple", box.SelectionBoxItem);
        Assert.NotSame(apple, box.SelectionBoxItem);

        // The selected container is never reparented into the face's content site.
        Assert.NotSame(box.ContentSitePart, apple.VisualParent);
    }

    [Fact] // C17.17: the unwrapped face still tracks selection changes, and the live container survives an open
    public void C17_17_ComboBoxItemFaceTracksSelectionAndDropDownRenders()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(28, 12) });
        using var _ = host;
        var apple = new ComboBoxItem { Content = "apple" };
        var banana = new ComboBoxItem { Content = "banana" };
        var box = new ComboBox
        {
            IsEditable = false,
            Width = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        box.Items.Add(apple);
        box.Items.Add(banana);
        host.ShowRoot(box);
        host.RunUntilIdle();

        box.SelectedItem = apple;
        host.RunUntilIdle();
        Assert.Equal("apple", box.SelectionBoxItem);

        box.SelectedItem = banana;
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectionBoxItem);

        // Opening the drop-down realizes the live containers in the panel — the face's unwrapping never stole them.
        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.True(AnyRowContains(host, "apple"));   // the live container renders in the list
        Assert.True(AnyRowContains(host, "banana"));
    }

    [Fact] // C17.18: editable mode with ComboBoxItems — the face text is the item's content, not the container type name
    public void C17_18_EditableFaceTextUnwrapsComboBoxItem()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(28, 12) });
        using var _ = host;
        var apple = new ComboBoxItem { Content = "apple" };
        var box = new ComboBox { IsEditable = true, Width = 16 };
        box.Items.Add(apple);
        box.Items.Add(new ComboBoxItem { Content = "banana" });
        host.ShowRoot(box);
        host.RunUntilIdle();

        box.SelectedItem = apple;
        host.RunUntilIdle();

        Assert.Equal("apple", box.Text); // not "Cursorial.UI.Controls.ComboBoxItem"
        Assert.Equal("apple", box.EditableTextBoxPart!.Text);
    }

    // ── edit-text search + inline completion (C17.19+): tentative highlight, single close policy ─────────

    [Fact] // C17.19: typing while open inline-completes — trailer appended, selected back-to-front, caret after the prefix
    public void C17_19_TypingWhileOpenInlineCompletes()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendText("b");
        host.RunUntilIdle();

        var part = box.EditableTextBoxPart!;
        Assert.Equal("banana", box.Text);         // "b" + the matched trailer
        Assert.Equal("banana", part.Text);
        Assert.Equal(1, part.CaretIndex);          // the caret sits right after the typed prefix…
        Assert.Equal(1, part.SelectionStart);      // …and the trailer is selected (back-to-front)
        Assert.Equal(5, part.SelectionLength);
        Assert.Equal("banana", box.SelectedItem);  // tentative highlight follows the match
        Assert.True(box.IsDropDownOpen);

        host.SendText("x"); // overwrites the selected trailer: "b" + "x" → no match
        host.RunUntilIdle();
        Assert.Equal("bx", box.Text);              // the trailer was consumed, nothing re-appended
        Assert.Null(box.SelectedItem);             // the tentative highlight rolled back to the pre-open selection
    }

    [Fact] // C17.20: Enter commits the tentative match; a dismissal (programmatic close) restores the pre-open selection
    public void C17_20_CommitVersusDismissal()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        host.SendText("b");
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem); // tentative

        box.IsDropDownOpen = false; // a dismissal, not a commit
        host.RunUntilIdle();
        Assert.Null(box.SelectedItem);            // the tentative selection rolled back…
        Assert.Equal("banana", box.Text);         // …but the visible text is untouched (only Escape reverts)

        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        host.SendKey(Key.Enter);                  // commit-close
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem);
        Assert.Equal("banana", box.Text);
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C17.21: Escape with a tentative match restores the pre-open selection AND reverts the text (never Select(-1))
    public void C17_21_EscapeRestoresPreOpenSelection()
    {
        var (host, box) = Show();
        using var _ = host;
        box.SelectedItem = "banana";
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        box.EditableTextBoxPart!.SelectAll();
        host.SendText("ch"); // tentative cherry
        host.RunUntilIdle();
        Assert.Equal("cherry", box.SelectedItem);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem); // restored, not cleared
        Assert.Equal("banana", box.Text);         // reverted to the pre-open selection's text
        Assert.False(box.IsDropDownOpen);
    }

    [Fact] // C17.22: a keyboard open pre-seeds the highlight from committed free text WITHOUT rewriting it
    public void C17_22_KeyboardOpenKeepsCommittedFreeText()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        host.SendText("b");
        host.SendKey(Key.Enter); // commit: no exact match → free text, no selection
        host.RunUntilIdle();
        Assert.Equal("b", box.Text);
        Assert.Null(box.SelectedItem);

        host.SendKey(Key.DownArrow); // open gesture
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);
        Assert.Equal("b", box.Text);              // mere opening never rewrites the text…
        Assert.Equal("b", box.EditableTextBoxPart!.Text);
        Assert.Equal("banana", box.SelectedItem); // …but the match is highlighted (pre-seed)

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.Null(box.SelectedItem);            // dismissal restores the pre-open (empty) selection
        Assert.Equal("b", box.Text);              // Escape reverts to the pre-open free text
    }

    [Fact] // C17.23: pasted text never drives the search (the type-ahead FromPaste rule) — no highlight, no trailer
    public void C17_23_PasteNeverSearches()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendInput(new PasteEvent { Text = "ch".AsMemory(), Timestamp = default }); // the terminal paste ⇒ TextInput{FromPaste}
        host.RunUntilIdle();
        Assert.Equal("ch", box.Text);  // no trailer appended
        Assert.Null(box.SelectedItem); // no tentative highlight
    }

    [Fact] // C17.24: a programmatic selection change mid-edit supersedes the tentative match — the close respects it
    public void C17_24_ProgrammaticSelectionSupersedesTentative()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        host.SendText("b");
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem); // tentative

        box.SelectedItem = "cherry"; // the app decides (uncommitted typed text is preserved — C17.11's rule)
        host.RunUntilIdle();
        Assert.Equal("banana", box.Text);

        box.IsDropDownOpen = false;  // dismissal: nothing tentative remains to roll back
        host.RunUntilIdle();
        Assert.Equal("cherry", box.SelectedItem); // the programmatic choice stands — never silently committed
        Assert.Equal("banana", box.Text);         // and the typed text was not normalized to "cherry"
    }

    [Fact] // C17.25: an external Text write while a tentative match is up lands intact (no self-wiping cascade)
    public void C17_25_ExternalTextWriteLandsIntact()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        host.SendText("b");
        host.RunUntilIdle();
        Assert.Equal("banana", box.SelectedItem);

        box.Text = "xy"; // external write supersedes the tentative match
        host.RunUntilIdle();
        Assert.Equal("xy", box.Text);  // the write survives (the old cascade wiped it to "")
        Assert.Null(box.SelectedItem); // the tentative highlight rolled back
    }

    [Fact] // C17.26: deleting never re-appends the trailer (backspace stays deletable), the highlight still tracks
    public void C17_26_DeletionNeverReappends()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();
        host.SendText("ba");
        host.RunUntilIdle();
        Assert.Equal("banana", box.Text); // "ba" + trailer

        host.SendKey(Key.Backspace); // deletes the selected trailer
        host.RunUntilIdle();
        Assert.Equal("ba", box.Text);
        Assert.Equal("banana", box.SelectedItem); // still highlighted (prefix still matches)…

        host.SendKey(Key.Backspace);
        host.RunUntilIdle();
        Assert.Equal("b", box.Text); // …and the character delete was not fought by a re-append
    }

    [Fact] // C17.27: the typed prefix keeps the user's casing until commit normalizes it to the item's display
    public void C17_27_PrefixCasingPreservedUntilCommit()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(28, 12) });
        using var _ = host;
        var box = new ComboBox { ItemsSource = new[] { "Alpha", "Beta" }, IsEditable = true, Width = 16 };
        host.ShowRoot(box);
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendText("al");
        host.RunUntilIdle();
        Assert.Equal("alpha", box.Text); // the user's "al" + the item's "pha" — not case-transformed yet
        Assert.Equal(2, box.EditableTextBoxPart!.CaretIndex);
        Assert.Equal("Alpha", box.SelectedItem);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal("Alpha", box.Text); // commit normalizes to the item's display casing
    }

    [Fact] // C17.28: when typing itself opens the session (StaysOpenOnEdit), Escape reverts to BEFORE the first
    // keystroke — not to the first typed character the open-time snapshot happened to see
    public void C17_28_AutoOpenEscapeRevertsToPreTypingText()
    {
        var (host, box) = Show();
        using var _ = host;
        box.StaysOpenOnEdit = true;
        host.RunUntilIdle();
        box.EditableTextBoxPart!.Focus();
        host.RunUntilIdle();

        host.SendText("al"); // the first keystroke auto-opens; both keystrokes land in one session
        host.RunUntilIdle();
        Assert.True(box.IsDropDownOpen);
        Assert.Equal("al", box.Text); // "a" completed to "a[pple]", then "l" overwrote the trailer; "al" matches nothing
        Assert.Null(box.SelectedItem); // so the tentative highlight rolled back

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
        Assert.Equal("", box.Text); // the whole typing session reverts (baseline was empty), never "a"
    }

    [Fact] // C17.29: Escape also rolls back a selection moved by LIST NAVIGATION this session (not just a text match) —
    // while a programmatic mid-session selection still stands (C17.24's contract)
    public void C17_29_EscapeRollsBackNavigatedSelection()
    {
        var (host, box) = Show();
        using var _ = host;
        box.EditableTextBoxPart!.Focus();
        box.IsDropDownOpen = true;
        host.RunUntilIdle();

        host.SendText("b"); // tentative banana
        host.SendKey(Key.DownArrow); // navigate: cherry — a user gesture, text follows
        host.RunUntilIdle();
        Assert.Equal("cherry", box.SelectedItem);
        Assert.Equal("cherry", box.Text);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(box.IsDropDownOpen);
        Assert.Null(box.SelectedItem); // the whole session rolls back: navigation too…
        Assert.Equal("", box.Text);    // …and the text reverts to the pre-open baseline
    }

    private static bool AnyRowContains(UIHeadlessHost host, string text)
    {
        for (var r = 0; r < 12; r++)
            if (host.GetRowText(r).Contains(text, StringComparison.Ordinal))
                return true;
        return false;
    }
}
