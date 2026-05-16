using Cursorial.Input.Parsing;
using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

public class VtTerminalNegotiatorDecRqmTests
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

    [Fact]
    public async Task NegotiateAsync_EmitsDecRqmQueriesForAppliedModes()
    {
        // Two DA1 sentinels — one for each probe phase. Verification phase has no DECRQM
        // responses in between; the negotiator should still complete (timeout-free since the
        // sentinel arrives).
        _source.Enqueue("\x1b[?64c"); // identification DA1
        _source.Enqueue("\x1b[?64c"); // verification DA1

        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                            EnableExtendedMouseTracking = true,
                                            EnableFocusEvents = true,
                                            EnableBracketedPaste = true,
                                            EnableMouseButtons = false,
                                            EnableMouseButtonTracking = false,
                                            EnableMouseTracking = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false
                                        });

        var written = await AllWrittenAsync();

        // DECRQM queries for the three opt-ins we applied (mouse = 1006 + 1002, focus = 1004,
        // paste = 2004) — order isn't strict but each query must appear.
        Assert.Contains("\x1b[?1006$p", written);
        Assert.Contains("\x1b[?1002$p", written);
        Assert.Contains("\x1b[?1004$p", written);
        Assert.Contains("\x1b[?2004$p", written);

        // Lesser/redundant mouse modes shouldn't be queried.
        Assert.DoesNotContain("\x1b[?1000$p", written);
        Assert.DoesNotContain("\x1b[?2026$p", written);

        // Modes we didn't enable shouldn't be queried.
        Assert.DoesNotContain("\x1b[?1003$p", written);
        Assert.DoesNotContain("\x1b[?2026$p", written);
    }

    [Fact]
    public async Task NegotiateAsync_EmitsDecRqmQueriesForBasicMouseOnly()
    {
        // Two DA1 sentinels — one for each probe phase. Verification phase has no DECRQM
        // responses in between; the negotiator should still complete (timeout-free since the
        // sentinel arrives).
        _source.Enqueue("\x1b[?64c"); // identification DA1
        _source.Enqueue("\x1b[?64c"); // verification DA1

        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                            EnableFocusEvents = false,
                                            EnableBracketedPaste = false,
                                            EnableMouseButtons = true,
                                            EnableMouseTracking = false,
                                            EnableMouseButtonTracking = true,
                                            EnableExtendedMouseTracking = false,
                                            EnableKittyKeyboard = false,
                                            EnableWin32InputMode = false,
                                            EnableSynchronizedOutput = false
                                        });

        var written = await AllWrittenAsync();

        // DECRQM queries for the three opt-ins we applied (mouse = 1000 + 1002) —
        // order isn't strict but each query must appear.
        Assert.Contains("\x1b[?1000$p", written);
        Assert.Contains("\x1b[?1002$p", written);

        // Modes we didn't enable shouldn't be queried.
        Assert.DoesNotContain("\x1b[?1003$p", written);
        Assert.DoesNotContain("\x1b[?1004$p", written);
        Assert.DoesNotContain("\x1b[?1006$p", written);
        Assert.DoesNotContain("\x1b[?2004$p", written);
        Assert.DoesNotContain("\x1b[?2026$p", written);
    }

    [Fact]
    public async Task DecRqmStatus0_ClearsTheAppliedBit()
    {
        // Terminal reports SGR mouse as unrecognized (status=0) — capability must reflect that.
        _source.Enqueue("\x1b[?64c");                             // identification DA1
        _source.Enqueue("\x1b[?1006;0$y\x1b[?1002;0$y\x1b[?64c"); // verification responses + DA1

        await using var negotiator = BuildNegotiator();

        TerminalCapabilities caps =
            await negotiator.NegotiateAsync(new NegotiationOptions
                                            {
                                                ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                EnableExtendedMouseTracking = true,
                                                EnableFocusEvents = false,
                                                EnableBracketedPaste = false,
                                                EnableMouseTracking = false,
                                                EnableKittyKeyboard = false,
                                                EnableWin32InputMode = false,
                                                EnableSynchronizedOutput = false
                                            });

        // SGR mouse came back unsupported → realized capabilities reflect no mouse tracking,
        // even though the negotiator emitted the DECSET sequences.
        // MouseCapabilities collapses to None when the MouseTracking bit is cleared — no press,
        // no drag, no wheel.
        Assert.False(caps.Input.Mouse.ButtonPress);
        Assert.False(caps.Input.Mouse.Drag);
        Assert.False(caps.Input.Mouse.Wheel);
    }

    [Fact]
    public async Task DecRqmStatus1_KeepsAppliedBit()
    {
        // Terminal confirms the mode is set (status=1) — capability stays.
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?1006;1$y\x1b[?1002;1$y\x1b[?64c");

        await using var negotiator = BuildNegotiator();

        TerminalCapabilities caps =
            await negotiator.NegotiateAsync(new NegotiationOptions
                                            {
                                                ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                EnableExtendedMouseTracking = true,
                                                EnableFocusEvents = false,
                                                EnableBracketedPaste = false,
                                                EnableMouseTracking = false,
                                                EnableKittyKeyboard = false,
                                                EnableWin32InputMode = false,
                                                EnableSynchronizedOutput = false
                                            });

        Assert.True(caps.Input.Mouse.ButtonPress);
    }

    [Fact]
    public async Task DecRqmStatus3_TreatsAsConfirmed()
    {
        // Status 3 = permanently set. Treat the same as set.
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?1004;3$y\x1b[?64c");

        await using var negotiator = BuildNegotiator();

        TerminalCapabilities caps =
            await negotiator.NegotiateAsync(new NegotiationOptions
                                            {
                                                ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                EnableExtendedMouseTracking = false,
                                                EnableFocusEvents = true,
                                                EnableBracketedPaste = false,
                                                EnableMouseTracking = false,
                                                EnableKittyKeyboard = false,
                                                EnableWin32InputMode = false,
                                                EnableSynchronizedOutput = false
                                            });

        Assert.True(caps.Input.Protocol.FocusEvents);
    }

    [Fact]
    public async Task DecRqmStatus2_ClearsTheAppliedBit()
    {
        // Status 2 = reset. The terminal acknowledged the mode but didn't set it (our DECSET
        // didn't stick). Clear the cap.
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?2004;2$y\x1b[?64c");

        await using var negotiator = BuildNegotiator();

        TerminalCapabilities caps =
            await negotiator.NegotiateAsync(new NegotiationOptions
                                            {
                                                ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                EnableExtendedMouseTracking = false,
                                                EnableFocusEvents = false,
                                                EnableBracketedPaste = true,
                                                EnableMouseTracking = false,
                                                EnableKittyKeyboard = false,
                                                EnableWin32InputMode = false,
                                                EnableSynchronizedOutput = false
                                            });

        Assert.False(caps.Input.Protocol.BracketedPaste);
    }

    [Fact]
    public async Task NoDecRqmResponse_LeavesAppliedBitAlone()
    {
        // Terminal doesn't respond to DECRQM at all — the verification phase times out (the
        // second DA1 still arrives), and we leave the cap as-is. "Silence" is not "unsupported."
        _source.Enqueue("\x1b[?64c");
        _source.Enqueue("\x1b[?64c"); // only the second DA1, no DECRQM responses

        await using var negotiator = BuildNegotiator();

        TerminalCapabilities caps =
            await negotiator.NegotiateAsync(new NegotiationOptions
                                            {
                                                ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                EnableExtendedMouseTracking = true,
                                                EnableFocusEvents = false,
                                                EnableBracketedPaste = false,
                                                EnableMouseTracking = false,
                                                EnableKittyKeyboard = false,
                                                EnableWin32InputMode = false,
                                                EnableSynchronizedOutput = false
                                            });

        // Mouse tracking remains advertised — we have no evidence it doesn't work.
        Assert.True(caps.Input.Mouse.ButtonPress);
    }

    [Fact]
    public async Task NoAppliedModes_SkipsVerificationPhase()
    {
        // With all opt-ins disabled there's nothing to verify; the negotiator must not emit
        // DECRQM queries and must not wait for a second DA1.
        _source.Enqueue("\x1b[?64c"); // single DA1 — no second one needed

        await using var negotiator = BuildNegotiator();

        await negotiator.NegotiateAsync(new NegotiationOptions
                                        {
                                            ProbeTimeout = TimeSpan.FromMilliseconds(100),
                                            OptIns = OptInPolicy.Ignored
                                        });

        var written = await AllWrittenAsync();
        Assert.DoesNotContain("$p", written);
    }
}