using GlowTerm.Core.Input.Parsing;

namespace GlowTerm.Core.Tests.Input.Parsing;

public class VtSequenceClassifierTests
{
    private readonly VtSequenceClassifier _classifier = new();
    private readonly RecordingTokenSink _sink = new();

    private void Feed(string utf8) => _classifier.Process(System.Text.Encoding.UTF8.GetBytes(utf8), _sink);
    private void Feed(params byte[] bytes) => _classifier.Process(bytes, _sink);
    private void Flush() => _classifier.Flush(_sink);

    [Fact]
    public void EmptyInput_DispatchesNothing()
    {
        Feed("");
        Assert.Empty(_sink.Tokens);
    }

    [Fact]
    public void PrintableRun_DispatchesOnePrintToken()
    {
        Feed("hello");

        var print = Assert.IsType<RecordedToken.Print>(Assert.Single(_sink.Tokens));
        Assert.Equal("hello"u8.ToArray(), print.Bytes);
    }

    [Fact]
    public void ControlCharacter_DispatchesExecute()
    {
        Feed(0x09); // Tab

        var exec = Assert.IsType<RecordedToken.Execute>(Assert.Single(_sink.Tokens));
        Assert.Equal(0x09, exec.ControlChar);
    }

    [Fact]
    public void DelByte_DispatchesAsExecute()
    {
        Feed(0x7F); // DEL

        var exec = Assert.IsType<RecordedToken.Execute>(Assert.Single(_sink.Tokens));
        Assert.Equal(0x7F, exec.ControlChar);
    }

    [Fact]
    public void PrintAndControl_AreSeparated()
    {
        Feed("ab\tcd");

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal("ab"u8.ToArray(), Assert.IsType<RecordedToken.Print>(t).Bytes),
            t => Assert.Equal((byte)0x09, Assert.IsType<RecordedToken.Execute>(t).ControlChar),
            t => Assert.Equal("cd"u8.ToArray(), Assert.IsType<RecordedToken.Print>(t).Bytes));
    }

    // ---- Bare ESC ----

    [Fact]
    public void BareEsc_WithoutFlush_DispatchesNothing()
    {
        Feed(0x1B);
        Assert.Empty(_sink.Tokens);
    }

    [Fact]
    public void BareEsc_FlushedAfterTimeout_DispatchesAsEscWithFinalZero()
    {
        Feed(0x1B);
        Flush();

        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal(0, esc.Final);
        Assert.Empty(esc.Intermediates);
    }

    [Fact]
    public void DoubleEsc_CommitsFirstEscThenStartsNewSequence()
    {
        Feed(0x1B, 0x1B);
        // First ESC committed; second ESC pending.
        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal(0, esc.Final);

        Flush();
        Assert.Equal(2, _sink.Tokens.Count);
        var second = Assert.IsType<RecordedToken.EscDispatch>(_sink.Tokens[1]);
        Assert.Equal(0, second.Final);
    }

    [Fact]
    public void EscWithFinal_DispatchesEscWithThatFinal()
    {
        Feed(0x1B, (byte)'7'); // ESC 7 = DECSC

        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'7', esc.Final);
        Assert.Empty(esc.Intermediates);
    }

    [Fact]
    public void EscWithIntermediateAndFinal_CapturesBoth()
    {
        Feed(0x1B, (byte)'(', (byte)'B'); // G0 charset = US ASCII

        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'B', esc.Final);
        Assert.Equal(new byte[] { (byte)'(' }, esc.Intermediates);
    }

    // ---- CSI ----

    [Fact]
    public void CsiArrowUp_DispatchesNoPrefixNoParamsFinalA()
    {
        Feed("\x1b[A");

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)0, csi.PrivatePrefix);
        Assert.Empty(csi.Parameters);
        Assert.Empty(csi.Intermediates);
        Assert.Equal((byte)'A', csi.Final);
    }

    [Fact]
    public void CsiWithParameters_PreservesRawParameterBytes()
    {
        Feed("\x1b[1;5A"); // Ctrl+Up

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)0, csi.PrivatePrefix);
        Assert.Equal("1;5"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'A', csi.Final);
    }

    [Fact]
    public void CsiWithSubParameters_PreservesColons()
    {
        Feed("\x1b[38:2::128:64:255m"); // 24-bit fg color via colon sub-params

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal("38:2::128:64:255"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'m', csi.Final);
    }

    [Fact]
    public void CsiPrivatePrefix_IsCaptured()
    {
        Feed("\x1b[?25h"); // DECSET 25 (show cursor)

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'?', csi.PrivatePrefix);
        Assert.Equal("25"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'h', csi.Final);
    }

    [Fact]
    public void CsiSecondaryPrefix_IsCaptured()
    {
        Feed("\x1b[>0c"); // DA2 query

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'>', csi.PrivatePrefix);
        Assert.Equal("0"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'c', csi.Final);
    }

    [Fact]
    public void CsiSgrMousePrefix_IsCaptured()
    {
        Feed("\x1b[<0;10;20M"); // SGR mouse press at (10, 20)

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'<', csi.PrivatePrefix);
        Assert.Equal("0;10;20"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'M', csi.Final);
    }

    [Fact]
    public void CsiAcrossMultipleProcessCalls_CompletesOnFinalByte()
    {
        Feed("\x1b[");
        Feed("1;5");
        Assert.Empty(_sink.Tokens);
        Feed("A");

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal("1;5"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'A', csi.Final);
    }

    [Fact]
    public void CsiOverlongParameters_AreIgnoredButFinalReturnsToGround()
    {
        // Overlong parameter buffer (default 256) — feed 300 digits then a final byte.
        var overlong = new string('1', 300);
        Feed("\x1b[" + overlong + "A");
        Assert.Empty(_sink.Tokens); // Sequence is ignored.
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);

        // The classifier is back to ground and ready for another sequence.
        Feed("\x1b[A");
        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'A', csi.Final);
    }

    // ---- OSC ----

    [Fact]
    public void OscTerminatedByBel_DispatchesBody()
    {
        Feed("\x1b]0;Hello\x07"); // Set window title

        var osc = Assert.IsType<RecordedToken.OscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal("0;Hello"u8.ToArray(), osc.Body);
    }

    [Fact]
    public void OscTerminatedByEscBackslash_DispatchesBody()
    {
        Feed("\x1b]11;rgb:0000/0000/0000\x1b\\"); // Background color report

        var osc = Assert.IsType<RecordedToken.OscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal("11;rgb:0000/0000/0000"u8.ToArray(), osc.Body);
    }

    [Fact]
    public void OscAcrossMultipleProcessCalls_DispatchesOnce()
    {
        Feed("\x1b]");
        Feed("11;rgb:");
        Assert.Empty(_sink.Tokens);
        Feed("ffff/ffff/ffff");
        Assert.Empty(_sink.Tokens);
        Feed("\x07");

        var osc = Assert.IsType<RecordedToken.OscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal("11;rgb:ffff/ffff/ffff"u8.ToArray(), osc.Body);
    }

    // ---- SS3 ----

    [Fact]
    public void Ss3Sequence_DispatchesEscWithOIntermediateAndFinal()
    {
        Feed("\x1bOA"); // SS3 Up arrow

        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal(new byte[] { (byte)'O' }, esc.Intermediates);
        Assert.Equal((byte)'A', esc.Final);
    }

    [Fact]
    public void Ss3PartialAcrossFeeds_CompletesOnFinal()
    {
        Feed("\x1bO");
        Assert.Empty(_sink.Tokens);

        Feed("P");
        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'P', esc.Final);
    }

    [Fact]
    public void Ss3Incomplete_FlushedAfterTimeout_RecoversAsBareEscPlusO()
    {
        // User typed ESC then O within ambiguity window, then paused.
        Feed(0x1B, (byte)'O');
        Assert.Empty(_sink.Tokens);

        Flush();
        Assert.Collection(
            _sink.Tokens,
            t =>
            {
                var esc = Assert.IsType<RecordedToken.EscDispatch>(t);
                Assert.Equal(0, esc.Final);
            },
            t =>
            {
                var print = Assert.IsType<RecordedToken.Print>(t);
                Assert.Equal(new byte[] { (byte)'O' }, print.Bytes);
            });
    }

    // ---- DCS ----

    [Fact]
    public void DcsXtVersionResponse_HooksAndUnhooks()
    {
        // DCS > | iTerm2 3.4 ST -- typical XTVERSION response shape.
        Feed("\x1bP>|iTerm2 3.4\x1b\\");

        Assert.Collection(
            _sink.Tokens,
            t =>
            {
                var hook = Assert.IsType<RecordedToken.DcsHook>(t);
                Assert.Equal((byte)'>', hook.PrivatePrefix);
                Assert.Empty(hook.Parameters);
                Assert.Empty(hook.Intermediates);
                Assert.Equal((byte)'|', hook.Final);
            },
            t => Assert.Equal("i"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("T"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("e"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("r"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("m"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("2"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal(" "u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("3"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("."u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.Equal("4"u8.ToArray(), Assert.IsType<RecordedToken.DcsPut>(t).Bytes),
            t => Assert.IsType<RecordedToken.DcsUnhook>(t));
    }

    // ---- X10 mouse framing ----

    [Fact]
    public void X10MouseDisabledByDefault_CsiMDispatchesAsOrdinaryCsiAndFollowBytesPrint()
    {
        // Without X10 framing enabled, CSI M is a regular CSI dispatch and the three follow
        // bytes flow into Ground as printable text.
        Feed(0x1B, (byte)'[', (byte)'M', 0x20, 0x21, 0x22);

        Assert.Collection(
            _sink.Tokens,
            t =>
            {
                var csi = Assert.IsType<RecordedToken.CsiDispatch>(t);
                Assert.Equal((byte)'M', csi.Final);
            },
            t =>
            {
                var print = Assert.IsType<RecordedToken.Print>(t);
                Assert.Equal(new byte[] { 0x20, 0x21, 0x22 }, print.Bytes);
            });
    }

    [Fact]
    public void X10MouseEnabled_CsiMFollowedByThreeBytes_DispatchesX10Mouse()
    {
        _classifier.X10MouseFramingEnabled = true;
        // Left press at column 5, row 10 → cb=0x20, cx=0x20+5+1=0x26, cy=0x20+10+1=0x2B.
        Feed(0x1B, (byte)'[', (byte)'M', 0x20, 0x26, 0x2B);

        var x10 = Assert.IsType<RecordedToken.X10MouseDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal(0x20, x10.Cb);
        Assert.Equal(0x26, x10.Cx);
        Assert.Equal(0x2B, x10.Cy);
    }

    [Fact]
    public void X10MouseEnabled_FollowBytesArriveAcrossFeeds_StillFrames()
    {
        _classifier.X10MouseFramingEnabled = true;
        Feed(0x1B, (byte)'[', (byte)'M');
        Assert.Empty(_sink.Tokens);

        Feed(0x20, 0x21);
        Assert.Empty(_sink.Tokens);

        Feed(0x22);
        var x10 = Assert.IsType<RecordedToken.X10MouseDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((0x20, 0x21, 0x22), (x10.Cb, x10.Cx, x10.Cy));
    }

    [Fact]
    public void X10MouseEnabled_CsiMWithParameters_RemainsOrdinaryCsi()
    {
        _classifier.X10MouseFramingEnabled = true;
        // CSI 5 M — Delete Lines, an actual CSI command with a parameter; must not trigger
        // X10 framing.
        Feed("\x1b[5M");

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal("5"u8.ToArray(), csi.Parameters);
        Assert.Equal((byte)'M', csi.Final);
    }

    [Fact]
    public void X10MouseEnabled_PartialReportFlushed_DiscardsAndReturnsToGround()
    {
        _classifier.X10MouseFramingEnabled = true;
        Feed(0x1B, (byte)'[', (byte)'M', 0x20); // Only cb arrived.
        Assert.Empty(_sink.Tokens);
        Assert.NotEqual(VtSequenceClassifier.State.Ground, _classifier.CurrentState);

        Flush();
        Assert.Empty(_sink.Tokens);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);

        // Subsequent input is parsed cleanly.
        Feed("\x1b[A");
        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'A', csi.Final);
    }

    [Fact]
    public void X10MouseEnabled_HighBitFollowBytes_ArePassedThroughRaw()
    {
        // Values > 95 produce bytes > 0x7F. Make sure the classifier doesn't accidentally
        // interpret them as anything else.
        _classifier.X10MouseFramingEnabled = true;
        Feed(0x1B, (byte)'[', (byte)'M', 0x40, 0xC1, 0xE2);

        var x10 = Assert.IsType<RecordedToken.X10MouseDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((0x40, 0xC1, 0xE2), (x10.Cb, x10.Cx, x10.Cy));
    }

    // ---- Reset / state observability ----

    [Fact]
    public void Reset_ReturnsClassifierToGround()
    {
        Feed("\x1b[1;5"); // Mid-CSI.
        Assert.NotEqual(VtSequenceClassifier.State.Ground, _classifier.CurrentState);

        _classifier.Reset();
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);

        // After reset, fresh input starts cleanly.
        Feed("\x1b[A");
        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte)'A', csi.Final);
    }

    [Fact]
    public void Process_ThrowsOnNullSink()
    {
        Assert.Throws<ArgumentNullException>(
            () => _classifier.Process(new byte[] { 0x1B }, sink: null!));
    }

    [Fact]
    public void Flush_ThrowsOnNullSink()
    {
        Assert.Throws<ArgumentNullException>(
            () => _classifier.Flush(sink: null!));
    }
}
