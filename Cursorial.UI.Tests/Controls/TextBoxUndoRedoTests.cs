using System.ComponentModel;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Controls;

// Spec for TextBox undo/redo (edit-based history, WPF/Avalonia-aligned): coalescing rules + their boundaries,
// the splice/caret restore, MaxLength + selection + multi-line interactions, UndoLimit, the Ctrl+Z/Y/Shift+Z
// chords, the two-way-binding echo (the design-review blocker), PasswordBox security, and read-only gating.
public sealed class TextBoxUndoRedoTests
{
    private static (UITestHost Host, T Box) Shown<T>(Action<T>? configure = null, bool focus = true)
        where T : TextBox, new()
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(30, 8),
            Capabilities = TestCapabilities.KittyTruecolor,
        });
        var box = new T
        {
            Width = 16,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        configure?.Invoke(box);
        host.ShowRoot(box);
        host.RunUntilIdle();
        if (focus)
        {
            box.Focus();
            host.RunUntilIdle();
        }

        return (host, box);
    }

    private static (UITestHost Host, TextBox Box) Shown(Action<TextBox>? configure = null, bool focus = true)
        => Shown<TextBox>(configure, focus);

    // ───────────────────────────── core undo / redo ─────────────────────────────

    [Fact] // typing then Undo restores the prior text; Redo re-applies it
    public void Undo_Redo_RoundTrip()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("abc"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text);
        Assert.True(box.CanUndo);
        Assert.False(box.CanRedo);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("", box.Text);
        Assert.True(box.CanRedo);

        box.Redo(); host.RunUntilIdle();
        Assert.Equal("abc", box.Text);
    }

    [Fact] // a run of typed characters coalesces into ONE undo unit
    public void TypedRun_CoalescesIntoOneUnit()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("hello"); host.RunUntilIdle();
        box.Undo(); host.RunUntilIdle();

        Assert.Equal("", box.Text); // one Undo removed the whole run
        Assert.False(box.CanUndo);
    }

    [Fact] // an explicit caret move seals the typing unit — the next run is separate
    public void CaretMove_BreaksCoalescing()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("ab"); host.RunUntilIdle();   // unit 1
        host.SendKey(Key.LeftArrow); host.RunUntilIdle();
        host.SendText("c"); host.RunUntilIdle();     // unit 2 ("c" inserted at index 1 → "acb")
        Assert.Equal("acb", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("ab", box.Text);
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("", box.Text);
    }

    [Fact] // consecutive Backspaces coalesce; Undo restores the run AND the pre-run caret
    public void BackspaceRun_Coalesces_RestoresCaret()
    {
        var (host, box) = Shown(b => b.Text = "hello");
        using var _ = host;
        box.CaretIndex = 5;

        host.SendKey(Key.Backspace); host.RunUntilIdle();
        host.SendKey(Key.Backspace); host.RunUntilIdle();
        host.SendKey(Key.Backspace); host.RunUntilIdle();
        Assert.Equal("he", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("hello", box.Text);
        Assert.Equal(5, box.CaretIndex); // caret before the FIRST backspace of the run
    }

    [Fact] // Backspace and forward-Delete never merge into one unit (would reorder text on undo) — audit #9
    public void BackspaceThenDelete_AreSeparateUnits_NoCorruption()
    {
        var (host, box) = Shown(b => b.Text = "abcde");
        using var _ = host;
        box.CaretIndex = 2; // between 'b' and 'c'

        host.SendKey(Key.Backspace); host.RunUntilIdle(); // deletes 'b' → "acde"
        host.SendKey(Key.Delete); host.RunUntilIdle();    // deletes 'c' → "ade"
        Assert.Equal("ade", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("acde", box.Text); // only the forward-delete undone
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("abcde", box.Text); // then the backspace — correct order, no corruption
    }

    [Fact] // typing over a selection is one atomic unit; Undo restores the replaced selection
    public void TypingOverSelection_IsAtomic_RestoresSelection()
    {
        var (host, box) = Shown(b => b.Text = "hello");
        using var _ = host;
        box.SelectionStart = 1;
        box.SelectionLength = 3; // "ell"

        host.SendText("X"); host.RunUntilIdle();
        Assert.Equal("hXo", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("hello", box.Text);
        Assert.Equal(1, box.SelectionStart);
        Assert.Equal(3, box.SelectionLength); // the selection is restored, not just the caret
        Assert.Equal("ell", box.SelectedText);
    }

    [Fact] // a new edit after an Undo invalidates the redo branch
    public void NewEdit_InvalidatesRedo()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("a"); host.RunUntilIdle();
        box.Undo(); host.RunUntilIdle();
        Assert.True(box.CanRedo);

        host.SendText("b"); host.RunUntilIdle();
        Assert.False(box.CanRedo); // the 'a' redo entry is gone
        Assert.Equal("b", box.Text);
    }

    // ───────────────────────────── multi-line / Clear / MaxLength ─────────────────────────────

    [Fact] // Enter is its own atomic undo unit (typing on each side is separate) — multi-line
    public void Newline_IsAtomicUnit()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Height = 4; });
        using var _ = host;

        host.SendText("a"); host.RunUntilIdle();
        host.SendKey(Key.Enter); host.RunUntilIdle();
        host.SendText("b"); host.RunUntilIdle();
        Assert.Equal("a\nb", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("a\n", box.Text); // the "b" run
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("a", box.Text);   // the newline
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("", box.Text);    // the "a" run
    }

    [Fact] // Clear() is an undoable edit (WPF parity)
    public void Clear_IsUndoable()
    {
        var (host, box) = Shown(b => b.Text = "hello");
        using var _ = host;

        box.Clear(); host.RunUntilIdle();
        Assert.Equal("", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("hello", box.Text);
    }

    [Fact] // the recorded insert is the post-MaxLength-trim text — Redo never re-inserts rejected chars (audit #5)
    public void MaxLength_RecordsTrimmedInsert()
    {
        var (host, box) = Shown(b => { b.Text = "abc"; b.MaxLength = 4; });
        using var _ = host;
        box.CaretIndex = 3;

        host.SendText("xy"); host.RunUntilIdle(); // only "x" fits
        Assert.Equal("abcx", box.Text);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("abc", box.Text);
        box.Redo(); host.RunUntilIdle();
        Assert.Equal("abcx", box.Text); // not "abcxy"
    }

    [Fact] // a Backspace at offset 0 / Delete at the end records nothing (no-op edit)
    public void NoOpEdits_RecordNothing()
    {
        var (host, box) = Shown(b => b.Text = "ab");
        using var _ = host;

        box.CaretIndex = 0;
        host.SendKey(Key.Backspace); host.RunUntilIdle(); // at start — no-op
        box.CaretIndex = 2;
        host.SendKey(Key.Delete); host.RunUntilIdle();    // at end — no-op

        Assert.Equal("ab", box.Text);
        Assert.False(box.CanUndo);
    }

    // ───────────────────────────── enable / limit / external ─────────────────────────────

    [Fact] // an external Text assignment discards the undo history (WPF resets undo on a programmatic set)
    public void ExternalTextSet_ClearsHistory()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("ab"); host.RunUntilIdle();
        Assert.True(box.CanUndo);

        box.Text = "zzz"; // external
        host.RunUntilIdle();
        Assert.False(box.CanUndo);
        Assert.False(box.CanRedo);
    }

    [Fact] // disabling undo clears and stops recording
    public void IsUndoEnabledFalse_DisablesAndClears()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("ab"); host.RunUntilIdle();
        box.IsUndoEnabled = false; host.RunUntilIdle();
        Assert.False(box.CanUndo);

        host.SendText("cd"); host.RunUntilIdle();
        Assert.Equal("abcd", box.Text);
        Assert.False(box.CanUndo); // nothing recorded while disabled
    }

    [Fact] // UndoLimit caps the history, dropping the oldest units
    public void UndoLimit_CapsHistory()
    {
        var (host, box) = Shown(b => b.UndoLimit = 2);
        using var _ = host;

        host.SendText("a"); host.RunUntilIdle(); host.SendKey(Key.End); host.RunUntilIdle();
        host.SendText("b"); host.RunUntilIdle(); host.SendKey(Key.End); host.RunUntilIdle();
        host.SendText("c"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text); // three units, but the cap is 2

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("ab", box.Text);
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("a", box.Text);
        Assert.False(box.CanUndo); // the oldest unit ("a") was dropped — cannot undo further
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("a", box.Text); // no-op
    }

    [Fact] // UndoLimit = 0 disables recording entirely
    public void UndoLimitZero_DisablesRecording()
    {
        var (host, box) = Shown(b => b.UndoLimit = 0);
        using var _ = host;

        host.SendText("abc"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text);
        Assert.False(box.CanUndo);
    }

    // ───────────────────────────── keyboard chords ─────────────────────────────

    [Fact] // Ctrl+Z undoes, Ctrl+Y redoes, Ctrl+Shift+Z redoes
    public void Chords_UndoRedo()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("abc"); host.RunUntilIdle();

        host.SendKey(Key.Character, KeyModifiers.Control, "z"); host.RunUntilIdle();
        Assert.Equal("", box.Text);

        host.SendKey(Key.Character, KeyModifiers.Control, "y"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text);

        host.SendKey(Key.Character, KeyModifiers.Control, "z"); host.RunUntilIdle();
        Assert.Equal("", box.Text);

        host.SendKey(Key.Character, KeyModifiers.Control | KeyModifiers.Shift, "z"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text);
    }

    // ───────────────────────────── read-only ─────────────────────────────

    [Fact] // a read-only field reports CanUndo false and Ctrl+Z does not change the text
    public void ReadOnly_BlocksUndo()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendText("abc"); host.RunUntilIdle();
        box.IsReadOnly = true; host.RunUntilIdle();

        Assert.False(box.CanUndo);
        box.Undo(); host.RunUntilIdle();
        Assert.Equal("abc", box.Text); // no-op

        host.SendKey(Key.Character, KeyModifiers.Control, "z"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text); // chord bubbles, no change
    }

    // ───────────────────────────── PasswordBox security ─────────────────────────────

    [Fact] // PasswordBox never records undo — and re-enabling IsUndoEnabled cannot defeat that (audit #8)
    public void PasswordBox_NeverRecordsUndo()
    {
        var (host, box) = Shown<PasswordBox>();
        using var _ = host;

        host.SendText("secret"); host.RunUntilIdle();
        Assert.False(box.CanUndo);

        box.IsUndoEnabled = true; // a runtime / XAML / binding set must NOT enable plaintext recording
        host.RunUntilIdle();
        host.SendText("more"); host.RunUntilIdle();
        Assert.False(box.CanUndo);
    }

    // ───────────────────────────── two-way binding echo (the design-review blocker) ─────────────────────────────

    [Fact] // a normal two-way binding does not interfere with undo
    public void TwoWayBinding_UndoStillWorks()
    {
        var vm = new GuardedVm();
        var (host, box) = Shown();
        using var _ = host;
        box.DataContext = vm;
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(GuardedVm.Value)) { Mode = BindingMode.TwoWay });

        host.SendText("ab"); host.RunUntilIdle();
        Assert.Equal("ab", vm.Value);

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("", box.Text);
        Assert.Equal("", vm.Value); // the undone value propagated to the source
    }

    [Fact] // an unguarded VM setter (raises INPC unconditionally) does not clear the undo stack on each keystroke
    public void UnguardedVm_DoesNotClearHistory()
    {
        var vm = new UnguardedVm();
        var (host, box) = Shown();
        using var _ = host;
        box.DataContext = vm;
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(UnguardedVm.Value)) { Mode = BindingMode.TwoWay });

        host.SendText("ab"); host.RunUntilIdle();
        Assert.True(box.CanUndo); // the synchronous source echo did not wipe the history

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("", box.Text);
    }

    [Fact] // a per-change NORMALIZING two-way binding stays undoable — the funnel records the actual landed text
    public void NormalizingVm_UndoWorks()
    {
        var vm = new UpperVm();
        var (host, box) = Shown();
        using var _ = host;
        box.DataContext = vm;
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(UpperVm.Value)) { Mode = BindingMode.TwoWay });

        host.SendText("a"); host.RunUntilIdle();
        Assert.Equal("A", box.Text); // the source upper-cased and echoed back

        box.Undo(); host.RunUntilIdle();
        Assert.Equal("", box.Text); // undo restores the real prior text (recorded post-normalization), no corruption
    }

    // ───────────────────────────── re-entrancy / read-only (audit regressions) ─────────────────────────────

    [Fact] // a TextChanged handler that calls Undo() re-entrantly never throws or corrupts (audit critical #0/#1)
    public void ReentrantUndo_FromTextChangedHandler_IsSafe()
    {
        var (host, box) = Shown();
        using var _ = host;
        host.SendText("a"); host.RunUntilIdle();
        host.SendKey(Key.End); host.RunUntilIdle();
        host.SendText("b"); host.RunUntilIdle(); // two units: "a", "b"

        var reentered = false;
        box.TextChanged += (_, _) =>
        {
            if (reentered || !box.CanUndo)
                return;
            reentered = true;
            box.Undo(); // re-enter from within the first undo's TextChanged
        };

        box.Undo(); host.RunUntilIdle(); // must not throw
        Assert.Equal("", box.Text);      // both units undone, cleanly
        Assert.False(box.CanUndo);
    }

    [Fact] // reading SelectedText from a TextChanged handler during a shrinking edit does not throw (audit #6)
    public void TextChangedHandler_ReadingSelectedText_DuringShrink_NoThrow()
    {
        var (host, box) = Shown(b => b.Text = "hello");
        using var _ = host;
        box.SelectAll(); // anchor 0, caret 5

        string? observed = null;
        box.TextChanged += (_, _) => observed = box.SelectedText; // must see valid bounds, not stale ones

        host.SendText("X"); host.RunUntilIdle(); // replaces the 5-char selection with 1 char → "X"
        Assert.Equal("X", box.Text);
        Assert.NotNull(observed); // the handler ran without an out-of-range throw
    }

    [Fact] // Clear() does not mutate a read-only field (audit #4)
    public void Clear_RespectsReadOnly()
    {
        var (host, box) = Shown(b => { b.Text = "hello"; b.IsReadOnly = true; });
        using var _ = host;

        box.Clear(); host.RunUntilIdle();
        Assert.Equal("hello", box.Text);
    }

    // ───────────────────────────── events ─────────────────────────────

    [Fact] // an attached undo raises TextChanged
    public void Undo_RaisesTextChanged()
    {
        var (host, box) = Shown();
        using var _ = host;
        host.SendText("ab"); host.RunUntilIdle();

        var changes = 0;
        box.TextChanged += (_, _) => changes++;
        box.Undo(); host.RunUntilIdle();

        Assert.Equal("", box.Text);
        Assert.True(changes >= 1);
    }

    private sealed class GuardedVm : INotifyPropertyChanged
    {
        private string _value = "";
        public string Value
        {
            get => _value;
            set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact] // REPRO: TextChanged handler reading SelectedText during a shrinking edit
    public void Repro_TextChangedReadsSelectedTextDuringShrinkingEdit()
    {
        var (host, box) = Shown();
        using var _ = host;
        box.Text = "hello"; host.RunUntilIdle();
        box.SelectAll(); host.RunUntilIdle();

        Exception? caught = null;
        string? observedSelectedText = null;
        (int, int) observedBounds = default;
        int observedCaret = -1;
        box.TextChanged += (_, _) =>
        {
            try
            {
                observedBounds = box.SelectionBounds;
                observedCaret = box.CaretIndex;
                observedSelectedText = box.SelectedText; // line 228: Text[start..end]
            }
            catch (Exception ex) { caught = ex; }
        };

        host.SendText("X"); host.RunUntilIdle();

        System.Console.WriteLine($"Text='{box.Text}' bounds={observedBounds} caret={observedCaret} caught={caught?.GetType().Name}");
        Assert.Null(caught); // FAILS if the claim is real
    }

    [Fact] // REPRO 2: an UNGUARDED handler throw escapes out of the typing path
    public void Repro_UnguardedHandlerThrowEscapesFunnel()
    {
        var (host, box) = Shown();
        using var _ = host;
        box.Text = "hello"; host.RunUntilIdle();
        box.SelectAll(); host.RunUntilIdle();

        box.TextChanged += (_, _) => { var s = box.SelectedText; System.GC.KeepAlive(s); }; // realistic handler, no try/catch

        // Direct synchronous API call (NOT through the input pump's exception funnel):
        var thrown = Record.Exception(() => box.SelectedText = "X");
        System.Console.WriteLine($"escaped={thrown?.GetType().Name} Text='{box.Text}'");
        Assert.Null(thrown); // FAILS if the throw escapes the funnel
    }

    private sealed class UnguardedVm : INotifyPropertyChanged
    {
        private string _value = "";
        public string Value
        {
            get => _value;
            set { _value = value ?? ""; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } // no equality guard
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class UpperVm : INotifyPropertyChanged
    {
        private string _value = "";
        public string Value
        {
            get => _value;
            set { var u = (value ?? "").ToUpperInvariant(); if (_value == u) return; _value = u; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
