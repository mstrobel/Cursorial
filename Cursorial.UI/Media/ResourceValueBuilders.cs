using Cursorial.Drawing;
using Cursorial.Output;

using DrawingMedia = Cursorial.Drawing.Media;

namespace Cursorial.UI.Media;

/// <summary>
/// The loader seam (design doc §11.9): at end-of-object the instantiator replaces a builder with
/// <see cref="Build"/>'s result everywhere it would be used. Built values are immutable Drawing
/// instances, boxed once and shared (sealed-dictionary-safe).
/// </summary>
public interface IResourceValueBuilder
{
    /// <summary>Produces the immutable Drawing value this builder describes.</summary>
    object Build();
}

/// <summary>
/// The XAML-facing (mutable, parameterless-ctor) twin of <see cref="DrawingMedia.SolidColorBrush"/>
/// (design doc §11.9; deliberate shadowing — C# consumers use Drawing directly). <see cref="Build"/>
/// produces the immutable Drawing brush.
/// </summary>
public sealed class SolidColorBrush : IResourceValueBuilder
{
    /// <summary>The brush color.</summary>
    public Color Color { get; set; }

    /// <summary>The brush opacity (0–1).</summary>
    public double Opacity { get; set; } = 1.0;

    /// <inheritdoc/>
    public object Build() => new DrawingMedia.SolidColorBrush(Color, Opacity);
}

/// <summary>A gradient stop (design doc §11.9) — the builder counterpart of <see cref="DrawingMedia.GradientStop"/>.</summary>
public sealed class GradientStop
{
    /// <summary>The stop offset (0–1).</summary>
    public double Offset { get; set; }

    /// <summary>The stop color.</summary>
    public Color Color { get; set; }

    internal DrawingMedia.GradientStop ToDrawing() => new(Offset, Color);
}

/// <summary>
/// The XAML-facing twin of <see cref="DrawingMedia.LinearGradientBrush"/> (design doc §11.9).
/// </summary>
public sealed class LinearGradientBrush : IResourceValueBuilder
{
    /// <summary>The gradient stops.</summary>
    public List<GradientStop> GradientStops { get; } = [];

    /// <summary>The gradient start point (relative to bounds).</summary>
    public RelativePoint StartPoint { get; set; } = RelativePoint.TopLeft;

    /// <summary>The gradient end point (relative to bounds).</summary>
    public RelativePoint EndPoint { get; set; } = RelativePoint.TopRight;

    /// <summary>The gradient spread mode.</summary>
    public DrawingMedia.GradientSpread Spread { get; set; } = DrawingMedia.GradientSpread.Pad;

    /// <summary>The brush opacity (0–1).</summary>
    public double Opacity { get; set; } = 1.0;

    /// <inheritdoc/>
    public object Build()
    {
        var stops = new DrawingMedia.GradientStop[GradientStops.Count];
        for (var i = 0; i < GradientStops.Count; i++)
            stops[i] = GradientStops[i].ToDrawing();

        return new DrawingMedia.LinearGradientBrush(stops, StartPoint, EndPoint, Spread, Opacity);
    }
}

/// <summary>
/// The XAML-facing twin of <see cref="Cursorial.Drawing.Pen"/> (a readonly record struct,
/// XAML-hostile — design doc §11.9). <see cref="Color"/> XOR <see cref="Brush"/> (both set ⇒
/// build-time IOE).
/// </summary>
public sealed class Pen : IResourceValueBuilder
{
    /// <summary>A solid pen color (mutually exclusive with <see cref="Brush"/>).</summary>
    public Color? Color { get; set; }

    /// <summary>A pen brush (mutually exclusive with <see cref="Color"/>).</summary>
    public DrawingMedia.IBrush? Brush { get; set; }

    /// <summary>The stroke weight.</summary>
    public DrawingMedia.StrokeWeight Weight { get; set; } = DrawingMedia.StrokeWeight.Light;

    /// <summary>The glyph repertoire.</summary>
    public DrawingMedia.GlyphSet GlyphSet { get; set; } = DrawingMedia.GlyphSet.Unicode;

    /// <inheritdoc/>
    public object Build()
    {
        if (Color is not null && Brush is not null)
            throw new InvalidOperationException("A Pen builder may set Color XOR Brush, not both (design doc §11.9).");

        var pen = new Cursorial.Drawing.Pen { Weight = Weight, GlyphSet = GlyphSet };

        if (Color is { } color)
            pen = pen with { Brush = new DrawingMedia.SolidColorBrush(color) };
        else if (Brush is { } brush)
            pen = pen with { Brush = brush };

        return pen;
    }
}
