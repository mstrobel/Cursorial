using System.IO.Pipelines;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Tests.Terminal;

namespace Cursorial.Tests.Input;

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

        // ReSharper disable once AccessToDisposedClosure
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

    // ---- ESC-prefix Alt+<control key> across the real ambiguity timer ----

    [Fact]
    public async Task EscPlusCarriageReturn_BecomesAltEnter_WithNoTrailingEscape()
    {
        // The end-to-end shape of the bug: `ESC CR` (Alt+Enter on any xterm-family terminal)
        // used to surface as a bare Enter and then — once the ambiguity timer fired on the ESC
        // the classifier was still holding — a phantom Escape that unwound a focus scope. Leave
        // the writer open so the timer, not completion, is what would produce that second event.
        _source.Enqueue([0x1B, 0x0D]);

        await using var device = BuildDevice(escTimeout: TimeSpan.FromMilliseconds(20));

        // Ask for two events; the collector gives up at its own timeout, well past the 20 ms
        // flush window, so a second event would have had ample opportunity to arrive.
        var events = await CollectAsync(device, count: 2, timeout: TimeSpan.FromMilliseconds(400));

        var k = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(Key.Enter, k.Key);
        Assert.Equal(KeyModifiers.Alt, k.Modifiers);
    }

    [Fact]
    public async Task DoubleEscByte_BecomesAltEscapeAfterTimeout()
    {
        _source.Enqueue([0x1B, 0x1B]);

        await using var device = BuildDevice(escTimeout: TimeSpan.FromMilliseconds(20));
        var events = await CollectAsync(device, count: 1, timeout: TimeSpan.FromSeconds(2));

        var k = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(Key.Escape, k.Key);
        Assert.Equal(KeyModifiers.Alt, k.Modifiers);
    }

    [Fact]
    public async Task TwoEscPressesSeparatedByTheIdleWindow_StayTwoPlainEscapes()
    {
        // The guard on Alt+Esc decoding: only a *burst* `ESC ESC` is one keypress. Two real
        // Escape presses are separated by more than the ambiguity window, so the first commits
        // on its own timer long before the second byte arrives.
        _source.Enqueue([0x1B]);

        await using var device = BuildDevice(escTimeout: TimeSpan.FromMilliseconds(30));

        // ReSharper disable once AccessToDisposedClosure
        var consumer = Task.Run(async () => await CollectAsync(device, count: 2, timeout: TimeSpan.FromSeconds(2)));

        await Task.Delay(80);
        _source.Enqueue([0x1B]);
        _source.CompleteWriter();

        var events = await consumer;

        Assert.Equal(2, events.Count);
        Assert.All(
            events.OfType<KeyEvent>(),
            k =>
            {
                Assert.Equal(Key.Escape, k.Key);
                Assert.Equal(KeyModifiers.None, k.Modifiers);
            });
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

    // ---- Idle pump does not wake on the ambiguity window ----

    /// <summary>
    /// Delegates everything to system time but counts <see cref="TimeProvider.CreateTimer"/>
    /// calls. The pump's only timer use is the bare-ESC ambiguity <c>Task.Delay</c>, so the
    /// count is a direct observation of how often the timer is armed.
    /// </summary>
    private sealed class TimerCountingTimeProvider : TimeProvider
    {
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timersCreated);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task GroundStateIdle_ArmsNoAmbiguityTimer()
    {
        var time = new TimerCountingTimeProvider();
        await using var device = new VtInputDevice(
            _source,
            InputCapabilities.None,
            mode: null,
            time,
            escapeAmbiguityTimeout: TimeSpan.FromMilliseconds(20));

        // Plain printables — the classifier returns to Ground within the batch, so the pump
        // must park on the pipe read alone without ever arming the ambiguity timer.
        _source.Enqueue("abc");
        var events = await CollectAsync(device, count: 3, timeout: TimeSpan.FromSeconds(2));
        Assert.Equal(3, events.Count);

        // Idle well past several ambiguity windows; a timeout-polling pump would arm repeatedly.
        await Task.Delay(150);
        Assert.Equal(0, time.TimersCreated);
    }

    [Fact]
    public async Task LoneEsc_ArmsTimerOnlyWhileSequencePending()
    {
        var time = new TimerCountingTimeProvider();
        await using var device = new VtInputDevice(
            _source,
            InputCapabilities.None,
            mode: null,
            time,
            escapeAmbiguityTimeout: TimeSpan.FromMilliseconds(20));

        _source.Enqueue([0x1B]);
        var events = await CollectAsync(device, count: 1, timeout: TimeSpan.FromSeconds(2));

        var k = Assert.IsType<KeyEvent>(Assert.Single(events));
        Assert.Equal(Key.Escape, k.Key);

        // The pending lone ESC armed the timer (the flush that produced the Escape event
        // happens before the event is written, so the count is settled by now).
        var armedWhilePending = time.TimersCreated;
        Assert.True(armedWhilePending >= 1);

        // After the flush the classifier is back at Ground; further idle time must not re-arm.
        await Task.Delay(150);
        Assert.Equal(armedWhilePending, time.TimersCreated);
    }

    // ---- Disposal liveness against a cancellation-deaf reader ----

    /// <summary>
    /// A worst-case BYO reader: <c>ReadAsync</c> never completes and honors neither the
    /// cancellation token nor <see cref="PipeReader.CancelPendingRead"/>. The idle pump parks
    /// on this read with no ambiguity timer armed, so disposal liveness rests entirely on the
    /// device's bounded pump-abandon path.
    /// </summary>
    private sealed class DeafByteSource : IInputByteSource
    {
        private sealed class DeafPipeReader : PipeReader
        {
            private readonly TaskCompletionSource<ReadResult> _never =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
                => new(_never.Task);

            public override bool TryRead(out ReadResult result)
            {
                result = default;
                return false;
            }

            public override void AdvanceTo(SequencePosition consumed) { }
            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
            public override void CancelPendingRead() { }
            public override void Complete(Exception? exception = null) { }
        }

        public PipeReader Reader { get; } = new DeafPipeReader();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DisposeAsync_CancellationDeafReader_DoesNotHang()
    {
        var device = new VtInputDevice(new DeafByteSource(), InputCapabilities.None);

        var consumer = Task.Run(async () =>
        {
            var events = new List<InputEvent>();
            await foreach (var ev in device.ReadAllAsync())
                events.Add(ev);
            return events;
        });

        // Let the pump start and park on the never-completing read.
        await Task.Delay(50);

        // Disposal must abandon the deaf pump within its bound rather than waiting for a byte
        // that never arrives; the completed channel still terminates the consumer.
        await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var events = await consumer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(events);
    }
}
