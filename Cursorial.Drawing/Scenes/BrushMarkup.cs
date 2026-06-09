using Cursorial.Output;
using Cursorial.Rendering.Text;

namespace Cursorial.Drawing;

/// <summary>
/// Wires gradient brushes into <see cref="TextMarkup"/> via a <c>[brush=VALUE]…[/brush]</c> tag. Two authoring
/// styles, one opaque tag (the §9 invariant holds — Rendering's parser never sees a brush):
/// <list type="bullet">
/// <item><b>Inline</b> — <c>[brush=linear:#f92672,#66d9ef]</c> / <c>[brush=radial:red,black]</c> /
/// <c>[brush=conic:0,4,2]</c>: a gradient kind plus a comma-separated color list (hex, palette index, or named
/// color), evenly spaced, with the brush's default geometry. Convenient for simple gradients.</item>
/// <item><b>Registry</b> — <c>[brush=sunset]</c>: looks the name up in a caller-supplied
/// name→<see cref="BrushedStyle"/> map, for complex gradients (custom directions, angles, multi-stop, scope).</item>
/// </list>
/// The resolved <see cref="BrushedStyle"/> is <see cref="DeclarationScope.Inline"/> by default (the run's
/// wrap-invariant 1-D strip); a registry entry may use any scope.
/// </summary>
public static class BrushMarkup
{
    /// <summary>
    /// A <see cref="TextMarkupOptions.BrushResolver"/>: parses inline gradient syntax or, failing that, looks
    /// <paramref name="registry"/> up by name. Returns null (the parser then raises "unrecognized brush") when
    /// neither matches.
    /// </summary>
    public static Func<string, object?> Resolver(IReadOnlyDictionary<string, BrushedStyle>? registry = null) =>
        value =>
        {
            if (TryParseInline(value, out var brushed)) return brushed;
            if (registry is not null && registry.TryGetValue(value, out var named)) return named;
            return null;
        };

    /// <summary>Build <see cref="TextMarkupOptions"/> wired with the brush <see cref="Resolver"/> (inline
    /// gradients + an optional registry) plus the given default style.</summary>
    public static TextMarkupOptions Options(Style defaultStyle = default,
                                            IReadOnlyDictionary<string, BrushedStyle>? registry = null) =>
        new() { DefaultStyle = defaultStyle, BrushResolver = Resolver(registry) };

    // Parse "kind:colorA,colorB[,colorC…]" into a BrushedStyle. Returns false for a non-inline value (no
    // recognized kind/colon) so the resolver can fall back to the registry.
    private static bool TryParseInline(string value, out BrushedStyle brushed)
    {
        brushed = default;

        int colon = value.IndexOf(':');
        if (colon <= 0) return false;
        string kind = value[..colon].ToLowerInvariant();

        var tokens = value[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return false;   // a gradient needs at least two stops

        var stops = new GradientStop[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!MarkupColor.TryParse(tokens[i], out var color)) return false;
            stops[i] = new GradientStop(i / (double) (tokens.Length - 1), color);
        }

        GradientBrush? gradient = kind switch
        {
            "linear" => new LinearGradientBrush(stops),
            "radial" => new RadialGradientBrush(stops),
            "conic"  => new ConicGradientBrush(stops),
            _        => null,
        };
        if (gradient is null) return false;

        brushed = new BrushedStyle(gradient);   // Inline scope by default — the run's 1-D strip
        return true;
    }
}
