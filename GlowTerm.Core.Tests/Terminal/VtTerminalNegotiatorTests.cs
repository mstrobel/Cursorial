using GlowTerm.Core.Input;
using GlowTerm.Core.Output;
using GlowTerm.Core.Terminal;

namespace GlowTerm.Core.Tests.Terminal;

public class VtTerminalNegotiatorTests
{
    private readonly InMemoryInputByteSource _source = new();
    private readonly InMemoryOutputByteSink _sink = new();
    private readonly StubEnvironmentReader _env = new();

    private VtTerminalNegotiator BuildNegotiator() =>
        new(_source, _sink, mode: null, timeProvider: null, environmentReader: _env);

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
        Assert.True(asString.IndexOf("\x1b[>q") < asString.IndexOf("\x1b[c"),
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

        Assert.Equal(TerminalFamily.Iterm2, caps.Terminal.Family);
        Assert.Equal("iTerm2", caps.Terminal.Name);
        Assert.Equal("3.4.5", caps.Terminal.Version);
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

    // ---- Identification from environment when no XTVERSION arrives ----

    [Fact]
    public async Task NoXtVersion_FallsBackToTermProgramEnv()
    {
        _env.Set("TERM_PROGRAM", "iTerm.app").Set("TERM_PROGRAM_VERSION", "3.4.5");
        _source.Enqueue("\x1b[?64c"); // DA1 only

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.Equal(TerminalFamily.Iterm2, caps.Terminal.Family);
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
        Assert.False(caps.Output.Graphics.Iterm2InlineImages);
    }

    [Fact]
    public async Task ITermFamily_ReportsInlineImages()
    {
        _source.Enqueue("\x1bP>|iTerm2 3.4.5\x1b\\");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(FastTimeout());

        Assert.True(caps.Output.Graphics.Iterm2InlineImages);
        Assert.False(caps.Output.Graphics.KittyGraphics);
    }

    // ---- Lifecycle ----

    [Fact]
    public async Task NegotiateAsyncTwice_Throws()
    {
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?64c"); // For the doomed second call (won't get there).

        await using var negotiator = BuildNegotiator();
        await negotiator.NegotiateAsync(FastTimeout());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await negotiator.NegotiateAsync(FastTimeout()));
    }

    [Fact]
    public async Task NegotiateAsyncAfterDispose_Throws()
    {
        var negotiator = BuildNegotiator();
        await negotiator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await negotiator.NegotiateAsync(FastTimeout()));
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
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await negotiator.NegotiateAsync(options: null!));
    }
}
