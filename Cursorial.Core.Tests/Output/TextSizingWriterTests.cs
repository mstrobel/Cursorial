using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Tests.Output;

public class TextSizingWriterTests
{
    private static string Encode(Action<IBufferWriter<byte>> action)
    {
        var w = new ArrayBufferWriter<byte>();
        action(w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    [Fact]
    public void Write_NormalSizing_EmptyMetadataBlock()
    {
        var s = Encode(w => TextSizingWriter.Write(w, TextSizing.Normal, "hi".AsSpan()));
        Assert.Equal("\x1b]66;;hi\x1b\\", s);
    }

    [Fact]
    public void Write_ScaleTwo_EmitsSEqualsTwo()
    {
        var s = Encode(w => TextSizingWriter.Write(w, new TextSizing(Scale: 2), "big".AsSpan()));
        Assert.Equal("\x1b]66;s=2;big\x1b\\", s);
    }

    [Fact]
    public void Write_Width_EmitsW_TheSpanWidthOfTheSequence()
    {
        // 'w' is the width in cells of the WHOLE text of the sequence ("all the text in that escape code must
        // be rendered in s·w cells" — maintainer-verified against kitty, 2026-09-12): a cat forced into two cells.
        var s = Encode(w => TextSizingWriter.Write(w, TextSizing.FixedWidth(2), "🐈".AsSpan()));
        Assert.Equal("\x1b]66;w=2;🐈\x1b\\", s);
    }

    [Fact]
    public void Write_FractionalScale_EmitsNAndDColonSeparated()
    {
        var s = Encode(w => TextSizingWriter.Write(w, new TextSizing(Numerator: 1, Denominator: 2), "half".AsSpan()));
        Assert.Equal("\x1b]66;n=1:d=2;half\x1b\\", s);
    }

    [Fact]
    public void Write_AllNonDefaultsEmittedInSpecOrder()
    {
        var sizing = new TextSizing(
            Scale: 2,
            Width: 1,
            Numerator: 1,
            Denominator: 2,
            Vertical: TextSizingVerticalAlignment.Center,
            Horizontal: TextSizingHorizontalAlignment.Right);
        var s = Encode(w => TextSizingWriter.Write(w, sizing, "x".AsSpan()));
        Assert.Equal("\x1b]66;s=2:w=1:n=1:d=2:v=2:h=1;x\x1b\\", s);
    }

    [Fact]
    public void TextSizing_DefaultIsNormal()
    {
        Assert.True(default(TextSizing).IsNormal);
        Assert.True(TextSizing.Normal.IsNormal);
        Assert.False(new TextSizing(Scale: 2).IsNormal);
    }

    [Fact]
    public void WriteSplit_ShortText_EmitsSingleSequence()
    {
        var s = Encode(w => TextSizingWriter.WriteSplit(w, TextSizing.Normal, "hello"));
        Assert.Equal("\x1b]66;;hello\x1b\\", s);
    }

    [Fact]
    public void WriteSplit_OversizedText_SplitsIntoMultipleSequences()
    {
        // Build a payload that exceeds the spec cap.
        var buffer = new char[VtOutputSequences.KittyTextSizing.MaxTextBytes + 100];

        for (int i = 0, c = 'a'; i < buffer.Length; i++, c++)
        {
            if (c > 'z') c = 'a';
            buffer[i] = (char)c;
        }

        var big = new string(buffer);
        var s = Encode(w => TextSizingWriter.WriteSplit(w, TextSizing.Normal, big));

        // The combined emission should still cover all the input text and round-trip cleanly
        // when the wire OSC envelopes are stripped.
        Assert.Contains(big[..100], s);
        Assert.Contains(big.AsSpan()[VtOutputSequences.KittyTextSizing.MaxTextBytes..], s);
        Assert.Contains("\x1b\\\x1b]66;;", s); // back-to-back close+open at split boundary
    }

    // ───────────────────────────── packing (several glyphs per cell) ─────────────────────────────

    [Fact] // A packed half-size superscript: two glyphs cost one cell — one sequence, w=1.
    public void WriteSplit_PackedSuperscript_TwoHalfGlyphs_ClaimOneCell()
    {
        var s = Encode(w => TextSizingWriter.WriteSplit(w, TextSizing.Superscript(), "12"));
        Assert.Equal("\x1b]66;w=1:n=1:d=2;12\x1b\\", s);
        Assert.Equal(1, TextSizing.Superscript().SpanColumns("12"));
    }

    [Fact] // Three half glyphs round up to two cells; the footprint and the emitted w agree.
    public void WriteSplit_PackedSubscript_ThreeHalfGlyphs_ClaimTwoCells()
    {
        var s = Encode(w => TextSizingWriter.WriteSplit(w, TextSizing.Subscript(), "123"));
        Assert.Equal("\x1b]66;w=2:n=1:d=2:v=1;123\x1b\\", s);
        Assert.Equal(2, TextSizing.Subscript().SpanColumns("123"));
    }

    [Fact] // The spec caps w at 7 per sequence: fifteen half glyphs go out as a full seven-cell sequence + a one-cell tail.
    public void WriteSplit_Packed_LongRun_ChunksAtSevenCells()
    {
        var text = "123456789012345"; // 15 half-size glyphs = 7.5 cells
        var s = Encode(w => TextSizingWriter.WriteSplit(w, TextSizing.Superscript(), text));
        Assert.Equal("\x1b]66;w=7:n=1:d=2;12345678901234\x1b\\\x1b]66;w=1:n=1:d=2;5\x1b\\", s);
        Assert.Equal(8, TextSizing.Superscript().SpanColumns(text)); // 7 + 1
        var chunks = TextSizingPacking.Chunks(TextSizing.Superscript(), text);
        Assert.Equal(2, chunks.Count);
        Assert.Equal((0, 14, 7), (chunks[0].Start, chunks[0].Length, chunks[0].Cells));
        Assert.Equal((14, 1, 1), (chunks[1].Start, chunks[1].Length, chunks[1].Cells));
    }

    [Fact] // Packing at normal size claims the natural width, never splitting a wide cluster.
    public void WriteSplit_PackedNormalSize_ClaimsNaturalWidth_WideClustersWhole()
    {
        var sizing = new TextSizing(Packed: true);
        var s = Encode(w => TextSizingWriter.WriteSplit(w, sizing, "a🐈b"));
        Assert.Equal("\x1b]66;w=4;a🐈b\x1b\\", s);
        Assert.Equal(4, sizing.SpanColumns("a🐈b"));
    }

    [Fact] // Scale multiplies the packed footprint: a double-size packed span of two half glyphs is 2 columns × 2 rows.
    public void SpanSize_ScaleMultipliesThePackedWidth()
    {
        var sizing = new TextSizing(Scale: 2, Numerator: 1, Denominator: 2, Packed: true);
        Assert.Equal((2, 2), sizing.SpanSize("12"));
        var s = Encode(w => TextSizingWriter.WriteSplit(w, sizing, "12"));
        Assert.Equal("\x1b]66;s=2:w=1:n=1:d=2;12\x1b\\", s);
    }

    [Fact] // An explicit width pins the sequence; Packed is then ignored.
    public void SpanColumns_ExplicitWidth_WinsOverPacked()
    {
        var sizing = new TextSizing(Width: 3, Numerator: 1, Denominator: 2, Packed: true);
        Assert.Equal(3, sizing.SpanColumns("1"));
        var s = Encode(w => TextSizingWriter.WriteSplit(w, sizing, "1"));
        Assert.Equal("\x1b]66;w=3:n=1:d=2;1\x1b\\", s);
    }

    // ───────────────────────────── capability gate ─────────────────────────────

    [Fact]
    public void IsSupported_WidthAndPacking_NeedTheWCapability()
    {
        var scaleOnly = Cursorial.Output.Capabilities.OutputCapabilities.None with { TextSizing = new(Width: false, Scale: true) };
        var both = Cursorial.Output.Capabilities.OutputCapabilities.None with { TextSizing = new(Width: true, Scale: true) };

        Assert.False(TextSizing.FixedWidth(2).IsSupported(scaleOnly));
        Assert.True(TextSizing.FixedWidth(2).IsSupported(both));
        Assert.False(TextSizing.Superscript().IsSupported(scaleOnly));
        Assert.True(TextSizing.Superscript().IsSupported(both));
        Assert.True(TextSizing.Double.IsSupported(scaleOnly));
        Assert.False(TextSizing.Normal.IsSupported(both)); // nothing to draw differently — the fallback is the better path
    }

    // ───────────────────────────── string form / converter ─────────────────────────────

    [Theory]
    [InlineData("Normal", "Normal")]
    [InlineData("double", "s=2")]
    [InlineData("3x", "s=3")]
    [InlineData("Superscript", "n=1:d=2:packed")]
    [InlineData("subscript 1/3", "n=1:d=3:v=1:packed")]
    [InlineData("s=2:n=1:d=2:v=2:h=1:w=1", "s=2:n=1:d=2:v=2:h=1:w=1")]
    [InlineData("s=2, v=bottom, h=center", "s=2:v=1:h=2")]
    [InlineData("2x 1/2 center packed", "s=2:n=1:d=2:v=2:packed")]
    [InlineData("w=2", "w=2")]
    [InlineData("P=1 n=1 d=4", "n=1:d=4:packed")]
    public void Parse_NamedAndMetadataForms_RoundTripThroughToString(string input, string canonical)
    {
        var sizing = TextSizing.Parse(input);
        Assert.Equal(canonical, sizing.ToString());
        Assert.Equal(sizing, TextSizing.Parse(canonical));
    }

    [Theory]
    [InlineData("")]
    [InlineData("huge")]
    [InlineData("8x")]
    [InlineData("w=8")]
    [InlineData("n=1")]      // a fraction needs both n and d
    [InlineData("2/2")]      // the spec requires d > n
    [InlineData("s=0")]
    [InlineData("v=3")]
    public void TryParse_RejectsBadInput(string input)
    {
        Assert.False(TextSizing.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => TextSizing.Parse(input));
    }

    [Fact]
    public void Converter_ConvertsBothWays()
    {
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(typeof(TextSizing));
        Assert.IsType<TextSizingConverter>(converter);
        var sizing = Assert.IsType<TextSizing>(converter.ConvertFromInvariantString("superscript"));
        Assert.Equal(TextSizing.Superscript(), sizing);
        Assert.Equal("n=1:d=2:packed", converter.ConvertToInvariantString(sizing));
    }
}
