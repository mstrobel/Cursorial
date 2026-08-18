using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Cursorial.Input.Capabilities;
using Cursorial.Input.Parsing;
using Cursorial.Media;
using Cursorial.Output.Capabilities;

namespace Cursorial.Terminal;

/// <summary>
/// Hand-rolled JSON serialization for <see cref="TerminalCapabilities"/> — the persistence
/// format behind the capability cache (docs/cli-design.md §6,
/// <see cref="TerminalSessionOptions.CachedCapabilities"/>). Writes with
/// <see cref="Utf8JsonWriter"/> and reads with <see cref="JsonDocument"/>, so the round-trip
/// involves zero reflection and is safe under Native AOT / full trimming without any
/// source-generated <c>JsonSerializerContext</c> in the consumer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hand-rolled.</b> The record tree is small and stable, and two of its leaves resist
/// reflection-shaped serializers anyway: <see cref="Color"/> is a readonly record struct with
/// a private constructor (only factory methods), and <see cref="KittyKeyboardFlags"/> is a
/// flags enum. Hand-rolling keeps Core free of any serializer opinion and gives the cache a
/// deliberate, versioned wire shape instead of an accidental one.
/// </para>
/// <para>
/// <b>Strictness.</b> <see cref="TryDeserialize"/> is all-or-nothing: a version mismatch, a
/// missing property, a wrong-typed value, or an unknown enum token all yield <c>false</c>.
/// That is the desired cache semantic — any drift (schema change, capability-record change,
/// hand-edited file, partial write) invalidates the entry and the caller falls back to a full
/// negotiation, which rewrites the cache in the current shape. Never throws on bad input.
/// </para>
/// </remarks>
public static class TerminalCapabilitiesSerializer
{
    /// <summary>
    /// The wire-schema version. Bump whenever the capability record tree or this format
    /// changes shape; readers reject any other version (the cache entry is simply cold).
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>Serialize <paramref name="capabilities"/> to indented UTF-8 JSON.</summary>
    public static byte[] Serialize(TerminalCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        using var stream = new MemoryStream(1024);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);

            WriteIdentification(writer, capabilities.Terminal);
            WriteInput(writer, capabilities.Input);
            WriteOutput(writer, capabilities.Output);

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Deserialize a snapshot previously produced by <see cref="Serialize"/>. Returns
    /// <c>false</c> — never throws — on malformed JSON, a schema-version mismatch, or any
    /// missing / mistyped / unrecognized field (see class remarks for why strictness is the
    /// right cache semantic).
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out TerminalCapabilities? capabilities)
    {
        capabilities = null;

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return false;
            if (Prop(root, "schemaVersion").GetInt32() != SchemaVersion) return false;

            capabilities = new TerminalCapabilities(
                Terminal: ReadIdentification(Prop(root, "terminal")),
                Input: ReadInput(Prop(root, "input")),
                Output: ReadOutput(Prop(root, "output")));
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            capabilities = null;
            return false;
        }
    }

    // ---- Writers ----

    private static void WriteIdentification(Utf8JsonWriter writer, TerminalIdentification terminal)
    {
        writer.WriteStartObject("terminal");
        writer.WriteString("family", terminal.Family.ToString());
        writer.WriteString("name", terminal.Name);
        writer.WriteString("version", terminal.Version);
        writer.WriteString("rawTermEnv", terminal.RawTermEnv);
        writer.WriteString("rawTermProgramEnv", terminal.RawTermProgramEnv);
        writer.WriteBoolean("insideMultiplexer", terminal.InsideMultiplexer);
        writer.WriteBoolean("advertisesSixel", terminal.AdvertisesSixel);
        writer.WriteEndObject();
    }

    private static void WriteInput(Utf8JsonWriter writer, InputCapabilities input)
    {
        writer.WriteStartObject("input");

        writer.WriteStartObject("mouse");
        writer.WriteBoolean("buttonPress", input.Mouse.ButtonPress);
        writer.WriteBoolean("buttonRelease", input.Mouse.ButtonRelease);
        writer.WriteBoolean("drag", input.Mouse.Drag);
        writer.WriteBoolean("motion", input.Mouse.Motion);
        writer.WriteBoolean("wheel", input.Mouse.Wheel);
        writer.WriteBoolean("pixelCoordinates", input.Mouse.PixelCoordinates);
        writer.WriteNumber("extendedButtonCount", input.Mouse.ExtendedButtonCount);
        writer.WriteBoolean("synthesizesClickCounts", input.Mouse.SynthesizesClickCounts);
        writer.WriteBoolean("synthesizesClicks", input.Mouse.SynthesizesClicks);
        writer.WriteEndObject();

        writer.WriteStartObject("keyboard");
        writer.WriteBoolean("distinguishesKeyUpDown", input.Keyboard.DistinguishesKeyUpDown);
        writer.WriteBoolean("reportsRepeats", input.Keyboard.ReportsRepeats);
        writer.WriteBoolean("detailedModifiers", input.Keyboard.DetailedModifiers);
        writer.WriteBoolean("textInput", input.Keyboard.TextInput);
        writer.WriteEndObject();

        writer.WriteStartObject("pointer");
        writer.WriteBoolean("pen", input.Pointer.Pen);
        writer.WriteBoolean("pressure", input.Pointer.Pressure);
        writer.WriteBoolean("tilt", input.Pointer.Tilt);
        writer.WriteBoolean("touch", input.Pointer.Touch);
        writer.WriteEndObject();

        writer.WriteStartObject("protocol");
        writer.WriteBoolean("bracketedPaste", input.Protocol.BracketedPaste);
        writer.WriteBoolean("focusEvents", input.Protocol.FocusEvents);
        writer.WriteBoolean("kittyKeyboardProtocol", input.Protocol.KittyKeyboardProtocol);
        writer.WriteBoolean("win32InputMode", input.Protocol.Win32InputMode);
        writer.WriteNumber("kittyKeyboardFlags", (uint) input.Protocol.KittyKeyboardFlags);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteOutput(Utf8JsonWriter writer, OutputCapabilities output)
    {
        writer.WriteStartObject("output");

        writer.WriteStartObject("color");
        writer.WriteString("depth", output.Color.Depth.ToString());
        writer.WriteBoolean("truecolorVerified", output.Color.TruecolorVerified);
        writer.WriteBoolean("defaultColorReset", output.Color.DefaultColorReset);
        writer.WriteBoolean("oscPaletteSet", output.Color.OscPaletteSet);
        WriteColor(writer, "defaultForeground", output.Color.DefaultForeground);
        WriteColor(writer, "defaultBackground", output.Color.DefaultBackground);
        WriteColor(writer, "defaultCursorColor", output.Color.DefaultCursorColor);
        writer.WriteEndObject();

        writer.WriteStartObject("styling");
        writer.WriteBoolean("italic", output.Styling.Italic);
        writer.WriteBoolean("underline", output.Styling.Underline);
        writer.WriteBoolean("extendedUnderline", output.Styling.ExtendedUnderline);
        writer.WriteBoolean("coloredUnderline", output.Styling.ColoredUnderline);
        writer.WriteBoolean("strikethrough", output.Styling.Strikethrough);
        writer.WriteBoolean("overline", output.Styling.Overline);
        writer.WriteBoolean("hyperlinks", output.Styling.Hyperlinks);
        writer.WriteEndObject();

        writer.WriteStartObject("textSizing");
        writer.WriteBoolean("width", output.TextSizing.Width);
        writer.WriteBoolean("scale", output.TextSizing.Scale);
        writer.WriteBoolean("reliableWideGlyphs", output.TextSizing.ReliableWideGlyphs);
        writer.WriteEndObject();

        writer.WriteStartObject("graphics");
        writer.WriteBoolean("sixel", output.Graphics.Sixel);
        writer.WriteBoolean("kittyGraphics", output.Graphics.KittyGraphics);
        writer.WriteBoolean("iterm2InlineImages", output.Graphics.ITerm2InlineImages);
        writer.WriteEndObject();

        writer.WriteStartObject("cursor");
        writer.WriteBoolean("shapeControl", output.Cursor.ShapeControl);
        writer.WriteBoolean("visibilityControl", output.Cursor.VisibilityControl);
        writer.WriteBoolean("blinkControl", output.Cursor.BlinkControl);
        writer.WriteBoolean("colorControl", output.Cursor.ColorControl);
        writer.WriteBoolean("multipleCursors", output.Cursor.MultipleCursors);
        writer.WriteEndObject();

        writer.WriteStartObject("window");
        writer.WriteBoolean("titleSet", output.Window.TitleSet);
        writer.WriteBoolean("iconSet", output.Window.IconSet);
        writer.WriteBoolean("sizeQueryInPixels", output.Window.SizeQueryInPixels);
        writer.WriteBoolean("alternateScreenBuffer", output.Window.AlternateScreenBuffer);
        writer.WriteBoolean("scrollRegion", output.Window.ScrollRegion);
        WriteNullableInt(writer, "cellPixelWidth", output.Window.CellPixelWidth);
        WriteNullableInt(writer, "cellPixelHeight", output.Window.CellPixelHeight);
        WriteNullableInt(writer, "textAreaColumns", output.Window.TextAreaColumns);
        WriteNullableInt(writer, "textAreaRows", output.Window.TextAreaRows);
        writer.WriteEndObject();

        writer.WriteStartObject("protocol");
        writer.WriteBoolean("bracketedPasteEnable", output.Protocol.BracketedPasteEnable);
        writer.WriteBoolean("focusReportingEnable", output.Protocol.FocusReportingEnable);
        writer.WriteBoolean("sgrMouseEnable", output.Protocol.SgrMouseEnable);
        writer.WriteBoolean("mouseButtonsEnable", output.Protocol.MouseButtonsEnable);
        writer.WriteBoolean("mouseDragEnable", output.Protocol.MouseDragEnable);
        writer.WriteBoolean("mouseMotionEnable", output.Protocol.MouseMotionEnable);
        writer.WriteBoolean("kittyKeyboardPush", output.Protocol.KittyKeyboardPush);
        writer.WriteBoolean("win32InputModeEnable", output.Protocol.Win32InputModeEnable);
        writer.WriteBoolean("clipboardWrite", output.Protocol.ClipboardWrite);
        writer.WriteBoolean("clipboardRead", output.Protocol.ClipboardRead);
        writer.WriteBoolean("synchronizedOutput", output.Protocol.SynchronizedOutput);
        writer.WriteBoolean("multiplexerPassthrough", output.Protocol.MultiplexerPassthrough);
        writer.WriteBoolean("mouseCursorShape", output.Protocol.MouseCursorShape);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    // Colors are written as compact strings: "default", "palette:<index>", or "#rrggbbaa".
    // The record struct's constructor is private (factory methods only), so a structural
    // object encoding would need special-casing anyway; a tagged string is smaller and reads
    // at a glance in the cache file.
    private static void WriteColor(Utf8JsonWriter writer, string name, Color? color)
    {
        if (color is not { } value)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value.Kind switch
        {
            ColorKind.Palette => $"palette:{value.PaletteIndex}",
            ColorKind.Rgb => $"#{value.Red:x2}{value.Green:x2}{value.Blue:x2}{value.Alpha:x2}",
            _ => "default",
        });
    }

    private static void WriteNullableInt(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    // ---- Readers ----

    private static TerminalIdentification ReadIdentification(JsonElement terminal) => new(
        Family: ReadEnum<TerminalFamily>(Prop(terminal, "family")),
        Name: Prop(terminal, "name").GetString(),
        Version: Prop(terminal, "version").GetString(),
        RawTermEnv: Prop(terminal, "rawTermEnv").GetString(),
        RawTermProgramEnv: Prop(terminal, "rawTermProgramEnv").GetString(),
        InsideMultiplexer: Prop(terminal, "insideMultiplexer").GetBoolean(),
        AdvertisesSixel: Prop(terminal, "advertisesSixel").GetBoolean());

    private static InputCapabilities ReadInput(JsonElement input)
    {
        var mouse = Prop(input, "mouse");
        var keyboard = Prop(input, "keyboard");
        var pointer = Prop(input, "pointer");
        var protocol = Prop(input, "protocol");

        return new InputCapabilities(
            Mouse: new MouseCapabilities(
                ButtonPress: Prop(mouse, "buttonPress").GetBoolean(),
                ButtonRelease: Prop(mouse, "buttonRelease").GetBoolean(),
                Drag: Prop(mouse, "drag").GetBoolean(),
                Motion: Prop(mouse, "motion").GetBoolean(),
                Wheel: Prop(mouse, "wheel").GetBoolean(),
                PixelCoordinates: Prop(mouse, "pixelCoordinates").GetBoolean(),
                ExtendedButtonCount: Prop(mouse, "extendedButtonCount").GetInt32())
            {
                SynthesizesClickCounts = Prop(mouse, "synthesizesClickCounts").GetBoolean(),
                SynthesizesClicks = Prop(mouse, "synthesizesClicks").GetBoolean(),
            },
            Keyboard: new KeyboardCapabilities(
                DistinguishesKeyUpDown: Prop(keyboard, "distinguishesKeyUpDown").GetBoolean(),
                ReportsRepeats: Prop(keyboard, "reportsRepeats").GetBoolean(),
                DetailedModifiers: Prop(keyboard, "detailedModifiers").GetBoolean(),
                TextInput: Prop(keyboard, "textInput").GetBoolean()),
            Pointer: new PointerCapabilities(
                Pen: Prop(pointer, "pen").GetBoolean(),
                Pressure: Prop(pointer, "pressure").GetBoolean(),
                Tilt: Prop(pointer, "tilt").GetBoolean(),
                Touch: Prop(pointer, "touch").GetBoolean()),
            Protocol: new ProtocolCapabilities(
                BracketedPaste: Prop(protocol, "bracketedPaste").GetBoolean(),
                FocusEvents: Prop(protocol, "focusEvents").GetBoolean(),
                KittyKeyboardProtocol: Prop(protocol, "kittyKeyboardProtocol").GetBoolean(),
                Win32InputMode: Prop(protocol, "win32InputMode").GetBoolean())
            {
                KittyKeyboardFlags = (KittyKeyboardFlags) Prop(protocol, "kittyKeyboardFlags").GetUInt32(),
            });
    }

    private static OutputCapabilities ReadOutput(JsonElement output)
    {
        var color = Prop(output, "color");
        var styling = Prop(output, "styling");
        var textSizing = Prop(output, "textSizing");
        var graphics = Prop(output, "graphics");
        var cursor = Prop(output, "cursor");
        var window = Prop(output, "window");
        var protocol = Prop(output, "protocol");

        return new OutputCapabilities(
            Color: new ColorCapabilities(
                Depth: ReadEnum<ColorDepth>(Prop(color, "depth")),
                TruecolorVerified: Prop(color, "truecolorVerified").GetBoolean(),
                DefaultColorReset: Prop(color, "defaultColorReset").GetBoolean(),
                OscPaletteSet: Prop(color, "oscPaletteSet").GetBoolean(),
                DefaultForeground: ReadColor(Prop(color, "defaultForeground")),
                DefaultBackground: ReadColor(Prop(color, "defaultBackground")),
                DefaultCursorColor: ReadColor(Prop(color, "defaultCursorColor"))),
            Styling: new TextStylingCapabilities(
                Italic: Prop(styling, "italic").GetBoolean(),
                Underline: Prop(styling, "underline").GetBoolean(),
                ExtendedUnderline: Prop(styling, "extendedUnderline").GetBoolean(),
                ColoredUnderline: Prop(styling, "coloredUnderline").GetBoolean(),
                Strikethrough: Prop(styling, "strikethrough").GetBoolean(),
                Overline: Prop(styling, "overline").GetBoolean(),
                Hyperlinks: Prop(styling, "hyperlinks").GetBoolean()),
            TextSizing: new TextSizingCapabilities(
                Width: Prop(textSizing, "width").GetBoolean(),
                Scale: Prop(textSizing, "scale").GetBoolean(),
                ReliableWideGlyphs: Prop(textSizing, "reliableWideGlyphs").GetBoolean()),
            Graphics: new GraphicsCapabilities(
                Sixel: Prop(graphics, "sixel").GetBoolean(),
                KittyGraphics: Prop(graphics, "kittyGraphics").GetBoolean(),
                ITerm2InlineImages: Prop(graphics, "iterm2InlineImages").GetBoolean()),
            Cursor: new CursorCapabilities(
                ShapeControl: Prop(cursor, "shapeControl").GetBoolean(),
                VisibilityControl: Prop(cursor, "visibilityControl").GetBoolean(),
                BlinkControl: Prop(cursor, "blinkControl").GetBoolean(),
                ColorControl: Prop(cursor, "colorControl").GetBoolean(),
                MultipleCursors: Prop(cursor, "multipleCursors").GetBoolean()),
            Window: new WindowCapabilities(
                TitleSet: Prop(window, "titleSet").GetBoolean(),
                IconSet: Prop(window, "iconSet").GetBoolean(),
                SizeQueryInPixels: Prop(window, "sizeQueryInPixels").GetBoolean(),
                AlternateScreenBuffer: Prop(window, "alternateScreenBuffer").GetBoolean(),
                ScrollRegion: Prop(window, "scrollRegion").GetBoolean(),
                CellPixelWidth: ReadNullableInt(Prop(window, "cellPixelWidth")),
                CellPixelHeight: ReadNullableInt(Prop(window, "cellPixelHeight")),
                TextAreaColumns: ReadNullableInt(Prop(window, "textAreaColumns")),
                TextAreaRows: ReadNullableInt(Prop(window, "textAreaRows"))),
            Protocol: new OutputProtocolCapabilities(
                BracketedPasteEnable: Prop(protocol, "bracketedPasteEnable").GetBoolean(),
                FocusReportingEnable: Prop(protocol, "focusReportingEnable").GetBoolean(),
                SgrMouseEnable: Prop(protocol, "sgrMouseEnable").GetBoolean(),
                MouseButtonsEnable: Prop(protocol, "mouseButtonsEnable").GetBoolean(),
                MouseDragEnable: Prop(protocol, "mouseDragEnable").GetBoolean(),
                MouseMotionEnable: Prop(protocol, "mouseMotionEnable").GetBoolean(),
                KittyKeyboardPush: Prop(protocol, "kittyKeyboardPush").GetBoolean(),
                Win32InputModeEnable: Prop(protocol, "win32InputModeEnable").GetBoolean(),
                ClipboardWrite: Prop(protocol, "clipboardWrite").GetBoolean(),
                ClipboardRead: Prop(protocol, "clipboardRead").GetBoolean(),
                SynchronizedOutput: Prop(protocol, "synchronizedOutput").GetBoolean(),
                MultiplexerPassthrough: Prop(protocol, "multiplexerPassthrough").GetBoolean(),
                MouseCursorShape: Prop(protocol, "mouseCursorShape").GetBoolean()));
    }

    /// <summary>Strict property access: absence raises (caught by <see cref="TryDeserialize"/> → cold cache).</summary>
    private static JsonElement Prop(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new FormatException($"Missing property '{name}' in capability snapshot.");

    /// <summary>
    /// Parse an enum stored by name. Rejects numeric tokens (Enum.TryParse would accept any
    /// integer, defined or not) and undefined names, so schema drift in the enum surfaces as a
    /// cold cache instead of a bogus value. AOT-safe: the generic TryParse/IsDefined overloads
    /// involve no reflection over the consumer's types.
    /// </summary>
    private static TEnum ReadEnum<TEnum>(JsonElement element) where TEnum : struct, Enum
    {
        var token = element.GetString();

        if (token is null || token.Length == 0 || char.IsAsciiDigit(token[0]) || token[0] == '-')
            throw new FormatException($"Invalid {typeof(TEnum).Name} token.");

        if (!Enum.TryParse<TEnum>(token, ignoreCase: false, out var value) || !Enum.IsDefined(value))
            throw new FormatException($"Unrecognized {typeof(TEnum).Name} token '{token}'.");

        return value;
    }

    private static int? ReadNullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    private static Color? ReadColor(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;

        var token = element.GetString() ?? throw new FormatException("Invalid color token.");

        if (token == "default") return Color.Default;

        if (token.StartsWith("palette:", StringComparison.Ordinal))
        {
            return byte.TryParse(token.AsSpan("palette:".Length), out var index)
                ? Color.FromPalette(index)
                : throw new FormatException($"Invalid palette color token '{token}'.");
        }

        return Color.TryParseHex(token, out var color, out _)
            ? color
            : throw new FormatException($"Invalid color token '{token}'.");
    }
}
