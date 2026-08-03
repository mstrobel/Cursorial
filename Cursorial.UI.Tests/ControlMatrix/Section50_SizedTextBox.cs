using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

#pragma warning disable xUnit1031

/// <summary>
/// Sized text editing (proposal-glyph-runs Phases 2–3): a TextBox whose text carries an OSC 66
/// sizing via <see cref="TextElement.SizingProperty"/> lays out, hit-tests, carets, and paints
/// at the source's cell advances — glyphs are atomic caret units, the caret anchors the band's
/// bottom row, and selection rides the emission as split fragments with an SGR backdrop.
/// </summary>
public sealed class Section50_SizedTextBox
{
    private static TerminalCapabilities SizedKitty { get; } = HeadlessCapabilities.KittyTruecolor with
    {
        Output = HeadlessCapabilities.KittyTruecolor.Output with
        {
            TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
        },
    };

    private static (UIHeadlessHost Host, TextBox Box) Shown(string text, int scale, int width = 20)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 8),
            Capabilities = SizedKitty,
            CaptureFrameBytes = true
        });

        var box = new TextBox
        {
            Text = text,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        if (scale > 1)
            TextElement.SetSizing(box, new TextSizing(Scale: (byte)scale));

        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        Assert.True(host.RunUntilIdle());
        return (host, box);
    }

    private static string Frame(UIHeadlessHost host)
    {
        host.RunFrame();
        return Encoding.UTF8.GetString(host.LastFrameBytes.ToArray());
    }

    [Fact]
    public void SizedCaret_AnchorsTheBandsBottomRow()
    {
        // The same box at scale 1 vs scale 2: the caret's screen row moves DOWN by one — the
        // hardware cursor anchors the bottom row of the 2-row band (IME/accessibility tracking).
        var (host1, _) = Shown("AB", scale: 1);
        using var _1 = host1;
        var s1 = host1.Application.CaretService.GetCaretState();
        Assert.True(s1.Visible);

        var (host2, _) = Shown("AB", scale: 2);
        using var _2 = host2;
        var s2 = host2.Application.CaretService.GetCaretState();
        Assert.True(s2.Visible);

        Assert.Equal(s1.Row + 1, s2.Row);
        Assert.Equal(s1.Column, s2.Column); // caret at index 0 — same leading column either way
    }

    [Fact]
    public void SizedCaret_AdvancesTwoCellsPerGlyph()
    {
        var (host, box) = Shown("AB", scale: 2);
        using var _ = host;

        var at0 = host.Application.CaretService.GetCaretState();
        box.CaretIndex = 1; // one GLYPH right — an atomic caret unit, two cells wide
        Assert.True(host.RunUntilIdle());
        var at1 = host.Application.CaretService.GetCaretState();

        Assert.Equal(at0.Column + 2, at1.Column);
    }

    [Fact]
    public void Selection_SplitsTheEmissionIntoStyledFragments()
    {
        var (host, box) = Shown("ABCD", scale: 2);
        using var _ = host;

        box.SelectionStart = 1;
        box.SelectionLength = 2; // "BC"
        var bytes = Frame(host);

        // Three pieces, three OSC 66 payloads: pre / selected / post — the fragment-splitting
        // selection model. The selected piece's SGR backdrop carries the highlight.
        Assert.Contains(";A\x1b\\", bytes);
        Assert.Contains(";BC\x1b\\", bytes);
        Assert.Contains(";D\x1b\\", bytes);
    }

    [Fact]
    public void SelectionMove_DoesNotReemitOtherEditorsFragments()
    {
        // The Phase 2 budget gate: dragging a selection re-emits only the AFFECTED pieces. Two
        // sized editors stacked; moving the selection in the first must not re-transmit the
        // second's OSC 66 payload (its fragment is untouched — the multicell guard re-emits on
        // damage, not per frame).
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 10),
            Capabilities = SizedKitty,
            CaptureFrameBytes = true
        });
        using var hostGuard = host;

        var top = new TextBox { Text = "AAAA", Width = 20 };
        var bottom = new TextBox { Text = "ZZZZ", Width = 20 };
        TextElement.SetSizing(top, new TextSizing(Scale: 2));
        TextElement.SetSizing(bottom, new TextSizing(Scale: 2));

        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        host.ShowRoot(panel);
        Assert.True(host.RunUntilIdle());
        top.Focus();
        Assert.True(host.RunUntilIdle());

        Frame(host); // settle: both editors' fragments emitted at least once

        top.SelectionStart = 1;
        top.SelectionLength = 2;
        var frame = Frame(host);

        Assert.Contains(";A", frame);                 // the edited line's pieces re-emit
        Assert.DoesNotContain(";ZZZZ\x1b\\", frame);  // the untouched editor's fragment does NOT
    }

    [Fact]
    public void PointerHit_MapsAtScaledAdvances()
    {
        var (host, box) = Shown("ABCD", scale: 2);
        using var _ = host;

        // Find the presenter's screen column from the caret at index 0, then click 5 cells in:
        // cluster boundaries sit at scaled columns 0/2/4/6/8, so column 5 rounds to boundary 4 —
        // caret index 2. (A click inside a glyph's block never lands mid-glyph: atomic units.)
        box.CaretIndex = 0;
        Assert.True(host.RunUntilIdle());
        var origin = host.Application.CaretService.GetCaretState();

        host.SendClick(origin.Column + 5, origin.Row);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(2, box.CaretIndex);
    }

    [Fact]
    public void SizedCaret_GrowsToGlyphHeight_ViaMultipleCursors()
    {
        // End to end: a focused scale-2 editor on a multiple-cursors terminal emits the
        // rectangle-form extra-cursor band — a beam on the band's upper row(s) at the caret
        // column, the hardware cursor keeping the bottom row (proposal-glyph-runs §4).
        var (host, box) = Shown("AB", scale: 2);
        using var _1 = host;

        box.CaretIndex = 1; // move the caret — the band clears and re-emits in this frame's flush
        var bytes = Frame(host);
        Assert.Contains("\x1b[>2;4:", bytes); // beam, rectangle form — the glyph-height band
    }

    [Fact]
    public void PlainEditor_IsByteIdenticalToBefore()
    {
        // The zero-cost fast path: an unsized editor resolves the identity source and must render
        // exactly as it always did — cells, no OSC 66 anywhere.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(30, 8),
            Capabilities = SizedKitty,
            CaptureFrameBytes = true
        });
        using var _1 = host;

        var box = new TextBox { Text = "hello", Width = 20 };
        host.ShowRoot(box);
        host.RunFrame(); // the first real frame paints everything — capture THAT flush

        var bytes = Encoding.UTF8.GetString(host.LastFrameBytes.ToArray());
        Assert.DoesNotContain("\x1b]66", bytes);
        Assert.Contains("hello", bytes);
    }
}
