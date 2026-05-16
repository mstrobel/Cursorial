using Cursorial.Input.Parsing;
using Cursorial.Output;
using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// Tests for the post-negotiation color probe phase — truecolor verification via OSC 4
/// set+query round-trip plus default-color queries via OSC 10 / 11 / 12.
/// </summary>
public class VtTerminalNegotiatorColorProbeTests
{
    private readonly InMemoryInputByteSource _source = new();
    private readonly InMemoryOutputByteSink _sink = new();
    private readonly StubEnvironmentReader _env = new();

    private VtTerminalNegotiator BuildNegotiator(VtInputMode? mode = null) =>
        new(_source, _sink, mode, timeProvider: null, environmentReader: _env);

    private async Task<string> AllWrittenAsync()
    {
        var bytes = await _sink.ReadAllWrittenAsync();
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Standard negotiation options for these tests — minimum-friction (all DEC mode opt-ins
    /// off so the DECRQM verification phase is a no-op), short timeout.
    /// </summary>
    private static NegotiationOptions Options()
        => new()
           {
               ProbeTimeout = TimeSpan.FromMilliseconds(200),
               EnableExtendedMouseTracking = false,
               EnableFocusEvents = false,
               EnableBracketedPaste = false,
               EnableMouseTracking = false,
               EnableKittyKeyboard = false,
               EnableWin32InputMode = false,
               EnableSynchronizedOutput = false,
           };

    [Fact]
    public async Task NegotiateAsync_EmitsColorProbeSequences()
    {
        _source.Enqueue("\x1b[?64c"); // identification DA1
        _source.Enqueue("\x1b[?64c"); // color-probe DA1

        await using var negotiator = BuildNegotiator();
        await negotiator.NegotiateAsync(Options());

        var written = await AllWrittenAsync();

        // OSC 4 set palette slot 255 to (0xAB, 0xCD, 0xEF) followed by a query for the same slot.
        Assert.Contains("\x1b]4;255;rgb:ab/cd/ef\x1b\\", written);
        Assert.Contains("\x1b]4;255;?\x1b\\", written);

        // OSC 10 / 11 / 12 default-color queries.
        Assert.Contains("\x1b]10;?\x1b\\", written);
        Assert.Contains("\x1b]11;?\x1b\\", written);
        Assert.Contains("\x1b]12;?\x1b\\", written);
    }

    [Fact]
    public async Task TruecolorVerified_TrueWhenPaletteRoundTripMatches()
    {
        _source.Enqueue("\x1b[?64c");
        // OSC 4 reply for slot 255 with 4-digit-hex form expanded from our 8-bit set value.
        _source.Enqueue("\x1b]4;255;rgb:abab/cdcd/efef\x1b\\\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.True(caps.Output.Color.TruecolorVerified);
    }

    [Fact]
    public async Task TruecolorVerified_TrueWhenPaletteEchoesTwoDigitFormat()
    {
        _source.Enqueue("\x1b[?64c");
        // Same value, two-digit-hex form (some terminals echo what they were sent).
        _source.Enqueue("\x1b]4;255;rgb:ab/cd/ef\x1b\\\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.True(caps.Output.Color.TruecolorVerified);
    }

    [Fact]
    public async Task TruecolorVerified_FalseWhenPaletteIsQuantized()
    {
        _source.Enqueue("\x1b[?64c");
        // Terminal quantizes our (AB, CD, EF) to the nearest 6x6x6 cube slot — different bytes
        // come back. Verification must reject.
        _source.Enqueue("\x1b]4;255;rgb:af/cf/ff\x1b\\\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.False(caps.Output.Color.TruecolorVerified);
    }

    [Fact]
    public async Task TruecolorVerified_FalseWhenNoResponse()
    {
        _source.Enqueue("\x1b[?64c");
        // No OSC 4 response — terminal didn't implement palette query. Verification fails.
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.False(caps.Output.Color.TruecolorVerified);
    }

    [Fact]
    public async Task DefaultForeground_PopulatedFromOsc10Response()
    {
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b]10;rgb:eeee/eeee/eeee\x1b\\\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.NotNull(caps.Output.Color.DefaultForeground);
        Assert.Equal(Color.FromRgb(0xEE, 0xEE, 0xEE), caps.Output.Color.DefaultForeground.Value);
    }

    [Fact]
    public async Task DefaultBackground_PopulatedFromOsc11Response()
    {
        _source.Enqueue("\x1b[?64c");
        // Black background — light-vs-dark detection would read this as "dark."
        _source.Enqueue("\x1b]11;rgb:0000/0000/0000\x1b\\\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.NotNull(caps.Output.Color.DefaultBackground);
        Assert.Equal(Color.FromRgb(0, 0, 0), caps.Output.Color.DefaultBackground.Value);
    }

    [Fact]
    public async Task DefaultCursorColor_PopulatedFromOsc12Response()
    {
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b]12;rgb:ffff/0000/0000\x1b\\\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.NotNull(caps.Output.Color.DefaultCursorColor);
        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0x00), caps.Output.Color.DefaultCursorColor.Value);
    }

    [Fact]
    public async Task DefaultColors_NullWhenNoResponses()
    {
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.Null(caps.Output.Color.DefaultForeground);
        Assert.Null(caps.Output.Color.DefaultBackground);
        Assert.Null(caps.Output.Color.DefaultCursorColor);
    }

    [Fact]
    public async Task AllProbes_TogetherInOneRoundTrip()
    {
        // The whole color-probe phase is a single round-trip — multiple queries, one DA1.
        // Enqueue every response interleaved before the final DA1 to mimic a chatty terminal.
        _source.Enqueue("\x1b[?64c");

        _source.Enqueue(
            "\x1b]4;255;rgb:abab/cdcd/efef\x1b\\" +
            "\x1b]10;rgb:cccc/cccc/cccc\x1b\\" +
            "\x1b]11;rgb:1212/3434/5656\x1b\\" +
            "\x1b]12;rgb:0000/ffff/0000\x1b\\" +
            "\x1b[?64c");

        await using var negotiator = BuildNegotiator();
        var caps = await negotiator.NegotiateAsync(Options());

        Assert.True(caps.Output.Color.TruecolorVerified);
        Assert.Equal(Color.FromRgb(0xCC, 0xCC, 0xCC), caps.Output.Color.DefaultForeground!.Value);
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), caps.Output.Color.DefaultBackground!.Value);
        Assert.Equal(Color.FromRgb(0x00, 0xFF, 0x00), caps.Output.Color.DefaultCursorColor!.Value);
    }

    [Fact]
    public async Task OptInsIgnored_SkipsColorProbe()
    {
        // OptInPolicy.Ignored disables both the DECRQM verification and the color probe — only
        // the identification phase runs.
        _source.Enqueue("\x1b[?64c"); // single DA1 sufficient

        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            OptIns = OptInPolicy.Ignored,
                                        });

        var written = await AllWrittenAsync();

        Assert.DoesNotContain("\x1b]4;", written);
        Assert.DoesNotContain("\x1b]10;?", written);
    }
}