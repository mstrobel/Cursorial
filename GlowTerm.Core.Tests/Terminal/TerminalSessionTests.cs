using GlowTerm.Core.Input;
using GlowTerm.Core.Input.Parsing;
using GlowTerm.Core.Output;
using GlowTerm.Core.Terminal;

namespace GlowTerm.Core.Tests.Terminal;

public class TerminalSessionTests
{
    private static TerminalSessionOptions FastTimeout(NegotiationOptions? negotiation = null) => new()
    {
        Negotiation = negotiation ?? new NegotiationOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(100),
            OptIns = OptInPolicy.Ignored,
        },
        EscapeAmbiguityTimeout = TimeSpan.FromMilliseconds(20),
    };

    // ---- Construction ----

    [Fact]
    public async Task OpenAsync_RejectsNullSource()
    {
        var sink = new InMemoryOutputByteSink();
        await using (sink)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await TerminalSession.OpenAsync(source: null!, sink, FastTimeout()));
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsNullSink()
    {
        var source = new InMemoryInputByteSource();
        await using (source)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await TerminalSession.OpenAsync(source, sink: null!, FastTimeout()));
        }
    }

    [Fact]
    public async Task OpenAsync_ReturnsSessionWithNegotiatedCapabilities()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            // Pre-populate the DA1 sentinel response.
            source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            Assert.Equal(TerminalFamily.Kitty, session.Capabilities.Terminal.Family);
            Assert.Same(sink, session.Output);
            Assert.NotNull(session.Input);
        }
    }

    // ---- Input flow ----

    [Fact]
    public async Task InputDevice_EmitsEventsForBytesArrivingAfterNegotiation()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            // Probe response, then user input.
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            source.Enqueue("hi");
            source.CompleteWriter();

            var events = new List<InputEvent>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await foreach (var ev in session.Input.ReadAllAsync(cts.Token))
                {
                    events.Add(ev);
                }
            }
            catch (OperationCanceledException) { }

            Assert.Equal(2, events.Count);
            Assert.Equal("h", new string(((KeyEvent)events[0]).Text.Span));
            Assert.Equal("i", new string(((KeyEvent)events[1]).Text.Span));
        }
    }

    // ---- Disposal ----

    [Fact]
    public async Task DisposeAsync_RestoresOptInsViaNegotiator()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\");
            source.Enqueue("\x1b[?64c");

            var options = new TerminalSessionOptions
            {
                Negotiation = new NegotiationOptions
                {
                    ProbeTimeout = TimeSpan.FromMilliseconds(100),
                    EnableMouseTracking = false,
                    EnableFocusEvents = true,
                    EnableBracketedPaste = true,
                    EnableKittyKeyboard = false,
                    EnableWin32InputMode = false,
                    EnableSynchronizedOutput = false,
                },
                EscapeAmbiguityTimeout = TimeSpan.FromMilliseconds(20),
            };

            var session = await TerminalSession.OpenAsync(source, sink, options);

            // Drain enables.
            await sink.ReadAllWrittenAsync();

            await session.DisposeAsync();

            var afterRestore = await sink.ReadAllWrittenAsync();
            var asString = System.Text.Encoding.ASCII.GetString(afterRestore);

            Assert.Contains("\x1b[?1004l", asString); // focus disable
            Assert.Contains("\x1b[?2004l", asString); // paste disable
        }
    }

    [Fact]
    public async Task DisposeAsync_DoesNotCloseByoTransports()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());
            await session.DisposeAsync();

            // After session disposal, the BYO sink is still writable — the session didn't
            // complete or dispose it.
            await sink.Writer.WriteAsync(new byte[] { 0x42 });
            var written = await sink.ReadAllWrittenAsync();
            Assert.Contains((byte)0x42, written);
        }
    }

    [Fact]
    public async Task DisposeAsync_StopsInputDevice()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            // Start an enumeration that sits waiting for bytes.
            var consumer = Task.Run(async () =>
            {
                var events = new List<InputEvent>();
                await foreach (var ev in session.Input.ReadAllAsync())
                {
                    events.Add(ev);
                }
                return events;
            });

            await Task.Delay(50); // let the pump start
            await session.DisposeAsync();

            // Consumer should terminate cleanly within a reasonable window.
            var events = await consumer.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(events);
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());
            await session.DisposeAsync();
            await session.DisposeAsync(); // should not throw
        }
    }

    // ---- Defaults ----

    [Fact]
    public async Task DefaultOptions_AppliedWhenNoneProvided()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            // Pre-populate so the default 500 ms timeout doesn't slow the test.
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink);

            Assert.NotNull(session.Capabilities);
            Assert.NotNull(session.Input);
        }
    }
}
