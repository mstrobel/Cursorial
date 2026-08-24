using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

/// <summary>
/// The System.Console smkx repair + the Kitty stack-balance fix (the "kitty custom key bindings die
/// until <c>reset</c>" investigation, 2026-08-24). Two distinct leaks:
/// <para>
/// (1) On the Unix runtime, the process's FIRST actual Console write (<c>Console.Out</c>/<c>Error</c>)
/// or cursor/window API call runs the BCL's one-time terminal init, which emits terminfo smkx
/// (<c>CSI ? 1 h</c> + <c>ESC =</c> — DECCKM/DECKPAM application modes) and NEVER restores it
/// (verified empirically on .NET 10/macOS; property reads do not trigger it). Session restore now
/// emits the rmkx pair (<c>CSI ? 1 l</c> + <c>ESC &gt;</c>) unconditionally — endwin parity.
/// </para>
/// <para>
/// (2) <see cref="TerminalSession.RenegotiateAsync"/> neutralizes the OLD negotiator by discarding
/// its restore sequence — correct for idempotent DECSET toggles, but the Kitty keyboard opt-in is
/// STACK-shaped: old push + new push with only one pop at disposal stranded an entry per
/// renegotiation. The session now emits one explicit pop per renegotiation.
/// </para>
/// </summary>
public class ConsoleSmkxRepairTests
{
    private const string RmkxPair = "\x1b[?1l\x1b>";

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

    private static string KittyPush() => $"\x1b[>{(uint) NegotiationOptions.DefaultKittyKeyboardFlags}u";
    private const string KittyPop = "\x1b[<u";

    private static int Count(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact] // (1) — the async restore path emits the rmkx pair, after the opt-in disables
    public async Task RestoreAsync_EmitsRmkxRepairPair()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            ScriptKittyTerminal(source);

            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.NegotiateAsync(DefaultOptions());
            _ = await sink.ReadAllWrittenAsync(); // drain probes + enables

            await negotiator.RestoreAsync();
            var restore = System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

            Assert.Contains(RmkxPair, restore);
            Assert.True(restore.IndexOf(RmkxPair, StringComparison.Ordinal) >
                        restore.IndexOf("\x1b[?2004l", StringComparison.Ordinal),
                        "the rmkx repair must follow the opt-in disables");
        }
    }

    [Fact] // (1) — the synchronous signal-path restore emits the same pair (parity)
    public async Task BuildRestoreSequence_EmitsRmkxRepairPair()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            ScriptKittyTerminal(source);

            await using var negotiator = new VtTerminalNegotiator(
                source, sink, mode: null, timeProvider: null, environmentReader: new StubEnvironmentReader());

            await negotiator.NegotiateAsync(DefaultOptions());

            var restore = System.Text.Encoding.ASCII.GetString(negotiator.BuildRestoreSequence().Span);
            Assert.Contains(RmkxPair, restore);
        }
    }

    [Fact] // (2) — a renegotiated session's Kitty pushes and pops balance across the whole lifetime
    public async Task Renegotiate_KittyStackPushesAndPopsBalance()
    {
        var source = new InMemoryInputByteSource();
        var sink = new InMemoryOutputByteSink();
        await using (source)
        await using (sink)
        {
            ScriptKittyTerminal(source);

            var sessionOptions = new TerminalSessionOptions
            {
                Negotiation = DefaultOptions(),
                EscapeAmbiguityTimeout = TimeSpan.FromMilliseconds(20),
            };

            var wire = new System.Text.StringBuilder();

            var session = await TerminalSession.OpenAsync(source, sink, sessionOptions);
            wire.Append(System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync()));

            ScriptKittyTerminal(source); // the renegotiation's probe responses
            await session.RenegotiateAsync();
            wire.Append(System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync()));

            await session.DisposeAsync();
            wire.Append(System.Text.Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync()));

            var bytes = wire.ToString();
            var pushes = Count(bytes, KittyPush());
            var pops = Count(bytes, KittyPop);

            Assert.Equal(2, pushes); // open + renegotiate
            Assert.Equal(pushes, pops); // the mid-renegotiation pop + the disposal pop
        }
    }
}
