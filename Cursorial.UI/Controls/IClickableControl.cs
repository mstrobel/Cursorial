namespace Cursorial.UI.Controls;

// ReSharper disable EventNeverSubscribedTo.Global

/// <summary>
/// Identifies controls that may be invoked via a pointer click gesture, spacebar/enter key press, access key,
/// or similar gesture. Allows such controls to be invoked programmatically.
/// </summary>
public interface IClickableControl
{
    event EventHandler<ClickEventArgs>? Click;

    /// <summary>
    /// Raises the <see cref="Click"/> event and performs any other necessary actions to handle.
    /// Should guard on <see cref="IsEffectivelyEnabled"/>. 
    /// </summary>
    void RaiseClick(InvokeMethod method);

    /// <summary>
    /// Gets a value indicating whether this control and all its parents are enabled.
    /// </summary>
    bool IsEffectivelyEnabled { get; }
}