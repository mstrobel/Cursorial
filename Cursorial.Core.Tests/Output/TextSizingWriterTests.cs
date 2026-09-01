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
    public void Write_WidthOnly_EmitsNoWKey()
    {
        // 'w' is unsupported by decision (spec-verified 2026-08-02: whole-sequence width, not
        // per-cluster; sub-cell layouts unmeasurable in whole cells) — a Width-only sizing emits
        // an empty metadata block, identical to normal text.
        var s = Encode(w => TextSizingWriter.Write(w, new TextSizing(Width: 2), "🐈".AsSpan()));
        Assert.Equal("\x1b]66;;🐈\x1b\\", s);
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
        Assert.Equal("\x1b]66;s=2:n=1:d=2:v=2:h=1;x\x1b\\", s); // no w= — unsupported by decision
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
}