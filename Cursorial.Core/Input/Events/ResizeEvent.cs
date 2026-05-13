namespace Cursorial.Input.Events;

/// <summary>
/// Terminal window resize notification. Delivered through the input pipeline so consumers
/// driving a single event loop see resizes in order with other input.
/// </summary>
public sealed record class ResizeEvent : InputEvent
{
    /// <summary>New width in character cells.</summary>
    public required int Columns { get; init; }

    /// <summary>New height in character cells.</summary>
    public required int Rows { get; init; }

    /// <summary>New width in pixels, when the terminal reports it.</summary>
    public int? PixelWidth { get; init; }

    /// <summary>New height in pixels, when the terminal reports it.</summary>
    public int? PixelHeight { get; init; }

    /// <summary>
    /// Width of a single character cell in pixels, when the terminal reports it. Useful to
    /// consumers driving Sixel/Kitty graphics, image overlays, or pixel-precise mouse hit tests.
    /// </summary>
    public int? CellPixelWidth { get; init; }

    /// <summary>Height of a single character cell in pixels, when the terminal reports it.</summary>
    public int? CellPixelHeight { get; init; }
}
