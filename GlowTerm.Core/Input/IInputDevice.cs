namespace GlowTerm.Core.Input;

/// <summary>
/// Common base for any input device. An input device is a source of <see cref="InputEvent"/>s
/// representing user input from a terminal — keyboard, mouse, pen/touch, focus, paste, resize,
/// and so on.
/// </summary>
/// <remarks>
/// <para>
/// Concrete consumption is exposed by the role-specific sub-interfaces:
/// <see cref="IAsyncInputDevice"/> for pull-based <see cref="IAsyncEnumerable{T}"/> consumption,
/// and <see cref="IEventInputDevice"/> for classic push-based delegate events.
/// </para>
/// <para>
/// <b>Single-surface contract.</b> A single implementation MAY implement both
/// <see cref="IAsyncInputDevice"/> and <see cref="IEventInputDevice"/>, but consumers MUST use
/// exactly one consumption surface per device instance. Each event is delivered through the
/// surface the consumer is using; if both surfaces are exercised concurrently on the same
/// instance, which surface receives any given event is implementation-defined and consumers
/// MUST NOT rely on broadcast semantics. Broadcast (every subscriber sees every event) is the
/// job of an explicit fan-out adapter, not of the device interface.
/// </para>
/// <para>
/// Devices form chains. A decorator wraps an inner <see cref="IInputDevice"/> and produces a new
/// device whose <see cref="Capabilities"/> may differ from the inner — for example, a key-up
/// synthesizer wrapping a VT input source can fabricate <see cref="KeyEvent"/>s with
/// <see cref="KeyEventKind.Up"/> on terminals that do not natively report key release, based on
/// state and timing heuristics. Decorators that wish to expose their inner device implement
/// <see cref="IInputDeviceDecorator"/>. Pure stream-shape transformations (filter, fabricate,
/// reorder) without device-lifetime responsibility are expressed as <see cref="IInputTransformer"/>.
/// </para>
/// </remarks>
public interface IInputDevice : IAsyncDisposable
{
    /// <summary>
    /// Describes which kinds of events this device can produce, including any capabilities
    /// added or removed by intermediate decorators.
    /// </summary>
    InputCapabilities Capabilities { get; }
}
