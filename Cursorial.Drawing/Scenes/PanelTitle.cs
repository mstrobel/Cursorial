using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A titled-box label: the title <see cref="Text"/>, its <see cref="Brush"/> (null = the pen's color),
/// placement (<see cref="Position"/>), and non-color SGR <see cref="Attributes"/>. The label is laid onto
/// the top border edge, the rule breaking on either side with a pad cell of margin so the line never
/// overstrikes the text. <c>default(PanelTitle)</c> has null text (no title) — a default value draws a
/// plain box.
/// </summary>
public readonly record struct PanelTitle
{
    /// <summary>Create a left-aligned title over <paramref name="text"/>, colored by the pen.</summary>
    public PanelTitle(string? text) => Text = text;

    /// <summary>The label text; null or empty draws no title (plain border). Newlines are not interpreted.</summary>
    public string? Text { get; init; }

    /// <summary>Color source for the title; null (default) inherits the pen's resolved brush.</summary>
    public IBrush? Brush { get; init; }

    /// <summary>Placement along the top edge (default <see cref="TitlePosition.Left"/>).</summary>
    public TitlePosition Position { get; init; }

    /// <summary>Non-color SGR styling for the title cells (default none).</summary>
    public TextAttributes Attributes { get; init; }

    /// <summary>Implicit lift from a string for the common left-aligned case.</summary>
    public static implicit operator PanelTitle(string? text) => new(text);

    /// <summary>Return a copy with a different <see cref="Brush"/>.</summary>
    public PanelTitle WithBrush(IBrush? brush) => this with { Brush = brush };

    /// <summary>Return a copy whose <see cref="Brush"/> is a solid <paramref name="color"/>.</summary>
    public PanelTitle WithColor(Color color) => this with { Brush = new SolidColorBrush(color) };

    /// <summary>Return a copy with a different <see cref="Position"/>.</summary>
    public PanelTitle WithPosition(TitlePosition position) => this with { Position = position };

    /// <summary>Return a copy with different <see cref="Attributes"/>.</summary>
    public PanelTitle WithAttributes(TextAttributes attributes) => this with { Attributes = attributes };
}
