using Cursorial.Input.Capabilities;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Terminal;

namespace Cursorial.UI.Testing;

/// <summary>
/// Scripted capability presets for <see cref="UITestHostOptions.Capabilities"/> (design doc
/// §10.10): no probes, no negotiation — these snapshots stand in for what a real
/// <c>VtTerminalNegotiator</c> would have realized.
/// </summary>
public static class TestCapabilities
{
    /// <summary>
    /// A modern Kitty-class terminal: verified truecolor, full styling, alt screen, cursor
    /// control, SGR mouse with motion, Kitty keyboard (key-up/repeats), bracketed paste + focus,
    /// and the OSC 22 pointer-shape protocol (<c>MouseCursorShape</c> — Kitty honors it live).
    /// The default preset.
    /// </summary>
    public static TerminalCapabilities KittyTruecolor { get; } = new(
        Terminal: new TerminalIdentification(
            Family: TerminalFamily.Kitty,
            Name: "kitty",
            Version: "0.40.0",
            RawTermEnv: "xterm-kitty",
            RawTermProgramEnv: null,
            InsideMultiplexer: false),
        Input: new InputCapabilities(
            Mouse: new MouseCapabilities(
                ButtonPress: true,
                ButtonRelease: true,
                Drag: true,
                Motion: true,
                Wheel: true,
                PixelCoordinates: false,
                ExtendedButtonCount: 2),
            Keyboard: new KeyboardCapabilities(
                DistinguishesKeyUpDown: true,
                ReportsRepeats: true,
                DetailedModifiers: true,
                TextInput: true),
            Pointer: PointerCapabilities.None,
            Protocol: new ProtocolCapabilities(
                BracketedPaste: true,
                FocusEvents: true,
                KittyKeyboardProtocol: true,
                Win32InputMode: false)),
        Output: OutputCapabilities.None with
        {
            Color = ColorCapabilities.None with
            {
                Depth = ColorDepth.Truecolor,
                TruecolorVerified = true,
                DefaultColorReset = true,
                DefaultForeground = Color.FromRgb(205, 214, 244),
                DefaultBackground = Color.FromRgb(30, 30, 46),
            },
            Styling = new TextStylingCapabilities(
                Italic: true,
                Underline: true,
                ExtendedUnderline: true,
                ColoredUnderline: true,
                Strikethrough: true,
                Overline: true,
                Hyperlinks: true),
            Cursor = new CursorCapabilities(
                ShapeControl: true,
                VisibilityControl: true,
                BlinkControl: true,
                ColorControl: true),
            Window = WindowCapabilities.None with
            {
                TitleSet = true,
                AlternateScreenBuffer = true,
                ScrollRegion = true,
            },
            Protocol = OutputProtocolCapabilities.None with { MouseCursorShape = true },
        });

    /// <summary>
    /// A legacy 16-color terminal: no truecolor, basic underline only, alt screen, press/release
    /// mouse without motion, no Kitty keyboard, no focus events.
    /// </summary>
    public static TerminalCapabilities Ansi16Legacy { get; } = new(
        Terminal: new TerminalIdentification(
            Family: TerminalFamily.Xterm,
            Name: null,
            Version: null,
            RawTermEnv: "xterm",
            RawTermProgramEnv: null,
            InsideMultiplexer: false),
        Input: new InputCapabilities(
            Mouse: new MouseCapabilities(
                ButtonPress: true,
                ButtonRelease: true,
                Drag: false,
                Motion: false,
                Wheel: true,
                PixelCoordinates: false,
                ExtendedButtonCount: 0),
            Keyboard: KeyboardCapabilities.None with { TextInput = true },
            Pointer: PointerCapabilities.None,
            Protocol: ProtocolCapabilities.None),
        Output: OutputCapabilities.None with
        {
            Color = ColorCapabilities.None with { Depth = ColorDepth.Ansi16 },
            Styling = new TextStylingCapabilities(
                Italic: false,
                Underline: true,
                ExtendedUnderline: false,
                ColoredUnderline: false,
                Strikethrough: false,
                Overline: false,
                Hyperlinks: false),
            Window = WindowCapabilities.None with { AlternateScreenBuffer = true },
        });

    /// <summary>The Kitty preset minus any-event mouse motion (hover-related capability gates).</summary>
    public static TerminalCapabilities NoMotion { get; } = KittyTruecolor with
    {
        Input = KittyTruecolor.Input with
        {
            Mouse = KittyTruecolor.Input.Mouse with { Motion = false, Drag = false },
        },
    };

    /// <summary>
    /// The Kitty preset minus the OSC 22 pointer-shape protocol (the §7.6 cursor-emission
    /// capability gate): hover/motion fully functional, but <c>UIElement.Cursor</c> must produce
    /// zero emission and zero tracking.
    /// </summary>
    public static TerminalCapabilities NoMouseCursorShape { get; } = KittyTruecolor with
    {
        Output = KittyTruecolor.Output with
        {
            Protocol = KittyTruecolor.Output.Protocol with { MouseCursorShape = false },
        },
    };

    /// <summary>The Kitty preset with the Kitty graphics protocol (APC G…) — images <b>and</b> occlusion
    /// (<c>caps-images</c> + <c>caps-image-occlusion</c>). Reports a cell-pixel size (CSI 16 t), as a real Kitty
    /// terminal does, so an image with no requested cell footprint can be sized from its pixels.</summary>
    public static TerminalCapabilities KittyGraphics { get; } = KittyTruecolor with
    {
        Output = KittyTruecolor.Output with
        {
            Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: true, ITerm2InlineImages: false),
            Window = KittyTruecolor.Output.Window with { CellPixelWidth = 8, CellPixelHeight = 16 },
        },
    };

    /// <summary>The Kitty preset with DEC Sixel graphics only — images but NO occlusion (Sixel paints into the
    /// cell grid; <c>caps-images</c> without <c>caps-image-occlusion</c>).</summary>
    public static TerminalCapabilities SixelGraphics { get; } = KittyTruecolor with
    {
        Output = KittyTruecolor.Output with
        {
            Graphics = new GraphicsCapabilities(Sixel: true, KittyGraphics: false, ITerm2InlineImages: false),
        },
    };

    /// <summary>The Kitty preset with iTerm2 / WezTerm inline images only — images but NO occlusion (excluded for
    /// now; <c>caps-images</c> without <c>caps-image-occlusion</c>).</summary>
    public static TerminalCapabilities ITerm2Graphics { get; } = KittyTruecolor with
    {
        Output = KittyTruecolor.Output with
        {
            Graphics = new GraphicsCapabilities(Sixel: false, KittyGraphics: false, ITerm2InlineImages: true),
        },
    };
}
