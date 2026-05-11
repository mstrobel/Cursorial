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

    // ---- modifyOtherKeys level 2 (CSI 27 ; mod ; codepoint ~) ----

    [Fact]
    public void ModifyOtherKeys_CtrlSemicolon_EmitsCharacterWithCtrl()
    {
        // xterm reports Ctrl+; as CSI 27 ; 5 ; 59 ~ when modifyOtherKeys level 2 is enabled.
        Feed("\x1b[27;5;59~");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
        Assert.Equal(KeyEventKind.Down, k.Kind);
        Assert.Equal(";", TextOf(k));
        Assert.Equal((uint)';', k.RawCode);
    }

    [Fact]
    public void ModifyOtherKeys_AltShiftA_CarriesBothModifiers()
    {
        // Alt+Shift+A → CSI 27 ; 4 ; 65 ~  (mod 4 = 1 + Shift|Alt bits = 1 + 1 + 2)
        Feed("\x1b[27;4;65~");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Alt, k.Modifiers);
        Assert.Equal("A", TextOf(k));
    }

    [Fact]
    public void ModifyOtherKeys_NonAsciiCodepoint_EncodesAsUtf16()
    {
        // Ctrl+é → CSI 27 ; 5 ; 233 ~  (U+00E9)
        Feed("\x1b[27;5;233~");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
        Assert.Equal("é", TextOf(k));
    }

    [Fact]
    public void ModifyOtherKeys_InvalidCodepoint_IsDropped()
    {
        // Codepoint 0xD800 (surrogate) is not a valid scalar.
        Feed("\x1b[27;5;55296~");

        Assert.Empty(_sink.Events);
    }

    // ---- SGR mouse ----

    [Fact]
    public void SgrMouse_LeftButtonPress_EmitsButtonDownAtZeroBasedPosition()
    {
        // CSI < 0 ; 10 ; 20 M  — left button press at column 10, row 20 (1-based in SGR).
        Feed("\x1b[<0;10;20M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.ButtonDown, m.Kind);
        Assert.Equal(MouseButton.Left, m.Button);
        Assert.Equal(MouseButtons.Left, m.ButtonsHeld);
        Assert.Equal(KeyModifiers.None, m.Modifiers);
        Assert.Equal(new CellPosition(9, 19), m.Position);
    }

    [Fact]
    public void SgrMouse_LeftButtonRelease_EmitsButtonUpAndClearsHeld()
    {
        Feed("\x1b[<0;10;20M"); // press
        Feed("\x1b[<0;10;20m"); // release

        Assert.Equal(2, _sink.Events.Count);
        var release = _sink.At<MouseEvent>(1);
        Assert.Equal(MouseEventKind.ButtonUp, release.Kind);
        Assert.Equal(MouseButton.Left, release.Button);
        Assert.Equal(MouseButtons.None, release.ButtonsHeld);
    }

    [Theory]
    [InlineData(0, MouseButton.Left, MouseButtons.Left)]
    [InlineData(1, MouseButton.Middle, MouseButtons.Middle)]
    [InlineData(2, MouseButton.Right, MouseButtons.Right)]
    public void SgrMouse_BasicButtons_MapCorrectly(int cb, MouseButton expectedButton, MouseButtons expectedMask)
    {
        Feed($"\x1b[<{cb};5;5M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.ButtonDown, m.Kind);
        Assert.Equal(expectedButton, m.Button);
        Assert.Equal(expectedMask, m.ButtonsHeld);
    }

    [Theory]
    [InlineData(4, KeyModifiers.Shift)]
    [InlineData(8, KeyModifiers.Alt)]
    [InlineData(16, KeyModifiers.Control)]
    [InlineData(20, KeyModifiers.Shift | KeyModifiers.Control)]
    [InlineData(28, KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control)]
    public void SgrMouse_ModifierBits_DecodeCorrectly(int modifierBits, KeyModifiers expected)
    {
        // cb = button(0=Left) + modifier bits.
        int cb = 0 | modifierBits;
        Feed($"\x1b[<{cb};5;5M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(expected, m.Modifiers);
        Assert.Equal(MouseButton.Left, m.Button); // Modifiers don't change button identity.
    }

    [Theory]
    [InlineData(64, MouseEventKind.Wheel, 120, 0)]   // wheel up
    [InlineData(65, MouseEventKind.Wheel, -120, 0)]  // wheel down
    [InlineData(66, MouseEventKind.Wheel, 0, -120)]  // wheel left
    [InlineData(67, MouseEventKind.Wheel, 0, 120)]   // wheel right
    public void SgrMouse_Wheel_EmitsWheelEventWithCorrectDeltas(int cb, MouseEventKind kind, int deltaY, int deltaX)
    {
        Feed($"\x1b[<{cb};5;5M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(kind, m.Kind);
        Assert.Equal(MouseButton.None, m.Button);
        Assert.Equal(deltaY, m.WheelDeltaY);
        Assert.Equal(deltaX, m.WheelDeltaX);
    }

    [Fact]
    public void SgrMouse_WheelWithModifier_PreservesModifier()
    {
        // Ctrl+Wheel-up: 64 (wheel up) | 16 (Ctrl) = 80.
        Feed("\x1b[<80;5;5M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.Wheel, m.Kind);
        Assert.Equal(KeyModifiers.Control, m.Modifiers);
        Assert.Equal(120, m.WheelDeltaY);
    }

    [Fact]
    public void SgrMouse_DragWithLeftButton_EmitsDragWithButtonHeld()
    {
        Feed("\x1b[<0;5;5M");    // press left
        Feed("\x1b[<32;6;5M");   // drag (motion + left bits) at new position

        Assert.Equal(2, _sink.Events.Count);
        var drag = _sink.At<MouseEvent>(1);
        Assert.Equal(MouseEventKind.Drag, drag.Kind);
        Assert.Equal(MouseButton.Left, drag.Button);
        Assert.Equal(MouseButtons.Left, drag.ButtonsHeld);
        Assert.Equal(new CellPosition(5, 4), drag.Position);
    }

    [Fact]
    public void SgrMouse_PureMotion_EmitsMoveWithNoButton()
    {
        // cb = 32 (motion) + 3 (no-button bits) = 35.
        Feed("\x1b[<35;7;3M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.Move, m.Kind);
        Assert.Equal(MouseButton.None, m.Button);
        Assert.Equal(MouseButtons.None, m.ButtonsHeld);
        Assert.Equal(new CellPosition(6, 2), m.Position);
    }

    [Fact]
    public void SgrMouse_MultiplePressesAccumulateButtonsHeld()
    {
        Feed("\x1b[<0;5;5M");  // press left
        Feed("\x1b[<2;6;6M");  // press right (without releasing left)

        Assert.Equal(2, _sink.Events.Count);
        var second = _sink.At<MouseEvent>(1);
        Assert.Equal(MouseButton.Right, second.Button);
        Assert.Equal(MouseButtons.Left | MouseButtons.Right, second.ButtonsHeld);
    }

    [Fact]
    public void SgrMouse_ReleaseAfterMultiplePresses_RemovesOnlyReleasedButton()
    {
        Feed("\x1b[<0;5;5M");  // press left
        Feed("\x1b[<2;5;5M");  // press right
        Feed("\x1b[<0;5;5m");  // release left

        Assert.Equal(3, _sink.Events.Count);
        var release = _sink.At<MouseEvent>(2);
        Assert.Equal(MouseButton.Left, release.Button);
        Assert.Equal(MouseButtons.Right, release.ButtonsHeld);
    }

    [Fact]
    public void SgrMouse_DragRetainsHeldFromPreviousButtons()
    {
        Feed("\x1b[<0;5;5M");   // press left
        Feed("\x1b[<2;5;5M");   // press right (without releasing)
        Feed("\x1b[<32;6;5M");  // drag (left bit + motion) at new position

        var drag = _sink.At<MouseEvent>(2);
        Assert.Equal(MouseEventKind.Drag, drag.Kind);
        Assert.Equal(MouseButton.Left, drag.Button); // Per the cb byte
        Assert.Equal(MouseButtons.Left | MouseButtons.Right, drag.ButtonsHeld); // Both still held
    }

    [Fact]
    public void SgrMouse_ExtendedButtonX1_DecodesAsX1()
    {
        // cb = 128 (extended) + 0 = 128 → X1.
        Feed("\x1b[<128;5;5M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseButton.X1, m.Button);
        Assert.Equal(MouseButtons.X1, m.ButtonsHeld);
    }

    [Fact]
    public void SgrMouse_ExtendedButtonX2_DecodesAsX2()
    {
        Feed("\x1b[<129;5;5M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseButton.X2, m.Button);
    }

    [Fact]
    public void SgrMouse_NegativeViewportPosition_PassesThrough()
    {
        // Coordinate 0 in SGR (not strictly legal, but some terminals send it on
        // out-of-viewport drag) maps to -1 zero-based.
        Feed("\x1b[<0;0;0M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(new CellPosition(-1, -1), m.Position);
    }

    [Fact]
    public void SgrMouse_MalformedTwoParams_IsDropped()
    {
        Feed("\x1b[<0;5M"); // Missing the row parameter.

        Assert.Empty(_sink.Events);
    }

    // ---- SGR-Pixels mouse (DECSET 1016) ----

    [Fact]
    public void SgrPixelsMouse_RoutesCoordinatesIntoPixelFields()
    {
        _mode.MouseEncoding = MouseEncoding.SgrPixels;
        // CSI < 0 ; 240 ; 80 M — left press at pixel (239, 79) zero-based.
        Feed("\x1b[<0;240;80M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.ButtonDown, m.Kind);
        Assert.Equal(0, m.Position.Column);
        Assert.Equal(0, m.Position.Row);
        Assert.Equal(239, m.Position.PixelX);
        Assert.Equal(79, m.Position.PixelY);
    }

    [Fact]
    public void SgrPixelsMouse_WheelCarriesPixelCoordinates()
    {
        _mode.MouseEncoding = MouseEncoding.SgrPixels;
        Feed("\x1b[<64;500;300M"); // wheel up at pixel (499, 299)

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.Wheel, m.Kind);
        Assert.Equal(120, m.WheelDeltaY);
        Assert.Equal(499, m.Position.PixelX);
        Assert.Equal(299, m.Position.PixelY);
    }

    [Fact]
    public void SgrMouse_DefaultEncoding_LeavesPixelFieldsNull()
    {
        // Mode defaults to MouseEncoding.None — cell coords still decode, pixel fields stay null.
        Feed("\x1b[<0;5;10M");

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(4, m.Position.Column);
        Assert.Equal(9, m.Position.Row);
        Assert.Null(m.Position.PixelX);
        Assert.Null(m.Position.PixelY);
    }

    // ---- X10 mouse (CSI M cb cx cy) ----

    [Fact]
    public void X10Mouse_LeftPress_EmitsButtonDownAtZeroBasedPosition()
    {
        _classifier.X10MouseFramingEnabled = true;
        // cb=0 (left press). cx byte = 0x20 + 1-based-col, so byte 0x26 = col 6 (1-based) → 5 (0-based).
        // cy byte 0x2B = row 11 (1-based) → 10 (0-based).
        Feed(0x1B, (byte)'[', (byte)'M', 0x20, 0x26, 0x2B);

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.ButtonDown, m.Kind);
        Assert.Equal(MouseButton.Left, m.Button);
        Assert.Equal(MouseButtons.Left, m.ButtonsHeld);
        Assert.Equal(new CellPosition(5, 10), m.Position);
    }

    [Fact]
    public void X10Mouse_ReleaseSentinel_EmitsButtonUpAndClearsHeldMask()
    {
        _classifier.X10MouseFramingEnabled = true;
        // Press left button first.
        Feed(0x1B, (byte)'[', (byte)'M', 0x20, 0x21, 0x21);
        // Then release: cb bits == 3 (NoButton sentinel), no motion, no extended.
        Feed(0x1B, (byte)'[', (byte)'M', 0x23, 0x21, 0x21);

        Assert.Equal(2, _sink.Events.Count);
        var release = _sink.At<MouseEvent>(1);
        Assert.Equal(MouseEventKind.ButtonUp, release.Kind);
        Assert.Equal(MouseButton.None, release.Button);
        Assert.Equal(MouseButtons.None, release.ButtonsHeld);
    }

    [Fact]
    public void X10Mouse_DragWithLeftHeld_EmitsDragWithHeldMask()
    {
        _classifier.X10MouseFramingEnabled = true;
        // Press left button.
        Feed(0x1B, (byte)'[', (byte)'M', 0x20, 0x21, 0x21);
        // Drag (motion bit + left button bits): cb = 0x20 (motion) | 0 (left) = 0x20 → +0x20 = 0x40.
        Feed(0x1B, (byte)'[', (byte)'M', 0x40, 0x22, 0x23);

        var drag = _sink.At<MouseEvent>(1);
        Assert.Equal(MouseEventKind.Drag, drag.Kind);
        Assert.Equal(MouseButton.Left, drag.Button);
        Assert.Equal(MouseButtons.Left, drag.ButtonsHeld);
    }

    [Fact]
    public void X10Mouse_WheelUp_EmitsWheelEventWithPositiveDeltaY()
    {
        _classifier.X10MouseFramingEnabled = true;
        // cb = WheelBit (0x40) | WheelUp (0) = 0x40 → +0x20 = 0x60.
        Feed(0x1B, (byte)'[', (byte)'M', 0x60, 0x21, 0x21);

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseEventKind.Wheel, m.Kind);
        Assert.Equal(120, m.WheelDeltaY);
    }

    [Fact]
    public void X10Mouse_ModifierBits_AreDecoded()
    {
        _classifier.X10MouseFramingEnabled = true;
        // Ctrl+left press: cb = LeftButton (0) | ControlBit (0x10) = 0x10 → +0x20 = 0x30.
        Feed(0x1B, (byte)'[', (byte)'M', 0x30, 0x21, 0x21);

        var m = _sink.Single<MouseEvent>();
        Assert.Equal(MouseButton.Left, m.Button);
        Assert.Equal(KeyModifiers.Control, m.Modifiers);
    }

    // ---- Device responses: DA1 / DA2 / DSR-CPR ----

    [Fact]
    public void Da1Response_EmitsPrimaryDeviceAttributesEvent()
    {
        // Typical xterm DA1 response: VT500 with extensions.
        Feed("\x1b[?65;1;9c");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.PrimaryDeviceAttributes, r.Kind);
        Assert.Equal("65;1;9", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void Da2Response_EmitsSecondaryDeviceAttributesEvent()
    {
        // CSI > 1 ; 95 ; 0 c — VT100 with version 95.
        Feed("\x1b[>1;95;0c");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.SecondaryDeviceAttributes, r.Kind);
        Assert.Equal("1;95;0", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void DsrCursorPositionReport_EmitsCursorPositionEvent()
    {
        // CSI 12 ; 34 R — cursor at row 12, col 34.
        Feed("\x1b[12;34R");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.CursorPositionReport, r.Kind);
        Assert.Equal("12;34", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void Da1ResponseOwnsPayloadAcrossLifetime()
    {
        Feed("\x1b[?65;1;9c");
        var r = _sink.Single<DeviceResponseEvent>();

        // Subsequent input must not corrupt the payload of the prior event.
        Feed("\x1b[?64c");

        Assert.Equal("65;1;9", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    // ---- Device responses: OSC color queries ----

    [Theory]
    [InlineData("\x1b]10;rgb:0000/0000/0000\x07", DeviceResponseKind.ForegroundColor, "rgb:0000/0000/0000")]
    [InlineData("\x1b]11;rgb:ffff/ffff/ffff\x07", DeviceResponseKind.BackgroundColor, "rgb:ffff/ffff/ffff")]
    [InlineData("\x1b]12;rgb:8080/8080/8080\x07", DeviceResponseKind.CursorColor, "rgb:8080/8080/8080")]
    public void OscColorResponse_EmitsExpectedDeviceResponse(string sequence, DeviceResponseKind kind, string expectedPayload)
    {
        Feed(sequence);

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(kind, r.Kind);
        Assert.Equal(expectedPayload, System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void OscPaletteResponse_PreservesIndexAndValueInPayload()
    {
        // OSC 4 ; 12 ; rgb:1234/5678/9abc — palette index 12 query response.
        Feed("\x1b]4;12;rgb:1234/5678/9abc\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.PaletteColor, r.Kind);
        Assert.Equal("12;rgb:1234/5678/9abc", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void OscWithEscBackslashTermination_DispatchesSameAsBel()
    {
        Feed("\x1b]10;rgb:0000/0000/0000\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.ForegroundColor, r.Kind);
    }

    [Fact]
    public void OscUnknownCode_EmitsNothing()
    {
        Feed("\x1b]7;file://localhost/tmp\x07"); // OSC 7 (working directory) — not yet recognized.
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void OscMalformedNoSeparator_EmitsNothing()
    {
        Feed("\x1b]nonsense\x07");
        Assert.Empty(_sink.Events);
    }

    // ---- Device responses: DCS XTVERSION ----

    [Fact]
    public void DcsXtVersionResponse_EmitsXtVersionDeviceResponse()
    {
        // DCS > | iTerm2 3.4.5 ST.
        Feed("\x1bP>|iTerm2 3.4.5\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.XtVersion, r.Kind);
        Assert.Equal("iTerm2 3.4.5", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void DcsXtVersionAcrossMultipleFeeds_AccumulatesBody()
    {
        Feed("\x1bP>|");
        Feed("WezTerm ");
        Feed("20240127");
        Assert.Empty(_sink.Events);

        Feed("\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.XtVersion, r.Kind);
        Assert.Equal("WezTerm 20240127", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void DcsDa3Response_EmitsTertiaryDeviceAttributes()
    {
        // DA3 response: DCS ! | <hex-id> ST. The payload is the unit-id hex string.
        Feed("\x1bP!|7E565430\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.TertiaryDeviceAttributes, r.Kind);
        Assert.Equal("7E565430", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void DcsDecRqssValidResponse_EmitsDecRqssResponse()
    {
        // DECRQSS valid response to "SGR": DCS 1 $ r 0;1;31 m ST.
        Feed("\x1bP1$r0;1;31m\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.DecRqss, r.Kind);
        Assert.Equal("0;1;31m", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void DcsDecRqssInvalidResponse_EmitsDecRqssResponseWithEmptyPayload()
    {
        // DECRQSS invalid response: DCS 0 $ r ST.
        Feed("\x1bP0$r\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.DecRqss, r.Kind);
        Assert.Equal(0, r.Payload.Length);
    }

    [Fact]
    public void DcsXtGetTcapResponse_EmitsXtGetTcapResponse()
    {
        // XTGETTCAP valid response for "TN" → "xterm": DCS 1 + r 544E=787465726D ST.
        Feed("\x1bP1+r544E=787465726D\x1b\\");

        var r = _sink.Single<DeviceResponseEvent>();
        Assert.Equal(DeviceResponseKind.XtGetTcap, r.Kind);
        Assert.Equal("544E=787465726D", System.Text.Encoding.ASCII.GetString(r.Payload.Span));
    }

    [Fact]
    public void DcsUnrecognizedHook_DiscardsBodyAndEmitsNothing()
    {
        // DCS with an unrecognized shape (private prefix '?' with no intermediates) — the
        // classifier still frames it but the interpreter has no DeviceResponseKind for it.
        Feed("\x1bP?|payload\x1b\\");
        Assert.Empty(_sink.Events);
    }

    // ---- Kitty keyboard protocol ----

    [Fact]
    public void KittyBareKey_LowercaseLetterEmitsCharacterDownWithText()
    {
        Feed("\x1b[97u"); // 'a'

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.None, k.Modifiers);
        Assert.Equal(KeyEventKind.Down, k.Kind);
        Assert.False(k.IsRepeat);
        Assert.Equal("a", TextOf(k));
        Assert.Equal((uint)97, k.RawCode);
    }

    [Fact]
    public void KittyKeyWithModifier_DecodesXtermModifierBits()
    {
        Feed("\x1b[97;5u"); // Ctrl+a (5 = 1 + Ctrl-bit-4)

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
        Assert.Equal("a", TextOf(k));
    }

    [Theory]
    [InlineData(2, KeyModifiers.Shift)]
    [InlineData(9, KeyModifiers.Super)]
    [InlineData(17, KeyModifiers.Hyper)]
    [InlineData(33, KeyModifiers.Meta)]
    [InlineData(65, KeyModifiers.CapsLock)]
    [InlineData(129, KeyModifiers.NumLock)]
    public void KittyExtendedModifiers_DecodeCorrectly(int modifierParam, KeyModifiers expected)
    {
        Feed($"\x1b[97;{modifierParam}u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Modifiers);
    }

    [Fact]
    public void KittyEventTypePress_ProducesDownNoRepeat()
    {
        Feed("\x1b[97;1:1u"); // explicit press

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(KeyEventKind.Down, k.Kind);
        Assert.False(k.IsRepeat);
    }

    [Fact]
    public void KittyEventTypeRepeat_ProducesDownWithIsRepeatTrue()
    {
        Feed("\x1b[97;1:2u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(KeyEventKind.Down, k.Kind);
        Assert.True(k.IsRepeat);
    }

    [Fact]
    public void KittyEventTypeRelease_ProducesUpEvent()
    {
        Feed("\x1b[97;1:3u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(KeyEventKind.Up, k.Kind);
        Assert.False(k.IsRepeat);
    }

    [Fact]
    public void KittyAlternateKeysSubParameters_AreParsedAndIgnoredCleanly()
    {
        // CSI 97 : 65 : 97 ; 2 u — 'a' with shifted='A' base='a', Shift modifier.
        // We don't surface alternate keys in v1 but the parser must not reject the sequence
        // or misattribute fields.
        Feed("\x1b[97:65:97;2u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Shift, k.Modifiers);
        Assert.Equal((uint)97, k.RawCode); // primary key code preserved
    }

    [Fact]
    public void KittyTextPayload_OverridesKeyCodeForTextField()
    {
        // CSI 97 : 65 ; 2 ; 65 u — Shift+a producing text 'A'.
        Feed("\x1b[97:65;2;65u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal("A", TextOf(k));
        Assert.Equal((uint)97, k.RawCode); // RawCode still the original key
    }

    [Fact]
    public void KittyEmptyModifierSection_DefaultsToNone()
    {
        // CSI 97 ; ; 65 u — text payload provided but mods omitted (empty between ;;).
        Feed("\x1b[97;;65u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(KeyModifiers.None, k.Modifiers);
        Assert.Equal("A", TextOf(k));
    }

    [Fact]
    public void KittySupplementaryPlaneCodepoint_ProducesSurrogatePair()
    {
        Feed("\x1b[128512u"); // U+1F600 😀

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal("😀", TextOf(k));
        Assert.Equal(2, k.Text.Length); // surrogate pair
    }

    // Functional keys

    [Theory]
    [InlineData(57344, Key.Escape)]
    [InlineData(57345, Key.Enter)]
    [InlineData(57346, Key.Tab)]
    [InlineData(57347, Key.Backspace)]
    [InlineData(57350, Key.LeftArrow)]
    [InlineData(57352, Key.UpArrow)]
    [InlineData(57356, Key.Home)]
    [InlineData(57357, Key.End)]
    [InlineData(57360, Key.NumLock)]
    [InlineData(57363, Key.Menu)]
    public void KittyFunctionalKeys_NavigationAndControl_MapCorrectly(int code, Key expected)
    {
        Feed($"\x1b[{code}u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
        Assert.Equal(ReadOnlyMemory<char>.Empty, k.Text);
    }

    [Theory]
    [InlineData(57364, Key.F1)]
    [InlineData(57375, Key.F12)]
    [InlineData(57387, Key.F24)]
    public void KittyFunctionKeysF1ThroughF24_MapCorrectly(int code, Key expected)
    {
        Feed($"\x1b[{code}u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
    }

    [Theory]
    [InlineData(57399, Key.Numpad0)]
    [InlineData(57408, Key.Numpad9)]
    [InlineData(57413, Key.NumpadAdd)]
    [InlineData(57414, Key.NumpadEnter)]
    public void KittyNumpadKeys_MapCorrectly(int code, Key expected)
    {
        Feed($"\x1b[{code}u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
    }

    [Theory]
    [InlineData(57441, Key.LeftShift)]
    [InlineData(57442, Key.LeftControl)]
    [InlineData(57445, Key.LeftHyper)]
    [InlineData(57452, Key.RightMeta)]
    public void KittyStandaloneModifierKeys_MapCorrectly(int code, Key expected)
    {
        Feed($"\x1b[{code}u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
    }

    [Theory]
    [InlineData(57428, Key.MediaPlay)]
    [InlineData(57430, Key.MediaPlayPause)]
    [InlineData(57440, Key.VolumeMute)]
    public void KittyMediaKeys_MapCorrectly(int code, Key expected)
    {
        Feed($"\x1b[{code}u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(expected, k.Key);
    }

    [Fact]
    public void KittyFunctionalKeyOutsideOurEnumRange_IsTreatedAsCharacter()
    {
        // Kitty F25 (57388) — not represented in our Key enum (we stop at F24). Falls through
        // to Character treatment with the codepoint as-is. Documented limitation.
        Feed("\x1b[57388u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal((uint)57388, k.RawCode);
    }

    [Fact]
    public void KittyFunctionalKeyUpEvent_ProducesUpKindWithEmptyText()
    {
        Feed("\x1b[57352;1:3u"); // UpArrow release

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.UpArrow, k.Key);
        Assert.Equal(KeyEventKind.Up, k.Kind);
        Assert.Equal(ReadOnlyMemory<char>.Empty, k.Text);
    }

    [Fact]
    public void KittyKeyAcrossMultipleFeeds_AssemblesCorrectly()
    {
        Feed("\x1b[");
        Feed("97;");
        Assert.Empty(_sink.Events);

        Feed("5u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.Character, k.Key);
        Assert.Equal(KeyModifiers.Control, k.Modifiers);
    }

    [Fact]
    public void KittyMalformedZeroKeyCode_EmitsNothing()
    {
        Feed("\x1b[u");
        Assert.Empty(_sink.Events);

        Feed("\x1b[0u");
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void KittyMultiCharacterTextPayload_ConcatenatesIntoSingleText()
    {
        // Two text codepoints separated by colon in the text section.
        // Hypothetical: a key that produces "ab" as combined text.
        Feed("\x1b[97;1;97:98u");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal("ab", TextOf(k));
    }

    // ---- Extended modifiers on legacy CSI sequences ----

    [Fact]
    public void XtermLegacyArrowWithHyperModifier_DecodesViaExtendedBits()
    {
        // Modifier param 17 = bits 16 = Hyper.
        Feed("\x1b[1;17A");

        var k = _sink.Single<KeyEvent>();
        Assert.Equal(Key.UpArrow, k.Key);
        Assert.Equal(KeyModifiers.Hyper, k.Modifiers);
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
