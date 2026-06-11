// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A brush declared on a rich-text run — its foreground <see cref="IBrush"/> and the
/// <see cref="DeclarationScope"/> it samples against. Attach it to a run via
/// <c>RichTextBuilder.BrushedRun(text, brushedStyle)</c>; it rides the run's opaque
/// <c>FormattedTextRun.Tag</c> through layout, and <c>DrawingContext.DrawFormattedText</c> samples it per cell.
/// A run's own brush wins over any document-level brush.
/// </summary>
/// <remarks>
/// 6a/6b carry only a foreground brush (the common case — gradient text). Background / underline brushes can
/// join later as additive members. Default scope is <see cref="DeclarationScope.Inline"/>, so a run's gradient
/// spans the run itself.
/// </remarks>
/// <param name="Foreground">The brush coloring the run's glyphs.</param>
/// <param name="Scope">The geometry the brush samples against (default: the run itself).</param>
public readonly record struct BrushedStyle(IBrush Foreground, DeclarationScope Scope = DeclarationScope.Inline);
