using System.Runtime.CompilerServices;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Tests.Input;

public class TransformingInputDeviceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MouseEvent Button(MouseEventKind kind, int col, int row, int atMs)
        => new()
           {
               Timestamp = T0.AddMilliseconds(atMs),
               Kind = kind,
               Position = new CellPosition(col, row),
               Button = MouseButton.Left,
               ButtonsHeld = MouseButtons.None,
               Modifiers = KeyModifiers.None,
           };

    [Fact]
    public async Task WithClickSynthesis_AdornsClickCount_AndAdvertisesCapability()
    {
        var inner = new FakeDevice(InputCapabilities.None,
                                   Button(MouseEventKind.ButtonDown, 2, 3, 0),
                                   Button(MouseEventKind.ButtonUp, 2, 3, 10),
                                   Button(MouseEventKind.ButtonDown, 2, 3, 100),
                                   Button(MouseEventKind.ButtonUp, 2, 3, 110));

        await using var device = inner.WithClickSynthesis();

        Assert.True(device.Capabilities.Mouse.SynthesizesClickCounts);

        var downs = new List<MouseEvent>();
        await foreach (var e in device.ReadAllAsync())
            if (e is MouseEvent { Kind: MouseEventKind.ButtonDown } m) downs.Add(m);

        Assert.Equal(1, downs[0].ClickCount);
        Assert.Equal(2, downs[1].ClickCount);   // double-click carried through the adapter
    }

    [Fact]
    public async Task ReadAllAsync_CalledTwice_Throws()
    {
        await using var device = new FakeDevice(InputCapabilities.None).WithClickSynthesis();

        _ = device.ReadAllAsync();
        Assert.Throws<InvalidOperationException>(() => device.ReadAllAsync());
    }

    [Fact]
    public async Task DisposeAsync_DisposesInner()
    {
        var inner = new FakeDevice(InputCapabilities.None);
        var device = inner.WithClickSynthesis();

        await device.DisposeAsync();

        Assert.True(inner.Disposed);
    }

    private sealed class FakeDevice(InputCapabilities capabilities, params InputEvent[] events) : IAsyncInputDevice
    {
        public InputCapabilities Capabilities { get; } = capabilities;
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<InputEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var e in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return e;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
