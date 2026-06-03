using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// Adapts an <see cref="IInputTransformer"/> onto an inner <see cref="IAsyncInputDevice"/>, yielding
/// a device whose event stream is the transformer applied to the inner's. This is the
/// "written-once" surface-bridging adapter <see cref="IInputTransformer"/> refers to — it lets a
/// pure stream transform (a click recognizer, an acceleration curve, a filter) participate in a
/// device chain without each transformer re-implementing device plumbing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capabilities</b> flow through <see cref="IInputTransformer.TransformCapabilities"/>, so the
/// adapted device advertises whatever the transformer adds or removes.
/// </para>
/// <para>
/// <b>Single-shot.</b> Like the inner device, <see cref="ReadAllAsync"/> may be called at most
/// once. <b>Ownership:</b> disposing the adapter disposes the inner device.
/// </para>
/// </remarks>
public sealed class TransformingInputDevice : IAsyncInputDevice, IInputDeviceDecorator
{
    private readonly IAsyncInputDevice _inner;
    private readonly IInputTransformer _transformer;
    private int _started;

    public TransformingInputDevice(IAsyncInputDevice inner, IInputTransformer transformer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _transformer = transformer ?? throw new ArgumentNullException(nameof(transformer));
    }

    /// <inheritdoc/>
    public IInputDevice Inner => _inner;

    /// <inheritdoc/>
    public InputCapabilities Capabilities => _transformer.TransformCapabilities(_inner.Capabilities);

    /// <inheritdoc/>
    public IAsyncEnumerable<InputEvent> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "ReadAllAsync was already called on this device. Decorators are single-shot; create "
                + "a new TransformingInputDevice for a fresh read.");
        }

        return _transformer.TransformAsync(_inner.ReadAllAsync(cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// Composition helpers for wrapping an <see cref="IAsyncInputDevice"/> with input transformers.
/// </summary>
public static class InputDeviceTransformExtensions
{
    /// <summary>
    /// Wrap <paramref name="device"/> so its events flow through <paramref name="transformer"/>.
    /// Equivalent to <c>new TransformingInputDevice(device, transformer)</c>.
    /// </summary>
    public static IAsyncInputDevice Transform(this IAsyncInputDevice device, IInputTransformer transformer)
        => new TransformingInputDevice(device, transformer);

    /// <summary>
    /// Wrap <paramref name="device"/> with a <see cref="MouseClickSynthesizer"/> using the supplied
    /// options (or the defaults). Consumers then see multi-click counts — and, when enabled,
    /// synthesized <see cref="MouseEventKind.Click"/> events.
    /// </summary>
    public static IAsyncInputDevice WithClickSynthesis(this IAsyncInputDevice device, MouseClickOptions? options = null)
        => device.Transform(new MouseClickSynthesizer(options));
}
