using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Drawing.Media;

/// <summary>
/// Drawing-side authoring sugar for brush-bearing rich text: declare a brush on a run while keeping
/// <see cref="IBrush"/> out of Rendering's <c>Style</c>. The brush rides the run's opaque tag through layout
/// and is sampled at paint time by <c>DrawingContext.DrawFormattedText</c>.
/// </summary>
public static class BrushedTextExtensions
{
    /// <summary>
    /// Append a text run colored by <paramref name="brushed"/> — sampled per cell at paint time (at the brush's
    /// <see cref="DeclarationScope"/>), winning over any document-level brush. <paramref name="baseStyle"/>
    /// supplies the run's non-color style (attributes, underline shape); colors come from the brush.
    /// </summary>
    public static RichTextBuilder BrushedRun(this RichTextBuilder builder, string text, BrushedStyle brushed,
                                             CellStyle baseStyle = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Run(text, baseStyle, tag: brushed);
    }
}
