using Cursorial.Drawing.Media;
using Cursorial.Rendering.Text;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Bridges the resource chain into text markup (design doc §11.9): produces a
/// <see cref="TextMarkupOptions.BrushResolver"/> over an element's chain so <c>[brush=Theme.AccentBrush]</c>
/// markup and <c>{DynamicResource Theme.AccentBrush}</c> resolve identically (one brush namespace).
/// Inline gradient grammar (<c>linear:…</c>) resolves via <see cref="BrushMarkup"/>; a bare name
/// resolves as a resource key through the element's chain to an <see cref="IBrush"/>. Resolution is
/// static-per-parse; freshness rides the <c>GetResourceVersion</c> cache-key contract (§11.6).
/// </summary>
public static class ResourceBrushResolver
{
    /// <summary>Creates a brush resolver over <paramref name="scope"/>'s chain.</summary>
    public static Func<string, object?> Create(UIElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var inline = BrushMarkup.Resolver();

        return value =>
        {
            // 1. Inline gradient grammar (linear:/radial:/conic:) — shared with text markup.
            if (inline(value) is { } parsed)
                return parsed;

            // 2. A bare resource name resolved through the element's chain to an IBrush.
            if (scope.TryFindResource(value, out var resolved) && resolved is IBrush brush)
                return new BrushedStyle(brush);

            return null; // unknown — the parser raises "Unrecognized brush"
        };
    }
}
