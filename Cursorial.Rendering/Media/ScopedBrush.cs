using Cursorial.Drawing;

namespace Cursorial.Rendering.Media;

/// <summary>
/// A brush declared on a rich-text run — its foreground <see cref="IBrush"/> and the
/// <see cref="DeclarationScope"/> it samples against. Attach it to a run via
/// <c>RichTextBuilder.BrushedRun(text, brushedStyle)</c>. A run's own brush wins over any
/// document-level brush.
/// </summary>
/// <remarks>
/// A TRANSLATING SHIM since the run carrier became a <see cref="BrushedStyle"/>: an
/// <see cref="DeclarationScope.Inline"/>-scoped instance is folded into the run's own
/// <see cref="Cursorial.Rendering.Text.TextRun.Style"/> on the way into the builder — the declaration site
/// implies the scope, so nothing rides the tag. Block / document scopes still travel the opaque
/// <c>FormattedTextRun.Tag</c> through layout, because the carrier has no scope channel for them; the
/// Drawing resolver consults the carrier first and this tag second. Carries only a foreground brush.
/// </remarks>
/// <param name="Foreground">The brush coloring the run's glyphs.</param>
/// <param name="Scope">The geometry the brush samples against (default: the run itself).</param>
public readonly record struct ScopedBrush(IBrush Foreground, DeclarationScope Scope = DeclarationScope.Inline);
