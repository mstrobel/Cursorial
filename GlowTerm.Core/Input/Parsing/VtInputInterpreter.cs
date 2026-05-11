using System.Buffers;
using System.Text;

namespace GlowTerm.Core.Input.Parsing;

/// <summary>
/// Consumes <see cref="IVtSequenceTokenSink"/> callbacks from <see cref="VtSequenceClassifier"/>
/// and emits <see cref="InputEvent"/>s to an <see cref="IInputEventSink"/>. Holds the mutable
/// session state — paste accumulator, UTF-8 continuation buffer, and a reference to the
/// <see cref="VtInputMode"/> the negotiator updates as opt-ins are pushed and popped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoder coverage:</b> printable UTF-8 runs (one <see cref="KeyEvent"/> per
/// <see cref="System.Text.Rune"/>), C0 control characters (Tab, Enter, Backspace, NUL→Ctrl+Space,
/// Ctrl+letter for 0x01–0x1A), DEL→Backspace, bare-ESC committed by classifier flush,
/// focus events (<c>CSI I</c> / <c>CSI O</c>), bracketed-paste accumulation
/// (<c>CSI 200~</c> … <c>CSI 201~</c>), CSI cursor keys (<c>A B C D H F</c>) and special
/// keys (Insert, Delete, Page Up/Down, Home, End), function keys F1–F20 via the
/// <c>CSI n ~</c> form, F1–F4 + cursor + Home / End via SS3 (<c>ESC O …</c>), BackTab
/// (<c>CSI Z</c> → Shift+Tab), xterm modifier-bearing variants (<c>CSI 1 ; mod letter</c>
/// and <c>CSI n ; mod ~</c>) decoding Shift / Alt / Ctrl / Super, modifyOtherKeys level 2
/// (<c>CSI 27 ; mod ; codepoint ~</c>) for modifier-bearing character keys, SGR mouse
/// (<c>CSI &lt; cb ; cx ; cy M/m</c>, DECSET 1006) including press / release / drag / motion /
/// wheel and X1–X4 extended buttons, and SGR-Pixels mouse (DECSET 1016, identical wire shape)
/// whose coordinates the interpreter routes into <see cref="CellPosition.PixelX"/> /
/// <see cref="CellPosition.PixelY"/> when <see cref="VtInputMode.MouseEncoding"/> is
/// <see cref="MouseEncoding.SgrPixels"/>. The interpreter accumulates <see cref="MouseButtons"/>
/// state across press / release events so drag and motion events carry an accurate held-button
/// mask. Device-response decoding: DA1 (<c>CSI ? … c</c>), DA2 (<c>CSI &gt; … c</c>), DSR-CPR
/// (<c>CSI row ; col R</c>), OSC 4 / 10 / 11 / 12 color responses, DCS XTVERSION
/// (<c>DCS &gt; | name ST</c>), DA3 (<c>DCS ! | hex-id ST</c>), DECRQSS
/// (<c>DCS valid $ r data ST</c>), and XTGETTCAP (<c>DCS valid + r hex-name=hex-value ST</c>)
/// — each emitted as a <see cref="DeviceResponseEvent"/> with the appropriate
/// <see cref="DeviceResponseKind"/> and a copied payload. Kitty keyboard protocol
/// (<c>CSI key[:shifted:base][;mods[:event]][;text] u</c>) — full functional key code mapping
/// (Esc / Enter / Tab / arrows / Home / End / F1–F24 / numpad / media / per-side modifiers),
/// up / down / repeat distinction via the event-type sub-parameter, modifier handling for the
/// xterm baseline plus Hyper / Meta / CapsLock / NumLock toggles, and text payloads for
/// IME-composed and Shift-modified output. Alternate-key sub-parameters are parsed but not
/// surfaced in v1 (<see cref="KeyEvent"/> doesn't yet carry shifted / base-layout keys).
/// </para>
/// <para>
/// X10 mouse (<c>CSI M cb cx cy</c>): when the host classifier has
/// <see cref="VtSequenceClassifier.X10MouseFramingEnabled"/> set, the three follow bytes are
/// dispatched here as a <see cref="MouseEvent"/>. X10 doesn't distinguish which button was
/// released, so per-button release fidelity requires SGR mouse instead.
/// </para>
/// <para>
/// <b>Not yet decoded</b> (silently dropped, will be added in subsequent passes): the
/// <c>CSI codepoint ; mod u</c> modifyOtherKeys variant (overlaps with the Kitty keyboard
/// <c>u</c> final), ESC charset designators, and Win32 input mode.
/// </para>
/// <para>
/// <b>Threading.</b> The interpreter is single-threaded with respect to its sink and mode —
/// the same thread that drives <see cref="VtSequenceClassifier.Process"/> calls the sink
/// callbacks here, and the consumer is expected to drive both from a single byte-pump.
/// </para>
/// </remarks>
public sealed class VtInputInterpreter : IVtSequenceTokenSink
{
    private readonly IInputEventSink _eventSink;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;

    // Paste accumulator. Empty when not in paste mode.
    private readonly StringBuilder _pasteBuffer = new();
    private bool _inPaste;

    // UTF-8 continuation buffer for printable runs split across Process boundaries.
    // Sized to the maximum UTF-8 sequence length (4 bytes); never holds a complete rune.
    private readonly byte[] _utf8Continuation = new byte[4];
    private int _utf8ContinuationLength;

    // Currently-held mouse buttons. Updated on every press / release so drag and motion
    // events can carry an accurate ButtonsHeld mask. SGR's per-event encoding doesn't tell
    // us this directly — we accumulate it ourselves.
    private MouseButtons _heldButtons;

    // DCS body accumulator. Active between OnDcsHook and OnDcsUnhook; classified at hook,
    // delivered as a DeviceResponseEvent at unhook. Default capacity sized for typical
    // XTVERSION responses (terminal name + version string).
    private readonly ArrayBufferWriter<byte> _dcsBody = new(initialCapacity: 128);
    private DeviceResponseKind _dcsKind = DeviceResponseKind.Unknown;
    private bool _dcsActive;

    public VtInputInterpreter(VtInputMode mode, IInputEventSink eventSink, TimeProvider? timeProvider = null)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The mode bag the interpreter consults; shared with the negotiator.</summary>
    public VtInputMode Mode => _mode;

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---- IVtSequenceTokenSink ----

    public void OnPrint(ReadOnlySpan<byte> bytes)
    {
        if (_inPaste)
        {
            AppendPasteText(bytes);
            return;
        }

        DecodePrintable(bytes);
    }

    public void OnExecute(byte controlChar)
    {
        if (_inPaste)
        {
            // Whitespace controls are preserved verbatim in the paste payload; everything
            // else is dropped. Keeping CR + LF distinct lets consumers detect line endings.
            switch (controlChar)
            {
                case VtInputSequences.Tab: _pasteBuffer.Append('\t'); break;
                case VtInputSequences.LineFeed: _pasteBuffer.Append('\n'); break;
                case VtInputSequences.CarriageReturn: _pasteBuffer.Append('\r'); break;
            }
            return;
        }

        EmitControlEvent(controlChar);
    }

    public void OnEscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        // Bare ESC committed by classifier flush.
        if (intermediates.IsEmpty && final == 0)
        {
            EmitNamedKey(Key.Escape);
            return;
        }

        // SS3 — application-mode arrow / F1-F4 / Home / End: ESC O <final>.
        if (intermediates.Length == 1 && intermediates[0] == (byte)'O')
        {
            DecodeSs3(final);
            return;
        }

        // Other ESC sequences (charset designators, etc.) — not yet decoded.
    }

    public void OnCsiDispatch(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final)
    {
        if (!intermediates.IsEmpty) return;

        // SGR mouse: CSI < cb ; cx ; cy M/m  (DECSET 1006).
        if (privatePrefix == VtInputSequences.SgrMousePrefix1006
            && (final == VtInputSequences.SgrMouse.PressFinal
                || final == VtInputSequences.SgrMouse.ReleaseFinal))
        {
            DecodeSgrMouse(parameters, isPress: final == VtInputSequences.SgrMouse.PressFinal);
            return;
        }

        // Device Attributes responses — DA1 (CSI ? … c) and DA2 (CSI > … c).
        if (final == (byte)'c')
        {
            switch (privatePrefix)
            {
                case VtInputSequences.DecPrivatePrefix:
                    EmitDeviceResponse(DeviceResponseKind.PrimaryDeviceAttributes, parameters);
                    return;
                case VtInputSequences.SecondaryPrefix:
                    EmitDeviceResponse(DeviceResponseKind.SecondaryDeviceAttributes, parameters);
                    return;
            }
        }

        // No other private-prefix CSI sequences are decoded yet.
        if (privatePrefix != 0) return;

        // Kitty keyboard protocol — CSI <key>[:<alt>:<base>][;<mods>[:<event>]][;<text>] u.
        // Distinguished by the 'u' final byte. Use a sub-parameter-aware parser since the
        // encoding makes structural use of the colon separator the simpler ParseParameters
        // collapses into a primary separator.
        if (final == (byte)'u')
        {
            DecodeKittyKey(parameters);
            return;
        }

        Span<int> parameterBuffer = stackalloc int[8];
        int parameterCount = ParseParameters(parameters, parameterBuffer);
        ReadOnlySpan<int> p = parameterBuffer[..parameterCount];

        // Cursor Position Report — CSI <row> ; <col> R, the response to DSR-CPR (CSI 6 n).
        if (p.Length == 2 && final == (byte)'R')
        {
            EmitDeviceResponse(DeviceResponseKind.CursorPositionReport, parameters);
            return;
        }

        if (p.IsEmpty)
        {
            DecodeCsiNoParams(final);
            return;
        }

        if (p.Length == 1 && final == (byte)'~')
        {
            DecodeCsiTildeOneParam(p[0]);
            return;
        }

        // Modifier-bearing arrows / Home / End: CSI 1 ; <mod> <A|B|C|D|H|F>.
        if (p.Length == 2 && p[0] == 1)
        {
            Key arrowKey = ArrowOrHomeEndKey(final);
            if (arrowKey != Key.None)
            {
                EmitNamedKey(arrowKey, ParseModifiersParam(p[1]));
                return;
            }
        }

        // Modifier-bearing function / special keys: CSI <n> ; <mod> ~.
        if (p.Length == 2 && final == (byte)'~')
        {
            if (TryFunctionOrSpecialKey(p[0], out Key funcKey))
            {
                EmitNamedKey(funcKey, ParseModifiersParam(p[1]));
            }
            return;
        }

        // modifyOtherKeys level 2: CSI 27 ; <mod> ; <codepoint> ~.
        if (p.Length == 3 && final == (byte)'~' && p[0] == 27)
        {
            EmitModifyOtherKeysCharacter(p[2], ParseModifiersParam(p[1]));
            return;
        }

        // Other CSI sequences — not yet decoded.
    }

    private void EmitModifyOtherKeysCharacter(int codepoint, KeyModifiers modifiers)
    {
        if (codepoint <= 0 || !Rune.IsValid(codepoint)) return;

        var rune = new Rune(codepoint);
        Span<char> chars = stackalloc char[2];
        int written = rune.EncodeToUtf16(chars);
        var text = new char[written];
        chars[..written].CopyTo(text);

        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = Key.Character,
            Modifiers = modifiers,
            Kind = KeyEventKind.Down,
            Text = text,
            RawCode = (uint)codepoint,
        });
    }

    private void DecodeCsiNoParams(byte final)
    {
        switch (final)
        {
            case VtInputSequences.FocusInFinal:
                _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = true });
                return;
            case VtInputSequences.FocusOutFinal:
                _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = false });
                return;
            case (byte)'Z':
                // BackTab — Shift+Tab.
                EmitNamedKey(Key.Tab, KeyModifiers.Shift);
                return;
        }

        Key key = ArrowOrHomeEndKey(final);
        if (key != Key.None) EmitNamedKey(key);
    }

    private void DecodeCsiTildeOneParam(int parameter)
    {
        switch (parameter)
        {
            case VtInputSequences.BracketedPasteStartParam:
                EnterPaste();
                return;
            case VtInputSequences.BracketedPasteEndParam:
                ExitPaste();
                return;
        }

        if (TryFunctionOrSpecialKey(parameter, out Key key))
        {
            EmitNamedKey(key);
        }
    }

    // ---- SGR mouse ----

    private void DecodeSgrMouse(ReadOnlySpan<byte> parameters, bool isPress)
    {
        Span<int> p = stackalloc int[3];
        int n = ParseParameters(parameters, p);
        if (n < 3) return;

        int cb = p[0];
        // SGR coordinates are 1-based on the wire; we expose 0-based values. Don't clamp to 0 —
        // terminals may report negative values when the pointer leaves the viewport, and the
        // contract permits that. In SGR-Pixels mode (DECSET 1016) the two coordinates are pixel
        // offsets rather than cell offsets; route them into CellPosition.PixelX/Y and leave the
        // cell fields at 0 (consumers know the active encoding from the negotiator capabilities).
        int x = p[1] - 1;
        int y = p[2] - 1;

        KeyModifiers modifiers = KeyModifiers.None;
        if ((cb & VtInputSequences.SgrMouse.ShiftBit) != 0) modifiers |= KeyModifiers.Shift;
        if ((cb & VtInputSequences.SgrMouse.AltBit) != 0) modifiers |= KeyModifiers.Alt;
        if ((cb & VtInputSequences.SgrMouse.ControlBit) != 0) modifiers |= KeyModifiers.Control;

        bool isMotion = (cb & VtInputSequences.SgrMouse.MotionBit) != 0;
        bool isWheel = (cb & VtInputSequences.SgrMouse.WheelBit) != 0;
        bool isExtended = (cb & VtInputSequences.SgrMouse.ExtendedBit) != 0;

        var position = _mode.MouseEncoding == MouseEncoding.SgrPixels
            ? new CellPosition(Column: 0, Row: 0, PixelX: x, PixelY: y)
            : new CellPosition(x, y);
        var ts = Now;

        if (isWheel)
        {
            // Direction bits select wheel axis / sign; deltas reported in 1/120-notch units.
            int direction = cb & VtInputSequences.SgrMouse.ButtonBitsMask;
            const int notch = VtInputSequences.SgrMouse.WheelDeltaPerNotch;
            int wheelDeltaY = direction switch
            {
                VtInputSequences.SgrMouse.WheelUp => notch,
                VtInputSequences.SgrMouse.WheelDown => -notch,
                _ => 0,
            };
            int wheelDeltaX = direction switch
            {
                VtInputSequences.SgrMouse.WheelLeft => -notch,
                VtInputSequences.SgrMouse.WheelRight => notch,
                _ => 0,
            };

            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = MouseEventKind.Wheel,
                Position = position,
                Button = MouseButton.None,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
                WheelDeltaY = wheelDeltaY,
                WheelDeltaX = wheelDeltaX,
            });
            return;
        }

        int buttonBits = cb & VtInputSequences.SgrMouse.ButtonBitsMask;
        MouseButton button = isExtended
            ? buttonBits switch
            {
                0 => MouseButton.X1,
                1 => MouseButton.X2,
                2 => MouseButton.X3,
                3 => MouseButton.X4,
                _ => MouseButton.None,
            }
            : buttonBits switch
            {
                VtInputSequences.SgrMouse.LeftButton => MouseButton.Left,
                VtInputSequences.SgrMouse.MiddleButton => MouseButton.Middle,
                VtInputSequences.SgrMouse.RightButton => MouseButton.Right,
                _ => MouseButton.None,
            };

        if (isMotion)
        {
            // SGR convention: button bits == NoButton in the non-extended encoding means pure
            // motion (any-event tracking with no button held).
            bool noButton = !isExtended && buttonBits == VtInputSequences.SgrMouse.NoButton;

            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = noButton ? MouseEventKind.Move : MouseEventKind.Drag,
                Position = position,
                Button = noButton ? MouseButton.None : button,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
            });
            return;
        }

        // Press / release.
        MouseButtons mask = ButtonToMask(button);
        if (isPress) _heldButtons |= mask;
        else _heldButtons &= ~mask;

        _eventSink.OnInputEvent(new MouseEvent
        {
            Timestamp = ts,
            Kind = isPress ? MouseEventKind.ButtonDown : MouseEventKind.ButtonUp,
            Position = position,
            Button = button,
            ButtonsHeld = _heldButtons,
            Modifiers = modifiers,
        });
    }

    // ---- X10 mouse ----

    public void OnX10MouseDispatch(byte cbByte, byte cxByte, byte cyByte)
    {
        // X10 encoding: each byte carries `value + 0x20`. For coordinates the value is 1-based,
        // so the zero-based column/row is byte - 0x21. Coords are clamped via wrap-around in
        // legacy clients (high-bit set for values > 95); we just unmask and let the consumer
        // see whatever the terminal sent.
        const int Bias = 0x20;
        int cb = cbByte - Bias;
        int column = cxByte - Bias - 1;
        int row = cyByte - Bias - 1;

        KeyModifiers modifiers = KeyModifiers.None;
        if ((cb & VtInputSequences.SgrMouse.ShiftBit) != 0) modifiers |= KeyModifiers.Shift;
        if ((cb & VtInputSequences.SgrMouse.AltBit) != 0) modifiers |= KeyModifiers.Alt;
        if ((cb & VtInputSequences.SgrMouse.ControlBit) != 0) modifiers |= KeyModifiers.Control;

        bool isMotion = (cb & VtInputSequences.SgrMouse.MotionBit) != 0;
        bool isWheel = (cb & VtInputSequences.SgrMouse.WheelBit) != 0;
        bool isExtended = (cb & VtInputSequences.SgrMouse.ExtendedBit) != 0;
        int buttonBits = cb & VtInputSequences.SgrMouse.ButtonBitsMask;

        var position = new CellPosition(column, row);
        var ts = Now;

        if (isWheel)
        {
            const int notch = VtInputSequences.SgrMouse.WheelDeltaPerNotch;
            int wheelDeltaY = buttonBits switch
            {
                VtInputSequences.SgrMouse.WheelUp => notch,
                VtInputSequences.SgrMouse.WheelDown => -notch,
                _ => 0,
            };
            int wheelDeltaX = buttonBits switch
            {
                VtInputSequences.SgrMouse.WheelLeft => -notch,
                VtInputSequences.SgrMouse.WheelRight => notch,
                _ => 0,
            };

            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = MouseEventKind.Wheel,
                Position = position,
                Button = MouseButton.None,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
                WheelDeltaY = wheelDeltaY,
                WheelDeltaX = wheelDeltaX,
            });
            return;
        }

        // X10 release sentinel: button bits == 3 with no motion and no extended bit means a
        // button was released, but the protocol doesn't tell us which one. Clear the held mask
        // and emit a ButtonUp with Button=None — consumers that need per-button release fidelity
        // must use SGR mouse (DECSET 1006).
        if (!isMotion && !isExtended && buttonBits == VtInputSequences.SgrMouse.NoButton)
        {
            _heldButtons = MouseButtons.None;
            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = MouseEventKind.ButtonUp,
                Position = position,
                Button = MouseButton.None,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
            });
            return;
        }

        MouseButton button = isExtended
            ? buttonBits switch
            {
                0 => MouseButton.X1,
                1 => MouseButton.X2,
                2 => MouseButton.X3,
                3 => MouseButton.X4,
                _ => MouseButton.None,
            }
            : buttonBits switch
            {
                VtInputSequences.SgrMouse.LeftButton => MouseButton.Left,
                VtInputSequences.SgrMouse.MiddleButton => MouseButton.Middle,
                VtInputSequences.SgrMouse.RightButton => MouseButton.Right,
                _ => MouseButton.None,
            };

        if (isMotion)
        {
            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = button == MouseButton.None ? MouseEventKind.Move : MouseEventKind.Drag,
                Position = position,
                Button = button,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
            });
            return;
        }

        // Press. X10 does not distinguish per-button release in its cb byte, so we treat every
        // non-release event with an identifiable button as a press.
        MouseButtons mask = ButtonToMask(button);
        _heldButtons |= mask;

        _eventSink.OnInputEvent(new MouseEvent
        {
            Timestamp = ts,
            Kind = MouseEventKind.ButtonDown,
            Position = position,
            Button = button,
            ButtonsHeld = _heldButtons,
            Modifiers = modifiers,
        });
    }

    // ---- Kitty keyboard protocol ----

    private void DecodeKittyKey(ReadOnlySpan<byte> rawParameters)
    {
        // Section / sub-section roles in the Kitty encoding:
        //   primary 0:  key-info       sub 0=key  sub 1=shifted-key  sub 2=base-layout-key
        //   primary 1:  modifier-info  sub 0=mods (1+bitfield)  sub 1=event-type (1=press,2=repeat,3=release)
        //   primary 2:  text           sub i=codepoint of the i-th text char
        var data = new KittyKeyData
        {
            Modifiers = 1, // 1 = no modifiers (1 + bitfield)
            EventType = VtInputSequences.Kitty.PressEvent,
        };
        Span<int> textCodepoints = stackalloc int[16];

        ParseKittyParameters(rawParameters, ref data, textCodepoints);

        if (data.KeyCode <= 0) return; // Malformed — no key code.

        KeyModifiers modifiers = ParseModifiersParam(data.Modifiers);

        KeyEventKind kind = data.EventType == VtInputSequences.Kitty.ReleaseEvent
            ? KeyEventKind.Up
            : KeyEventKind.Down;
        bool isRepeat = data.EventType == VtInputSequences.Kitty.RepeatEvent;

        Key key = TryMapKittyFunctionalKey(data.KeyCode, out Key mapped)
            ? mapped
            : Key.Character;

        ReadOnlyMemory<char> text;
        if (key == Key.Character)
        {
            text = data.TextCount > 0
                ? CodepointsToUtf16(textCodepoints[..data.TextCount])
                : CodepointToUtf16(data.KeyCode);
        }
        else
        {
            // Functional key — the text payload, when present, is what the key would have
            // produced as a character (rare: e.g. Kitty Enter with shifted form). Carry it
            // through if we got one; otherwise leave Text empty.
            text = data.TextCount > 0
                ? CodepointsToUtf16(textCodepoints[..data.TextCount])
                : ReadOnlyMemory<char>.Empty;
        }

        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = key,
            Modifiers = modifiers,
            Kind = kind,
            IsRepeat = isRepeat,
            Text = text,
            RawCode = (uint)data.KeyCode,
        });
    }

    private struct KittyKeyData
    {
        public int KeyCode;
        public int ShiftedKey;
        public int BaseKey;
        public int Modifiers;
        public int EventType;
        public int TextCount;
    }

    private static void ParseKittyParameters(
        ReadOnlySpan<byte> raw,
        ref KittyKeyData data,
        Span<int> textCodepoints)
    {
        int primaryIndex = 0;
        int subIndex = 0;
        int currentValue = 0;
        bool started = false;

        for (int i = 0; i <= raw.Length; i++)
        {
            bool atEnd = i == raw.Length;
            byte b = atEnd ? (byte)0 : raw[i];

            if (!atEnd && b is >= (byte)'0' and <= (byte)'9')
            {
                currentValue = AccumulateDecimalSaturating(currentValue, b - (byte)'0');
                started = true;
                continue;
            }

            if (atEnd || b == (byte)':' || b == (byte)';')
            {
                int value = started ? currentValue : 0;

                switch (primaryIndex)
                {
                    case 0:
                        switch (subIndex)
                        {
                            case 0: data.KeyCode = value; break;
                            case 1: data.ShiftedKey = value; break;
                            case 2: data.BaseKey = value; break;
                        }
                        break;
                    case 1:
                        // Empty mods/event_type leave the defaults (1) in place.
                        if (started)
                        {
                            switch (subIndex)
                            {
                                case 0: data.Modifiers = value; break;
                                case 1: data.EventType = value; break;
                            }
                        }
                        break;
                    case 2:
                        if (started && data.TextCount < textCodepoints.Length)
                        {
                            textCodepoints[data.TextCount++] = value;
                        }
                        break;
                }

                currentValue = 0;
                started = false;

                if (atEnd) return;
                if (b == (byte)':')
                {
                    subIndex++;
                }
                else // ';'
                {
                    primaryIndex++;
                    subIndex = 0;
                }
                continue;
            }

            // Unexpected byte — abort parse.
            return;
        }
    }

    private static bool TryMapKittyFunctionalKey(int code, out Key key)
    {
        // Kitty assigns functional key codes in the Unicode private-use area starting at
        // 57344. Codes outside this range (and outside the small set of recognized
        // functional key codes) are treated as printable Unicode codepoints.
        key = code switch
        {
            // Navigation / control
            VtInputSequences.Kitty.EscapeKey => Key.Escape,
            VtInputSequences.Kitty.EnterKey => Key.Enter,
            VtInputSequences.Kitty.TabKey => Key.Tab,
            VtInputSequences.Kitty.BackspaceKey => Key.Backspace,
            VtInputSequences.Kitty.InsertKey => Key.Insert,
            VtInputSequences.Kitty.DeleteKey => Key.Delete,
            VtInputSequences.Kitty.LeftArrowKey => Key.LeftArrow,
            VtInputSequences.Kitty.RightArrowKey => Key.RightArrow,
            VtInputSequences.Kitty.UpArrowKey => Key.UpArrow,
            VtInputSequences.Kitty.DownArrowKey => Key.DownArrow,
            VtInputSequences.Kitty.PageUpKey => Key.PageUp,
            VtInputSequences.Kitty.PageDownKey => Key.PageDown,
            VtInputSequences.Kitty.HomeKey => Key.Home,
            VtInputSequences.Kitty.EndKey => Key.End,
            VtInputSequences.Kitty.CapsLockKey => Key.CapsLock,
            VtInputSequences.Kitty.ScrollLockKey => Key.ScrollLock,
            VtInputSequences.Kitty.NumLockKey => Key.NumLock,
            VtInputSequences.Kitty.PrintScreenKey => Key.PrintScreen,
            VtInputSequences.Kitty.PauseKey => Key.Pause,
            VtInputSequences.Kitty.MenuKey => Key.Menu,

            // Function keys F1–F24 are contiguous in Kitty's encoding; F25–F35 fall through
            // to Character treatment since our Key enum stops at F24.
            >= VtInputSequences.Kitty.F1Key and <= VtInputSequences.Kitty.F24Key
                => (Key)((int)Key.F1 + (code - VtInputSequences.Kitty.F1Key)),

            // Numpad digits / operators / Enter / equals.
            VtInputSequences.Kitty.Numpad0Key => Key.Numpad0,
            VtInputSequences.Kitty.Numpad1Key => Key.Numpad1,
            VtInputSequences.Kitty.Numpad2Key => Key.Numpad2,
            VtInputSequences.Kitty.Numpad3Key => Key.Numpad3,
            VtInputSequences.Kitty.Numpad4Key => Key.Numpad4,
            VtInputSequences.Kitty.Numpad5Key => Key.Numpad5,
            VtInputSequences.Kitty.Numpad6Key => Key.Numpad6,
            VtInputSequences.Kitty.Numpad7Key => Key.Numpad7,
            VtInputSequences.Kitty.Numpad8Key => Key.Numpad8,
            VtInputSequences.Kitty.Numpad9Key => Key.Numpad9,
            VtInputSequences.Kitty.NumpadDecimalKey => Key.NumpadDecimal,
            VtInputSequences.Kitty.NumpadDivideKey => Key.NumpadDivide,
            VtInputSequences.Kitty.NumpadMultiplyKey => Key.NumpadMultiply,
            VtInputSequences.Kitty.NumpadSubtractKey => Key.NumpadSubtract,
            VtInputSequences.Kitty.NumpadAddKey => Key.NumpadAdd,
            VtInputSequences.Kitty.NumpadEnterKey => Key.NumpadEnter,
            VtInputSequences.Kitty.NumpadEqualsKey => Key.NumpadEquals,

            // Numpad navigation keys — collapse to the main-keyboard equivalent for v1.
            // Distinguishing numpad-arrow from main-arrow would need new Key enum entries.
            VtInputSequences.Kitty.NumpadLeftArrowKey => Key.LeftArrow,
            VtInputSequences.Kitty.NumpadRightArrowKey => Key.RightArrow,
            VtInputSequences.Kitty.NumpadUpArrowKey => Key.UpArrow,
            VtInputSequences.Kitty.NumpadDownArrowKey => Key.DownArrow,
            VtInputSequences.Kitty.NumpadPageUpKey => Key.PageUp,
            VtInputSequences.Kitty.NumpadPageDownKey => Key.PageDown,
            VtInputSequences.Kitty.NumpadHomeKey => Key.Home,
            VtInputSequences.Kitty.NumpadEndKey => Key.End,
            VtInputSequences.Kitty.NumpadInsertKey => Key.Insert,
            VtInputSequences.Kitty.NumpadDeleteKey => Key.Delete,

            // Media keys.
            VtInputSequences.Kitty.MediaPlayKey => Key.MediaPlay,
            VtInputSequences.Kitty.MediaPauseKey => Key.MediaPause,
            VtInputSequences.Kitty.MediaPlayPauseKey => Key.MediaPlayPause,
            VtInputSequences.Kitty.MediaStopKey => Key.MediaStop,
            VtInputSequences.Kitty.MediaTrackNextKey => Key.MediaNext,
            VtInputSequences.Kitty.MediaTrackPreviousKey => Key.MediaPrevious,
            VtInputSequences.Kitty.VolumeDownKey => Key.VolumeDown,
            VtInputSequences.Kitty.VolumeUpKey => Key.VolumeUp,
            VtInputSequences.Kitty.VolumeMuteKey => Key.VolumeMute,

            // Per-side modifier keys reported as standalone events.
            VtInputSequences.Kitty.LeftShiftKey => Key.LeftShift,
            VtInputSequences.Kitty.LeftControlKey => Key.LeftControl,
            VtInputSequences.Kitty.LeftAltKey => Key.LeftAlt,
            VtInputSequences.Kitty.LeftSuperKey => Key.LeftSuper,
            VtInputSequences.Kitty.LeftHyperKey => Key.LeftHyper,
            VtInputSequences.Kitty.LeftMetaKey => Key.LeftMeta,
            VtInputSequences.Kitty.RightShiftKey => Key.RightShift,
            VtInputSequences.Kitty.RightControlKey => Key.RightControl,
            VtInputSequences.Kitty.RightAltKey => Key.RightAlt,
            VtInputSequences.Kitty.RightSuperKey => Key.RightSuper,
            VtInputSequences.Kitty.RightHyperKey => Key.RightHyper,
            VtInputSequences.Kitty.RightMetaKey => Key.RightMeta,

            _ => Key.None,
        };
        return key != Key.None;
    }

    private static ReadOnlyMemory<char> CodepointToUtf16(int codepoint)
    {
        if (codepoint <= 0 || !Rune.IsValid(codepoint)) return ReadOnlyMemory<char>.Empty;

        var rune = new Rune(codepoint);
        Span<char> buffer = stackalloc char[2];
        int written = rune.EncodeToUtf16(buffer);

        var heap = new char[written];
        buffer[..written].CopyTo(heap);
        return heap;
    }

    private static ReadOnlyMemory<char> CodepointsToUtf16(ReadOnlySpan<int> codepoints)
    {
        if (codepoints.IsEmpty) return ReadOnlyMemory<char>.Empty;

        // Worst case: every codepoint encodes as a UTF-16 surrogate pair.
        Span<char> buffer = stackalloc char[codepoints.Length * 2];
        int written = 0;

        foreach (int cp in codepoints)
        {
            if (cp <= 0 || !Rune.IsValid(cp)) continue;
            written += new Rune(cp).EncodeToUtf16(buffer[written..]);
        }

        if (written == 0) return ReadOnlyMemory<char>.Empty;

        var heap = new char[written];
        buffer[..written].CopyTo(heap);
        return heap;
    }

    // ---- Device-response helpers ----

    private void EmitDeviceResponse(DeviceResponseKind kind, ReadOnlySpan<byte> payload)
    {
        _eventSink.OnInputEvent(new DeviceResponseEvent
        {
            Timestamp = Now,
            Kind = kind,
            Payload = payload.ToArray(),
        });
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.IsEmpty) return false;

        foreach (byte b in bytes)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = AccumulateDecimalSaturating(value, b - (byte)'0');
        }
        return true;
    }

    /// <summary>
    /// Decimal-shift a non-negative integer by one digit, saturating at <see cref="int.MaxValue"/>
    /// instead of overflowing. All parameter parsers in this interpreter use this — pathological
    /// input (terminals or fuzz tests producing absurdly long digit runs) clamps to a sentinel
    /// rather than throwing through the byte-pump as an unhandled <see cref="OverflowException"/>.
    /// Decoders are expected to validate the parameter range before acting.
    /// </summary>
    private static int AccumulateDecimalSaturating(int current, int digit)
    {
        if (current > (int.MaxValue - digit) / 10) return int.MaxValue;
        return current * 10 + digit;
    }

    private static MouseButtons ButtonToMask(MouseButton button) => button switch
    {
        MouseButton.Left => MouseButtons.Left,
        MouseButton.Middle => MouseButtons.Middle,
        MouseButton.Right => MouseButtons.Right,
        MouseButton.X1 => MouseButtons.X1,
        MouseButton.X2 => MouseButtons.X2,
        MouseButton.X3 => MouseButtons.X3,
        MouseButton.X4 => MouseButtons.X4,
        _ => MouseButtons.None,
    };

    private void DecodeSs3(byte final)
    {
        Key key = final switch
        {
            (byte)'A' => Key.UpArrow,
            (byte)'B' => Key.DownArrow,
            (byte)'C' => Key.RightArrow,
            (byte)'D' => Key.LeftArrow,
            (byte)'H' => Key.Home,
            (byte)'F' => Key.End,
            (byte)'P' => Key.F1,
            (byte)'Q' => Key.F2,
            (byte)'R' => Key.F3,
            (byte)'S' => Key.F4,
            _ => Key.None,
        };

        if (key != Key.None) EmitNamedKey(key);
    }

    private static Key ArrowOrHomeEndKey(byte final) => final switch
    {
        (byte)'A' => Key.UpArrow,
        (byte)'B' => Key.DownArrow,
        (byte)'C' => Key.RightArrow,
        (byte)'D' => Key.LeftArrow,
        (byte)'H' => Key.Home,
        (byte)'F' => Key.End,
        _ => Key.None,
    };

    private static bool TryFunctionOrSpecialKey(int parameter, out Key key)
    {
        // CSI n ~ encoding for special and function keys (xterm + vt220 / vt320 conventions).
        key = parameter switch
        {
            // Special navigation keys (alternate codes per terminal).
            VtInputSequences.CsiTildeKey.Home or VtInputSequences.CsiTildeKey.HomeAlternate => Key.Home,
            VtInputSequences.CsiTildeKey.End or VtInputSequences.CsiTildeKey.EndAlternate => Key.End,
            VtInputSequences.CsiTildeKey.Insert => Key.Insert,
            VtInputSequences.CsiTildeKey.Delete => Key.Delete,
            VtInputSequences.CsiTildeKey.PageUp => Key.PageUp,
            VtInputSequences.CsiTildeKey.PageDown => Key.PageDown,

            // Function keys F1–F12 (xterm).
            VtInputSequences.CsiTildeKey.F1 => Key.F1,
            VtInputSequences.CsiTildeKey.F2 => Key.F2,
            VtInputSequences.CsiTildeKey.F3 => Key.F3,
            VtInputSequences.CsiTildeKey.F4 => Key.F4,
            VtInputSequences.CsiTildeKey.F5 => Key.F5,
            VtInputSequences.CsiTildeKey.F6 => Key.F6,
            VtInputSequences.CsiTildeKey.F7 => Key.F7,
            VtInputSequences.CsiTildeKey.F8 => Key.F8,
            VtInputSequences.CsiTildeKey.F9 => Key.F9,
            VtInputSequences.CsiTildeKey.F10 => Key.F10,
            VtInputSequences.CsiTildeKey.F11 => Key.F11,
            VtInputSequences.CsiTildeKey.F12 => Key.F12,

            // Extended function keys F13–F20 (vt220 / vt320).
            VtInputSequences.CsiTildeKey.F13 => Key.F13,
            VtInputSequences.CsiTildeKey.F14 => Key.F14,
            VtInputSequences.CsiTildeKey.F15 => Key.F15,
            VtInputSequences.CsiTildeKey.F16 => Key.F16,
            VtInputSequences.CsiTildeKey.F17 => Key.F17,
            VtInputSequences.CsiTildeKey.F18 => Key.F18,
            VtInputSequences.CsiTildeKey.F19 => Key.F19,
            VtInputSequences.CsiTildeKey.F20 => Key.F20,

            _ => Key.None,
        };
        return key != Key.None;
    }

    private static KeyModifiers ParseModifiersParam(int parameter)
    {
        // Modifier param encoding (xterm baseline + Kitty extensions): parameter = 1 + bitfield.
        // Bits 0-3 are the xterm-conformant modifiers; bits 4-7 are Kitty-specific extensions.
        if (parameter < 1) return KeyModifiers.None;
        int bits = parameter - 1;

        KeyModifiers modifiers = KeyModifiers.None;
        if ((bits & VtInputSequences.ModifierParam.ShiftBit) != 0) modifiers |= KeyModifiers.Shift;
        if ((bits & VtInputSequences.ModifierParam.AltBit) != 0) modifiers |= KeyModifiers.Alt;
        if ((bits & VtInputSequences.ModifierParam.ControlBit) != 0) modifiers |= KeyModifiers.Control;
        if ((bits & VtInputSequences.ModifierParam.SuperBit) != 0) modifiers |= KeyModifiers.Super;
        if ((bits & VtInputSequences.ModifierParam.HyperBit) != 0) modifiers |= KeyModifiers.Hyper;
        if ((bits & VtInputSequences.ModifierParam.MetaBit) != 0) modifiers |= KeyModifiers.Meta;
        if ((bits & VtInputSequences.ModifierParam.CapsLockBit) != 0) modifiers |= KeyModifiers.CapsLock;
        if ((bits & VtInputSequences.ModifierParam.NumLockBit) != 0) modifiers |= KeyModifiers.NumLock;
        return modifiers;
    }

    public void OnOscDispatch(ReadOnlySpan<byte> body)
    {
        // OSC body shape: <code>;<value>. Identify recognized response codes and emit a
        // DeviceResponseEvent carrying the value portion (everything after the first ';').
        int separator = body.IndexOf((byte)';');
        if (separator < 0) return;

        if (!TryParseAsciiInt(body[..separator], out int code)) return;

        DeviceResponseKind? kind = code switch
        {
            VtInputSequences.OscCode.PaletteColor => DeviceResponseKind.PaletteColor,
            VtInputSequences.OscCode.ForegroundColor => DeviceResponseKind.ForegroundColor,
            VtInputSequences.OscCode.BackgroundColor => DeviceResponseKind.BackgroundColor,
            VtInputSequences.OscCode.CursorColor => DeviceResponseKind.CursorColor,
            _ => null,
        };

        if (kind is null) return;

        EmitDeviceResponse(kind.Value, body[(separator + 1)..]);
    }

    public void OnDcsHook(byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        _dcsBody.Clear();
        _dcsActive = true;
        _dcsKind = ClassifyDcs(privatePrefix, parameters, intermediates, final);
    }

    public void OnDcsPut(ReadOnlySpan<byte> bytes)
    {
        if (!_dcsActive || bytes.IsEmpty) return;
        bytes.CopyTo(_dcsBody.GetSpan(bytes.Length));
        _dcsBody.Advance(bytes.Length);
    }

    public void OnDcsUnhook()
    {
        if (!_dcsActive) return;

        if (_dcsKind != DeviceResponseKind.Unknown)
        {
            _eventSink.OnInputEvent(new DeviceResponseEvent
            {
                Timestamp = Now,
                Kind = _dcsKind,
                Payload = _dcsBody.WrittenSpan.ToArray(),
            });
        }

        _dcsBody.Clear();
        _dcsActive = false;
        _dcsKind = DeviceResponseKind.Unknown;
    }

    private static DeviceResponseKind ClassifyDcs(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final)
    {
        if (privatePrefix == 0 && intermediates.Length == 1)
        {
            // DA3 (Tertiary Device Attributes) response: DCS ! | <hex-id> ST.
            if (intermediates[0] == (byte)'!' && final == (byte)'|' && parameters.IsEmpty)
            {
                return DeviceResponseKind.TertiaryDeviceAttributes;
            }

            // DECRQSS (Request Status String) response: DCS <valid> $ r <data> ST.
            // <valid> is 1 when the request was honored, 0 when the terminal couldn't answer.
            if (intermediates[0] == (byte)'$' && final == (byte)'r')
            {
                return DeviceResponseKind.DecRqss;
            }

            // XTGETTCAP (Get Termcap) response: DCS <valid> + r <hex-name>=<hex-value> ST.
            if (intermediates[0] == (byte)'+' && final == (byte)'r')
            {
                return DeviceResponseKind.XtGetTcap;
            }
        }

        // XTVERSION response: DCS > | <name> ST.
        if (privatePrefix == VtInputSequences.SecondaryPrefix
            && parameters.IsEmpty
            && intermediates.IsEmpty
            && final == (byte)'|')
        {
            return DeviceResponseKind.XtVersion;
        }

        // Unknown DCS shape — the body is still accumulated and discarded at unhook.
        return DeviceResponseKind.Unknown;
    }

    // ---- Print / UTF-8 decode ----

    private void DecodePrintable(ReadOnlySpan<byte> bytes)
    {
        var ts = Now;

        // Combine any pending continuation bytes with this chunk and decode runes.
        Span<byte> combined = stackalloc byte[_utf8ContinuationLength + bytes.Length];
        _utf8Continuation.AsSpan(0, _utf8ContinuationLength).CopyTo(combined);
        bytes.CopyTo(combined[_utf8ContinuationLength..]);
        _utf8ContinuationLength = 0;

        ReadOnlySpan<byte> remaining = combined;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                // Save trailing bytes for next call.
                remaining.CopyTo(_utf8Continuation);
                _utf8ContinuationLength = remaining.Length;
                return;
            }

            // OperationStatus.Done or InvalidData: emit (substitution rune for invalid)
            // and advance.
            EmitPrintableRune(rune, ts);
            remaining = remaining[consumed..];
        }
    }

    private void EmitPrintableRune(Rune rune, DateTimeOffset timestamp)
    {
        Span<char> charBuf = stackalloc char[2];
        int written = rune.EncodeToUtf16(charBuf);
        var text = new char[written];
        charBuf[..written].CopyTo(text);

        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = timestamp,
            Key = Key.Character,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = text,
        });
    }

    // ---- Control character → KeyEvent ----

    private void EmitControlEvent(byte controlChar)
    {
        var ts = Now;
        KeyEvent? evt = controlChar switch
        {
            VtInputSequences.Tab => Named(Key.Tab),
            VtInputSequences.CarriageReturn or VtInputSequences.LineFeed => Named(Key.Enter),
            VtInputSequences.Backspace or VtInputSequences.Delete => Named(Key.Backspace),
            VtInputSequences.Nul => CtrlSpace(),
            >= 0x01 and <= 0x1A => CtrlLetter(controlChar),
            _ => null, // Other low-range controls (0x1C-0x1F, BEL, etc.) — ignored in v1.
        };

        if (evt is not null) _eventSink.OnInputEvent(evt);

        KeyEvent Named(Key key) => new()
        {
            Timestamp = ts,
            Key = key,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
        };

        KeyEvent CtrlSpace() => new()
        {
            Timestamp = ts,
            Key = Key.Space,
            Modifiers = KeyModifiers.Control,
            Kind = KeyEventKind.Down,
        };

        KeyEvent CtrlLetter(byte b)
        {
            // 0x01-0x1A → 'a'-'z' (Ctrl strips bits 5-6).
            char letter = (char)(b + 0x60);
            return new KeyEvent
            {
                Timestamp = ts,
                Key = Key.Character,
                Modifiers = KeyModifiers.Control,
                Kind = KeyEventKind.Down,
                Text = new[] { letter },
            };
        }
    }

    private void EmitNamedKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = key,
            Modifiers = modifiers,
            Kind = KeyEventKind.Down,
        });
    }

    // ---- Bracketed paste ----

    private void EnterPaste()
    {
        _inPaste = true;
        _pasteBuffer.Clear();
    }

    private void ExitPaste()
    {
        if (!_inPaste) return;

        var text = _pasteBuffer.ToString();
        _pasteBuffer.Clear();
        _inPaste = false;

        _eventSink.OnInputEvent(new PasteEvent
        {
            Timestamp = Now,
            Text = text.AsMemory(),
        });
    }

    private void AppendPasteText(ReadOnlySpan<byte> bytes)
    {
        // Worst case: every byte is a 1-byte ASCII char.
        Span<char> chars = stackalloc char[bytes.Length];
        int written = Encoding.UTF8.GetChars(bytes, chars);
        _pasteBuffer.Append(chars[..written]);
    }

    // ---- Parameter parsing helpers ----

    /// <summary>
    /// Parse a CSI parameter byte run into integer values, splitting on <c>;</c>. For v1, the
    /// sub-parameter separator <c>:</c> is treated identically to <c>;</c> — protocols that
    /// use sub-parameters meaningfully (Kitty keyboard, SGR colon-form colors) will get a
    /// dedicated parser when those decoders land.
    /// </summary>
    /// <returns>The number of parameters written to <paramref name="output"/>.</returns>
    private static int ParseParameters(ReadOnlySpan<byte> raw, Span<int> output)
    {
        if (raw.IsEmpty || output.IsEmpty) return 0;

        int count = 0;
        int current = 0;
        bool started = false;

        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                current = AccumulateDecimalSaturating(current, b - (byte)'0');
                started = true;
            }
            else if (b is (byte)';' or (byte)':')
            {
                if (count < output.Length)
                {
                    output[count++] = started ? current : 0;
                }
                current = 0;
                started = false;
            }
            // Other bytes are ignored — the classifier already filtered to valid CSI.
        }

        // Final parameter (CSI sequences end the param run before the final byte).
        if (count < output.Length)
        {
            output[count++] = started ? current : 0;
        }

        return count;
    }
}
