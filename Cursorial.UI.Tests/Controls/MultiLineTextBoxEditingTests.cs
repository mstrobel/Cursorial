using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

// Phase 3 spec for multi-line TextBox editing: vertical caret movement with a sticky desired column,
// per-visual-line vs document Home/End, PageUp/PageDown by viewport, Enter→newline / Tab→tab under the
// AcceptsReturn/AcceptsTab gates, multi-line pointer hit-testing, and selection across a line break.
public sealed class MultiLineTextBoxEditingTests
{
    private static (UIHeadlessHost Host, TextBox Box) Shown(
        Action<TextBox> configure, int width = 16, int? height = 6, bool focus = true)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 12),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        var box = new TextBox
        {
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (height is { } h)
            box.Height = h;
        configure(box);
        host.ShowRoot(box);
        host.RunUntilIdle();
        if (focus)
        {
            box.Focus();
            host.RunUntilIdle();
        }

        return (host, box);
    }

    // Offsets in "hello\nhi\nworld": h0 e1 l2 l3 o4 \n5 h6 i7 \n8 w9 o10 r11 l12 d13.

    [Fact] // Down keeps a sticky desired column: a short line clamps the caret but the column is remembered
    public void DownArrow_StickyColumn_ClampsThenRestores()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "hello\nhi\nworld"; });
        using var _ = host;
        box.CaretIndex = 4; // column 4 of "hello" (the 'o')

        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(8, box.CaretIndex); // "hi" is too short → clamps to its end (column 2 visually)

        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(13, box.CaretIndex); // the desired column 4 is restored on "world" (the 'd')
    }

    [Fact] // Up mirrors Down with the same sticky-column behavior
    public void UpArrow_StickyColumn_ClampsThenRestores()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "hello\nhi\nworld"; });
        using var _ = host;
        box.CaretIndex = 13; // column 4 of "world"

        host.SendKey(Key.UpArrow); host.RunUntilIdle();
        Assert.Equal(8, box.CaretIndex); // clamps to the end of "hi"

        host.SendKey(Key.UpArrow); host.RunUntilIdle();
        Assert.Equal(4, box.CaretIndex); // back to column 4 of "hello"
    }

    [Fact] // a horizontal caret move forgets the sticky column — the next vertical move uses the live column
    public void HorizontalMove_ResetsDesiredColumn()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "hello\nhi\nworld"; });
        using var _ = host;
        box.CaretIndex = 4; // column 4 of "hello"

        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(8, box.CaretIndex); // on "hi", sticky column still 4

        host.SendKey(Key.LeftArrow); host.RunUntilIdle();
        Assert.Equal(7, box.CaretIndex); // moved to column 1 of "hi" — resets the sticky column

        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(10, box.CaretIndex); // column 1 of "world" (NOT 13 — the stale column 4 was forgotten)
    }

    [Fact] // Home/End act per visual line; Ctrl+Home/Ctrl+End jump to the document extremes
    public void HomeEnd_PerLine_CtrlIsDocument()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "hello\nworld"; });
        using var _ = host; // h0 e1 l2 l3 o4 \n5 w6 o7 r8 l9 d10
        box.CaretIndex = 8; // column 2 of "world"

        host.SendKey(Key.Home); host.RunUntilIdle();
        Assert.Equal(6, box.CaretIndex); // start of the "world" line

        host.SendKey(Key.End); host.RunUntilIdle();
        Assert.Equal(11, box.CaretIndex); // end of the "world" line content

        host.SendKey(Key.Home, KeyModifiers.Control); host.RunUntilIdle();
        Assert.Equal(0, box.CaretIndex); // document start

        host.SendKey(Key.End, KeyModifiers.Control); host.RunUntilIdle();
        Assert.Equal(11, box.CaretIndex); // document end
    }

    [Fact] // PageDown / PageUp move the caret by one viewport of visual lines
    public void PageDownUp_MovesByViewport()
    {
        // 10 single-digit lines; a fixed 3-row height makes the field scroll with a 3-line viewport.
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "0\n1\n2\n3\n4\n5\n6\n7\n8\n9"; }, height: 3);
        using var _ = host;
        box.CaretIndex = 0;

        var page = box.Presenter!.ViewportRows;
        Assert.True(page >= 1);

        host.SendKey(Key.PageDown); host.RunUntilIdle();
        // Each line is "d\n" (2 chars); line k starts at offset 2k. Clamped to the last line.
        Assert.Equal(2 * Math.Min(page, 9), box.CaretIndex);

        host.SendKey(Key.PageUp); host.RunUntilIdle();
        Assert.Equal(0, box.CaretIndex); // back to the top
    }

    [Fact] // Enter inserts a newline when AcceptsReturn is set
    public void Enter_InsertsNewline_WhenAcceptsReturn()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "ab"; });
        using var _ = host;
        box.CaretIndex = 1;

        host.SendKey(Key.Enter); host.RunUntilIdle();

        Assert.Equal("a\nb", box.Text);
        Assert.Equal(2, box.CaretIndex); // past the inserted '\n'
    }

    [Fact] // a single-line field never inserts on Enter (it bubbles for IsDefault / form submit)
    public void Enter_DoesNotInsert_WhenSingleLine()
    {
        var (host, box) = Shown(b => { b.Text = "ab"; }, height: 1); // single-line (default)
        using var _ = host;
        box.CaretIndex = 1;

        host.SendKey(Key.Enter); host.RunUntilIdle();

        Assert.Equal("ab", box.Text); // unchanged — Enter was not consumed as an edit
    }

    [Fact] // Tab inserts a tab character when AcceptsTab is set
    public void Tab_InsertsTab_WhenAcceptsTab()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.AcceptsTab = true; b.Text = "ab"; });
        using var _ = host;
        box.CaretIndex = 1;

        host.SendKey(Key.Tab); host.RunUntilIdle();

        Assert.Equal("a\tb", box.Text);
        Assert.Equal(2, box.CaretIndex);
    }

    [Fact] // with AcceptsTab off, Tab moves focus instead of inserting (WPF default)
    public void Tab_MovesFocus_WhenNotAcceptsTab()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 12),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;
        var first = new TextBox { Text = "ab", AcceptsReturn = true, Width = 12, Height = 3 };
        var second = new TextBox { Text = "cd", Width = 12, Height = 1 };
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        panel.Children.Add(first);
        panel.Children.Add(second);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        first.Focus();
        first.CaretIndex = 1;
        host.RunUntilIdle();

        host.SendKey(Key.Tab); host.RunUntilIdle();

        Assert.Equal("ab", first.Text);     // no tab inserted
        Assert.True(second.IsFocused, "Tab did not move focus to the next field");
    }

    [Fact] // single-line Up/Down are not consumed (no vertical movement, caret unchanged)
    public void UpDown_NoOp_WhenSingleLine()
    {
        var (host, box) = Shown(b => { b.Text = "hello"; }, height: 1);
        using var _ = host;
        box.CaretIndex = 3;

        host.SendKey(Key.UpArrow); host.RunUntilIdle();
        Assert.Equal(3, box.CaretIndex);
        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(3, box.CaretIndex);
    }

    [Fact] // clicking a lower visual line places the caret on that line (multi-line pointer hit-testing)
    public void PointerClick_HitsLowerLine()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "AAA\nBBB\nCCC"; }, width: 10);
        using var _ = host;

        // The box is at screen (0,0); the Border padding (1,0) puts the presenter at column 1, row 0.
        // Screen (col 2, row 1) → presenter-local (1, 1) → the "BBB" line. ("BBB" is offsets 4..7.)
        host.SendClick(2, 1);
        host.RunUntilIdle();

        Assert.InRange(box.CaretIndex, 4, 7);
    }

    // ───────────────────────────── soft-wrap navigation (audit regressions) ─────────────────────────────

    [Fact] // Down through soft-wrapped lines visits every line exactly once (audit #1: no skip)
    public void SoftWrap_DownDoesNotSkipWrappedLine()
    {
        // MinWidth=0 lets Width=6 win over the theme's MinWidth=12 → presenter width 4 (Border padding 1,0),
        // so "aaaabbbbcccc" wraps to aaaa/bbbb/cccc.
        var (host, box) = Shown(b => { b.MinWidth = 0; b.TextWrapping = WrapMode.WordWrap; b.Text = "WXYZ\naaaabbbbcccc"; }, width: 6, height: 4);
        using var _ = host;
        Assert.Equal(4, box.Presenter!.ViewportColumns); // guard: the wrap layout below assumes a width-4 viewport
        box.CaretIndex = 4; // end of "WXYZ" (visual column 4)

        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(9, box.CaretIndex);  // end of "aaaa"
        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(13, box.CaretIndex); // end of "bbbb" — NOT skipped to "cccc" (17)
        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(17, box.CaretIndex); // end of "cccc"
    }

    [Fact] // Up through soft-wrapped lines never gets stuck (audit #1: no stick)
    public void SoftWrap_UpDoesNotStick()
    {
        var (host, box) = Shown(b => { b.MinWidth = 0; b.TextWrapping = WrapMode.WordWrap; b.Text = "WXYZ\naaaabbbbcccc"; }, width: 6, height: 4);
        using var _ = host;
        Assert.Equal(4, box.Presenter!.ViewportColumns); // guard: the wrap layout below assumes a width-4 viewport
        box.CaretIndex = 17; // end of "cccc"

        host.SendKey(Key.UpArrow); host.RunUntilIdle();
        Assert.Equal(13, box.CaretIndex); // end of "bbbb"
        host.SendKey(Key.UpArrow); host.RunUntilIdle();
        Assert.Equal(9, box.CaretIndex);  // end of "aaaa" — did not stick at 13
        host.SendKey(Key.UpArrow); host.RunUntilIdle();
        Assert.Equal(4, box.CaretIndex);  // end of "WXYZ"
    }

    [Fact] // End on a soft-wrapped line rests at that line's visual end, not the next line's start (audit #2)
    public void SoftWrap_EndStaysOnWrappedLine()
    {
        // Box width 8 → presenter width 6. "hello world" wraps to "hello "[0,6) and "world"[6,11).
        var (host, box) = Shown(b => { b.TextWrapping = WrapMode.WordWrap; b.Text = "hello world"; }, width: 8, height: 3);
        using var _ = host;
        box.CaretIndex = 2; // column 2 of the first wrapped line

        host.SendKey(Key.End); host.RunUntilIdle();
        Assert.Equal(6, box.CaretIndex);            // content end of "hello "
        Assert.True(box.CaretLineEndAffinity);      // affinity keeps it on line 0's visual end (renders there, not line 1 col 0)

        host.SendKey(Key.End); host.RunUntilIdle();
        Assert.Equal(6, box.CaretIndex);            // idempotent — does not jump down to "world"

        host.SendKey(Key.DownArrow); host.RunUntilIdle();
        Assert.Equal(11, box.CaretIndex);           // Down from the wrapped-line end reaches "world"'s end
    }

    [Fact] // Shift+Down extends the selection across a line break
    public void ShiftDown_SelectsAcrossLineBreak()
    {
        var (host, box) = Shown(b => { b.AcceptsReturn = true; b.Text = "hello\nworld"; });
        using var _ = host;
        box.CaretIndex = 2; // column 2 of "hello"

        host.SendKey(Key.DownArrow, KeyModifiers.Shift); host.RunUntilIdle();

        Assert.Equal(2, box.SelectionStart);
        Assert.Equal(6, box.SelectionLength); // [2, 8): "llo\nwo"
        Assert.Equal("llo\nwo", box.SelectedText);
    }
}
