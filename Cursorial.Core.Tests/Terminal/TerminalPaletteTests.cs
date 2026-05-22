using System.Text;

using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Terminal;

namespace Cursorial.Tests.Terminal;

public class TerminalPaletteTests
{
    private static OutputCapabilities CapsWith(bool oscPaletteSet) =>
        OutputCapabilities.None with
        {
            Color = OutputCapabilities.None.Color with { OscPaletteSet = oscPaletteSet }
        };

    private static DeviceResponseEvent PaletteResponse(int index, string rgbHex)
    {
        // rgbHex like "abab/cdcd/efef"
        var payload = Encoding.ASCII.GetBytes($"{index};rgb:{rgbHex}");
        return new DeviceResponseEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = DeviceResponseKind.PaletteColor,
            Payload = payload,
        };
    }

    // ---- Capability gating ----

    [Fact]
    public void IsSupported_ReflectsCapability()
    {
        var sinkUnsupported = new InMemoryOutputByteSink();
        var sinkSupported = new InMemoryOutputByteSink();

        Assert.False(new TerminalPalette(sinkUnsupported, CapsWith(false)).IsSupported);
        Assert.True(new TerminalPalette(sinkSupported, CapsWith(true)).IsSupported);
    }

    [Fact]
    public async Task Set_OnUnsupportedTerminal_IsNoOp()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(false));

        palette.Set(1, Color.FromRgb(255, 0, 0));
        var bytes = await sink.ReadAllWrittenAsync();

        Assert.Empty(bytes);
        Assert.Empty(palette.ModifiedIndices);
    }

    [Fact]
    public async Task Query_OnUnsupportedTerminal_ReturnsNullWithoutWriting()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(false));

        var result = await palette.QueryAsync(42, TimeSpan.FromMilliseconds(50));
        var bytes = await sink.ReadAllWrittenAsync();

        Assert.Null(result);
        Assert.Empty(bytes);
    }

    // ---- Set / Reset ----

    [Fact]
    public async Task Set_WritesOscFourSequence_AndTracksModification()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        palette.Set(7, Color.FromRgb(0x12, 0x34, 0x56));
        var ascii = Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

        Assert.Equal("\x1b]4;7;rgb:1212/3434/5656\x1b\\", ascii);
        Assert.Contains((byte) 7, palette.ModifiedIndices);
    }

    [Fact]
    public void Set_NonRgbColor_Throws()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        Assert.Throws<ArgumentException>(() => palette.Set(0, Color.FromPalette(5)));
        Assert.Throws<ArgumentException>(() => palette.Set(0, Color.Default));
    }

    [Fact]
    public async Task Reset_WritesOscOneZeroFour_AndUntracksIndex()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        palette.Set(7, Color.FromRgb(0x10, 0x20, 0x30));
        await sink.ReadAllWrittenAsync(); // drain
        palette.Reset(7);

        var ascii = Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());
        Assert.Equal("\x1b]104;7\x1b\\", ascii);
        Assert.Empty(palette.ModifiedIndices);
    }

    [Fact]
    public async Task ResetAll_WritesOscOneZeroFour_AndClearsTracking()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        palette.Set(1, Color.FromRgb(0xAA, 0xBB, 0xCC));
        palette.Set(2, Color.FromRgb(0xDD, 0xEE, 0xFF));
        await sink.ReadAllWrittenAsync();

        palette.ResetAll();
        var ascii = Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

        Assert.Equal("\x1b]104\x1b\\", ascii);
        Assert.Empty(palette.ModifiedIndices);
    }

    // ---- Dispose restores modifications ----

    [Fact]
    public async Task Dispose_EmitsOscOneZeroFourForAllModifiedIndices()
    {
        var sink = new InMemoryOutputByteSink();
        var palette = new TerminalPalette(sink, CapsWith(true));

        palette.Set(1, Color.FromRgb(0xAA, 0xBB, 0xCC));
        palette.Set(7, Color.FromRgb(0xDD, 0xEE, 0xFF));
        palette.Set(42, Color.FromRgb(0x12, 0x34, 0x56));
        await sink.ReadAllWrittenAsync(); // drain set sequences

        palette.Dispose();
        var ascii = Encoding.ASCII.GetString(await sink.ReadAllWrittenAsync());

        // Expect a single OSC 104 with all indices listed (the order within HashSet isn't
        // guaranteed; verify each index appears).
        Assert.StartsWith("\x1b]104;", ascii);
        Assert.EndsWith("\x1b\\", ascii);
        Assert.Contains(";1", ascii);
        Assert.Contains(";7", ascii);
        Assert.Contains(";42", ascii);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sink = new InMemoryOutputByteSink();
        var palette = new TerminalPalette(sink, CapsWith(true));

        palette.Dispose();
        palette.Dispose(); // no-throw
    }

    [Fact]
    public async Task Dispose_OnUnmodifiedPalette_WritesNothing()
    {
        var sink = new InMemoryOutputByteSink();
        var palette = new TerminalPalette(sink, CapsWith(true));
        palette.Dispose();

        Assert.Empty(await sink.ReadAllWrittenAsync());
    }

    // ---- Query / response routing ----

    [Fact]
    public async Task QueryAsync_ResolvesOnMatchingResponse()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        var queryTask = palette.QueryAsync(33, TimeSpan.FromSeconds(1));
        await Task.Yield(); // give the query a chance to register its TCS

        palette.OnInputEvent(PaletteResponse(33, "abab/cdcd/efef"));

        var result = await queryTask;
        Assert.NotNull(result);
        Assert.Equal(ColorKind.Rgb, result!.Value.Kind);
        Assert.Equal(0xAB, result.Value.Red);
        Assert.Equal(0xCD, result.Value.Green);
        Assert.Equal(0xEF, result.Value.Blue);
    }

    [Fact]
    public async Task QueryAsync_IgnoresResponsesForOtherIndices()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        var queryTask = palette.QueryAsync(5, TimeSpan.FromMilliseconds(150));
        palette.OnInputEvent(PaletteResponse(6, "0000/0000/0000")); // wrong index

        var result = await queryTask;
        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_Timeout_ReturnsNull()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        var result = await palette.QueryAsync(99, TimeSpan.FromMilliseconds(50));
        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_ParsesShortHexChannels()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        var queryTask = palette.QueryAsync(1, TimeSpan.FromSeconds(1));
        await Task.Yield();

        // Single-digit channels: 'f' should expand to 0xFF (duplicated nibble).
        palette.OnInputEvent(PaletteResponse(1, "f/0/8"));

        var result = await queryTask;
        Assert.NotNull(result);
        Assert.Equal(0xFF, result!.Value.Red);
        Assert.Equal(0x00, result.Value.Green);
        Assert.Equal(0x88, result.Value.Blue);
    }

    [Fact]
    public void OnInputEvent_IgnoresNonPaletteEvents()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        // Should not throw or otherwise affect state.
        palette.OnInputEvent(new ResizeEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Columns = 80,
            Rows = 24,
        });

        palette.OnInputEvent(new DeviceResponseEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = DeviceResponseKind.PrimaryDeviceAttributes,
            Payload = Encoding.ASCII.GetBytes("64;1"),
        });
    }

    [Fact]
    public async Task QueryAsync_OverlappingQueryForSameIndex_CancelsFirst()
    {
        var sink = new InMemoryOutputByteSink();
        using var palette = new TerminalPalette(sink, CapsWith(true));

        // First query — never gets a response.
        var first = palette.QueryAsync(7, TimeSpan.FromSeconds(2));
        await Task.Yield();

        // Second query for the same index supersedes it.
        var second = palette.QueryAsync(7, TimeSpan.FromSeconds(2));
        await Task.Yield();
        palette.OnInputEvent(PaletteResponse(7, "1111/2222/3333"));

        // The first task should resolve to null (canceled mid-flight, treated as no-result).
        Assert.Null(await first);
        Assert.NotNull(await second);
    }
}
