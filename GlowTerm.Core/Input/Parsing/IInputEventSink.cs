namespace GlowTerm.Core.Input.Parsing;

/// <summary>
/// Receives <see cref="InputEvent"/>s emitted by an interpreter as it consumes classifier
/// tokens. Concrete <see cref="IAsyncInputDevice"/> / <see cref="IEventInputDevice"/>
/// implementations supply a sink that adapts events into the device's outward delivery
/// surface (typically a <see cref="System.Threading.Channels.Channel{T}"/> for the async
/// surface or direct event invocation for the push surface).
/// </summary>
/// <remarks>
/// Implementations MUST treat the call as synchronous and non-blocking — the interpreter
/// invokes the sink from inside its own <see cref="IVtSequenceTokenSink"/> callbacks, which
/// are themselves invoked from the byte-pump loop. Any deferral (queueing, marshalling) is
/// the sink's job, not the interpreter's.
/// </remarks>
public interface IInputEventSink
{
    /// <summary>Receives a single <see cref="InputEvent"/> from the interpreter.</summary>
    void OnInputEvent(InputEvent inputEvent);
}
