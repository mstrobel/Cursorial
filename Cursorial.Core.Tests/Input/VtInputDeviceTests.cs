using Cursorial.Core.Input;
using Cursorial.Core.Input.Parsing;
using Cursorial.Core.Tests.Terminal;

namespace Cursorial.Core.Tests.Input;

public class VtInputDeviceTests
{
    private readonly InMemoryInputByteSource _source = new();

    private VtInputDevice BuildDevice(TimeSpan? escTimeout = null) =>
        new(
            _source,
            InputCapabilities.None,
            mode: null,
            timeProvider: null,
            escapeAmbiguityTimeout: escTimeout ?? TimeSpan.FromMilliseconds(50));

    private static async Task<List<InputEvent>> CollectAsync(
        IAsyncInputDevice device,
        int count,
        TimeSpan timeout)
    {
        var collected = new List<InputEvent>();
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await foreach (var ev in device.ReadAllAsync(cts.Token))
            {
                collected.Add(ev);
                if (collected.Count >= count) break;
            }
        }
        catch (OperationCanceledException) { }

        return collected;
    }

    private static async Task<List<InputEvent>> CollectUntilCompletionAsync(
        IAsyncInputDevice device,
        TimeSpan timeout)
    {
        var collected = new List<InputEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var ev in device.ReadAllAsync(cts.Token))
            {
                collected.Add(ev);
            }
        }
        catch (OperationCanceledException) { }
        return collected;
    }

    // ---- Capabilities ----

    [Fact]
    public void Capabilities_ReturnsValuePassedToConstructor()
    {
        var caps = new InputCapabilities(
            Mouse: MouseCapabilities.None with { ButtonPress = true },
            Keyboard: KeyboardCapabilities.None,
            Pointer: PointerCapabilities.None,
            Protocol: ProtocolCapabilities.None);

        var device = new VtInputDevice(_source, caps);

        Assert.Same(caps, device.Capabilities);
    }

    // ---- Basic event flow ----

    [Fact]
    public async Task PrintableBytes_FlowAsKeyEvents()
    {
        _source.Enqueue("ab");
        _source.CompleteWriter();

        await using var device = BuildDevice();
        var events = await CollectUntilCompletionAsync(device, TimeSpan.FromSeconds(2));

        Assert.Equal(2, events.Count);
        Assert.Equal("a", new string(((KeyEvent)events[0]).Text.Span));
        Assert.Equal("b", new string(((KeyEvent)events[1]).Text.Span));
    }

    [Fact]
    public async Task CsiArrowSequence_DispatchesUpKey()
    {
        _source.Enqueue("\x1b[A");
        _source.CompleteWriter();

        await using var device = BuildDevice();
        var events = await CollectUntilCompletionAsync(device, TimeSpan.FromSeconds(2));

        var k = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(Key.UpArrow, k.Key);
    }

    [Fact]
    public async Task SourceCompletion_TerminatesEnumerationCleanly()
    {
        _source.Enqueue("x");
        _source.CompleteWriter();

        await using var device = BuildDevice();
        var events = await CollectUntilCompletionAsync(device, TimeSpan.FromSeconds(2));

        Assert.Single(events);
        // No hang — enumeration completes naturally without timeout.
    }

    // ---- Bare-ESC ambiguity ----

    [Fact]
    public async Task LoneEscByte_BecomesEscapeKeyAfterTimeout()
    {
        // Don't complete the writer — we want the timeout to be the only thing that flushes.
        _source.Enqueue([0x1B]);

        await using var device = BuildDevice(escTimeout: TimeSpan.FromMilliseconds(20));
        var events = await CollectAsync(device, count: 1, timeout: TimeSpan.FromSeconds(2));

        var k = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(Key.Escape, k.Key);
    }

    [Fact]
    public async Task EscFollowedByCsiSequence_DispatchesAsSequenceNotEscape()
    {
        // The full CSI arrives in one chunk, well before any timeout fires.
        _source.Enqueue("\x1b[A");
        _source.CompleteWriter();

        await using var device = BuildDevice(escTimeout: TimeSpan.FromMilliseconds(50));
        var events = await CollectUntilCompletionAsync(device, TimeSpan.FromSeconds(2));

        var k = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(Key.UpArrow, k.Key);
    }

    [Fact]
    public async Task EscThenLaterSequence_BothEventsEmitted()
    {
        // ESC, wait long enough for the timeout, then a CSI sequence.
        _source.Enqueue([0x1B]);

        await using var device = BuildDevice(escTimeout: TimeSpan.FromMilliseconds(30));

        var consumer = Task.Run(async () => await CollectAsync(device, count: 2, timeout: TimeSpan.FromSeconds(2)));

        // Give the timeout time to fire and the bare-ESC to flush.
        await Task.Delay(80);
        _source.Enqueue("\x1b[B"); // Down arrow.
        _source.CompleteWriter();

        var events = await consumer;

        Assert.Equal(2, events.Count);
        Assert.Equal(Key.Escape, ((KeyEvent)events[0]).Key);
        Assert.Equal(Key.DownArrow, ((KeyEvent)events[1]).Key);
    }

    // ---- Lifecycle ----

    [Fact]
    public async Task ReadAllAsync_TwiceThrows()
    {
        _source.Enqueue("x");
        _source.CompleteWriter();

        await using var device = BuildDevice();

        // Drain the first enumeration.
        await foreach (var _ in device.ReadAllAsync()) { }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in device.ReadAllAsync()) { }
        });
    }

    [Fact]
    public async Task ReadAllAsync_AfterDisposeThrows()
    {
        var device = BuildDevice();
        await device.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in device.ReadAllAsync()) { }
        });
    }

    [Fact]
    public async Task DisposeAsync_StopsPumpAndTerminatesEnumeration()
    {
        // No bytes — pump is just sitting on a read.
        var device = BuildDevice();

        var consumer = Task.Run(async () =>
        {
            var events = new List<InputEvent>();
            await foreach (var ev in device.ReadAllAsync())
            {
                events.Add(ev);
            }
            return events;
        });

        // Let the pump start.
        await Task.Delay(50);

        await device.DisposeAsync();

        var events = await consumer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(events);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var device = BuildDevice();
        await device.DisposeAsync();
        await device.DisposeAsync(); // should not throw
    }

    [Fact]
    public async Task DisposeAsync_BeforeAnyEnumeration_DoesNotStartPump()
    {
        var device = BuildDevice();
        await device.DisposeAsync();
        // No way to directly assert pump didn't start — but absence of hang or throw is the
        // observable property. Disposing an unstarted device shouldn't hang on pump await.
    }

    // ---- Constructor validation ----

    [Fact]
    public void Constructor_RejectsNullSource()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new VtInputDevice(source: null!, InputCapabilities.None));
    }

    [Fact]
    public void Constructor_RejectsNullCapabilities()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new VtInputDevice(_source, capabilities: null!));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VtInputDevice(_source, InputCapabilities.None, escapeAmbiguityTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VtInputDevice(_source, InputCapabilities.None, escapeAmbiguityTimeout: TimeSpan.FromMilliseconds(-1)));
    }

    // ---- Mode propagation ----

    [Fact]
    public void Mode_PropertyReturnsConstructorMode()
    {
        var mode = new VtInputMode { BracketedPasteEnabled = true };
        var device = new VtInputDevice(_source, InputCapabilities.None, mode);

        Assert.Same(mode, device.Mode);
    }

    [Fact]
    public async Task EnqueueExternalEvent_DeliversAlongsideByteStreamEvents()
    {
        // Resize monitors and similar out-of-band sources push events directly into the
        // device's stream via EnqueueExternalEvent. Verify the event appears in the consumer's
        // enumeration and is ordered relative to byte-stream events as enqueued.
        await using var device = new VtInputDevice(_source, InputCapabilities.None);

        device.EnqueueExternalEvent(new ResizeEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Rows = 24,
            Columns = 80,
        });
        _source.Enqueue("a");
        _source.CompleteWriter();

        var events = await CollectAsync(device, 2, TimeSpan.FromSeconds(2));
        var resize = Assert.IsType<ResizeEvent>(events[0]);
        Assert.Equal(24, resize.Rows);
        Assert.Equal(80, resize.Columns);

        var key = Assert.IsType<KeyEvent>(events[1]);
        Assert.Equal(Key.Character, key.Key);
    }

    [Fact]
    public async Task EnqueueExternalEvent_AfterDisposalIsNoOp()
    {
        var device = new VtInputDevice(_source, InputCapabilities.None);
        await device.DisposeAsync();

        // Must not throw even though the channel is completed.
        device.EnqueueExternalEvent(new ResizeEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Rows = 24,
            Columns = 80,
        });
    }

    [Fact]
    public async Task X10MouseEncodingOnMode_EnablesClassifierFraming()
    {
        // With MouseEncoding = X10, the pump must mirror the flag onto the classifier so the
        // X10 wire form (CSI M followed by three raw bytes) is framed correctly and decoded
        // into a MouseEvent rather than misinterpreted as printable text.
        var mode = new VtInputMode { MouseEncoding = MouseEncoding.X10 };
        await using var device = new VtInputDevice(_source, InputCapabilities.None, mode);

        // X10 left-button press at column 5, row 10: bytes 0x20, 0x26, 0x2B.
        _source.Enqueue([0x1B, (byte)'[', (byte)'M', 0x20, 0x26, 0x2B]);
        _source.CompleteWriter();

        var events = await CollectAsync(device, 1, TimeSpan.FromSeconds(2));
        var m = Assert.IsType<MouseEvent>(Assert.Single(events));
        Assert.Equal(MouseEventKind.ButtonDown, m.Kind);
        Assert.Equal(MouseButton.Left, m.Button);
    }
}
