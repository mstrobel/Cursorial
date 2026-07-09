using System.ComponentModel;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C11 — TextBox (P9.6): a single-line editable field. The caret is the real terminal
// cursor (BlinkingBar) published by the TextPresenter; caret offsets pin to grapheme-cluster boundaries;
// Text is two-way with per-change source push; Copy/Cut route through the OSC 52 clipboard service.
public sealed class Section24_TextBox
{
    private static (UITestHost Host, TextBox Box) Shown(string text = "", int width = 12, bool focus = true,
                                                        TerminalCapabilities? capabilities = null)
    {
        var options = new UITestHostOptions
        {
            InitialSize = new Size(30, 4),
            Capabilities = capabilities ?? TestCapabilities.KittyTruecolor,
            CaptureFrameBytes = true
        };

        var host = UITestHost.Create(options);
        var box = new TextBox
        {
            Text = text,
            Width = width,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(box);
        host.RunUntilIdle();
        if (focus)
        {
            box.Focus();
            host.RunUntilIdle();
        }

        return (host, box);
    }

    // ───────────────────────────── binding + model ─────────────────────────────

    [Fact] // C11.1: Text binds two-way by default (BindsTwoWayByDefault)
    public void C11_1_TextIsTwoWayByDefault()
    {
        var box = new TextBox();
        var expression = box.SetBinding(TextBox.TextProperty, new Binding("Name") { Mode = BindingMode.Default });
        Assert.Equal(BindingMode.TwoWay, expression.EffectiveMode);
    }

    [Fact] // C11.1b: typing pushes to the binding source per keystroke (the pinned per-change default)
    public void C11_1b_TypingPushesPerKeystroke()
    {
        var vm = new Vm { Name = "" };
        var (host, box) = Shown();
        using var _ = host;
        box.DataContext = vm;
        box.SetBinding(TextBox.TextProperty, new Binding("Name") { Mode = BindingMode.TwoWay });

        host.SendText("ab");
        host.RunUntilIdle();

        Assert.Equal("ab", box.Text);
        Assert.Equal("ab", vm.Name); // pushed per change, not on commit
    }

    [Fact] // C11.2: typed characters insert at the caret (OnTextInput), caret advances
    public void C11_2_TypingInserts()
    {
        var (host, box) = Shown("ac");
        using var _ = host;
        box.CaretIndex = 1; // between a and c

        host.SendText("b");
        host.RunUntilIdle();

        Assert.Equal("abc", box.Text);
        Assert.Equal(2, box.CaretIndex);
    }

    [Fact] // C11.3: the caret pins to a grapheme-cluster boundary (never inside a wide cluster)
    public void C11_3_CaretPinsToClusterBoundary()
    {
        var (host, box) = Shown("a😀b"); // 😀 is two chars (a surrogate pair), one cluster
        using var _ = host;

        box.CaretIndex = 2; // mid-emoji → pins back to the cluster start (index 1)
        Assert.Equal(1, box.CaretIndex);

        // Right-arrow from index 1 steps over the whole emoji cluster to index 3.
        box.CaretIndex = 1;
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(3, box.CaretIndex);
    }

    // ───────────────────────────── navigation ─────────────────────────────

    [Fact] // C11.4: Left / Right move by one cluster; Home / End jump to the extremes
    public void C11_4_ArrowAndHomeEnd()
    {
        var (host, box) = Shown("hello");
        using var _ = host;
        box.CaretIndex = 5;

        host.SendKey(Key.LeftArrow); host.RunUntilIdle();
        Assert.Equal(4, box.CaretIndex);
        host.SendKey(Key.Home); host.RunUntilIdle();
        Assert.Equal(0, box.CaretIndex);
        host.SendKey(Key.End); host.RunUntilIdle();
        Assert.Equal(5, box.CaretIndex);
    }

    [Fact] // C11.5: Ctrl+Left / Ctrl+Right move by whitespace-delimited word
    public void C11_5_WordNavigation()
    {
        var (host, box) = Shown("one two three");
        using var _ = host;
        box.CaretIndex = 0;

        host.SendKey(Key.RightArrow, KeyModifiers.Control); host.RunUntilIdle();
        Assert.Equal(3, box.CaretIndex); // after "one"
        host.SendKey(Key.RightArrow, KeyModifiers.Control); host.RunUntilIdle();
        Assert.Equal(7, box.CaretIndex); // after "two"
        host.SendKey(Key.LeftArrow, KeyModifiers.Control); host.RunUntilIdle();
        Assert.Equal(4, box.CaretIndex); // back to start of "two"
    }

    [Fact] // C11.6: Shift+arrow extends the selection from the anchor
    public void C11_6_ShiftExtendsSelection()
    {
        var (host, box) = Shown("hello");
        using var _ = host;
        box.CaretIndex = 0;

        host.SendKey(Key.RightArrow, KeyModifiers.Shift); host.RunUntilIdle();
        host.SendKey(Key.RightArrow, KeyModifiers.Shift); host.RunUntilIdle();

        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(2, box.SelectionLength);
        Assert.Equal("he", box.SelectedText);
    }

    // ───────────────────────────── editing ─────────────────────────────

    [Fact] // C11.7: Backspace deletes the cluster before the caret; Delete the one after
    public void C11_7_BackspaceAndDelete()
    {
        var (host, box) = Shown("abc");
        using var _ = host;
        box.CaretIndex = 2;

        host.SendKey(Key.Backspace); host.RunUntilIdle();
        Assert.Equal("ac", box.Text);
        Assert.Equal(1, box.CaretIndex);

        host.SendKey(Key.Delete); host.RunUntilIdle();
        Assert.Equal("a", box.Text);
    }

    [Fact] // C11.8: Backspace/Delete with a selection removes the selection (not a single cluster)
    public void C11_8_DeleteRemovesSelection()
    {
        var (host, box) = Shown("hello");
        using var _ = host;
        box.SelectionStart = 1;
        box.SelectionLength = 3; // "ell"

        host.SendKey(Key.Backspace); host.RunUntilIdle();
        Assert.Equal("ho", box.Text);
        Assert.Equal(1, box.CaretIndex);
        Assert.Equal(0, box.SelectionLength);
    }

    [Fact] // C11.9: Ctrl+A selects all
    public void C11_9_SelectAll()
    {
        var (host, box) = Shown("hello");
        using var _ = host;
        box.CaretIndex = 0;

        host.SendKey(Key.Character, KeyModifiers.Control, "a"); host.RunUntilIdle();

        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(5, box.SelectionLength);
    }

    [Fact] // C11.10: MaxLength caps insertion
    public void C11_10_MaxLength()
    {
        var (host, box) = Shown("abc");
        using var _ = host;
        box.MaxLength = 4;
        box.CaretIndex = 3;

        host.SendText("xy"); // only one more char fits
        host.RunUntilIdle();

        Assert.Equal("abcx", box.Text);
    }

    [Fact] // C11.11: a read-only field rejects typing and delete but stays navigable
    public void C11_11_ReadOnly()
    {
        var (host, box) = Shown("abc");
        using var _ = host;
        box.IsReadOnly = true;
        box.CaretIndex = 1;

        host.SendText("X"); host.RunUntilIdle();
        Assert.Equal("abc", box.Text); // no insert

        host.SendKey(Key.Backspace); host.RunUntilIdle();
        Assert.Equal("abc", box.Text); // no delete

        host.SendKey(Key.RightArrow); host.RunUntilIdle();
        Assert.Equal(2, box.CaretIndex); // navigation still works
    }

    [Fact] // C11.12: SelectedText get/set replaces the selected range
    public void C11_12_SelectedTextSet()
    {
        var (host, box) = Shown("hello");
        using var _ = host;
        box.SelectionStart = 1;
        box.SelectionLength = 3;
        Assert.Equal("ell", box.SelectedText);

        box.SelectedText = "i";
        Assert.Equal("hio", box.Text);
    }

    [Fact] // C11.13: paste flattens newlines/tabs to spaces (TextInput{FromPaste})
    public void C11_13_PasteFlattensNewlines()
    {
        var (host, box) = Shown();
        using var _ = host;

        host.SendInput(new PasteEvent { Text = "a\r\nb\tc".AsMemory(), Timestamp = default });
        host.RunUntilIdle();

        Assert.Equal("a b c", box.Text);
    }

    // ───────────────────────────── caret service ─────────────────────────────

    [Fact] // C11.14: the terminal caret publishes (BlinkingBar) while focused and clears on blur
    public void C11_14_CaretPublishesWhileFocused()
    {
        var (host, box) = Shown("hi");
        using var _ = host;

        Assert.True(box.IsFocused);
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(CursorShape.BlinkingBar, host.FrameBuffer.CursorShape);

        host.Application.FocusManager.ClearFocus(); // blur — the caret publication is withdrawn
        host.RunUntilIdle();
        Assert.False(host.FrameBuffer.CursorVisible);
    }

    [Fact] // C11.15: detaching a focused field clears the terminal caret (no stale cursor)
    public void C11_15_DetachClearsCaret()
    {
        var (host, box) = Shown("hi");
        using var _ = host;
        Assert.True(host.FrameBuffer.CursorVisible);

        host.Application.RootElement = null; // detach the whole tree
        host.RunUntilIdle();

        Assert.Equal(0, host.Application.CaretService.PublicationCount);
        Assert.False(host.FrameBuffer.CursorVisible);
        GC.KeepAlive(box);
    }

    // ───────────────────────────── mouse ─────────────────────────────

    [Fact] // C11.16: clicking places the caret at the hit cluster; double-click selects the word
    public void C11_16_MouseClickAndDoubleClick()
    {
        var (host, box) = Shown("one two", focus: false);
        using var _ = host;
        var origin = box.TranslateToWindow(0, 0);

        // Click on the 'w' of "two" (text starts after the 1-col border padding).
        host.SendClick(origin.Column + 1 + 4, origin.Row); // padding(1) + column 4 ('t')
        host.RunUntilIdle();
        Assert.True(box.IsFocused);
        Assert.Equal(4, box.CaretIndex);

        host.SendClick(origin.Column + 1 + 4, origin.Row, MouseButton.Left, clickCount: 2);
        host.RunUntilIdle();
        Assert.Equal("two", box.SelectedText);

        host.SendClick(origin.Column + 1 + 4, origin.Row, MouseButton.Left, clickCount: 3);
        host.RunUntilIdle();
        Assert.Equal(0, box.SelectionStart);
        Assert.Equal(7, box.SelectionLength); // triple-click selects all ("one two")
    }

    [Fact] // C11.16b: a left-button drag extends the selection from the click anchor to the pointer
    public void C11_16b_MouseDragExtends()
    {
        var (host, box) = Shown("hello", focus: false);
        using var _ = host;
        var origin = box.TranslateToWindow(0, 0);
        var col = origin.Column + 1; // first text cell (after the 1-col padding)

        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(col + 1, origin.Row), // anchor at index 1 ('e')
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.None,
            Timestamp = default
        });
        host.RunUntilIdle();

        host.SendMouseMove(col + 4, origin.Row); // drag to index 4 ('o')
        host.RunUntilIdle();
        Assert.Equal(1, box.SelectionStart);
        Assert.Equal(3, box.SelectionLength); // "ell" selected during the drag

        host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp,
            Position = new CellPosition(col + 4, origin.Row),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = default
        });
        host.RunUntilIdle();
        Assert.Equal("ell", box.SelectedText); // selection persists after release
    }

    // ───────────────────────────── scrolling ─────────────────────────────

    [Fact] // C11.17: a long line scrolls horizontally to keep the caret in view
    public void C11_17_HorizontalScroll()
    {
        var (host, box) = Shown(new string('x', 40), width: 12);
        using var _ = host;
        var presenter = box.Presenter!;

        box.CaretIndex = 0; // caret home → no scroll
        host.RunUntilIdle();
        Assert.Equal(0, presenter.ScrollOffset);

        box.CaretIndex = 40; // caret at the far end → scrolled so the caret is visible
        host.RunUntilIdle();
        Assert.True(presenter.ScrollOffset > 0);
    }

    // ───────────────────────────── pseudo-classes + placeholder ─────────────────────────────

    [Fact] // C11.18: :empty flips with text presence; :readonly mirrors IsReadOnly
    public void C11_18_PseudoClasses()
    {
        var (host, box) = Shown();
        using var _ = host;
        Assert.True(box.HasCustomPseudoClass(":empty"));

        box.Text = "x";
        host.RunUntilIdle();
        Assert.False(box.HasCustomPseudoClass(":empty"));

        Assert.False(box.HasCustomPseudoClass(":readonly"));
        box.IsReadOnly = true;
        host.RunUntilIdle();
        Assert.True(box.HasCustomPseudoClass(":readonly"));
    }

    [Fact] // C11.19: the placeholder renders while empty and disappears once text is present
    public void C11_19_PlaceholderRenders()
    {
        var (host, box) = Shown(focus: false);
        using var _ = host;
        box.Placeholder = "name";
        host.RunUntilIdle();

        var origin = box.TranslateToWindow(0, 0);
        // "name" begins after the 1-col border padding.
        Assert.Equal("n", host.GetCell(origin.Column + 1, origin.Row).Grapheme);

        box.Text = "Z";
        host.RunUntilIdle();
        Assert.Equal("Z", host.GetCell(origin.Column + 1, origin.Row).Grapheme); // text replaces placeholder
    }

    [Fact] // C11.20: a selection renders distinctly from unselected text
    public void C11_20_SelectionRenders()
    {
        var (host, box) = Shown("hello", focus: false);
        using var _ = host;
        var origin = box.TranslateToWindow(0, 0);
        var col = origin.Column + 1; // first text cell (after padding)

        var unselected = host.GetCell(col, origin.Row).Style;
        box.SelectionStart = 0;
        box.SelectionLength = 2;
        host.RunUntilIdle();
        var selected = host.GetCell(col, origin.Row).Style;

        Assert.NotEqual(unselected, selected); // the selected cell's style differs (brush or Inverse)
    }

    // ───────────────────────────── clipboard ─────────────────────────────

    [Fact] // C11.21: Copy with a selection writes OSC 52 when the terminal supports clipboard writes
    public void C11_21_CopyWritesOsc52()
    {
        var caps = TestCapabilities.KittyTruecolor;
        var withClipboard = caps with
        {
            Output = caps.Output with { Protocol = caps.Output.Protocol with { ClipboardWrite = true } }
        };
        var (host, box) = Shown("hello", capabilities: withClipboard);
        using var _ = host;
        Assert.True(host.Application.Clipboard.CanWrite);

        box.SelectionStart = 0;
        box.SelectionLength = 3;
        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        Assert.True(ContainsSequence(CollectFrames(host), "\u001b]52;"u8));
    }

    [Fact] // C11.22: Copy is a no-op when the terminal lacks clipboard-write support (no OSC 52)
    public void C11_22_CopyNoOpWithoutCapability()
    {
        var (host, box) = Shown("hello"); // stock preset: ClipboardWrite == false
        using var _ = host;
        Assert.False(host.Application.Clipboard.CanWrite);

        box.SelectionStart = 0;
        box.SelectionLength = 3;
        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        Assert.False(ContainsSequence(CollectFrames(host), "\u001b]52;"u8));
    }

    [Fact] // C11.23: Cut copies then deletes the selection
    public void C11_23_Cut()
    {
        var caps = TestCapabilities.KittyTruecolor;
        var withClipboard = caps with
        {
            Output = caps.Output with { Protocol = caps.Output.Protocol with { ClipboardWrite = true } }
        };
        var (host, box) = Shown("hello", capabilities: withClipboard);
        using var _ = host;

        box.SelectionStart = 0;
        box.SelectionLength = 2;
        host.SendKey(Key.Character, KeyModifiers.Control, "x");
        var emitted = CollectFrames(host);

        Assert.Equal("llo", box.Text);
        Assert.True(ContainsSequence(emitted, "\u001b]52;"u8));
    }

    [Fact] // C11.23a: Ctrl+V kicks the OSC 52 read; the terminal's reply inserts at the caret (FromPaste path)
    public void C11_23a_PasteReadsOsc52()
    {
        var caps = TestCapabilities.KittyTruecolor;
        var withRead = caps with
        {
            Output = caps.Output with { Protocol = caps.Output.Protocol with { ClipboardRead = true } }
        };
        var (host, box) = Shown("ab", capabilities: withRead);
        using var _ = host;
        Assert.True(host.Application.Clipboard.CanRead);

        box.CaretIndex = 1;
        host.SendKey(Key.Character, KeyModifiers.Control, "v");
        Assert.True(ContainsSequence(CollectFrames(host), "]52;c;?"u8)); // the query went out

        host.SendBytes("\x1b]52;c;aGVsbG8=\x1b\\"u8); // the terminal answers "hello"
        host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
        host.RunUntilIdle();

        Assert.Equal("ahellob", box.Text);
        Assert.Equal(6, box.CaretIndex); // caret lands after the inserted run
    }

    [Fact] // C11.23c: a second Ctrl+V while the first read is still pending is a no-op — the reply inserts ONCE
           // (OSC 52 has no request id, so a fan-out reply would otherwise duplicate the paste into the model)
    public void C11_23c_PasteWhileInFlight_InsertsOnce()
    {
        var caps = TestCapabilities.KittyTruecolor;
        var withRead = caps with
        {
            Output = caps.Output with { Protocol = caps.Output.Protocol with { ClipboardRead = true } }
        };
        var (host, box) = Shown("ab", capabilities: withRead);
        using var _ = host;

        box.CaretIndex = 1;
        host.SendKey(Key.Character, KeyModifiers.Control, "v"); // first read — in flight
        host.SendKey(Key.Character, KeyModifiers.Control, "v"); // second while pending — must be dropped
        host.RunUntilIdle();

        host.SendBytes("\x1b]52;c;aGVsbG8=\x1b\\"u8); // the terminal answers "hello" once
        host.DrainParsedInputAsync().GetAwaiter().GetResult();
        host.RunUntilIdle();

        Assert.Equal("ahellob", box.Text); // inserted once, not "ahellohellob"
    }

    [Fact] // C11.23b: without read support the chord stays a consumed no-op — no stray 'v', no query, no hang
    public void C11_23b_PasteNoOpWithoutCapability()
    {
        var (host, box) = Shown("ab"); // stock preset: ClipboardRead == false
        using var _ = host;
        Assert.False(host.Application.Clipboard.CanRead);

        host.SendKey(Key.Character, KeyModifiers.Control, "v");
        Assert.False(ContainsSequence(CollectFrames(host), "]52;"u8));
        Assert.Equal("ab", box.Text);
    }

    [Fact] // C11.24: Ctrl+C with no selection is not consumed (bubbles — an app may bind quit)
    public void C11_24_CtrlCNoSelectionBubbles()
    {
        var (host, box) = Shown("hello");
        using var _ = host;
        box.CaretIndex = 0; // no selection
        var bubbled = false;
        box.AddHandler(UIElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Character && (e.Modifiers & KeyModifiers.Control) != 0 && !e.Handled)
                bubbled = true;
        });

        host.SendKey(Key.Character, KeyModifiers.Control, "c");
        host.RunUntilIdle();

        Assert.True(bubbled); // reached the instance handler unhandled
    }

    [Fact] // C11.25: Shift+Delete cuts a selection; with no selection it is not consumed (bubbles, like Ctrl+X)
    public void C11_25_ShiftDeleteCut()
    {
        var caps = TestCapabilities.KittyTruecolor;
        var withClipboard = caps with
        {
            Output = caps.Output with { Protocol = caps.Output.Protocol with { ClipboardWrite = true } }
        };
        var (host, box) = Shown("hello", capabilities: withClipboard);
        using var _ = host;

        box.SelectionStart = 0;
        box.SelectionLength = 2;
        host.SendKey(Key.Delete, KeyModifiers.Shift);
        host.RunUntilIdle();
        Assert.Equal("llo", box.Text); // cut removed the selection

        // With no selection, Shift+Delete is not consumed — it bubbles (matches Ctrl+X / Ctrl+C).
        var bubbled = false;
        box.AddHandler(UIElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Delete && (e.Modifiers & KeyModifiers.Shift) != 0 && !e.Handled)
                bubbled = true;
        });
        host.SendKey(Key.Delete, KeyModifiers.Shift); // caret collapsed after the cut → no selection
        host.RunUntilIdle();
        Assert.True(bubbled);
    }

    [Fact] // C11.26: collapsing the viewport to zero resets the scroll offset (no negative caret column)
    public void C11_26_ViewportCollapseResetsScroll()
    {
        var (host, box) = Shown(new string('x', 40), width: 12);
        using var _ = host;
        box.CaretIndex = 40; // scroll to the far end
        host.RunUntilIdle();
        Assert.True(box.Presenter!.ScrollOffset > 0);

        box.MinWidth = 0;  // clear the theme floor so Width can actually collapse the field
        box.Width = 2;     // Border padding (1,0) then leaves the presenter a zero-width viewport
        host.RunUntilIdle();
        Assert.Equal(0, box.Presenter!.ScrollOffset); // reset, not left stale
    }

    // ───────────────────────────── helpers ─────────────────────────────

    // True when haystack contains needle as a contiguous byte run (structural, not reference).
    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty)
            return true;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    // Runs frames after a gesture and concatenates their emitted bytes. LastFrameBytes is per-frame and
    // CaptureFrame drains the sink each frame, so a multi-frame settle would otherwise lose the one frame
    // that carried an out-of-band control sequence (the OSC 52 clipboard write).
    private static byte[] CollectFrames(UITestHost host, int frames = 4)
    {
        var all = new List<byte>();
        for (var i = 0; i < frames; i++)
        {
            host.RunFrame();
            all.AddRange(host.LastFrameBytes.ToArray());
        }

        return [.. all];
    }

    private sealed class Vm : INotifyPropertyChanged
    {
        private string _name = "";

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value)
                    return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
