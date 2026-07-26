using Cursorial.Output;
using Cursorial.Terminal;

// ReSharper disable MethodHasAsyncOverload

namespace Cursorial.Tests.Terminal;

/// <summary>
/// Identification + capability coverage for the terminal families added in the 2026-07 expansion, plus the
/// audit corrections applied to the pre-existing families (OSC 52 read membership, VTE styling, the Apple
/// Terminal DECSCUSR fix, WezTerm/Rio graphics, the Zellij non-passthrough rule, and two classifier bug fixes).
/// </summary>
public class TerminalFamilyExpansionTests
{
    private readonly InMemoryInputByteSource _source = new();
    private readonly InMemoryOutputByteSink _sink = new();
    private readonly StubEnvironmentReader _env = new();

    private static NegotiationOptions Fast() => new() { ProbeTimeout = TimeSpan.FromMilliseconds(100) };

    private VtTerminalNegotiator Build() => new(_source, _sink, mode: null, timeProvider: null, environmentReader: _env);

    // Identify via a passive probe (no opt-ins written): enqueue an optional XTVERSION reply + the DA1 sentinel.
    private async Task<TerminalCapabilities> IdentifyAsync(string? xtVersion = null)
    {
        if (xtVersion is not null)
            _source.Enqueue(xtVersion);
        _source.Enqueue("\x1b[?64c"); // DA1 sentinel

        await using var negotiator = Build();
        return await negotiator.NegotiateAsync(Fast());
    }

    // ───────────────────────────── new-family identification ─────────────────────────────

    [Fact]
    public async Task Warp_IdentifiedByTermProgram()
    {
        _env.Set("TERM_PROGRAM", "WarpTerminal");
        var caps = await IdentifyAsync();
        Assert.Equal(TerminalFamily.Warp, caps.Terminal.Family);
    }

    [Fact]
    public async Task VsCode_IdentifiedByTermProgram()
    {
        _env.Set("TERM_PROGRAM", "vscode");
        Assert.Equal(TerminalFamily.VisualStudioCode, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task VsCode_IdentifiedByVscodePidWhenTermProgramMissing()
    {
        _env.Set("VSCODE_PID", "4242");
        Assert.Equal(TerminalFamily.VisualStudioCode, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task Hyper_IdentifiedByTermProgram()
    {
        _env.Set("TERM_PROGRAM", "Hyper");
        Assert.Equal(TerminalFamily.Hyper, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task WaveTerminal_IdentifiedByTermProgram()
    {
        _env.Set("TERM_PROGRAM", "waveterm");
        Assert.Equal(TerminalFamily.WaveTerminal, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task WaveTerminal_IdentifiedByEnvWhenTermProgramMissing()
    {
        _env.Set("WAVETERM", "1");
        Assert.Equal(TerminalFamily.WaveTerminal, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task Termux_IdentifiedByTermuxVersion()
    {
        _env.Set("TERMUX_VERSION", "0.118.0");
        Assert.Equal(TerminalFamily.Termux, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task Mintty_IdentifiedByXtVersion()
    {
        var caps = await IdentifyAsync("\x1bP>|mintty 3.7.0\x1b\\");
        Assert.Equal(TerminalFamily.Mintty, caps.Terminal.Family);
        Assert.Equal("3.7.0", caps.Terminal.Version);
    }

    [Fact]
    public async Task Contour_IdentifiedByXtVersion()
    {
        var caps = await IdentifyAsync("\x1bP>|contour 0.4.3\x1b\\");
        Assert.Equal(TerminalFamily.Contour, caps.Terminal.Family);
    }

    [Fact]
    public async Task SimpleTerminal_IdentifiedByTerm()
    {
        _env.Set("TERM", "st-256color");
        Assert.Equal(TerminalFamily.SimpleTerminal, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task PuTTY_IdentifiedByTerm()
    {
        _env.Set("TERM", "putty-256color");
        Assert.Equal(TerminalFamily.PuTTY, (await IdentifyAsync()).Terminal.Family);
    }

    [Fact]
    public async Task Vte_VersionEnv_ResolvesToTheVteRepresentative()
    {
        // GNOME Console (kgx), Black Box, Tilix, xfce4-terminal all set VTE_VERSION and are indistinguishable,
        // so they resolve to the VTE representative (GnomeTerminal).
        _env.Set("VTE_VERSION", "7803");
        Assert.Equal(TerminalFamily.GnomeTerminal, (await IdentifyAsync()).Terminal.Family);
    }

    // ───────────────────────────── Zellij: multiplexer, but no passthrough ─────────────────────────────

    [Fact]
    public async Task Zellij_IdentifiedAndFlaggedInsideMultiplexer_ButNotPassthrough()
    {
        _env.Set("ZELLIJ", "0");
        var caps = await IdentifyAsync();

        Assert.Equal(TerminalFamily.Zellij, caps.Terminal.Family);
        Assert.True(caps.Terminal.InsideMultiplexer);
        // Unlike tmux, Zellij re-renders with no generic escape passthrough.
        Assert.False(caps.Output.Protocol.MultiplexerPassthrough);
    }

    [Fact]
    public async Task Tmux_IsPassthrough_UnlikeZellij()
    {
        _env.Set("TMUX", "/tmp/tmux-1000/default,1,0");
        _env.Set("TERM", "tmux-256color");
        Assert.True((await IdentifyAsync()).Output.Protocol.MultiplexerPassthrough);
    }

    // ───────────────────────────── audit corrections: OSC 52 read ─────────────────────────────

    [Theory]
    [InlineData("\x1bP>|kitty 0.40.0\x1b\\", true)]   // prompts, but implements the query
    [InlineData("\x1bP>|foot 1.18.0\x1b\\", true)]    // allowed silently
    [InlineData("\x1bP>|mintty 3.9.0\x1b\\", true)]   // added 3.8.2
    [InlineData("\x1bP>|contour 0.4.0\x1b\\", true)]
    [InlineData("\x1bP>|Rio 0.1.0\x1b\\", false)]     // WRITE-only — no clipboard_load path
    [InlineData("\x1bP>|iTerm2 3.5.0\x1b\\", false)]  // documented write-only
    public async Task ClipboardRead_MembershipMatchesTheAudit(string xtVersion, bool canRead)
    {
        var caps = await IdentifyAsync(xtVersion);
        Assert.Equal(canRead, caps.Output.Protocol.ClipboardRead);
        // Every one of these still WRITES — read is the strictly smaller set.
        Assert.True(caps.Output.Protocol.ClipboardWrite);
    }

    [Fact]
    public async Task Tmux_WritesButDoesNotReadClipboard()
    {
        _env.Set("TMUX", "/tmp/tmux-1000/default,1,0");
        _env.Set("TERM", "tmux-256color"); // the family (not just InsideMultiplexer) classification signal
        var caps = await IdentifyAsync();
        Assert.True(caps.Output.Protocol.ClipboardWrite);
        Assert.False(caps.Output.Protocol.ClipboardRead); // tmux doesn't reflect an inner app's query
    }

    // ───────────────────────────── audit corrections: styling ─────────────────────────────

    [Fact]
    public async Task Vte_GetsHyperlinksAndStyledUnderlineAndOverline()
    {
        _env.Set("VTE_VERSION", "7803");
        var s = (await IdentifyAsync()).Output.Styling;
        Assert.True(s.Hyperlinks);        // VTE 0.50+
        Assert.True(s.ExtendedUnderline); // VTE 0.52+
        Assert.True(s.ColoredUnderline);
        Assert.True(s.Overline);          // VTE 0.52+ — the blanket "nobody" was wrong
    }

    [Theory]
    [InlineData("\x1bP>|ghostty 1.0.0\x1b\\", true)]
    [InlineData("\x1bP>|WezTerm 20240101\x1b\\", true)]
    [InlineData("\x1bP>|kitty 0.40.0\x1b\\", false)]  // Kitty does NOT honor SGR 53
    [InlineData("\x1bP>|foot 1.18.0\x1b\\", false)]
    public async Task Overline_IsPerTerminal(string xtVersion, bool overline)
    {
        Assert.Equal(overline, (await IdentifyAsync(xtVersion)).Output.Styling.Overline);
    }

    [Fact]
    public async Task SimpleTerminal_BaseBuild_HasNoHyperlinks()
    {
        // st's OSC 8 is a patch — do not infer it from the st-256color identity.
        _env.Set("TERM", "st-256color");
        Assert.False((await IdentifyAsync()).Output.Styling.Hyperlinks);
    }

    // ───────────────────────────── audit corrections: graphics ─────────────────────────────

    [Fact]
    public async Task WezTerm_ShipsKittyGraphics()
    {
        var g = (await IdentifyAsync("\x1bP>|WezTerm 20240101\x1b\\")).Output.Graphics;
        Assert.True(g.KittyGraphics);
        Assert.True(g.Sixel);
        Assert.True(g.ITerm2InlineImages);
    }

    [Fact]
    public async Task Rio_ShipsKittyGraphics()
    {
        var g = (await IdentifyAsync("\x1bP>|Rio 0.2.0\x1b\\")).Output.Graphics;
        // Assert.True(g.Sixel); // <- require Rio to _advertise_ Sixel in its DA1 response; it's still a new feature.
        Assert.True(g.KittyGraphics);
    }

    // ───────────────────────────── audit corrections: cursor ─────────────────────────────

    [Fact]
    public async Task AppleTerminal_LacksDecscusrShapeAndBlink_ButKeepsVisibility()
    {
        _env.Set("TERM_PROGRAM", "Apple_Terminal");
        var c = (await IdentifyAsync()).Output.Cursor;
        Assert.False(c.ShapeControl); // the fixed bug — the comment excluded it but the code didn't
        Assert.False(c.BlinkControl);
        Assert.False(c.ColorControl);
        Assert.True(c.VisibilityControl); // DECTCEM (mode 25) still works
    }

    [Fact]
    public async Task Termux_LacksCursorColor()
    {
        _env.Set("TERMUX_VERSION", "0.118.0");
        var c = (await IdentifyAsync()).Output.Cursor;
        Assert.True(c.ShapeControl);
        Assert.False(c.ColorControl); // no OSC 12
    }

    // ───────────────────────────── audit corrections: mouse / pixel ─────────────────────────────

    [Fact]
    public async Task Rio_DoesNotAdvertiseSgrPixelsMouse()
    {
        // Rio maps only 1006 → SGR, not 1016.
        var caps = await IdentifyAsync("\x1bP>|Rio 0.2.0\x1b\\");
        Assert.False(caps.Input.Mouse.PixelCoordinates);
    }

    [Fact]
    public async Task ITerm2_GetsPointerShape()
    {
        var caps = await IdentifyAsync("\x1bP>|iTerm2 3.5.0\x1b\\");
        Assert.True(caps.Output.Protocol.MouseCursorShape);
    }

    // ───────────────────────────── classifier bug fixes ─────────────────────────────

    [Fact]
    public async Task ItermTerm_ClassifiesAsITerm2_NotRio()
    {
        // Regression: the TERM=iTerm branch used to (wrongly) return Rio.
        _env.Set("TERM", "iTerm2");
        Assert.Equal(TerminalFamily.ITerm2, (await IdentifyAsync()).Terminal.Family);
    }
}
