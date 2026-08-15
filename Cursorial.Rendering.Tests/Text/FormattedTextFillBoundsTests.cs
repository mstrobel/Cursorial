using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// The <see cref="FormattedText.FillEntireBounds"/> background fill — one behavioural test per rung
/// of the design table (docs/text-carrier-design.md, "`FillEntireBounds`: the clear becomes a
/// background fill"): channel absent ⇒ untouched; a transparent sample ⇒ tint no-op; a translucent
/// sample ⇒ the glyph survives with the sample stored verbatim; an opaque sample (Default/palette
/// kinds included) ⇒ blanked and owned as the DURABLE whitespace cell. Blank-vs-tint derives from
/// the SAMPLED colour per cell (<see cref="Color.IsOpaque"/>), never from the brush.
/// </summary>
/// <remarks>
/// The durable kind on the opaque arm is the probe's answer (see
/// <c>FillEntireBoundsGroupTests</c> in Cursorial.Drawing.Tests): a non-durable styled blank lets a
/// lower layer's glyph ride through the compositor's merge path, so "owns the rect" only holds at
/// composite with the fill family's occluder cell.
/// </remarks>
public class FormattedTextFillBoundsTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Green = Color.FromRgb(0, 128, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color BlueHalf = Color.FromRgba(0, 0, 255, 128);

    // A one-line document with no stated background anywhere; painted into 3 rows it re-centres to
    // row 1, leaving rows 0 and 2 (and the line's right) as surround.
    private static FormattedText Document(PartialStyle? defaultStyle = null)
        => new TextFormatter().Format(new RichTextBuilder(defaultStyle ?? default).Run("hi").Build(),
                                      10, fillEntireBounds: true);

    private static readonly Rect Bounds = new(0, 0, 10, 3);

    // A stale cell planted in the surround BEFORE the paint — the design table's
    // ("x", old style) row. Bold + a red foreground are the side-channels the opaque arm must drop
    // and the tint arm must keep.
    private static readonly Cell Stale =
        new("x", CellKind.Single, CellStyle.Default.WithForeground(Red).WithAttributes(TextAttributes.Bold));

    private static CellBuffer Painted(FormattedText text, IBrush? background, out Cell staleBefore)
    {
        var buffer = new CellBuffer(10, 3);
        buffer[7, 2] = Stale;
        staleBefore = buffer[7, 2];

        text.Paint(buffer.AsView(), Bounds, OutputCapabilities.None, resolver: null, background: background);
        return buffer;
    }

    // ---- Rung 1: channel absent ⇒ SKIP — the surround is untouched -----------------------------

    [Fact]
    public void NoResolvableBackground_LeavesEveryUnpaintedCellUntouched()
    {
        // No preference background, no document-default background: the guard is channel absence,
        // and the fill contributes nothing — the stale cell and the buffer's own blanks survive
        // byte-identically. (≡ filling Transparent, which the next rung pins.)
        var buffer = Painted(Document(), background: null, out var staleBefore);

        Assert.Equal(staleBefore, buffer[7, 2]);
        Assert.Equal(Cell.Blank with { Style = buffer.DefaultStyle }, buffer[0, 0]);
        Assert.Equal("h", buffer[0, 1].Grapheme);   // the document still painted, re-centred
    }

    // ---- Rung 2: a transparent sample ⇒ tint no-op — observably untouched ----------------------

    [Fact]
    public void TransparentBackground_IsATintNoOp()
    {
        var buffer = Painted(Document(), new SolidColorBrush(Color.Transparent), out var staleBefore);

        Assert.Equal(staleBefore, buffer[7, 2]);
        Assert.Equal(Cell.Blank with { Style = buffer.DefaultStyle }, buffer[0, 0]);
    }

    // ---- Rung 3: a translucent sample ⇒ the glyph survives; the sample is stored verbatim ------

    [Fact]
    public void TranslucentBackground_TintsTheCell_GlyphAndInkSurvive_AlphaStoredVerbatim()
    {
        var buffer = Painted(Document(), new SolidColorBrush(BlueHalf), out _);

        var tinted = buffer[7, 2];
        Assert.Equal("x", tinted.Grapheme);                                  // the glyph survives
        Assert.Equal(Red, tinted.Style.Foreground);                          // ink channels kept
        Assert.Equal(TextAttributes.Bold, tinted.Style.Attributes);
        Assert.Equal(BlueHalf, tinted.Style.Background);                     // verbatim — alpha 128 intact
    }

    // ---- Rung 4: an opaque sample ⇒ blanked and owned, as the durable occluder -----------------

    [Fact]
    public void OpaqueRgbBackground_BlanksAndOwns_TheDurableCellOverTheDefaultGround()
    {
        var buffer = Painted(Document(), new SolidColorBrush(Blue), out _);

        var owned = buffer[7, 2];
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, owned.Grapheme);       // durable — owns at composite
        Assert.Equal(CellKind.Single, owned.Kind);
        Assert.Equal(CellStyle.Default.WithBackground(Blue), owned.Style);   // stale side-channels gone
    }

    [Fact]
    public void DefaultKindBackground_IsOpaque_AndOwnsTheRectInTerminalDefault()
    {
        // Color.Default is a non-RGB kind ⇒ opaque ⇒ owns — NoColor's "own the rect in terminal
        // default" (the TextPresenter selection-backdrop shape), with no sentinel special-casing.
        var buffer = Painted(Document(), new SolidColorBrush(Color.Default), out _);

        var owned = buffer[7, 2];
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, owned.Grapheme);
        Assert.Equal(CellStyle.Default, owned.Style);                        // bg Default IS the default
    }

    // ---- The per-cell derivation: one brush, both arms -----------------------------------------

    /// <summary>
    /// A position-dependent brush that samples translucent on the left half and opaque on the
    /// right. Its <see cref="IBrush.IsOpaque"/> is the interface DEFAULT (true — it speaks for the
    /// Opacity knob alone; <c>ColorAt</c> is positional, so the interface cannot know its colours'
    /// alpha — defect 6's structural half). The fill must derive blank-vs-tint from the SAMPLED
    /// colour per cell, so one fill takes BOTH arms; gating on the brush-level bit (the rejected
    /// trigger) collapses them to one and this test names the corpse.
    /// </summary>
    private sealed class SplitAlphaBrush : IBrush
    {
        public Color ColorAt(int column, int row, Rect bounds)
            => column < bounds.Column + bounds.Columns / 2 ? BlueHalf : Blue;
    }

    [Fact]
    public void MixedAlphaBrush_TintsWhereTranslucent_OwnsWhereOpaque_PerSampledCell()
    {
        var brush = new SplitAlphaBrush();
        Assert.True(((IBrush) brush).IsOpaque);   // the brush-level bit is USELESS here — by design

        var buffer = new CellBuffer(10, 3);
        buffer[1, 2] = Stale;                     // left half — translucent samples
        buffer[8, 2] = Stale;                     // right half — opaque samples

        Document().Paint(buffer.AsView(), Bounds, OutputCapabilities.None, resolver: null, background: brush);

        Assert.Equal("x", buffer[1, 2].Grapheme);                              // tinted — glyph survives
        Assert.Equal(BlueHalf, buffer[1, 2].Style.Background);
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, buffer[8, 2].Grapheme);  // owned — durable blank
        Assert.Equal(CellStyle.Default.WithBackground(Blue), buffer[8, 2].Style);
    }

    // ---- The source ladder: preference Background first, then the document default's -----------

    [Fact]
    public void PreferenceBackground_WinsOverTheDocumentDefaults()
    {
        var text = Document(PartialStyle.WithBackground(Green));

        var buffer = Painted(text, new SolidColorBrush(Blue), out _);
        Assert.Equal(Blue, buffer[0, 0].Style.Background);      // the preference's opinion fills

        var fallthrough = Painted(text, background: null, out _);
        Assert.Equal(Green, fallthrough[0, 0].Style.Background); // absent ⇒ the document rung fills
    }

    [Fact]
    public void WithoutFillEntireBounds_ABackgroundPreferenceTouchesNothing()
    {
        // The preference's Background has exactly one leg — the surround fill. On a document that
        // does not fill, it must not reach a single cell (it never rides the per-run resolver,
        // whose decode drops Background deliberately).
        var text = new TextFormatter().Format(new RichTextBuilder().Run("hi").Build(), 10);
        var buffer = new CellBuffer(10, 3);

        text.Paint(buffer.AsView(), Bounds, OutputCapabilities.None, resolver: null,
                   background: new SolidColorBrush(Blue));

        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 10; column++)
            Assert.NotEqual(Blue, buffer[column, row].Style.Background);
    }
}
