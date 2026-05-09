namespace GlowTerm.Core.Input;

/// <summary>
/// Identifies the kind of <see cref="DeviceResponseEvent"/>. <see cref="Unknown"/> is used
/// when the response was recognized as a query reply but its specific kind could not be
/// classified — consumers may still inspect <see cref="DeviceResponseEvent.Payload"/>.
/// </summary>
public enum DeviceResponseKind
{
    Unknown = 0,
    PrimaryDeviceAttributes,
    SecondaryDeviceAttributes,
    TertiaryDeviceAttributes,
    CursorPositionReport,
    DeviceStatusReport,
    TerminalNameQuery,
    TerminalVersionQuery,
    BackgroundColorQuery,
    ForegroundColorQuery,
    PaletteColorQuery,
}
