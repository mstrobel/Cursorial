using Cursorial.Input.Events;
using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

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
                    EnableExtendedMouseTracking = false,
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

    // ---- PauseIOAsync ----

    [Fact]
    public async Task PauseIOAsync_DelegatesToPausableSource_AndFlushesOutput()
    {
        var source = new PausableInMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            // Pre-write some buffered output that should be flushed by the pause.
            await sink.Writer.WriteAsync(new byte[] { 0xAB, 0xCD });

            await using (await session.PauseIOAsync())
            {
                Assert.Equal(1, source.PauseCount);
                Assert.Equal(1, source.ActivePauseCount);
                Assert.Equal(0, source.ResumeCount);

                // The bytes we wrote pre-pause should be visible on the wire.
                var written = await sink.ReadAllWrittenAsync();
                Assert.Contains((byte) 0xAB, written);
                Assert.Contains((byte) 0xCD, written);
            }

            Assert.Equal(0, source.ActivePauseCount);
            Assert.Equal(1, source.ResumeCount);
        }
    }

    [Fact]
    public async Task PauseIOAsync_NonPausableSource_StillReturnsDisposable()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            // No pause capability — should still complete and produce a usable handle.
            await using var handle = await session.PauseIOAsync();
            Assert.NotNull(handle);
        }
    }

    [Fact]
    public async Task PauseIOAsync_NestedScopes_DelegateOnceEachAndUnwindInOrder()
    {
        var source = new PausableInMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            await using (await session.PauseIOAsync())
            {
                Assert.Equal(1, source.ActivePauseCount);

                await using (await session.PauseIOAsync())
                {
                    // Each session call forwards to the source; the source owns the ref count.
                    Assert.Equal(2, source.PauseCount);
                    Assert.Equal(2, source.ActivePauseCount);
                }

                // Inner scope released one share — outer scope still wants it paused.
                Assert.Equal(1, source.ActivePauseCount);
            }

            // Both scopes released — source is fully resumed.
            Assert.Equal(0, source.ActivePauseCount);
            Assert.Equal(2, source.ResumeCount);
        }
    }

    [Fact]
    public async Task PauseIOAsync_ResumeIsIdempotent_DoubleDisposeOnHandleNoOps()
    {
        var source = new PausableInMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            var handle = await session.PauseIOAsync();
            await handle.DisposeAsync();
            await handle.DisposeAsync(); // second dispose — must not double-decrement on the source

            Assert.Equal(1, source.PauseCount);
            Assert.Equal(1, source.ResumeCount);
            Assert.Equal(0, source.ActivePauseCount);
        }
    }

    [Fact]
    public async Task PauseIOAsync_AfterDisposedSession_Throws()
    {
        var source = new PausableInMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());
            await session.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await session.PauseIOAsync());
        }
    }

    [Fact]
    public async Task PauseIOAsync_PauseFailure_LeavesNoOutstandingPause()
    {
        var source = new PausableInMemoryInputByteSource { ThrowOnPause = true };
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            await using var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.PauseIOAsync());

            // No partial pause state — a subsequent successful call behaves as a clean first pause.
            source.ThrowOnPause = false;

            await using (await session.PauseIOAsync())
            {
                Assert.Equal(1, source.PauseCount);
                Assert.Equal(1, source.ActivePauseCount);
            }

            Assert.Equal(0, source.ActivePauseCount);
        }
    }

    [Fact]
    public async Task PauseIOAsync_DisposeAfterPauseWithoutResume_Cleans()
    {
        // A caller may forget to dispose the resume handle before disposing the session.
        // Session disposal must still proceed cleanly — it tears down the source itself.
        var source = new PausableInMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            source.Enqueue("\x1b[?64c");

            var session = await TerminalSession.OpenAsync(source, sink, FastTimeout());

            _ = await session.PauseIOAsync();
            await session.DisposeAsync(); // must not deadlock or throw
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
