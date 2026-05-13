using Cursorial.Input.Capabilities;

namespace Cursorial.Output.Capabilities;

/// <summary>
/// Aggregate description of which output features a terminal honors, broken down by category.
/// Mirrors the structure of <see cref="InputCapabilities"/> on the input
/// side — together they describe the full negotiated feature surface of a terminal session.
/// </summary>
public sealed record OutputCapabilities(ColorCapabilities Color,
                                        TextStylingCapabilities Styling,
                                        TextSizingCapabilities TextSizing,
                                        GraphicsCapabilities Graphics,
                                        CursorCapabilities Cursor,
                                        WindowCapabilities Window,
                                        OutputProtocolCapabilities Protocol)
{
    /// <summary>A capability set reporting no supported output features. Useful as a default or sentinel.</summary>
    public static OutputCapabilities None { get; } = new(Color: ColorCapabilities.None,
                                                         Styling: TextStylingCapabilities.None,
                                                         TextSizing: TextSizingCapabilities.None,
                                                         Graphics: GraphicsCapabilities.None,
                                                         Cursor: CursorCapabilities.None,
                                                         Window: WindowCapabilities.None,
                                                         Protocol: OutputProtocolCapabilities.None);
}
