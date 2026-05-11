namespace Cursorial.Core.Input;

/// <summary>
/// Reports that the terminal window gained or lost keyboard focus. Only emitted when the
/// device's <see cref="ProtocolCapabilities.FocusEvents"/> capability is true.
/// </summary>
public sealed record class FocusEvent : InputEvent
{
    /// <summary>True when the terminal gained focus; false when it lost focus.</summary>
    public required bool HasFocus { get; init; }
}
