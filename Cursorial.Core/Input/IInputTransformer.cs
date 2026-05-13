using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// Transforms a stream of input events — filtering, reordering, fabricating, or rewriting —
/// without taking responsibility for the underlying device's lifetime or consumption surface.
/// </summary>
/// <remarks>
/// <para>
/// Transformers are the recommended way to express input-pipeline stages that don't need to
/// own their source. A key-up synthesizer, an idle-detection filter, or a mouse-acceleration
/// curve are all natural transformers — each takes an <see cref="IAsyncEnumerable{T}"/> of
/// events and yields another, possibly with different <see cref="InputCapabilities"/> implied.
/// </para>
/// <para>
/// Because a transformer operates only on the event stream, the same transformer composes with
/// either an <see cref="IAsyncInputDevice"/> source or an <see cref="IEventInputDevice"/> source —
/// the surface-bridging adapter (typically backed by <see cref="System.Threading.Channels.Channel{T}"/>)
/// is written once and reused, rather than each transformer re-implementing it.
/// </para>
/// <para>
/// Transformers describe how they affect the device's reported capabilities through
/// <see cref="TransformCapabilities"/>; pipeline assemblers can use this to compute the
/// effective <see cref="IInputDevice.Capabilities"/> of the composed device without running it.
/// </para>
/// </remarks>
public interface IInputTransformer
{
    /// <summary>
    /// Returns an asynchronous stream of events derived from <paramref name="source"/>.
    /// Iteration of the returned sequence drives iteration of the source. The transformer
    /// must respect <paramref name="cancellationToken"/> and propagate completion and errors
    /// from the source.
    /// </summary>
    IAsyncEnumerable<InputEvent> TransformAsync(
        IAsyncEnumerable<InputEvent> source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the capabilities a device would have after this transformer is applied to a
    /// device with the given <paramref name="sourceCapabilities"/>. Pure function; must not
    /// have side effects. Default implementation returns the input unchanged — override when
    /// the transformer adds, removes, or alters reported capabilities (for example, a
    /// synthesizer that adds <see cref="KeyboardCapabilities.DistinguishesKeyUpDown"/>).
    /// </summary>
    InputCapabilities TransformCapabilities(InputCapabilities sourceCapabilities) => sourceCapabilities;
}