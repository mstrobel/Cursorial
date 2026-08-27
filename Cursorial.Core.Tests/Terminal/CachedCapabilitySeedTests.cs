using Cursorial.Input.Capabilities;
using Cursorial.Media;
using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// The capability-cache seed path (docs/cli-design.md §6, FW-1):
/// <see cref="VtTerminalNegotiator.ApplyCachedAsync"/> and
/// <see cref="TerminalSessionOptions.CachedCapabilities"/>. The contract under test — a seeded
/// session emits the SAME opt-in enable bytes a full negotiation's opt-in round emits, emits NO
/// probe queries, and restores with the SAME disable bytes (signal-path restore parity).
/// </summary>
public class CachedCapabilitySeedTests
{
    // Fully scripted "kitty" terminal: identification (XTVERSION + DA1), DECRQM verification
    // (every applied mode confirmed as set, status=1, + DA1), color round (no replies, + DA1).
    // Confirming every DECRQM keeps the cold run's post-verify applied set identical to its
    // pre-verify decision, so cold restore and warm restore must match byte-for-byte.
    private static void ScriptKittyTerminal(InMemoryInputByteSource source)
    {
        source.Enqueue("\x1bP>|kitty 0.34.1\x1b\\"); // XTVERSION → Family.Kitty
        source.Enqueue("\x1b[?64c");                 // identification DA1
        source.Enqueue("\x1b[?1006;1$y\x1b[?1002;1$y\x1b[?1003;1$y\x1b[?1004;1$y\x1b[?2004;1$y\x1b[?2026;1$y");
        source.Enqueue("\x1b[?64c");                 // verification DA1
        source.Enqueue("\x1b[?64c");                 // color-round DA1
    }

    private static NegotiationOptions DefaultOptions() => new()
                                                          {
                                                              ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                          };

    // The opt-in enable run a full negotiation emits for a kitty-family terminal under default
    // options: SGR mouse (1006), button-event (1002), any-event (1003), focus (1004), bracketed
    // paste (2004), Kitty keyboard push with the default flag set. No SGR-Pixels (off by
    // default), no Win32 input mode (family-gated to Windows).
    private static string ExpectedKittyEnableRun() =>
        "\x1b[?1006h\x1b[?1002h\x1b[?1003h\x1b[?1004h\x1b[?2004h" +
        $"\x1b[>{(uint) NegotiationOptions.DefaultKittyKeyboardFlags}u";

    private static async Task<(TerminalCapabilities Capabilities, string Written, byte[] Restore)> RunColdAsync(
        NegotiationOptions options)
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            ScriptKittyTerminal(source);

            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            var capabilities = await negotiator.NegotiateAsync(options);
            var written = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());
            var restore = negotiator.BuildRestoreSequence().ToArray();

            return (capabilities, written, restore);
        }
    }

    // ---- Enable-byte parity ----

    [Fact]
    public async Task ApplyCached_EmitsExactlyTheEnableBytesTheOptInRoundEmits()
    {
        var options = DefaultOptions();
        var (cold, coldWritten, _) = await RunColdAsync(options);

        // The cold run's opt-in round is a contiguous byte run in its output.
        Assert.Contains(ExpectedKittyEnableRun(), coldWritten);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            var seeded = await negotiator.ApplyCachedAsync(cold, options);
            var warmWritten = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

            // The seeded path writes the enable run and NOTHING else.
            Assert.Equal(ExpectedKittyEnableRun(), warmWritten);

            // The snapshot is returned verbatim.
            Assert.Same(cold, seeded);
        }
    }

    [Fact]
    public async Task ApplyCached_EmitsNoProbeQueries()
    {
        var options = DefaultOptions();
        var (cold, _, _) = await RunColdAsync(options);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.ApplyCachedAsync(cold, options);
            var written = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

            Assert.DoesNotContain("\x1b[>q", written);  // XTVERSION
            Assert.DoesNotContain("\x1b[16t", written); // cell size
            Assert.DoesNotContain("\x1b[18t", written); // text-area size
            Assert.DoesNotContain("\x1b[c", written);   // DA1 sentinel
            Assert.DoesNotContain("$p", written);       // DECRQM
            Assert.DoesNotContain("\x1b]", written);    // any OSC (color set/queries)
            Assert.DoesNotContain("\x1b[> q", written); // Kitty multiple-cursors query
        }
    }

    // ---- Restore parity ----

    [Fact]
    public async Task ApplyCached_RestoreSequenceMatchesColdNegotiationByteForByte()
    {
        var options = DefaultOptions();
        var (cold, _, coldRestore) = await RunColdAsync(options);

        Assert.NotEmpty(coldRestore); // sanity: the cold run had opt-ins to reverse

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.ApplyCachedAsync(cold, options);
            var warmRestore = negotiator.BuildRestoreSequence().ToArray();

            Assert.Equal(coldRestore, warmRestore);
        }
    }

    [Fact]
    public async Task ApplyCached_RestoreAsyncWritesTheMatchingDisables()
    {
        var options = DefaultOptions();
        var (cold, _, coldRestore) = await RunColdAsync(options);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.ApplyCachedAsync(cold, options);
            _ = await sink.ReadAllWrittenAsync(); // drop the enables

            await negotiator.RestoreAsync();
            var restoreWritten = await sink.ReadAllWrittenAsync();

            Assert.Equal(coldRestore, restoreWritten);

            await negotiator.DisposeAsync();
        }
    }

    // ---- Lifecycle ----

    [Fact]
    public async Task ApplyCached_CountsAsTheSingleNegotiation()
    {
        var options = DefaultOptions();
        var (cold, _, _) = await RunColdAsync(options);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.ApplyCachedAsync(cold, options);

            await Assert.ThrowsAsync<InvalidOperationException>(() => negotiator.NegotiateAsync(options));
            await Assert.ThrowsAsync<InvalidOperationException>(() => negotiator.ApplyCachedAsync(cold, options));
        }
    }

    [Fact]
    public async Task ApplyCached_WithOptInsIgnored_WritesNothingAndReportsNoOptIns()
    {
        var (cold, _, _) = await RunColdAsync(DefaultOptions());

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            var seeded = await negotiator.ApplyCachedAsync(cold, new NegotiationOptions
                                                                 {
                                                                     ProbeTimeout = TimeSpan.FromMilliseconds(200),
                                                                     OptIns = OptInPolicy.Ignored,
                                                                 });

            // Nothing on the wire at all.
            Assert.Empty(await sink.ReadAllWrittenAsync());

            // The snapshot's opt-in-derived claims are cleared (nothing was enabled), while
            // identification and family-gated passive capabilities survive.
            Assert.Equal(cold.Terminal, seeded.Terminal);
            Assert.Equal(ProtocolCapabilities.None, seeded.Input.Protocol);
            Assert.False(seeded.Input.Mouse.ButtonPress);
            Assert.False(seeded.Output.Protocol.SgrMouseEnable);
            Assert.False(seeded.Output.Protocol.KittyKeyboardPush);
            Assert.False(seeded.Output.Protocol.BracketedPasteEnable);
            Assert.True(seeded.Output.Protocol.ClipboardWrite); // family-gated, not an opt-in
            Assert.Equal(cold.Output.Color, seeded.Output.Color);
        }
    }

    // ---- The FW-2 preset over the seed path ----

    [Fact]
    public async Task ApplyCached_WithMinimalPromptPreset_EmitsOnlyFocusAndPasteEnables()
    {
        var (cold, _, _) = await RunColdAsync(DefaultOptions());

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.ApplyCachedAsync(cold, NegotiationOptions.MinimalPrompt);
            var written = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

            // No mouse modes, no Kitty push — focus + paste only (Win32 is family-gated off
            // for a kitty snapshot).
            Assert.Equal("\x1b[?1004h\x1b[?2004h", written);
        }
    }

    // ---- Session-level integration ----

    [Fact]
    public async Task Session_OpenedWithCachedCapabilities_SkipsProbesAndAdoptsTheSnapshot()
    {
        var options = DefaultOptions();
        var (cold, _, _) = await RunColdAsync(options);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            var sessionOptions = new TerminalSessionOptions
            {
                Negotiation = options,
                CachedCapabilities = cold,
                EscapeAmbiguityTimeout = TimeSpan.FromMilliseconds(20),
            };

            await using var session = await TerminalSession.OpenAsync(source, sink, sessionOptions);

            // The session's capabilities ARE the snapshot.
            Assert.Same(cold, session.Capabilities);

            var written = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

            // Same enable run a full negotiation's opt-in round emits — and no probes.
            Assert.Equal(ExpectedKittyEnableRun(), written);
            Assert.DoesNotContain("\x1b[>q", written);
            Assert.DoesNotContain("\x1b[c", written);
        }
    }

    [Fact]
    public async Task Session_OpenedWithCachedCapabilities_RestoresOnDisposeLikeAColdSession()
    {
        var options = DefaultOptions();
        var (cold, _, coldRestore) = await RunColdAsync(options);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            var sessionOptions = new TerminalSessionOptions
            {
                Negotiation = options,
                CachedCapabilities = cold,
                EscapeAmbiguityTimeout = TimeSpan.FromMilliseconds(20),
            };

            var session = await TerminalSession.OpenAsync(source, sink, sessionOptions);
            _ = await sink.ReadAllWrittenAsync(); // drop the enables

            await session.DisposeAsync();
            var restoreWritten = await sink.ReadAllWrittenAsync();

            Assert.Equal(coldRestore, restoreWritten);
        }
    }

    [Fact]
    public async Task Session_SerializedRoundTrip_SeedsIdenticallyToTheLiveSnapshot()
    {
        // The end-to-end cache shape: negotiate cold → serialize → deserialize → seed. The
        // seeded session must adopt an equal snapshot and emit the same enable run.
        var options = DefaultOptions();
        var (cold, _, _) = await RunColdAsync(options);

        Assert.True(TerminalCapabilitiesSerializer.TryDeserialize(
                        TerminalCapabilitiesSerializer.Serialize(cold), out var thawed));
        Assert.Equal(cold, thawed);

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            var sessionOptions = new TerminalSessionOptions
            {
                Negotiation = options,
                CachedCapabilities = thawed,
                EscapeAmbiguityTimeout = TimeSpan.FromMilliseconds(20),
            };

            await using var session = await TerminalSession.OpenAsync(source, sink, sessionOptions);

            Assert.Equal(cold, session.Capabilities);
            Assert.Equal(ExpectedKittyEnableRun(),
                         System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync()));
        }
    }

    // ---- FW-2: RefreshColorsFromCache — refresh the volatile default colours on a warm seed ----

    [Fact]
    public async Task ApplyCached_RefreshColorsFromCache_OverridesTheStaleBackground()
    {
        var (cold, _, _) = await RunColdAsync(DefaultOptions());

        // A cache written when the terminal was LIGHT: white background, near-black foreground.
        var stale = cold with
        {
            Output = cold.Output with
            {
                Color = cold.Output.Color with
                {
                    DefaultBackground = Color.FromRgb(0xFF, 0xFF, 0xFF),
                    DefaultForeground = Color.FromRgb(0x11, 0x11, 0x11),
                },
            },
        };

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            // The terminal is DARK now — it answers OSC 11 with a near-black background (bg only;
            // no OSC 10 foreground / OSC 12 cursor reply), then the DA1 sentinel ends the probe.
            source.Enqueue("\x1b]11;rgb:2e2e/3434/4848\x07");
            source.Enqueue("\x1b[?64c");

            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            var seeded = await negotiator.ApplyCachedAsync(
                stale, DefaultOptions() with { RefreshColorsFromCache = true });

            var written = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());
            Assert.Contains("\x1b]11;?", written); // the fresh background query went out

            // The fresh dark background overrides the stale white one...
            Assert.Equal(Color.FromRgb(0x2E, 0x34, 0x48), seeded.Output.Color.DefaultBackground);
            // ...while the foreground (no OSC 10 reply) keeps its cached value — no clobber to null.
            Assert.Equal(Color.FromRgb(0x11, 0x11, 0x11), seeded.Output.Color.DefaultForeground);
        }
    }

    [Fact]
    public async Task ApplyCached_RefreshColorsFromCache_KeepsCachedColorsWhenTerminalStaysSilent()
    {
        var (cold, _, _) = await RunColdAsync(DefaultOptions());

        var stale = cold with
        {
            Output = cold.Output with
            {
                Color = cold.Output.Color with { DefaultBackground = Color.FromRgb(0x20, 0x20, 0x20) },
            },
        };

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            // A terminal that doesn't support OSC colour queries: only the DA1 sentinel comes back.
            source.Enqueue("\x1b[?64c");

            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            var seeded = await negotiator.ApplyCachedAsync(
                stale, DefaultOptions() with { RefreshColorsFromCache = true });

            // No reply → the cached value is preserved (not clobbered to null).
            Assert.Equal(Color.FromRgb(0x20, 0x20, 0x20), seeded.Output.Color.DefaultBackground);
        }
    }

    [Fact]
    public async Task ApplyCached_WithoutRefreshFlag_DoesNotProbeColors()
    {
        // The default (flag off) still emits no OSC at all and returns the snapshot by reference.
        var (cold, _, _) = await RunColdAsync(DefaultOptions());

        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            var seeded = await negotiator.ApplyCachedAsync(cold, DefaultOptions());
            var written = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

            Assert.DoesNotContain("\x1b]", written); // no OSC colour query
            Assert.Same(cold, seeded);               // snapshot returned verbatim
        }
    }
}
