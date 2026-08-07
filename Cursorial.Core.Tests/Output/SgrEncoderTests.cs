using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Tests.Output;

public class SgrEncoderTests
{
    // ReSharper disable once InconsistentNaming
    private static CellStyle DS => CellStyle.Default;

    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var writer = new ArrayBufferWriter<byte>();
        action(writer);
        return Encoding.ASCII.GetString(writer.WrittenSpan);
    }

    // ---- WriteReset ----

    [Fact]
    public void WriteReset_EmitsCsi0m()
    {
        var s = Encode(SgrEncoder.WriteReset);
        Assert.Equal("\x1b[0m", s);
    }

    // ---- WriteAbsolute ----

    [Fact]
    public void WriteAbsolute_DefaultStyle_EmitsResetOnly()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, default));
        Assert.Equal("\x1b[0m", s);
    }

    [Fact]
    public void WriteAbsolute_BoldOnly_EmitsResetThenBold()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithAttributes(TextAttributes.Bold)));
        Assert.Equal("\x1b[0;1m", s);
    }

    [Fact]
    public void WriteAbsolute_AllSimpleAttributes_EmitsAllCodes()
    {
        var s = Encode(w =>
                          {
                              const TextAttributes allAttributes = TextAttributes.Bold | 
                                                                   TextAttributes.Italic |
                                                                   TextAttributes.Inverse |
                                                                   TextAttributes.Strikethrough |
                                                                   TextAttributes.Overline;

                              SgrEncoder.WriteAbsolute(w, DS.WithAttributes(allAttributes));
                          });

        Assert.Equal("\x1b[0;1;3;7;9;53m", s);
    }

    [Fact]
    public void WriteAbsolute_Ansi16Foreground_EmitsSgr3037()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithForeground(Color.FromPalette(1)))); // red.
        Assert.Equal("\x1b[0;31m", s);
    }

    [Fact]
    public void WriteAbsolute_BrightAnsi_EmitsSgr9097()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithForeground(Color.FromPalette(9)))); // bright red.
        Assert.Equal("\x1b[0;91m", s);
    }

    [Fact]
    public void WriteAbsolute_PaletteForeground_EmitsSgr385n()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithForeground(Color.FromPalette(196))));
        Assert.Equal("\x1b[0;38;5;196m", s);
    }

    [Fact]
    public void WriteAbsolute_RgbForeground_EmitsSgr382rgb()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithForeground(Color.FromRgb(255, 128, 0))));
        Assert.Equal("\x1b[0;38;2;255;128;0m", s);
    }

    [Fact]
    public void WriteAbsolute_RgbBackground_EmitsSgr482rgb()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithBackground(Color.FromRgb(0, 0, 255))));
        Assert.Equal("\x1b[0;48;2;0;0;255m", s);
    }

    [Fact]
    public void WriteAbsolute_FgBgAndBold_OrderedConsistently()
    {
        CellStyle style = DS.WithForeground(Color.FromPalette(2))
                        .WithBackground(Color.FromPalette(7))
                        .WithAttributes(TextAttributes.Bold);

        var s = Encode(w => SgrEncoder.WriteAbsolute(w, style));
        // Attributes come first, then FG, then BG.
        Assert.Equal("\x1b[0;1;32;47m", s);
    }

    [Fact]
    public void WriteAbsolute_UnderlineSingle_EmitsSgr4()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithAttributes(TextAttributes.Underline)));
        Assert.Equal("\x1b[0;4m", s);
    }

    [Theory]
    [InlineData(UnderlineStyle.Double, "\x1b[0;4:2m")]
    [InlineData(UnderlineStyle.Curly, "\x1b[0;4:3m")]
    [InlineData(UnderlineStyle.Dotted, "\x1b[0;4:4m")]
    [InlineData(UnderlineStyle.Dashed, "\x1b[0;4:5m")]
    public void WriteAbsolute_ExtendedUnderline_EmitsSgr4ColonForm(UnderlineStyle style, string expected)
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, DS.WithAttributes(TextAttributes.Underline).WithUnderlineStyle(style)));
        Assert.Equal(expected, s);
    }

    [Fact]
    public void WriteAbsolute_UnderlineColorRgb_EmitsSgr582rgb()
    {
        CellStyle style = DS
                      .WithAttributes(TextAttributes.Underline)
                      .WithUnderlineColor(Color.FromRgb(255, 0, 128));

        // Underline color uses the ITU-T T.416 colon sub-parameter form (58:2::r:g:b, with the
        // empty color-space-id field) rather than the legacy semicolon form — see
        // SgrEncoder.WriteUnderlineColor for why (ConPTY leaks the semicolon sub-values as
        // standalone SGR codes, surfacing as a spurious screen-wide blink/faint).
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, style));
        Assert.Equal("\x1b[0;4;58:2::255:0:128m", s);
    }

    [Fact]
    public void WriteAbsolute_UnderlineColorPalette_EmitsSgr585idx()
    {
        CellStyle style = DS
                      .WithAttributes(TextAttributes.Underline)
                      .WithUnderlineColor(Color.FromPalette(5));

        var s = Encode(w => SgrEncoder.WriteAbsolute(w, style));
        Assert.Equal("\x1b[0;4;58:5:5m", s);
    }

    // ---- WriteDelta ----

    [Fact]
    public void WriteDelta_IdenticalStyles_EmitsNothing()
    {
        CellStyle style = DS
                      .WithForeground(Color.FromPalette(3))
                      .WithAttributes(TextAttributes.Bold);

        var s = Encode(w => SgrEncoder.WriteDelta(w, style, style));
        Assert.Equal("", s);
    }

    [Fact]
    public void WriteDelta_ChangeForegroundOnly_EmitsOnlyForeground()
    {
        CellStyle from = DS.WithForeground(Color.FromPalette(1));
        CellStyle to = DS.WithForeground(Color.FromPalette(2));
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[32m", s);
    }

    [Fact]
    public void WriteDelta_AddBoldKeepingFg_EmitsOnlyBold()
    {
        CellStyle from = DS.WithForeground(Color.FromPalette(1));
        CellStyle to = from.AddAttributes(TextAttributes.Bold);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[1m", s);
    }

    [Fact]
    public void WriteDelta_RemoveItalic_EmitsResetCode23()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Italic);
        CellStyle to = DS;
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[23m", s);
    }

    [Fact]
    public void WriteDelta_RemoveBoldAndFaint_EmitsSingleSgr22()
    {
        // SGR 22 resets both Bold and Faint — emit it once, not twice.
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Faint);
        CellStyle to = DS;
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22m", s);
    }

    [Fact]
    public void WriteDelta_SwapBoldForItalic_EmitsResetAndAdd()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold);
        CellStyle to = DS.WithAttributes(TextAttributes.Italic);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;3m", s);
    }

    [Fact]
    public void WriteDelta_RemoveBoldOnly_EmitsSgr22WithNothingReAdded()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold);
        CellStyle to = DS;
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22m", s);
    }

    [Fact]
    public void WriteDelta_RemoveFaintOnly_EmitsSgr22WithNothingReAdded()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Faint);
        CellStyle to = DS;
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22m", s);
    }

    [Fact]
    public void WriteDelta_DropFaintKeepingBold_ReEmitsBold()
    {
        // SGR 22 clears BOTH weights, so the surviving Bold has to be re-emitted even though it
        // was already set in `from` (and therefore isn't in the added set).
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Faint);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;1m", s);
    }

    [Fact]
    public void WriteDelta_DropBoldKeepingFaint_ReEmitsFaint()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Faint);
        CellStyle to = DS.WithAttributes(TextAttributes.Faint);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;2m", s);
    }

    [Fact]
    public void WriteDelta_SwapBoldForFaint_EmitsResetThenFaint()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold);
        CellStyle to = DS.WithAttributes(TextAttributes.Faint);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;2m", s);
    }

    [Fact]
    public void WriteDelta_SwapFaintForBold_EmitsResetThenBold()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Faint);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;1m", s);
    }

    [Fact]
    public void WriteDelta_SurvivingWeightWithOtherAttributeChanges_OrdersResetsBeforeSets()
    {
        // Faint and Italic go away, Underline arrives, Bold survives the shared SGR 22 reset.
        // Reset codes come first (22, 23), then the set codes in attribute order (1, 4).
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Faint | TextAttributes.Italic);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;23;1;4m", s);
    }

    [Fact]
    public void WriteDelta_WeightSwapWithExtendedUnderline_EmitsResetThenSetsInOrder()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Faint);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;1;4:3m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineShapeChange_ReEmitsUnderline()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Underline)
                       .WithUnderlineStyle(UnderlineStyle.Single);
        CellStyle to = from.WithUnderlineStyle(UnderlineStyle.Curly);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[4:3m", s);
    }

    // ---- WriteDelta: underline shape is a value carried by SGR 4 ----
    //
    // The shape is not a flag of its own: it rides on the same SGR 4 parameter that turns the
    // underline on. So it has to be re-emitted whenever it changes, even when the Underline flag
    // itself survives the transition unchanged (and is therefore in neither the added nor the
    // removed set).

    [Fact]
    public void WriteDelta_UnderlineShapeChangeAlongsideOtherAttributeChanges_ReEmitsUnderline()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Single);
        CellStyle to = DS.WithAttributes(TextAttributes.Italic | TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;3;4:3m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineShapeChangeWithAttributeRemoval_ReEmitsUnderline()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Italic | TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Curly);
        CellStyle to = DS.WithAttributes(TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Dotted);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[23;4:4m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineShapeChangeToSingleWithAttributeAdded_ReEmitsPlainSgr4()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Double);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Single);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[1;4m", s);
    }

    [Fact]
    public void WriteDelta_WeightResetAndUnderlineShapeChange_ReEmitsBoth()
    {
        // Both failure modes at once: SGR 22 collaterally clears the surviving Bold, and the
        // underline shape changes under a surviving Underline flag.
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Faint | TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Single);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;1;4:3m", s);
    }

    [Fact]
    public void WriteDelta_UnchangedUnderlineShapeWithAttributeAdded_DoesNotReEmitUnderline()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Curly);
        CellStyle to = from.AddAttributes(TextAttributes.Bold);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[1m", s);
    }

    [Fact]
    public void WriteDelta_WeightResetWithSurvivingUnderline_DoesNotReEmitUnderline()
    {
        // SGR 22 clears the weights only — the underline is untouched by it, so nothing to restore.
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Curly);
        CellStyle to = DS.WithAttributes(TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineRemovedWithShapeChange_EmitsOnlySgr24()
    {
        // The destination has no underline, so the (now irrelevant) shape must not leak out.
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Single);
        CellStyle to = DS.WithAttributes(TextAttributes.Bold)
                         .WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[24m", s);
    }

    [Fact]
    public void WriteDelta_ShapeChangeWithUnderlineOff_EmitsNothing()
    {
        // Styles differ, but only in a field neither side surfaces — no empty `ESC [ m`.
        CellStyle from = DS.WithUnderlineStyle(UnderlineStyle.Single);
        CellStyle to = DS.WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("", s);
    }

    // ---- WriteDelta: single-member resets ----

    [Theory]
    [InlineData(TextAttributes.Italic, "\x1b[23m")]
    [InlineData(TextAttributes.Blink, "\x1b[25m")]
    [InlineData(TextAttributes.Inverse, "\x1b[27m")]
    [InlineData(TextAttributes.Hidden, "\x1b[28m")]
    [InlineData(TextAttributes.Strikethrough, "\x1b[29m")]
    [InlineData(TextAttributes.Overline, "\x1b[55m")]
    public void WriteDelta_RemoveSingleMemberAttribute_EmitsOnlyItsResetCode(
        TextAttributes attribute,
        string expected)
    {
        CellStyle from = DS.WithAttributes(attribute);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, DS));
        Assert.Equal(expected, s);
    }

    [Fact]
    public void WriteDelta_RemoveEveryAttribute_EmitsResetCodesAscending()
    {
        CellStyle from = DS.WithAttributes(AllAttributes);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, DS));
        Assert.Equal("\x1b[22;23;24;25;27;28;29;55m", s);
    }

    [Fact]
    public void WriteDelta_AddEveryAttribute_EmitsSetCodesAscending()
    {
        CellStyle to = DS.WithAttributes(AllAttributes).WithUnderlineStyle(UnderlineStyle.Single);
        var s = Encode(w => SgrEncoder.WriteDelta(w, DS, to));
        Assert.Equal("\x1b[1;2;3;4;5;7;8;9;53m", s);
    }

    // ---- WriteDelta: cross-axis ordering ----

    [Fact]
    public void WriteDelta_ForegroundChangeWithWeightReset_EmitsColorThenResetThenSet()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Faint)
                           .WithForeground(Color.FromPalette(1));
        CellStyle to = DS.WithAttributes(TextAttributes.Bold)
                         .WithForeground(Color.FromPalette(2));

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[32;22;1m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineShapeChangeWithUnchangedUnderlineColor_OmitsColor()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Single)
                           .WithUnderlineColor(Color.FromRgb(255, 0, 128));
        CellStyle to = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly)
                         .WithUnderlineColor(Color.FromRgb(255, 0, 128));

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[1;4:3m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineShapeAndColorChange_EmitsShapeBeforeColor()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Single);
        CellStyle to = DS.WithAttributes(TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly)
                         .WithUnderlineColor(Color.FromPalette(5));

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[4:3;58:5:5m", s);
    }

    [Fact]
    public void WriteDelta_WeightResetWithUnderlineColorReset_EmitsResetThenColorReset()
    {
        CellStyle from = DS.WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                           .WithUnderlineStyle(UnderlineStyle.Curly)
                           .WithUnderlineColor(Color.FromPalette(5));
        CellStyle to = DS.WithAttributes(TextAttributes.Underline)
                         .WithUnderlineStyle(UnderlineStyle.Curly);

        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;59m", s);
    }

    private const TextAttributes AllAttributes = TextAttributes.Bold |
                                                 TextAttributes.Faint |
                                                 TextAttributes.Italic |
                                                 TextAttributes.Underline |
                                                 TextAttributes.Blink |
                                                 TextAttributes.Inverse |
                                                 TextAttributes.Hidden |
                                                 TextAttributes.Strikethrough |
                                                 TextAttributes.Overline;
}