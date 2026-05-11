using GlowTerm.Core.Input;
using GlowTerm.Core.Input.Parsing;

namespace GlowTerm.Core.Tests.Input.Parsing;

public class VtInputInterpreterTests
{
    private readonly VtInputMode _mode = new();
    private readonly RecordingInputEventSink _sink = new();
    private readonly VtSequenceClassifier _classifier = new();
    private readonly VtInputInterpreter _interpreter;

    public VtInputInterpreterTests()
    {
        _interpreter = new VtInputInterpreter(_mode, _sink, TimeProvider.System);
    }

    private void Feed(string utf8) => _classifier.Process(System.Text.Encoding.UTF8.GetBytes(utf8), _interpreter);
    private void Feed(params byte[] bytes) => _classifier.Process(bytes, _interpreter);
    private void Flush() => _classifier.Flush(_interpreter);

    private static string TextOf(KeyEvent k) => new(k.Text.Span);

    // ---- Printable / character keys ----

    [Fact]
    public void AsciiPrintable_EmitsOneKeyEventPerCharacter()
    {
        Feed("ab");

        Assert.Equal(2, _sink.Events.Count);

        var first = _sink.At<KeyEvent>(0);
        Assert.Equal(Key.Character, first.Key);
        Assert.Equal(KeyModifiers.None, first.Modifiers);
        Assert.Equal(KeyEventKind.Down, first.Kind);
        Assert.Equal("a", TextOf(first));

        var second = _sink.At<KeyEvent>(1);
        Assert.Equal("b", TextOf(second));
    }

    [Fact]
    public void MultiByteUtf8_DecodesAsSingleRune()
    {
        Feed("é"); // U+00E9 LATIN SMALL LETTER E WITH ACUTE — 0xC3 0xA9 in UTF-8

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal("é", TextOf(k));
    }

    [Fact]
    public void SupplementaryPlane_DecodesAsSurrogatePair()
    {
        Feed("😀"); // U+1F600 — encodes to 4 bytes UTF-8, 2 chars UTF-16

        var k = _sink.Single<KeyEvent>();
        Assert.Equal("😀", TextOf(k));
        Assert.Equal(2, k.Text.Length); // surrogate pair
    }

    [Fact]
    public void Utf8SplitAcrossFeeds_BuffersContinuation()
    {
        // "é" = 0xC3 0xA9. Split between feeds.
        Feed(0xC3);
        Assert.Empty(_sink.Events);

        Feed(0xA9);
        var k = _sink.Single<KeyEvent>();
        Assert.Equal("é", TextOf(k));
    }

    // ---- Control characters ----

    [Fact]
    public void Tab_EmitsTabKey()
    {
        Feed(0x09);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Tab, k.Key);
        Assert.Equal(KeyModifiers.None, k.Modifiers);
    }

    [Fact]
    public void CarriageReturn_EmitsEnterKey()
    {
        Feed(0x0D);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Enter, k.Key);
    }

    [Fact]
    public void LineFeed_EmitsEnterKey()
    {
        Feed(0x0A);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Enter, k.Key);
    }

    [Fact]
    public void Backspace_BothBsAndDel_EmitBackspaceKey()
    {
        Feed(0x08);
        Feed(0x7F);

        Assert.Equal(2, _sink.Events.Count);
        Assert.Equal(Key.Backspace, _sink.At<KeyEvent>(0).Key);
        Assert.Equal(Key.Backspace, _sink.At<KeyEvent>(1).Key);
    }

    [Fact]
    public void Nul_EmitsCtrlSpace()
    {
        Feed(0x00);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Space, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
    }

    [Fact]
    public void CtrlA_EmitsCharacterAWithControlModifier()
    {
        Feed(0x01); // Ctrl+A

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
        Assert.Equal("a", TextOf(k));
    }

    [Fact]
    public void CtrlZ_EmitsCharacterZWithControlModifier()
    {
        Feed(0x1A); // Ctrl+Z

        var k = _sink.Single<KeyEvent>();
        Assert.Equal("z", TextOf(k));
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
    }

    // ---- Bare ESC ----

    [Fact]
    public void BareEsc_AfterFlush_EmitsEscapeKey()
    {
        Feed(0x1B);
        Assert.Empty(_sink.Events);

        Flush();
        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Escape, k.Key);
    }

    // ---- Focus events ----

    [Fact]
    public void CsiI_EmitsFocusInEvent()
    {
        Feed("\x1b[I");

        var f = _sink.Single<FocusEvent>();
        Assert.True(f.HasFocus);
    }

    [Fact]
    public void CsiO_EmitsFocusOutEvent()
    {
        Feed("\x1b[O");

        var f = _sink.Single<FocusEvent>();
        Assert.False(f.HasFocus);
    }

    // ---- Bracketed paste ----

    [Fact]
    public void BracketedPaste_EmitsSinglePasteEventOnEnd()
    {
        Feed("\x1b[200~hello\x1b[201~");

        var p = _sink.Single<PasteEvent>();
        Assert.Equal("hello", new string(p.Text.Span));
    }

    [Fact]
    public void BracketedPasteStart_EmitsNothingUntilEnd()
    {
        Feed("\x1b[200~");
        Assert.Empty(_sink.Events);

        Feed("partial content");
        Assert.Empty(_sink.Events);

        Feed("\x1b[201~");
        var p = _sink.Single<PasteEvent>();
        Assert.Equal("partial content", new string(p.Text.Span));
    }

    [Fact]
    public void BracketedPaste_PreservesEmbeddedTabAndNewline()
    {
        Feed("\x1b[200~line1");
        Feed(0x0A); // LF
        Feed("col1");
        Feed(0x09); // Tab
        Feed("col2\x1b[201~");

        var p = _sink.Single<PasteEvent>();
        Assert.Equal("line1\ncol1\tcol2", new string(p.Text.Span));
    }

    [Fact]
    public void BracketedPaste_DropsControlCharsThatArentWhitespace()
    {
        // Ctrl+A (0x01) inside paste should be dropped (not appended, not emitted as a key).
        Feed("\x1b[200~ab");
        Feed(0x01);
        Feed("cd\x1b[201~");

        var p = _sink.Single<PasteEvent>();
        Assert.Equal("abcd", new string(p.Text.Span));
    }

    [Fact]
    public void BracketedPasteEndWithoutStart_IsIgnored()
    {
        Feed("\x1b[201~");
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void BracketedPaste_PrintableBeforeStart_StillEmitsKeyEvents()
    {
        Feed("hi\x1b[200~paste\x1b[201~bye");

        Assert.Collection(
            _sink.Events,
            e => Assert.Equal("h", TextOf(Assert.IsType<KeyEvent>(e))),
            e => Assert.Equal("i", TextOf(Assert.IsType<KeyEvent>(e))),
            e => Assert.Equal("paste", new string(Assert.IsType<PasteEvent>(e).Text.Span)),
            e => Assert.Equal("b", TextOf(Assert.IsType<KeyEvent>(e))),
            e => Assert.Equal("y", TextOf(Assert.IsType<KeyEvent>(e))),
            e => Assert.Equal("e", TextOf(Assert.IsType<KeyEvent>(e))));
    }

    // ---- CSI cursor keys (no modifiers) ----

    [Theory]
    [InlineData("\x1b[A", Key.UpArrow)]
    [InlineData("\x1b[B", Key.DownArrow)]
    [InlineData("\x1b[C", Key.RightArrow)]
    [InlineData("\x1b[D", Key.LeftArrow)]
    [InlineData("\x1b[H", Key.Home)]
    [InlineData("\x1b[F", Key.End)]
    public void CsiNoParamArrowOrHomeEnd_EmitsExpectedKey(string sequence, Key expected)
    {
        Feed(sequence);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
        Assert.Equal(KeyModifiers.None, k.Modifiers);
        Assert.Equal(KeyEventKind.Down, k.Kind);
    }

    [Fact]
    public void CsiZ_EmitsShiftTab()
    {
        Feed("\x1b[Z"); // BackTab

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Tab, k.Key);
        Assert.Equal(KeyModifiers.Shift, k.Modifiers);
    }

    // ---- CSI ~ special and function keys ----

    [Theory]
    [InlineData("\x1b[1~", Key.Home)]
    [InlineData("\x1b[2~", Key.Insert)]
    [InlineData("\x1b[3~", Key.Delete)]
    [InlineData("\x1b[4~", Key.End)]
    [InlineData("\x1b[5~", Key.PageUp)]
    [InlineData("\x1b[6~", Key.PageDown)]
    [InlineData("\x1b[7~", Key.Home)]
    [InlineData("\x1b[8~", Key.End)]
    public void CsiSpecialKey_EmitsExpectedKey(string sequence, Key expected)
    {
        Feed(sequence);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
        Assert.Equal(KeyModifiers.None, k.Modifiers);
    }

    [Theory]
    [InlineData("\x1b[11~", Key.F1)]
    [InlineData("\x1b[12~", Key.F2)]
    [InlineData("\x1b[13~", Key.F3)]
    [InlineData("\x1b[14~", Key.F4)]
    [InlineData("\x1b[15~", Key.F5)]
    [InlineData("\x1b[17~", Key.F6)]
    [InlineData("\x1b[18~", Key.F7)]
    [InlineData("\x1b[19~", Key.F8)]
    [InlineData("\x1b[20~", Key.F9)]
    [InlineData("\x1b[21~", Key.F10)]
    [InlineData("\x1b[23~", Key.F11)]
    [InlineData("\x1b[24~", Key.F12)]
    [InlineData("\x1b[25~", Key.F13)]
    [InlineData("\x1b[34~", Key.F20)]
    public void CsiTildeFunctionKey_EmitsExpectedKey(string sequence, Key expected)
    {
        Feed(sequence);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
        Assert.Equal(KeyModifiers.None, k.Modifiers);
    }

    // ---- SS3 application-mode keys ----

    [Theory]
    [InlineData("\x1bOA", Key.UpArrow)]
    [InlineData("\x1bOB", Key.DownArrow)]
    [InlineData("\x1bOC", Key.RightArrow)]
    [InlineData("\x1bOD", Key.LeftArrow)]
    [InlineData("\x1bOH", Key.Home)]
    [InlineData("\x1bOF", Key.End)]
    [InlineData("\x1bOP", Key.F1)]
    [InlineData("\x1bOQ", Key.F2)]
    [InlineData("\x1bOR", Key.F3)]
    [InlineData("\x1bOS", Key.F4)]
    public void Ss3Key_EmitsExpectedKey(string sequence, Key expected)
    {
        Feed(sequence);

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
        Assert.Equal(KeyModifiers.None, k.Modifiers);
    }

    // ---- Modifier-bearing arrows / Home / End ----

    [Theory]
    [InlineData(2, KeyModifiers.Shift)]
    [InlineData(3, KeyModifiers.Alt)]
    [InlineData(4, KeyModifiers.Shift | KeyModifiers.Alt)]
    [InlineData(5, KeyModifiers.Control)]
    [InlineData(6, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(7, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(8, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)]
    public void CsiArrowWithModifierParam_EmitsModifiedKey(int modifierParam, KeyModifiers expected)
    {
        // CSI 1 ; <mod> A — Up with given modifiers.
        Feed($"\x1b[1;{modifierParam}A");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.UpArrow, k.Key);
        Assert.Equal(expected, k.Modifiers);
    }

    [Fact]
    public void CsiHomeWithCtrlShiftModifier_EmitsCtrlShiftHome()
    {
        Feed("\x1b[1;6H"); // Ctrl+Shift+Home

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Home, k.Key);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, k.Modifiers);
    }

    [Fact]
    public void CsiArrowWithSuperModifier_EmitsSuperPlusKey()
    {
        // Modifier param 9 = bits 1000 = Super only.
        Feed("\x1b[1;9D");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.LeftArrow, k.Key);
        Assert.Equal(KeyModifiers.Super, k.Modifiers);
    }

    // ---- Modifier-bearing function / special keys ----

    [Fact]
    public void CsiF5WithAltModifier_EmitsAltF5()
    {
        Feed("\x1b[15;3~"); // Alt+F5

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.F5, k.Key);
        Assert.Equal(KeyModifiers.Alt, k.Modifiers);
    }

    [Fact]
    public void CsiDeleteWithShiftModifier_EmitsShiftDelete()
    {
        Feed("\x1b[3;2~");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Delete, k.Key);
        Assert.Equal(KeyModifiers.Shift, k.Modifiers);
    }

    [Fact]
    public void CsiPageUpWithCtrlModifier_EmitsCtrlPageUp()
    {
        Feed("\x1b[5;5~");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.PageUp, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
    }

    // ---- Defensive: unrecognized sequences are dropped ----

    [Fact]
    public void CsiUnrecognizedFinal_EmitsNothing()
    {
        Feed("\x1b[42q"); // Random CSI we don't decode.
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void CsiUnrecognizedTildeParam_EmitsNothing()
    {
        Feed("\x1b[99~"); // Not a known function/special-key code.
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void CsiPrivatePrefix_IsDropped()
    {
        Feed("\x1b[?25h"); // DECSET 25 — output side, shouldn't appear in input but if it does we drop it.
        Assert.Empty(_sink.Events);
    }

    // ---- Constructor validation ----

    [Fact]
    public void Constructor_RejectsNullMode()
    {
        Assert.Throws<ArgumentNullException>(
            () => new VtInputInterpreter(mode: null!, _sink));
    }

    [Fact]
    public void Constructor_RejectsNullEventSink()
    {
        Assert.Throws<ArgumentNullException>(
            () => new VtInputInterpreter(_mode, eventSink: null!));
    }

    // ---- Timestamp injection ----

    [Fact]
    public void TimestampsComeFromInjectedTimeProvider()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        var sink = new RecordingInputEventSink();
        var interpreter = new VtInputInterpreter(new VtInputMode(), sink, new FixedTimeProvider(fixedTime));
        var classifier = new VtSequenceClassifier();

        classifier.Process("a"u8, interpreter);

        var k = Assert.IsType<KeyEvent>(Assert.Single(sink.Events));
        Assert.Equal(fixedTime, k.Timestamp);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
