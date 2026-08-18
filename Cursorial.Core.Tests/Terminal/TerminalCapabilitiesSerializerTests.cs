using System.Text;

using Cursorial.Input.Capabilities;
using Cursorial.Input.Parsing;
using Cursorial.Media;
using Cursorial.Output.Capabilities;
using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// Round-trip and rejection tests for <see cref="TerminalCapabilitiesSerializer"/> — the
/// persistence format behind the capability cache. Every rejection path must read as a cold
/// cache (<c>false</c>), never an exception.
/// </summary>
public class TerminalCapabilitiesSerializerTests
{
    /// <summary>
    /// A snapshot with every field populated away from its default — nullable strings set,
    /// nullable ints set, all three color kinds represented, a non-trivial flags value — so a
    /// field the serializer forgot to write (or read) breaks record equality.
    /// </summary>
    private static TerminalCapabilities MaximalSnapshot() => new(
        Terminal: new TerminalIdentification(
            Family: TerminalFamily.Kitty,
            Name: "kitty",
            Version: "0.34.1",
            RawTermEnv: "xterm-kitty",
            RawTermProgramEnv: "kitty \"quoted\" — π",
            InsideMultiplexer: true,
            AdvertisesSixel: true),
        Input: new InputCapabilities(
            Mouse: new MouseCapabilities(
                ButtonPress: true,
                ButtonRelease: true,
                Drag: true,
                Motion: true,
                Wheel: true,
                PixelCoordinates: true,
                ExtendedButtonCount: 4)
            {
                SynthesizesClickCounts = true,
                SynthesizesClicks = true,
            },
            Keyboard: new KeyboardCapabilities(
                DistinguishesKeyUpDown: true,
                ReportsRepeats: true,
                DetailedModifiers: true,
                TextInput: true),
            Pointer: new PointerCapabilities(
                Pen: true,
                Pressure: false,
                Tilt: true,
                Touch: false),
            Protocol: new ProtocolCapabilities(
                BracketedPaste: true,
                FocusEvents: true,
                KittyKeyboardProtocol: true,
                Win32InputMode: false)
            {
                KittyKeyboardFlags = NegotiationOptions.DefaultKittyKeyboardFlags,
            }),
        Output: new OutputCapabilities(
            Color: new ColorCapabilities(
                Depth: ColorDepth.Truecolor,
                TruecolorVerified: true,
                DefaultColorReset: true,
                OscPaletteSet: true,
                DefaultForeground: Color.FromRgb(0xAB, 0xCD, 0xEF),
                DefaultBackground: Color.FromPalette(17),
                DefaultCursorColor: Color.Default),
            Styling: new TextStylingCapabilities(
                Italic: true,
                Underline: true,
                ExtendedUnderline: true,
                ColoredUnderline: true,
                Strikethrough: true,
                Overline: false,
                Hyperlinks: true),
            TextSizing: new TextSizingCapabilities(
                Width: true,
                Scale: true,
                ReliableWideGlyphs: false),
            Graphics: new GraphicsCapabilities(
                Sixel: true,
                KittyGraphics: true,
                ITerm2InlineImages: false),
            Cursor: new CursorCapabilities(
                ShapeControl: true,
                VisibilityControl: true,
                BlinkControl: true,
                ColorControl: true,
                MultipleCursors: true),
            Window: new WindowCapabilities(
                TitleSet: true,
                IconSet: false,
                SizeQueryInPixels: true,
                AlternateScreenBuffer: true,
                ScrollRegion: true,
                CellPixelWidth: 9,
                CellPixelHeight: 19,
                TextAreaColumns: 120,
                TextAreaRows: 40),
            Protocol: new OutputProtocolCapabilities(
                BracketedPasteEnable: true,
                FocusReportingEnable: true,
                SgrMouseEnable: true,
                MouseButtonsEnable: true,
                MouseDragEnable: true,
                MouseMotionEnable: true,
                KittyKeyboardPush: true,
                Win32InputModeEnable: false,
                ClipboardWrite: true,
                ClipboardRead: true,
                SynchronizedOutput: true,
                MultiplexerPassthrough: true,
                MouseCursorShape: true)));

    [Fact]
    public void RoundTrip_MaximalSnapshot_IsValueEqual()
    {
        var original = MaximalSnapshot();

        var json = TerminalCapabilitiesSerializer.Serialize(original);

        Assert.True(TerminalCapabilitiesSerializer.TryDeserialize(json, out var thawed));
        Assert.Equal(original, thawed);
    }

    [Fact]
    public void RoundTrip_NoneSnapshot_IsValueEqual()
    {
        // The all-defaults snapshot: null strings, null ints, null colors, Unknown family.
        var original = TerminalCapabilities.None;

        var json = TerminalCapabilitiesSerializer.Serialize(original);

        Assert.True(TerminalCapabilitiesSerializer.TryDeserialize(json, out var thawed));
        Assert.Equal(original, thawed);
    }

    [Fact]
    public void RoundTrip_AlphaColor_SurvivesExactly()
    {
        var original = TerminalCapabilities.None with
        {
            Output = TerminalCapabilities.None.Output with
            {
                Color = ColorCapabilities.None with
                {
                    DefaultBackground = Color.FromRgba(0x11, 0x22, 0x33, 0x44),
                },
            },
        };

        Assert.True(TerminalCapabilitiesSerializer.TryDeserialize(
                        TerminalCapabilitiesSerializer.Serialize(original), out var thawed));
        Assert.Equal(original, thawed);
    }

    // ---- Rejection paths — every one must be a quiet 'false' (cold cache), never a throw ----

    [Fact]
    public void TryDeserialize_Garbage_ReturnsFalse()
    {
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize("not json at all"u8, out var caps));
        Assert.Null(caps);
    }

    [Fact]
    public void TryDeserialize_Empty_ReturnsFalse()
    {
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize([], out _));
    }

    [Fact]
    public void TryDeserialize_NonObjectRoot_ReturnsFalse()
    {
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize("[1,2,3]"u8, out _));
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize("42"u8, out _));
    }

    [Fact]
    public void TryDeserialize_Truncated_ReturnsFalse()
    {
        var json = TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot());

        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(json.AsSpan(0, json.Length / 2), out _));
    }

    [Fact]
    public void TryDeserialize_SchemaVersionDrift_ReturnsFalse()
    {
        var json = Encoding.UTF8.GetString(TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot()));
        var drifted = json.Replace($"\"schemaVersion\": {TerminalCapabilitiesSerializer.SchemaVersion}",
                                   $"\"schemaVersion\": {TerminalCapabilitiesSerializer.SchemaVersion + 1}");

        Assert.NotEqual(json, drifted); // guard the surgery actually hit
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(Encoding.UTF8.GetBytes(drifted), out _));
    }

    [Fact]
    public void TryDeserialize_MissingProperty_ReturnsFalse()
    {
        var json = Encoding.UTF8.GetString(TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot()));
        var mangled = json.Replace("\"insideMultiplexer\"", "\"insideMultiplexerRenamed\"");

        Assert.NotEqual(json, mangled);
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(Encoding.UTF8.GetBytes(mangled), out _));
    }

    [Fact]
    public void TryDeserialize_UnknownFamilyToken_ReturnsFalse()
    {
        var json = Encoding.UTF8.GetString(TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot()));
        var mangled = json.Replace("\"family\": \"Kitty\"", "\"family\": \"KittyNext\"");

        Assert.NotEqual(json, mangled);
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(Encoding.UTF8.GetBytes(mangled), out _));
    }

    [Fact]
    public void TryDeserialize_NumericFamilyToken_ReturnsFalse()
    {
        // Enum.TryParse would happily accept "2" (or "999") — the reader must not, or an enum
        // reorder between versions would silently remap cached families.
        var json = Encoding.UTF8.GetString(TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot()));
        var mangled = json.Replace("\"family\": \"Kitty\"", "\"family\": \"2\"");

        Assert.NotEqual(json, mangled);
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(Encoding.UTF8.GetBytes(mangled), out _));
    }

    [Fact]
    public void TryDeserialize_MistypedValue_ReturnsFalse()
    {
        var json = Encoding.UTF8.GetString(TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot()));
        var mangled = json.Replace("\"extendedButtonCount\": 4", "\"extendedButtonCount\": \"four\"");

        Assert.NotEqual(json, mangled);
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(Encoding.UTF8.GetBytes(mangled), out _));
    }

    [Fact]
    public void TryDeserialize_InvalidColorToken_ReturnsFalse()
    {
        var json = Encoding.UTF8.GetString(TerminalCapabilitiesSerializer.Serialize(MaximalSnapshot()));
        var mangled = json.Replace("\"defaultForeground\": \"#abcdefff\"", "\"defaultForeground\": \"#zzz\"");

        Assert.NotEqual(json, mangled);
        Assert.False(TerminalCapabilitiesSerializer.TryDeserialize(Encoding.UTF8.GetBytes(mangled), out _));
    }
}
