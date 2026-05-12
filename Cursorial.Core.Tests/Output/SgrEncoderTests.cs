using System.Buffers;
using System.Text;
using Cursorial.Core.Output;

namespace Cursorial.Core.Tests.Output;

public class SgrEncoderTests
{
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
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, Style.Default.WithAttributes(TextAttributes.Bold)));
        Assert.Equal("\x1b[0;1m", s);
    }

    [Fact]
    public void WriteAbsolute_AllSimpleAttributes_EmitsAllCodes()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, Style.Default.WithAttributes(
            TextAttributes.Bold | TextAttributes.Italic | TextAttributes.Inverse
            | TextAttributes.Strikethrough | TextAttributes.Overline)));
        Assert.Equal("\x1b[0;1;3;7;9;53m", s);
    }

    [Fact]
    public void WriteAbsolute_Ansi16Foreground_EmitsSgr3037()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithForeground(Color.FromPalette(1)))); // red.
        Assert.Equal("\x1b[0;31m", s);
    }

    [Fact]
    public void WriteAbsolute_BrightAnsi_EmitsSgr9097()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithForeground(Color.FromPalette(9)))); // bright red.
        Assert.Equal("\x1b[0;91m", s);
    }

    [Fact]
    public void WriteAbsolute_PaletteForeground_EmitsSgr385n()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithForeground(Color.FromPalette(196))));
        Assert.Equal("\x1b[0;38;5;196m", s);
    }

    [Fact]
    public void WriteAbsolute_RgbForeground_EmitsSgr382rgb()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithForeground(Color.FromRgb(255, 128, 0))));
        Assert.Equal("\x1b[0;38;2;255;128;0m", s);
    }

    [Fact]
    public void WriteAbsolute_RgbBackground_EmitsSgr482rgb()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithBackground(Color.FromRgb(0, 0, 255))));
        Assert.Equal("\x1b[0;48;2;0;0;255m", s);
    }

    [Fact]
    public void WriteAbsolute_FgBgAndBold_OrderedConsistently()
    {
        var style = Style.Default
            .WithForeground(Color.FromPalette(2))
            .WithBackground(Color.FromPalette(7))
            .WithAttributes(TextAttributes.Bold);
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, style));
        // Attributes come first, then FG, then BG.
        Assert.Equal("\x1b[0;1;32;47m", s);
    }

    [Fact]
    public void WriteAbsolute_UnderlineSingle_EmitsSgr4()
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithAttributes(TextAttributes.Underline)));
        Assert.Equal("\x1b[0;4m", s);
    }

    [Theory]
    [InlineData(UnderlineStyle.Double, "\x1b[0;4:2m")]
    [InlineData(UnderlineStyle.Curly, "\x1b[0;4:3m")]
    [InlineData(UnderlineStyle.Dotted, "\x1b[0;4:4m")]
    [InlineData(UnderlineStyle.Dashed, "\x1b[0;4:5m")]
    public void WriteAbsolute_ExtendedUnderline_EmitsSgr4ColonForm(UnderlineStyle style, string expected)
    {
        var s = Encode(w => SgrEncoder.WriteAbsolute(w,
            Style.Default.WithAttributes(TextAttributes.Underline).WithUnderlineStyle(style)));
        Assert.Equal(expected, s);
    }

    [Fact]
    public void WriteAbsolute_UnderlineColorRgb_EmitsSgr582rgb()
    {
        var style = Style.Default
            .WithAttributes(TextAttributes.Underline)
            .WithUnderlineColor(Color.FromRgb(255, 0, 128));
        var s = Encode(w => SgrEncoder.WriteAbsolute(w, style));
        Assert.Equal("\x1b[0;4;58;2;255;0;128m", s);
    }

    // ---- WriteDelta ----

    [Fact]
    public void WriteDelta_IdenticalStyles_EmitsNothing()
    {
        var style = Style.Default
            .WithForeground(Color.FromPalette(3))
            .WithAttributes(TextAttributes.Bold);
        var s = Encode(w => SgrEncoder.WriteDelta(w, style, style));
        Assert.Equal("", s);
    }

    [Fact]
    public void WriteDelta_ChangeForegroundOnly_EmitsOnlyForeground()
    {
        var from = Style.Default.WithForeground(Color.FromPalette(1));
        var to = Style.Default.WithForeground(Color.FromPalette(2));
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[32m", s);
    }

    [Fact]
    public void WriteDelta_AddBoldKeepingFg_EmitsOnlyBold()
    {
        var from = Style.Default.WithForeground(Color.FromPalette(1));
        var to = from.AddAttributes(TextAttributes.Bold);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[1m", s);
    }

    [Fact]
    public void WriteDelta_RemoveItalic_EmitsResetCode23()
    {
        var from = Style.Default.WithAttributes(TextAttributes.Italic);
        var to = Style.Default;
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[23m", s);
    }

    [Fact]
    public void WriteDelta_RemoveBoldAndFaint_EmitsSingleSgr22()
    {
        // SGR 22 resets both Bold and Faint — emit it once, not twice.
        var from = Style.Default.WithAttributes(TextAttributes.Bold | TextAttributes.Faint);
        var to = Style.Default;
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22m", s);
    }

    [Fact]
    public void WriteDelta_SwapBoldForItalic_EmitsResetAndAdd()
    {
        var from = Style.Default.WithAttributes(TextAttributes.Bold);
        var to = Style.Default.WithAttributes(TextAttributes.Italic);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[22;3m", s);
    }

    [Fact]
    public void WriteDelta_UnderlineShapeChange_ReEmitsUnderline()
    {
        var from = Style.Default
            .WithAttributes(TextAttributes.Underline)
            .WithUnderlineStyle(UnderlineStyle.Single);
        var to = from.WithUnderlineStyle(UnderlineStyle.Curly);
        var s = Encode(w => SgrEncoder.WriteDelta(w, from, to));
        Assert.Equal("\x1b[4:3m", s);
    }
}
