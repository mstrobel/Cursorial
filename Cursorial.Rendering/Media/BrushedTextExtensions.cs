using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering.Text;

namespace Cursorial.Rendering.Media;

/// <summary>
/// Drawing-side authoring sugar for brush-bearing rich text: declare a brush on a run while keeping
/// <see cref="IBrush"/> out of Rendering's <c>Style</c>. The brush rides the run's opaque tag through layout
/// and is sampled at paint time by <c>DrawingContext.DrawFormattedText</c>.
/// </summary>
public static class BrushedTextExtensions
{
    /// <summary>
    /// Append a text run colored by <paramref name="brushed"/> — sampled per cell at paint time (at the brush's
    /// <see cref="DeclarationScope"/>), winning over any document-level brush. The run's non-color style comes
    /// from the builder's current style; colors come from the brush.
    /// </summary>
    /// <remarks>
    /// This took a <c>CellStyle legacyBaseStyle</c> that no caller ever passed — every call site, product and
    /// tests and <c>Cursorial.Demo</c>, left it at <c>default</c>, which <c>Run</c> then replaced with the
    /// document's own default style. Dropped rather than deprecated: the framework is pre-1.0 with no external
    /// consumers, so a source or binary break costs nothing and an <c>[Obsolete]</c>-then-remove sequence would
    /// only leave dead surface for a release.
    /// </remarks>
    public static RichTextBuilder BrushedRun(this RichTextBuilder builder, string text, ScopedBrush brushed)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Run(text, default, tag: brushed);
    }
}
