namespace Cursorial.Core.Input;

/// <summary>
/// Optional marker implemented by input devices that wrap another device. Useful for
/// diagnostics, capability inspection, and walking a composition chain back to its source.
/// </summary>
/// <remarks>
/// Implementing this interface is not required for chaining to work — any device that takes
/// another device as a constructor argument and forwards or transforms its events is a valid
/// decorator. Implement <see cref="IInputDeviceDecorator"/> when you want consumers to be able
/// to discover the chain shape (for logging, debugging, or capability negotiation).
/// </remarks>
public interface IInputDeviceDecorator : IInputDevice
{
    /// <summary>
    /// The inner device wrapped by this decorator. May itself be an
    /// <see cref="IInputDeviceDecorator"/>, allowing the chain to be walked to its origin.
    /// </summary>
    IInputDevice Inner { get; }
}
