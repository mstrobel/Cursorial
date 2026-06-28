using Cursorial.Input.Parsing;

namespace Cursorial.Tests.Input.Parsing;

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
        Assert.Equal(new[] { (byte)'(' }, esc.Intermediates);
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
        Assert.Equal(new[] { (byte)'O' }, esc.Intermediates);
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
    public void Ss3Incomplete_FlushedAfterTimeout_RecoversAsAltO()
    {
        // ESC O with no SS3 final yet was Alt+O typed under meta-sends-escape; the idle flush surfaces it as
        // a single ESC dispatch with final 'O' and no intermediate — the interpreter maps that to a clean
        // Alt+O (the empty-intermediate 'O' bypasses SS3 decode). Was previously recovered as bare-ESC +
        // literal 'O' (the Escape key followed by the letter O), which is what made Alt+O unusable.
        Feed(0x1B, (byte)'O');
        Assert.Empty(_sink.Tokens);

        Flush();
        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Empty(esc.Intermediates);
        Assert.Equal((byte)'O', esc.Final);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);
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

    // ---- ESC mid-CSI: Alt+[ surfacing + no parameter leak ----

    [Fact]
    public void BareAltBracket_InterruptedByEsc_SurfacesAltBracketThenTheNextKey()
    {
        // `ESC [` (Alt+[) followed by another Alt key `ESC g`: the bare ESC[ is surfaced as Alt+[ (an ESC
        // dispatch with final '['), then ESC g decodes normally as Alt+g — neither is swallowed.
        Feed(0x1B, (byte) '['); // ESC [  (= Alt+[)
        Feed(0x1B, (byte) 'g'); // interrupting ESC, then 'g'  (= Alt+g)

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) '[', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) 'g', Assert.IsType<RecordedToken.EscDispatch>(t).Final));
    }

    [Fact]
    public void AbortedCsiWithParameters_DoesNotLeakIntoTheNextCsi()
    {
        // `ESC [ 1 ; 2` carries parameters, so an interrupting ESC discards it (NOT Alt+[) and clears the
        // buffers — the following plain `ESC [ A` (cursor Up) must dispatch with EMPTY parameters, not "1;2".
        Feed(0x1B, (byte) '[', (byte) '1', (byte) ';', (byte) '2'); // incomplete CSI carrying params
        Feed(0x1B, (byte) '[', (byte) 'A');                          // ESC aborts it, then a real CSI 'A'

        var csi = Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens));
        Assert.Equal((byte) 'A', csi.Final);
        Assert.Empty(csi.Parameters); // not the leaked "1;2"
    }

    // ---- Lone Alt+<introducer> recovered on the idle flush (the ESC [ / ESC ] / ESC P / ESC O ambiguity) ----

    [Fact]
    public void AltBracket_FlushedAfterTimeout_SurfacesAltBracket()
    {
        // `ESC [` (Alt+[) with nothing following parks in CsiEntry; before flush nothing has surfaced.
        Feed(0x1B, (byte) '[');
        Assert.Empty(_sink.Tokens);

        Flush();
        Assert.Equal((byte) '[', Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens)).Final);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);

        // The parser is fully reset, so a following REAL CSI parses cleanly with no leaked state.
        _sink.Tokens.Clear();
        Feed("\x1b[A");
        Assert.Equal((byte) 'A', Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens)).Final);
    }

    [Fact]
    public void AltCloseBracket_FlushedAfterTimeout_SurfacesAltCloseBracket()
    {
        // The headline bug: `ESC ]` (Alt+]) parks in OscString awaiting an OSC terminator a keypress never
        // sends — so before flush nothing has surfaced (and every later byte would be eaten as OSC body until
        // a stray terminator unstuck the parser). The idle flush recovers it as Alt+] instead of swallowing.
        Feed(0x1B, (byte) ']');
        Assert.Empty(_sink.Tokens);

        Flush();
        Assert.Equal((byte) ']', Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens)).Final);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);
    }

    [Fact]
    public void AltP_FlushedAfterTimeout_SurfacesAltP()
    {
        // `ESC P` (Alt+P) parks in DcsEntry; the idle flush recovers it as Alt+P.
        Feed(0x1B, (byte) 'P');
        Assert.Empty(_sink.Tokens);

        Flush();
        Assert.Equal((byte) 'P', Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens)).Final);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);
    }

    [Fact]
    public void Ss3InterruptedByEsc_SurfacesAltOThenTheNextKey()
    {
        // `ESC O` (Alt+O) interrupted by another Alt key `ESC g`: the partial SS3 is recovered as Alt+O,
        // then ESC g decodes as Alt+g — mirroring BareAltBracket_InterruptedByEsc for the SS3 introducer.
        Feed(0x1B, (byte) 'O');
        Feed(0x1B, (byte) 'g');

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) 'O', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) 'g', Assert.IsType<RecordedToken.EscDispatch>(t).Final));
    }

    [Fact]
    public void TruncatedOscWithBody_FlushedAfterTimeout_IsDroppedNotRecovered()
    {
        // A REAL OSC 52 whose bytes began arriving (ESC ] 5 2 ;) then stalled across the idle window: the
        // non-empty body means it is a fragmented device response, not Alt+] — drop it, surface nothing.
        Feed(0x1B, (byte) ']', (byte) '5', (byte) '2', (byte) ';');
        Flush();
        Assert.Empty(_sink.Tokens);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);
    }

    [Fact]
    public void TruncatedCsiWithParams_FlushedAfterTimeout_IsDroppedNotRecovered()
    {
        // `ESC [ 1 ; 2` carries parameters → a fragmented real CSI, not Alt+[: the empty-body guard drops it.
        Feed(0x1B, (byte) '[', (byte) '1', (byte) ';', (byte) '2');
        Flush();
        Assert.Empty(_sink.Tokens);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);
    }

    [Fact]
    public void TruncatedDcsWithParams_FlushedAfterTimeout_IsDroppedNotRecovered()
    {
        // `ESC P 1 ; 2` carries parameters and stops before its final (so it never hooks) → a fragmented real
        // DCS, not Alt+P: the empty-body guard drops it. (`ESC P > |` would NOT be truncated — `|` is a valid
        // DCS final that hooks the sequence, e.g. the XTVERSION introducer.)
        Feed(0x1B, (byte) 'P', (byte) '1', (byte) ';', (byte) '2');
        Flush();
        Assert.Empty(_sink.Tokens);
        Assert.Equal(VtSequenceClassifier.State.Ground, _classifier.CurrentState);
    }

    [Fact]
    public void RealOscAfterAltCloseBracketRecovery_StillParses()
    {
        // After recovering Alt+] on a flush, a subsequent REAL OSC (set window title) must parse normally —
        // proves the recovery fully reset state and left no OSC-body residue.
        Feed(0x1B, (byte) ']');
        Flush();
        Assert.Equal((byte) ']', Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens)).Final);

        _sink.Tokens.Clear();
        Feed("\x1b]0;title\x07"); // OSC 0 ; title BEL
        Assert.Equal("0;title"u8.ToArray(), Assert.IsType<RecordedToken.OscDispatch>(Assert.Single(_sink.Tokens)).Body);
    }

    // ---- Back-to-back Alt+<introducer> beating the idle timer (no flush between the two keys) ----

    [Fact]
    public void AltCloseBracket_InterruptedByEsc_SurfacesAltCloseBracketThenTheNextKey()
    {
        // `ESC ]` (Alt+]) immediately followed by `ESC g` (Alt+g): the bare ESC ] is surfaced as Alt+] at the
        // interrupting ESC (not deferred to OscEsc, since the body is empty), then ESC g decodes as Alt+g —
        // the OSC twin of BareAltBracket_InterruptedByEsc. Neither key is swallowed, no flush needed.
        Feed(0x1B, (byte) ']');
        Feed(0x1B, (byte) 'g');

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) ']', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) 'g', Assert.IsType<RecordedToken.EscDispatch>(t).Final));
    }

    [Fact]
    public void AltBracketThenAltCloseBracket_BackToBack_SurfacesBoth()
    {
        // The user's "Alt+[ then Alt+]" pressed fast enough to arrive together: ESC [ ESC ]. Alt+[ surfaces at
        // the interrupting ESC (StepCsiParam), Alt+] parks in OscString and recovers on the idle flush.
        Feed(0x1B, (byte) '[', 0x1B, (byte) ']');
        Flush();

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) '[', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) ']', Assert.IsType<RecordedToken.EscDispatch>(t).Final));
    }

    [Fact]
    public void AltCloseBracketThenAltBracket_BackToBack_SurfacesBoth()
    {
        // The reverse order (ESC ] ESC [) — the case that previously dropped the Alt+] entirely, because the
        // OSC deferred to OscEsc and aborted without surfacing it. Now Alt+] surfaces at the interrupting ESC
        // and Alt+[ recovers on the flush.
        Feed(0x1B, (byte) ']', 0x1B, (byte) '[');
        Flush();

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) ']', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) '[', Assert.IsType<RecordedToken.EscDispatch>(t).Final));
    }

    [Fact]
    public void AltCloseBracket_TerminatedByBel_SurfacesAltCloseBracketThenBel()
    {
        // Alt+] (ESC ]) immediately followed by Ctrl+G (BEL = 0x07): the bare ESC ] is surfaced as Alt+] (an
        // empty-body OSC terminated by BEL is meaningless, never a real sequence), then the BEL goes through
        // as its own control. Previously this dispatched a spurious empty OSC and lost both keys.
        Feed(0x1B, (byte) ']', 0x07);

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) ']', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) 0x07, Assert.IsType<RecordedToken.Execute>(t).ControlChar));
    }

    [Fact]
    public void HookedButUnterminatedDcs_FlushedAfterTimeout_ResetsSoNextInputParses()
    {
        // A DCS that hooked (ESC P q …, e.g. a Sixel/DECRQSS-shaped response) but never received its ST, then
        // the input idles: the Flush() backstop must reset to Ground so the parser doesn't strand in
        // DcsPassthrough and swallow every subsequent keystroke as DCS body.
        Feed("\x1bPq");       // hooks the DCS — now in passthrough
        Feed((byte) 'x');     // a body byte
        _sink.Tokens.Clear(); // ignore the hook/put tokens; we care only about post-flush recovery
        Flush();

        Feed("\x1b[A");       // a following real CSI (cursor Up) must parse, not be eaten as DCS body
        Assert.Equal((byte) 'A', Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens)).Final);
    }

    [Fact]
    public void AltP_InterruptedByEsc_SurfacesAltPThenTheNextKey()
    {
        // `ESC P` (Alt+P) immediately followed by `ESC g`: the DCS twin — Alt+P then Alt+g, neither swallowed
        // (previously ESC P ESC … fell into DcsIgnore and ate everything until an ST).
        Feed(0x1B, (byte) 'P');
        Feed(0x1B, (byte) 'g');

        Assert.Collection(
            _sink.Tokens,
            t => Assert.Equal((byte) 'P', Assert.IsType<RecordedToken.EscDispatch>(t).Final),
            t => Assert.Equal((byte) 'g', Assert.IsType<RecordedToken.EscDispatch>(t).Final));
    }

    [Fact]
    public void TruncatedOscInterruptedByEsc_DropsTheOscAndRecoversTheNextKey()
    {
        // A REAL OSC that began arriving (ESC ] 5 2) then was interrupted by ESC [ : the non-empty body means
        // it is NOT Alt+] — the OSC is dropped (no bogus key), and the following ESC [ recovers as Alt+[ only.
        Feed(0x1B, (byte) ']', (byte) '5', (byte) '2', 0x1B, (byte) '[');
        Flush();

        Assert.Equal((byte) '[', Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens)).Final);
    }

    // ---- Legacy 8-bit meta input (opt-in) ----

    [Fact]
    public void EightBitMeta_Off_HighByteStaysInThePrintRun()
    {
        // Default (UTF-8 mode): high bytes are printable and coalesced; the classifier does not decode UTF-8.
        Feed(0xE1, 0x80, 0x80);
        Assert.IsType<RecordedToken.Print>(Assert.Single(_sink.Tokens));
    }

    [Fact]
    public void EightBitMeta_On_HighByteDecodesAsEscPlusStrippedByte()
    {
        _classifier.EightBitMetaEnabled = true;
        Feed(0xE1); // 'a' (0x61) | 0x80  → equivalent to ESC 'a' (= Alt+a)

        var esc = Assert.IsType<RecordedToken.EscDispatch>(Assert.Single(_sink.Tokens));
        Assert.Empty(esc.Intermediates);
        Assert.Equal((byte) 'a', esc.Final);
    }

    [Fact]
    public void EightBitMeta_On_HighBracketEntersCsi_ExactlyLikeEscBracket()
    {
        _classifier.EightBitMetaEnabled = true;
        Feed(0xDB);          // '[' (0x5B) | 0x80  → behaves exactly like ESC [
        Feed((byte) 'A');    // completes a CSI 'A'

        Assert.Equal((byte) 'A', Assert.IsType<RecordedToken.CsiDispatch>(Assert.Single(_sink.Tokens)).Final);
    }
}
