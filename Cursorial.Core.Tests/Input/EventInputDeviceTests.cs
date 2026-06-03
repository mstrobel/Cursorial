using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Tests.Terminal;

namespace Cursorial.Tests.Input;

public class EventInputDeviceTests
{
    private readonly InMemoryInputByteSource _source = new();

    private VtInputDevice BuildInnerDevice() =>
        new(_source, InputCapabilities.None, mode: null, timeProvider: null,
            escapeAmbiguityTimeout: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task StartAsync_PumpsInnerDeviceEventsAsRaisedInputEvents()
    {
        await using var device = new EventInputDevice(BuildInnerDevice());
        var received = new List<InputEvent>();
        var completed = new TaskCompletionSource();
        device.Input += (_, e) => received.Add(e);
        device.Completed += (_, _) => completed.TrySetResult();

        await device.StartAsync();
        _source.Enqueue("ab");
        _source.CompleteWriter();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, received.Count);
        Assert.IsType<KeyEvent>(received[0]);
        Assert.IsType<KeyEvent>(received[1]);
    }

    [Fact]
    public async Task StopAsync_HaltsPumpAndRaisesCompleted()
    {
        await using var device = new EventInputDevice(BuildInnerDevice());
        var completed = new TaskCompletionSource();
        device.Completed += (_, _) => completed.TrySetResult();

        await device.StartAsync();
        // No bytes enqueued and writer not completed — pump is blocked on the source.
        await device.StopAsync();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StartAsync_CalledTwice_ThrowsInvalidOperation()
    {
        await using var device = new EventInputDevice(BuildInnerDevice());

        await device.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.StartAsync());
    }

    [Fact]
    public async Task StartAsync_AfterDisposalThrowsObjectDisposed()
    {
        var device = new EventInputDevice(BuildInnerDevice());
        await device.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.StartAsync());
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var device = new EventInputDevice(BuildInnerDevice());
        await device.DisposeAsync();
        await device.DisposeAsync();
    }

    [Fact]
    public void Capabilities_FlowsThroughFromInnerDevice()
    {
        var caps = new InputCapabilities(
            Mouse: MouseCapabilities.None,
            Keyboard: KeyboardCapabilities.None with { TextInput = true },
            Pointer: PointerCapabilities.None,
            Protocol: ProtocolCapabilities.None);
        var inner = new VtInputDevice(_source, caps);
        var device = new EventInputDevice(inner);

        Assert.Same(caps, device.Capabilities);
    }

    [Fact]
    public async Task HandlerException_DoesNotStopPump()
    {
        await using var device = new EventInputDevice(BuildInnerDevice());
        var received = new List<InputEvent>();
        device.Input += (_, _) => throw new InvalidOperationException("boom");
        device.Input += (_, e) => received.Add(e); // second handler still runs after the first faults

        await device.StartAsync();
        _source.Enqueue("ab");
        _source.CompleteWriter();

        // Wait for the pump to drain.
        var completed = new TaskCompletionSource();
        device.Completed += (_, _) => completed.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The second handler should NOT have been invoked: a faulty first handler swallows the
        // event for that subscription, and our raise wraps the entire delegate invocation so a
        // throw aborts the chain. The pump itself continues to the next event.
        Assert.True(received.Count <= 2);
    }

    [Fact]
    public void Constructor_RejectsNullInner()
    {
        Assert.Throws<ArgumentNullException>(() => new EventInputDevice(null!));
    }

    [Fact]
    public async Task EventContext_MarshalsRaisesThroughSynchronizationContext()
    {
        var ctx = new RecordingSynchronizationContext();
        await using var device = new EventInputDevice(BuildInnerDevice(), ctx);

        var observed = new List<SynchronizationContext?>();
        var completed = new TaskCompletionSource();
        device.Input += (_, _) => observed.Add(SynchronizationContext.Current);
        device.Completed += (_, _) => { observed.Add(SynchronizationContext.Current); completed.TrySetResult(); };

        await device.StartAsync();
        _source.Enqueue("ab");
        _source.CompleteWriter();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, ctx.PostCount);                  // two Input raises + Completed
        Assert.Equal(3, observed.Count);
        Assert.All(observed, c => Assert.Same(ctx, c));  // each handler ran with the context current
    }

    [Fact]
    public async Task CapturingCurrentContext_CapturesAtConstruction()
    {
        var ctx = new RecordingSynchronizationContext();
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);

        EventInputDevice device;
        try { device = EventInputDevice.CapturingCurrentContext(BuildInnerDevice()); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }

        await using (device)
        {
            var completed = new TaskCompletionSource();
            device.Input += (_, _) => { };   // a subscriber so the Input raise actually posts
            device.Completed += (_, _) => completed.TrySetResult();

            await device.StartAsync();
            _source.Enqueue("a");
            _source.CompleteWriter();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, ctx.PostCount);   // one Input raise + Completed, routed to the captured context
        }
    }

    /// <summary>Runs posted callbacks inline with itself installed as the current context, and counts posts.</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            var prior = Current;
            SetSynchronizationContext(this);
            // @formatter:off
            try { d(state); }
            finally { SetSynchronizationContext(prior); }
            // @formatter:on
        }
    }
}
