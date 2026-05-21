using Cursorial.Input.Parsing;
using Cursorial.Output;
using Cursorial.Terminal;

// ReSharper disable MethodHasAsyncOverload

namespace Cursorial.Tests.Terminal;

public class VtTerminalNegotiatorTests
{
    private readonly InMemoryInputByteSource _source = new();
    private readonly InMemoryOutputByteSink _sink = new();
    private readonly StubEnvironmentReader _env = new();

    private VtTerminalNegotiator BuildNegotiator(VtInputMode? mode = null) =>
        new(_source, _sink, mode, timeProvider: null, environmentReader: _env);

    private static NegotiationOptions DisableAllOptIns() => new()
                                                            {
                                                                ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                                                OptIns = OptInPolicy.Ignored,
                                                            };

    private async Task<string> AllWrittenAsync()
    {
        var bytes = await _sink.ReadAllWrittenAsync();
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static NegotiationOptions FastTimeout() => new()
                                                       {
                                                           ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                                       };

    private static NegotiationOptions ImmediateTimeout() => new()
                                                            {
                                                                ProbeTimeout = TimeSpan.FromMilliseconds(50),
                                                            };

    // ---- Probe writes ----

    [Fact]
    public async Task NegotiateAsync_WritesXtVersionThenDa1()
    {
        // Pre-populate the DA1 sentinel response so the negotiator returns promptly.
        _source.Enqueue("\x1b[?64;1;9c");

        await using var negotiator = BuildNegotiator();
        await negotiator.NegotiateAsync(FastTimeout());

        var written = await _sink.ReadAllWrittenAsync();
        var asString = System.Text.Encoding.ASCII.GetString(written);

        Assert.Contains("\x1b[>q", asString); // XTVERSION
        Assert.Contains("\x1b[c", asString);  // DA1 sentinel

        Assert.True(asString.IndexOf("\x1b[>q", StringComparison.Ordinal) < asString.IndexOf("\x1b[c", StringComparison.Ordinal),
                    "XTVERSION must precede the DA1 sentinel.");
    }

    // ---- Identification from XTVERSION + DA1 ----

    [Fact]
    public async Task XtVersionResponseIdentifiesKitty()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\"); // XTVERSION response
        _source.Enqueue("\x1b[?64;1;9c");             // DA1 sentinel

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Kitty, caps.Terminal.Family);
        Assert.Equal("kitty", caps.Terminal.Name);
        Assert.Equal("0.34.1", caps.Terminal.Version);
    }

    [Fact]
    public async Task XtVersionResponseIdentifiesITerm()
    {
        _source.Enqueue("\x1bP>|iTerm2 3.4.5\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.ITerm2, caps.Terminal.Family);
        Assert.Equal("iTerm2", caps.Terminal.Name);
        Assert.Equal("3.4.5", caps.Terminal.Version);
    }

    [Fact]
    public async Task Da1WithSixelParameter_FlagsAdvertisesSixelAndEnablesGraphicsSixel()
    {
        // DA1 parameter 4 = Sixel graphics per the DEC spec. Parsing it lets us detect Sixel
        // on terminals the family allow-list misses (xterm-with-sixel, modern Kitty / Konsole).
        _source.Enqueue("\x1b[?64;1;4;9c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Terminal.AdvertisesSixel);
        Assert.True(caps.Output.Graphics.Sixel);
    }

    [Fact]
    public async Task Da1WithoutSixelParameter_LeavesAdvertisesSixelFalse()
    {
        _source.Enqueue("\x1b[?64;1;9;22c"); // no 4

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.False(caps.Terminal.AdvertisesSixel);
        Assert.False(caps.Output.Graphics.Sixel);
    }

    [Fact]
    public async Task Da1WithParameter44_DoesNotMatchSixel()
    {
        // Parameter 44 is PCTerm — must not match a substring search on "4". The parser
        // tokenizes by ';' and compares values exactly.
        _source.Enqueue("\x1b[?64;1;44c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.False(caps.Terminal.AdvertisesSixel);
    }

    [Fact]
    public async Task Da1WithSixelParameterFirst_AlsoDetected()
    {
        // Tokenization must work regardless of position in the parameter list.
        _source.Enqueue("\x1b[?4;1;22c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Terminal.AdvertisesSixel);
    }

    [Fact]
    public async Task Da1AdvertisesSixel_OverridesFamilyWithoutSixel()
    {
        // Family is Kitty (Sixel: false in the family list) but DA1 advertises it. Modern Kitty
        // (0.41+) is exactly this case. The DA1 advertisement wins.
        _source.Enqueue("\x1bP>|kitty(0.46.2)\x1b\\");
        _source.Enqueue("\x1b[?62;4;22c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Kitty, caps.Terminal.Family);
        Assert.True(caps.Output.Graphics.Sixel);
        Assert.True(caps.Output.Graphics.KittyGraphics); // family-derived caps preserved
    }

    [Fact]
    public async Task XtVersionResponseIdentifiesWezTerm()
    {
        _source.Enqueue("\x1bP>|WezTerm 20240127\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.WezTerm, caps.Terminal.Family);
    }

    [Fact]
    public async Task XtVersionResponseWithParenVersion_KittyShape_ExtractsVersion()
    {
        // Kitty 0.46+ reports XTVERSION as "kitty(0.46.2)" instead of the older "kitty 0.34.1"
        // space-separated form. The parenthesized payload looks like a semantic version
        // (contains a '.'), so the parser pulls it out.
        _source.Enqueue("\x1bP>|kitty(0.46.2)\x1b\\");
        _source.Enqueue("\x1b[?64;1;9c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Kitty, caps.Terminal.Family);
        Assert.Equal("kitty", caps.Terminal.Name);
        Assert.Equal("0.46.2", caps.Terminal.Version);
    }

    [Fact]
    public async Task XtVersionResponseWithParenIdentifier_VteShape_TreatsAsOpaqueName()
    {
        // GNOME Terminal's libvte reports XTVERSION as "VTE(7600)" where 7600 is a build
        // identifier, NOT a version. The parenthesized content doesn't look like a version
        // (no dot), so the parser leaves the whole "VTE(7600)" string as the name and version
        // stays null. The family classifier handles the literal "VTE(7600)" pattern.
        _source.Enqueue("\x1bP>|VTE(7600)\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal("VTE(7600)", caps.Terminal.Name);
        Assert.Null(caps.Terminal.Version);
    }

    // ---- Identification from environment when no XTVERSION arrives ----

    [Fact]
    public async Task NoXtVersion_FallsBackToTermProgramEnv()
    {
        _env.Set("TERM_PROGRAM", "iTerm.app").Set("TERM_PROGRAM_VERSION", "3.4.5");
        _source.Enqueue("\x1b[?64c"); // DA1 only

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.ITerm2, caps.Terminal.Family);
        Assert.Equal("iTerm.app", caps.Terminal.Name);
        Assert.Equal("3.4.5", caps.Terminal.Version);
    }

    [Fact]
    public async Task KittyPidEnv_IdentifiesKittyEvenWithoutXtVersion()
    {
        _env.Set("KITTY_PID", "1234");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Kitty, caps.Terminal.Family);
    }

    [Fact]
    public async Task WtSessionEnv_IdentifiesWindowsTerminal()
    {
        _env.Set("WT_SESSION", "guid");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.WindowsTerminal, caps.Terminal.Family);
    }

    [Fact]
    public async Task WindowsConsoleAttachedWithoutWtSession_IdentifiesConHost()
    {
        // On Windows, when WT_SESSION isn't set but stdin is attached to a real console handle,
        // we're under conhost.exe (cmd.exe / PowerShell without Windows Terminal). This unlocks
        // Win32 Input Mode and the rest of the conhost-aware capability gates.
        _env.WithWindowsConsoleAttached();
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.WindowsConsoleHost, caps.Terminal.Family);
    }

    [Fact]
    public async Task WindowsConsoleAttachedWithWtSession_StillIdentifiesWindowsTerminal()
    {
        // Belt-and-braces: when WT_SESSION is set we choose WindowsTerminal even if a console
        // is also attached (Windows Terminal hosts a console internally).
        _env.WithWindowsConsoleAttached()
            .Set("WT_SESSION", "guid");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.WindowsTerminal, caps.Terminal.Family);
    }

    [Fact]
    public async Task NoConsoleAndNoTerminalSignals_FallsThroughToUnknown()
    {
        // Process not attached to a console, no env signals → can't classify. Negotiator
        // returns Unknown rather than guessing.
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Unknown, caps.Terminal.Family);
    }

    [Fact]
    public async Task ConHost_GatesWin32InputModeOn()
    {
        // Verify the legacy-conhost identification flows through to the Win32-Input-Mode
        // gate — conhost is in that family list, so DECSET 9001 should be queued.
        _env.WithWindowsConsoleAttached();
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.WindowsConsoleHost, caps.Terminal.Family);

        // Win32 Input Mode is a write-side opt-in: gate fires when the family matches.
        var written = System.Text.Encoding.ASCII.GetString(await _sink.ReadAllWrittenAsync());
        Assert.Contains("\x1b[?9001h", written);
    }

    [Fact]
    public async Task TmuxEnv_FlagsInsideMultiplexer()
    {
        _env.Set("TMUX", "/tmp/tmux-1000/default,1234,0");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Terminal.InsideMultiplexer);
    }

    [Fact]
    public async Task ScreenTerm_FlagsInsideMultiplexer()
    {
        _env.Set("TERM", "screen-256color");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Terminal.InsideMultiplexer);
        Assert.Equal(TerminalFamily.GnuScreen, caps.Terminal.Family);
    }

    [Fact]
    public async Task XtermTerm_IdentifiesXterm()
    {
        _env.Set("TERM", "xterm-256color");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Xterm, caps.Terminal.Family);
        Assert.Equal("xterm-256color", caps.Terminal.RawTermEnv);
    }

    [Fact]
    public async Task NoEnvAndNoResponses_UnknownFamilyAfterTimeout()
    {
        // No DA1 response — negotiator should bail after timeout, not hang.
        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(ImmediateTimeout());

        Assert.Equal(TerminalFamily.Unknown, caps.Terminal.Family);
    }

    // ---- Color depth ----

    [Fact]
    public async Task ColorTermTruecolor_GivesTruecolorDepth()
    {
        _env.Set("COLORTERM", "truecolor").Set("TERM", "xterm");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(ColorDepth.Truecolor, caps.Output.Color.Depth);
    }

    [Fact]
    public async Task KittyFamily_AssumedTruecolorWithoutColorTerm()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(ColorDepth.Truecolor, caps.Output.Color.Depth);
        Assert.False(caps.Output.Color.TruecolorVerified, "Verification round-trip not implemented yet.");
    }

    [Fact]
    public async Task XtermColorTerm_FallsTo256ColorByTermName()
    {
        _env.Set("TERM", "xterm-256color");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(ColorDepth.Ansi256, caps.Output.Color.Depth);
    }

    [Fact]
    public async Task WindowsTerminal_GivesTruecolorDepth()
    {
        _env.Set("WT_SESSION", "guid");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(ColorDepth.Truecolor, caps.Output.Color.Depth);
    }

    [Fact]
    public async Task WindowsConsoleHost_GivesTruecolorDepth()
    {
        _env.WithWindowsConsoleAttached();
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(ColorDepth.Truecolor, caps.Output.Color.Depth);
    }

    // ---- Graphics inference ----

    [Fact]
    public async Task KittyFamily_ReportsKittyGraphics()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Output.Graphics.KittyGraphics);
        Assert.False(caps.Output.Graphics.Sixel);
        Assert.False(caps.Output.Graphics.ITerm2InlineImages);
    }

    [Fact]
    public async Task ITermFamily_ReportsInlineImages()
    {
        _source.Enqueue("\x1bP>|iTerm2 3.4.5\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Output.Graphics.ITerm2InlineImages);
        Assert.False(caps.Output.Graphics.KittyGraphics);
    }

    [Fact]
    public async Task KittyFamily_ModernVersion_ReportsTextSizingWidthAndScale()
    {
        // Kitty 0.40+ ships OSC 66 text sizing.
        _source.Enqueue("\x1bP>|kitty(0.46.2)\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Output.TextSizing.Width);
        Assert.True(caps.Output.TextSizing.Scale);
    }

    [Fact]
    public async Task KittyFamily_OldVersion_DisablesTextSizing()
    {
        // OSC 66 text sizing landed in Kitty 0.40.0 (Oct 2024). Older versions render the
        // envelope's metadata block as literal text. The negotiator must gate the capability
        // on version so we don't corrupt the display on 0.3x.x builds.
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.False(caps.Output.TextSizing.Width);
        Assert.False(caps.Output.TextSizing.Scale);
    }

    [Fact]
    public async Task KittyFamily_UnknownVersion_DisablesTextSizing()
    {
        // No XTVERSION response → can't know the version. Default conservatively to off so
        // pre-0.40 Kitty (or other terminals that get misidentified as Kitty by the family
        // classifier) don't get OSC 66 emissions they can't render.
        _source.Enqueue("\x1b[?64;1;9c"); // DA1 only
        _env.Set("TERM", "xterm-kitty");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Kitty, caps.Terminal.Family);
        Assert.False(caps.Output.TextSizing.Width);
        Assert.False(caps.Output.TextSizing.Scale);
    }

    [Fact]
    public async Task NonKittyFamily_ReportsNoTextSizing()
    {
        _source.Enqueue("\x1bP>|iTerm2 3.4.5\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.False(caps.Output.TextSizing.Width);
        Assert.False(caps.Output.TextSizing.Scale);
    }

    // ---- Lifecycle ----

    [Fact]
    public async Task NegotiateAsyncTwice_Throws()
    {
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?64c"); // For the doomed second call (won't get there).

        await using var negotiator = BuildNegotiator();
        await negotiator.NegotiateAsync(FastTimeout());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await negotiator.NegotiateAsync(FastTimeout()));
    }

    [Fact]
    public async Task NegotiateAsyncAfterDispose_Throws()
    {
        var negotiator = BuildNegotiator();
        await negotiator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await negotiator.NegotiateAsync(FastTimeout()));
    }

    [Fact]
    public async Task RestoreAsyncWithoutNegotiation_IsNoOp()
    {
        await using var negotiator = BuildNegotiator();
        await negotiator.RestoreAsync(); // Should not throw, should not write anything.

        var written = await _sink.ReadAllWrittenAsync();
        Assert.Empty(written);
    }

    [Fact]
    public async Task DisposalIsIdempotent()
    {
        var negotiator = BuildNegotiator();
        await negotiator.DisposeAsync();
        await negotiator.DisposeAsync(); // Should not throw on second call.
    }

    [Fact]
    public async Task CancellationToken_IsHonoredDuringProbe()
    {
        // No response queued — negotiator will be waiting on the read.
        using var cts = new CancellationTokenSource();
        await using var negotiator = BuildNegotiator();

        var task = negotiator.NegotiateAsync(
            new NegotiationOptions { ProbeTimeout = TimeSpan.FromSeconds(30) },
            cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task NullOptions_ThrowsArgumentNullException()
    {
        await using var negotiator = BuildNegotiator();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await negotiator.NegotiateAsync(options: null!));
    }

    // ---- Opt-in application ----

    [Fact]
    public async Task OptInsIgnore_WritesProbesOnlyNoOptIns()
    {
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(DisableAllOptIns());

        var written = await AllWrittenAsync();
        Assert.DoesNotContain("?1006h", written);
        Assert.DoesNotContain("?1004h", written);
        Assert.DoesNotContain("?2004h", written);
    }

    [Fact]
    public async Task EnableMouseTracking_WritesSgrAndButtonEventEnables()
    {
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = true,
                                            EnableMouseTracking = false,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        var written = await AllWrittenAsync();
        Assert.Contains("\x1b[?1006h", written);       // SGR mouse
        Assert.Contains("\x1b[?1002h", written);       // button-event tracking
        Assert.DoesNotContain("\x1b[?1003h", written); // any-event NOT enabled
    }

    [Fact]
    public async Task EnableAnyEventMouse_AddsMotionEnableOnTopOfBaseline()
    {
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = true,
                                            EnableMouseTracking = true,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        var written = await AllWrittenAsync();
        Assert.Contains("\x1b[?1006h", written);
        Assert.Contains("\x1b[?1002h", written);
        Assert.Contains("\x1b[?1003h", written);
    }

    [Fact]
    public async Task EnableFocusAndPaste_WritesBothEnables()
    {
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = true,
                                            EnableBracketedPaste = true,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        var written = await AllWrittenAsync();
        Assert.Contains("\x1b[?1004h", written);
        Assert.Contains("\x1b[?2004h", written);
    }

    [Fact]
    public async Task EnableKittyKeyboardOnKittyFamily_WritesPushWithFlags()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = true,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                            // Default flags = 1+2+4+8+16 = 31.
                                        });

        var written = await AllWrittenAsync();
        Assert.Contains("\x1b[>31u", written);
    }

    [Fact]
    public async Task EnableKittyKeyboardOnXtermFamily_DoesNotPush()
    {
        _env.Set("TERM", "xterm-256color");
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = true,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        var written = await AllWrittenAsync();
        // Kitty push has the form CSI > <digits> u. The only legitimate `>` write is the
        // XTVERSION probe (`CSI > q`), so finding any `>` followed by digits indicates push.
        Assert.DoesNotMatch(@"\[>\d+u", written);
    }

    [Fact]
    public async Task EnableWin32InputModeOnNonWindowsFamily_DoesNotEnable()
    {
        _env.Set("TERM", "xterm-256color");
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = true,
                                            EnableSynchronizedOutput = false,
                                        });

        var written = await AllWrittenAsync();
        Assert.DoesNotContain("9001", written);
    }

    [Fact]
    public async Task EnableWin32InputModeOnWindowsTerminal_WritesEnable()
    {
        _env.Set("WT_SESSION", "guid");
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = true,
                                            EnableSynchronizedOutput = false,
                                        });

        var written = await AllWrittenAsync();
        Assert.Contains("\x1b[?9001h", written);
    }

    [Fact]
    public async Task EnableSynchronizedOutputOnSupportedFamily_RecordsCapabilityWithoutEnable()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        var caps = await negotiator.NegotiateAsync(new NegotiationOptions
                                                   {
                                                       ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                                       EnableExtendedMouseTracking = false,
                                                       EnableFocusEvents = false,
                                                       EnableBracketedPaste = false,
                                                       EnableKittyKeyboard = false,
                                                       EnableWin32InputMode = false,
                                                       EnableSynchronizedOutput = true,
                                                   });

        // Capability is reported as supported so per-frame consumers (FrameRenderer) can wrap
        // their redraws in DECSET/DECRST 2026. But the negotiator must NOT issue a session-level
        // DECSET 2026 — that begins a sync block which buffers all output until disposal on
        // strictly-conforming terminals (WezTerm, Kitty).
        Assert.True(caps.Output.Protocol.SynchronizedOutput);
        var written = await AllWrittenAsync();
        Assert.DoesNotContain("\x1b[?2026h", written);
        Assert.DoesNotContain("\x1b[?2026l", written);
    }

    // ---- Capability reflection ----

    [Fact]
    public async Task FullDefaultOptIns_PopulateInputAndOutputCapabilities()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");
        await using var negotiator = BuildNegotiator();

        var caps = await negotiator.NegotiateAsync(new NegotiationOptions
                                                   {
                                                       ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                                   });

        Assert.True(caps.Input.Mouse.ButtonPress);
        Assert.True(caps.Input.Mouse.Motion); // EnableAnyEventMouse default true
        Assert.True(caps.Input.Mouse.Wheel);
        Assert.True(caps.Input.Keyboard.DistinguishesKeyUpDown); // Kitty + ReportEventTypes
        Assert.True(caps.Input.Keyboard.TextInput);              // Kitty + ReportAssociatedText
        Assert.True(caps.Input.Protocol.BracketedPaste);
        Assert.True(caps.Input.Protocol.FocusEvents);
        Assert.True(caps.Input.Protocol.KittyKeyboardProtocol);

        Assert.True(caps.Output.Protocol.SgrMouseEnable);
        Assert.True(caps.Output.Protocol.BracketedPasteEnable);
        Assert.True(caps.Output.Protocol.MouseMotionEnable);
        Assert.True(caps.Output.Protocol.FocusReportingEnable);
        Assert.True(caps.Output.Protocol.BracketedPasteEnable);
        Assert.True(caps.Output.Protocol.KittyKeyboardPush);
        Assert.True(caps.Output.Protocol.SynchronizedOutput);
    }

    [Fact]
    public async Task NegotiationUpdatesSharedVtInputMode()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");
        var mode = new VtInputMode();
        await using var negotiator = BuildNegotiator(mode);

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                        });

        Assert.Equal(MouseEncoding.Sgr, mode.MouseEncoding);
        Assert.True(mode.BracketedPasteEnabled);
        Assert.True(mode.FocusReportingEnabled);
        Assert.NotEqual(KittyKeyboardFlags.None, mode.KittyKeyboard);
    }

    // ---- Restore ----

    [Fact]
    public async Task RestoreAsync_EmitsKittyMultiCursorClear_Unconditionally()
    {
        // Even when no opt-ins were applied, restore must emit the Kitty multi-cursor clear
        // as a defensive cleanup — the protocol is fire-and-forget (no enable tracked), and
        // the clear is a no-op on terminals that don't implement it.
        _source.Enqueue("\x1b[?64c"); // DA1 only — no XTVERSION → no family-gated opt-ins
        var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        await _sink.ReadAllWrittenAsync();

        await negotiator.RestoreAsync();
        var restored = await AllWrittenAsync();

        Assert.Contains("\x1b[>0;4 q", restored);

        await negotiator.DisposeAsync();
    }

    [Fact]
    public async Task RestoreAsync_EmitsDisablesInLifoOrder()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");
        var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                        });

        // Drain the writes from negotiation so we only inspect what restore emits.
        await _sink.ReadAllWrittenAsync();

        await negotiator.RestoreAsync();
        var restored = await AllWrittenAsync();

        // LIFO: kitty pop → bracketed paste → focus → any-event mouse → button-event → SGR.
        // Synchronized output is NOT a session-level opt-in (FrameRenderer wraps per-frame),
        // so no DECRST 2026 appears in restore.
        int idxKittyPop = restored.IndexOf("\x1b[<u", StringComparison.Ordinal);
        int idxPaste = restored.IndexOf("\x1b[?2004l", StringComparison.Ordinal);
        int idxFocus = restored.IndexOf("\x1b[?1004l", StringComparison.Ordinal);
        int idxAnyEvent = restored.IndexOf("\x1b[?1003l", StringComparison.Ordinal);
        int idxButtonEvent = restored.IndexOf("\x1b[?1002l", StringComparison.Ordinal);
        int idxSgr = restored.IndexOf("\x1b[?1006l", StringComparison.Ordinal);

        Assert.DoesNotContain("\x1b[?2026l", restored);
        Assert.True(idxKittyPop >= 0);
        Assert.True(idxPaste > idxKittyPop);
        Assert.True(idxFocus > idxPaste);
        Assert.True(idxAnyEvent > idxFocus);
        Assert.True(idxButtonEvent > idxAnyEvent);
        Assert.True(idxSgr > idxButtonEvent);

        await negotiator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_TriggersRestore()
    {
        _source.Enqueue("\x1b[?64c");
        var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = true,
                                            EnableBracketedPaste = true,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        await _sink.ReadAllWrittenAsync(); // drain the enables

        await negotiator.DisposeAsync();
        var restored = await AllWrittenAsync();

        Assert.Contains("\x1b[?1004l", restored);
        Assert.Contains("\x1b[?2004l", restored);
    }

    [Fact]
    public async Task RestoreAsync_TwiceIsIdempotent()
    {
        _source.Enqueue("\x1b[?64c");
        var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            EnableExtendedMouseTracking = false,
                                            EnableFocusEvents = true,
                                            EnableBracketedPaste = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false,
                                        });

        await _sink.ReadAllWrittenAsync();

        await negotiator.RestoreAsync();
        var firstRestore = await AllWrittenAsync();
        Assert.Contains("\x1b[?1004l", firstRestore);

        await negotiator.RestoreAsync();
        var secondRestore = await AllWrittenAsync();
        Assert.Empty(secondRestore);

        await negotiator.DisposeAsync();
    }

    [Fact]
    public async Task RestoreAsync_ResetsSharedModeBag()
    {
        _source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
        _source.Enqueue("\x1b[?64c");
        var mode = new VtInputMode();
        var negotiator = BuildNegotiator(mode);

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                        });

        Assert.Equal(MouseEncoding.Sgr, mode.MouseEncoding);

        await negotiator.RestoreAsync();

        Assert.Equal(MouseEncoding.None, mode.MouseEncoding);
        Assert.False(mode.BracketedPasteEnabled);
        Assert.False(mode.FocusReportingEnabled);
        Assert.Equal(KittyKeyboardFlags.None, mode.KittyKeyboard);

        await negotiator.DisposeAsync();
    }
}