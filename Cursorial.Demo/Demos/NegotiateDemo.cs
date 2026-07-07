using System.Text;

using Cursorial.Terminal;

// ReSharper disable CheckNamespace

// The one-shot reference demo. Opens a session, captures the negotiated TerminalCapabilities,
// disposes (restoring cooked mode) before printing the multi-line report — otherwise the report
// would be written through a raw-mode terminal (OPOST off) where bare \n doesn't get a CR.
// Implements IDemo directly (no render loop, so no InteractiveDemo harness).
internal sealed class NegotiateDemo : IDemo
{
    public string Name => "negotiate";
    public IReadOnlyList<string> Aliases => ["caps"];
    public string Description =>
        "Open a session, dump negotiated TerminalCapabilities, restore.";

    public async Task RunAsync(string argument)
    {
        // Capture capabilities inside the session, dispose before printing — otherwise we'd
        // write multi-line output through a raw-mode terminal (OPOST off) where bare \n doesn't
        // get a CR.
        TerminalCapabilities caps;

        var sessionOptions = new TerminalSessionOptions { Negotiation = new NegotiationOptions { ProbeTimeout = TimeSpan.FromSeconds(3) } };

        await using (var session = await TerminalSession.OpenAsync(sessionOptions))
        {
            caps = session.Capabilities;
        }

        Console.Write(FormatCapabilities(caps));
    }

    private static string FormatCapabilities(TerminalCapabilities caps)
    {
        var sb = new StringBuilder();

        void Header(string title)
        {
            sb.AppendLine();
            sb.AppendLine(title);
            sb.AppendLine(new string('─', title.Length));
        }

        void Row(string label, object? value)
        {
            sb.Append("  ").Append(label.PadRight(28)).AppendLine(value?.ToString() ?? "(null)");
        }

        Header("Terminal");
        Row("Family",                caps.Terminal.Family);
        Row("Name",                  caps.Terminal.Name ?? "(unknown)");
        Row("Version",               caps.Terminal.Version ?? "(unknown)");
        Row("TERM env",              caps.Terminal.RawTermEnv ?? "(unset)");
        Row("TERM_PROGRAM env",      caps.Terminal.RawTermProgramEnv ?? "(unset)");
        Row("Inside multiplexer",    caps.Terminal.InsideMultiplexer);

        Header("Input — Mouse");
        Row("Button press / release", $"{caps.Input.Mouse.ButtonPress} / {caps.Input.Mouse.ButtonRelease}");
        Row("Drag",                  caps.Input.Mouse.Drag);
        Row("Motion (any-event)",    caps.Input.Mouse.Motion);
        Row("Wheel",                 caps.Input.Mouse.Wheel);
        Row("Pixel coordinates",     caps.Input.Mouse.PixelCoordinates);
        Row("Extended buttons",      caps.Input.Mouse.ExtendedButtonCount);

        Header("Input — Keyboard");
        Row("Distinguishes up/down", caps.Input.Keyboard.DistinguishesKeyUpDown);
        Row("Reports repeats",       caps.Input.Keyboard.ReportsRepeats);
        Row("Detailed modifiers",    caps.Input.Keyboard.DetailedModifiers);
        Row("Text input",            caps.Input.Keyboard.TextInput);

        Header("Input — Protocol");
        Row("Bracketed paste",       caps.Input.Protocol.BracketedPaste);
        Row("Focus events",          caps.Input.Protocol.FocusEvents);
        Row("Kitty keyboard",        caps.Input.Protocol.KittyKeyboardProtocol);
        Row("Win32 input mode",      caps.Input.Protocol.Win32InputMode);

        Header("Output — Color");
        Row("Depth",                 caps.Output.Color.Depth);
        Row("Truecolor verified",    caps.Output.Color.TruecolorVerified);
        Row("Default-color reset",   caps.Output.Color.DefaultColorReset);
        Row("OSC palette set",       caps.Output.Color.OscPaletteSet);

        Header("Output — Styling");
        Row("Italic",                caps.Output.Styling.Italic);
        Row("Underline",             caps.Output.Styling.Underline);
        Row("Extended underline",    caps.Output.Styling.ExtendedUnderline);
        Row("Colored underline",     caps.Output.Styling.ColoredUnderline);
        Row("Strikethrough",         caps.Output.Styling.Strikethrough);
        Row("Hyperlinks (OSC 8)",    caps.Output.Styling.Hyperlinks);

        Header("Output — Text Sizing (Kitty OSC 66)");
        Row("Width (w=)",            caps.Output.TextSizing.Width);
        Row("Scale (s=, n=/d=)",     caps.Output.TextSizing.Scale);
        Row("Reliable Wide Glyphs",  caps.Output.TextSizing.ReliableWideGlyphs);

        Header("Output — Graphics");
        Row("Sixel",                 caps.Output.Graphics.Sixel);
        Row("Kitty graphics",        caps.Output.Graphics.KittyGraphics);
        Row("iTerm2 inline images",  caps.Output.Graphics.ITerm2InlineImages);

        Header("Output — Cursor / Window");
        Row("Cursor shape control",  caps.Output.Cursor.ShapeControl);
        Row("Cursor visibility",     caps.Output.Cursor.VisibilityControl);
        Row("Cursor blink control",  caps.Output.Cursor.BlinkControl);
        Row("Cursor color control",  caps.Output.Cursor.ColorControl);
        Row("Cell pixel width",      caps.Output.Window.CellPixelWidth);
        Row("Cell pixel height",     caps.Output.Window.CellPixelHeight);
        Row("Title set",             caps.Output.Window.TitleSet);
        Row("Pixel size query",      caps.Output.Window.SizeQueryInPixels);
        Row("Alt screen buffer",     caps.Output.Window.AlternateScreenBuffer);
        Row("Native cursor shape",   caps.Output.Protocol.MouseCursorShape);

        Header("Output — Protocol opt-ins enabled");
        Row("SGR mouse reporting",   caps.Output.Protocol.SgrMouseEnable);
        Row("Mouse buttons",         caps.Output.Protocol.MouseButtonsEnable);
        Row("Mouse drag"     ,       caps.Output.Protocol.MouseDragEnable);
        Row("Mouse motion",          caps.Output.Protocol.MouseMotionEnable);
        Row("Focus reporting",       caps.Output.Protocol.FocusReportingEnable);
        Row("Bracketed paste",       caps.Output.Protocol.BracketedPasteEnable);
        Row("Kitty keyboard push",   caps.Output.Protocol.KittyKeyboardPush);
        Row("Win32 input mode",      caps.Output.Protocol.Win32InputModeEnable);
        Row("Synchronized output",   caps.Output.Protocol.SynchronizedOutput);

        return sb.ToString();
    }
}
