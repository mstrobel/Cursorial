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

    // ---- FIGlet-lane pins (maintainer report: caret splits collapsed whitespace; no selection highlight) ----

    private static (UIHeadlessHost Host, TextBox Box) ShownFiglet(string text, int width = 60)
    {
        // KittyTruecolor WITHOUT TextSizing: the scaled source resolves to its bundled FIGlet
        // fallback at layout — the editor runs the direct-face lane end to end.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(80, 14),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            CaptureFrameBytes = true
        });

        var box = new TextBox
        {
            Text = text,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        TextElement.SetSizing(box, new TextSizing(Scale: 2));

        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        Assert.True(host.RunUntilIdle());
        return (host, box);
    }

    private static string[] BandRows(UIHeadlessHost host, int rows)
    {
        var lines = new string[rows];
        for (int r = 0; r < rows; r++)
            lines[r] = host.GetRowText(r);
        return lines;
    }

    [Fact]
    public void CaretSplit_DoesNotCollapseWhitespace()
    {
        // "quick| brown" used to render as "quick|brown": the piece right of the caret split
        // went through ScaledText's placeholder, whose default-WordWrap re-format DROPPED the
        // leading space and left-shifted every following glyph. The render must be IDENTICAL
        // whatever the caret position — pieces repartition, glyphs never move.
        var (host, box) = ShownFiglet("quick brown");
        using var _ = host;

        box.CaretIndex = 0;
        Assert.True(host.RunUntilIdle());
        var atStart = BandRows(host, 12);

        box.CaretIndex = 5; // "quick| brown" — the reported repro
        Assert.True(host.RunUntilIdle());
        var atSplit = BandRows(host, 12);

        Assert.Equal(atStart, atSplit);

        box.CaretIndex = 7; // "quick |b|rown"-adjacent variants from the report
        Assert.True(host.RunUntilIdle());
        Assert.Equal(atStart, BandRows(host, 12));
    }

    [Fact]
    public void FigletSelection_PaintsTheBackdrop()
    {
        // Selection highlighting "didn't work at all with figlet fonts": the placeholder cache
        // swallowed the style. The selected span must now carry the selection background across
        // its band — filled first (glyph ink is sparse), glyphs painted over it.
        var (host, box) = ShownFiglet("quick");
        using var _ = host;

        var highlight = Color.FromRgb(200, 40, 40);
        box.SelectionBrush = new Cursorial.Drawing.Media.SolidColorBrush(highlight);
        box.SelectionStart = 1;
        box.SelectionLength = 2;
        Assert.True(host.RunUntilIdle());

        var buffer = host.Application.FrameBufferInternal!;
        bool found = false;

        for (int r = 0; r < 12 && !found; r++)
        for (int c = 0; c < 60 && !found; c++)
            found = buffer[c, r].Style.Background == highlight;

        Assert.True(found, "no cell carries the selection background");
    }

    // ---- Rigid word gaps + tint selection (maintainer reports, SmallSlant) ----

    private static (UIHeadlessHost Host, TextBox Box) ShownFace(string text, int width = 70)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(80, 8),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
            CaptureFrameBytes = true
        });

        var box = new TextBox
        {
            Text = text,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        TextElement.SetFont(box, Cursorial.Rendering.Fonts.FigletFonts.SmallSlant);

        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        Assert.True(host.RunUntilIdle());
        return (host, box);
    }

    [Fact]
    public void WordGap_IsRigid_AtTheSpaceGlyphsWidth()
    {
        // Maintainer acceptance: a space in this face gaps at about a normal glyph's width.
        // SmallSlant kerns 2-3 columns into each side of its (hardblank-carrying) space under a
        // single-call paint — whitespace-rigid advances hold the FULL space width instead, and
        // no neighbor's ink may enter the gap's column span.
        var face = Cursorial.Rendering.Fonts.FigletFonts.SmallSlant;
        var metrics = face.GetMetrics();
        int spaceWidth = face.Measure(" ").Columns;

        var layout = Cursorial.Rendering.Text.GraphemeLayout.Build("quick brown", metrics);
        Assert.Equal(face.Measure("quick").Columns + spaceWidth + face.Measure("brown").Columns,
                     layout.TotalColumns);

        var (host, box) = ShownFace("quick brown");
        using var _1 = host;

        // Screen-anchor via the caret at index 0 — the presenter sits inside the TextBox chrome.
        box.CaretIndex = 0;
        Assert.True(host.RunUntilIdle());
        var origin = host.Application.CaretService.GetCaretState();
        int left = origin.Column;
        int top = origin.Row - (face.Height - 1); // caret anchors the band's BOTTOM row

        int gapStart = left + layout.ColumnOf(5);
        int gapEnd = left + layout.ColumnOf(6);
        Assert.Equal(spaceWidth, gapEnd - gapStart);

        for (int row = top; row < top + face.Height; row++)
        {
            var line = host.GetRowText(row);
            var span = line.Length > gapStart
                           ? line.Substring(gapStart, Math.Min(gapEnd, line.Length) - gapStart)
                           : "";
            Assert.True(span.TrimEnd().Length == 0, $"ink in the word gap on row {row}: '{span}'");
        }
    }

    [Fact]
    public void FaceSelection_TintsWithoutMovingGlyphs()
    {
        // Non-rectangular faces highlighted quirkily when the selection SPLIT the paint (kerning
        // broke at the boundary and glyphs shifted). Selection is now a cell TINT: the rendered
        // graphemes are IDENTICAL with and without a selection; only styles change, and the
        // selected span carries the backdrop.
        var (host, box) = ShownFace("quick");
        using var _ = host;

        var face = Cursorial.Rendering.Fonts.FigletFonts.SmallSlant;
        var before = new string[face.Height];
        for (int r = 0; r < face.Height; r++) before[r] = host.GetRowText(r);

        var highlight = Color.FromRgb(40, 160, 220);
        box.SelectionBrush = new Cursorial.Drawing.Media.SolidColorBrush(highlight);
        box.SelectionStart = 1;
        box.SelectionLength = 3;
        Assert.True(host.RunUntilIdle());

        for (int r = 0; r < face.Height; r++)
            Assert.Equal(before[r], host.GetRowText(r)); // geometry pinned — glyphs never move

        var metrics = face.GetMetrics();
        var layout = Cursorial.Rendering.Text.GraphemeLayout.Build("quick", metrics);
        var buffer = host.Application.FrameBufferInternal!;
        bool tinted = false;

        for (int r = 0; r < face.Height && !tinted; r++)
        for (int c = layout.ColumnOf(1); c < layout.ColumnOf(4) && !tinted; c++)
            tinted = buffer[c, r].Style.Background == highlight;

        Assert.True(tinted, "no cell in the selected span carries the tint");
    }

    [Fact]
    public void LeftwardSelectionFromTheEnd_TintsExactlyTheSelectedSpan_WhileScrolled()
    {
        // Maintainer repro: selecting leftward from the end of a long NoWrap line. The end-caret
        // horizontally scrolls the viewport, which used to CRASH the face lane (Rect refused the
        // negative column of the first off-screen word) — words and the tint now clip like cell
        // writes, and the tint lands exactly on the selected span's kerned columns, one band.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(90, 30),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;

        var face = Cursorial.Rendering.Fonts.FigletFonts.SmallSlant;
        var box = new TextBox
        {
            Text = "The quick brown fox\njumps over the lazy dog.",
            Width = 80,
            AcceptsReturn = true,
            Font = face,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();

        var unique = Color.FromRgb(199, 21, 133);
        box.SelectionBrush = new Cursorial.Drawing.Media.SolidColorBrush(unique);

        // Screen-anchor the expectation FIRST — the CaretIndex setter collapses any selection.
        int end = box.Text.Length;
        box.CaretIndex = end; // scroll to the end, as a leftward drag from the end would
        Assert.True(host.RunUntilIdle());
        box.CaretIndex = end - 4; // the 'd' of "dog." — the selection's left edge
        Assert.True(host.RunUntilIdle());
        var anchor = host.Application.CaretService.GetCaretState();
        int bandTop = anchor.Row - (face.Height - 1);

        // Select "dog" WITHOUT the final period: the tint's right edge then sits several columns
        // short of the viewport's, so any smear toward the edge (the Intersection width/end mixup
        // rendered the tint clear to the viewport's right edge) breaks the width pin loudly.
        box.SelectionStart = end - 4;
        box.SelectionLength = 3;
        Assert.True(host.RunUntilIdle());

        var line2 = "jumps over the lazy dog.";
        var layout = Cursorial.Rendering.Text.GraphemeLayout.Build(line2, face.GetMetrics());
        int expectedWidth = layout.ColumnOf(line2.Length - 1) - layout.ColumnOf(line2.Length - 4);

        var buffer = host.Application.FrameBufferInternal!;
        int minC = int.MaxValue, maxC = -1, minR = int.MaxValue, maxR = -1;
        for (int r = 0; r < 30; r++)
        for (int c = 0; c < 90; c++)
        {
            if (buffer[c, r].Style.Background != unique) continue;
            minC = Math.Min(minC, c); maxC = Math.Max(maxC, c);
            minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
        }

        Assert.Equal(anchor.Column, minC);                  // tint starts at the caret's cell
        Assert.Equal(expectedWidth, maxC - minC + 1);       // spans exactly the kerned "dog" columns
        Assert.Equal(bandTop, minR);                        // one band —
        Assert.Equal(anchor.Row, maxR);                     // — line 2's, and nothing else
    }

    [Fact]
    public void DeletingAllSizedText_ErasesEveryEmission()
    {
        // Maintainer repro: Ctrl+A + Delete in a sized editor left the ENTIRE text standing on the
        // terminal (partially selection-styled). The scene wipe cleared cells but not the FRAGMENT
        // registry, so the deleted emission stayed registered end to end: the compositor carried it
        // to the target every frame and the renderer — seeing it live — re-emitted it whenever its
        // cells were damaged. The wipe now empties the registry, the target drops the fragment, and
        // the renderer's vanished-fragment guard rewrites the footprint over the stale glyphs.
        var (host, box) = Shown("AB", scale: 2);
        using var _ = host;

        box.SelectAll();
        Frame(host);

        host.SendKey(Cursorial.Input.Key.Delete);
        var del = Frame(host);

        Assert.Equal("", box.Text);
        Assert.DoesNotContain("\x1b]66", del);                 // nothing re-emits deleted text —
#pragma warning disable xUnit2013
        Assert.Equal(0, host.Application.FrameBufferInternal!.Fragments.Count); // — and nothing stays registered
#pragma warning restore xUnit2013
        Assert.Contains("\x1b[1;2H", del);                     // the vacated band is REWRITTEN (erased)
    }

    [Fact]
    public void BackspacingTheLastGlyph_ErasesItsEmission()
    {
        // The char-by-char variant: every intermediate backspace has a successor fragment at the
        // same anchor (which replaces the registry entry and repaints), but the LAST one vanishes
        // with no successor — pre-fix that frame emitted nothing at all, stranding the first
        // glyph of every line on the terminal.
        var (host, box) = Shown("AB", scale: 2);
        using var _ = host;

        box.CaretIndex = 2;
        host.RunUntilIdle();

        host.SendKey(Cursorial.Input.Key.Backspace);
        var afterFirst = Frame(host);
        Assert.Contains(";A\x1b\\", afterFirst);              // successor emission still out

        host.SendKey(Cursorial.Input.Key.Backspace);
        var afterLast = Frame(host);

        Assert.Equal("", box.Text);
        Assert.DoesNotContain("\x1b]66", afterLast);
#pragma warning disable xUnit2013
        Assert.Equal(0, host.Application.FrameBufferInternal!.Fragments.Count);
#pragma warning restore xUnit2013
        Assert.Contains("\x1b[1;2H", afterLast);               // the final footprint is rewritten
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
