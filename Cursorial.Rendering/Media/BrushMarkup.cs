using Cursorial.Output;
using Cursorial.Rendering.Text;

namespace Cursorial.Rendering.Media;

/// <summary>
/// Wires gradient brushes into <see cref="TextMarkup"/> via a <c>[brush=VALUE]…[/brush]</c> tag. Two authoring
/// styles, one resolved <see cref="IBrush"/> the parser states on the wrapped runs' own carriers:
/// <list type="bullet">
/// <item><b>Inline</b> — <c>[brush=linear:#f92672,#66d9ef]</c> / <c>[brush=radial:red,black]</c> /
/// <c>[brush=conic:0,4,2]</c>: a gradient kind plus a comma-separated color list (hex, palette index, or named
/// color), evenly spaced, with the brush's default geometry. Convenient for simple gradients.</item>
/// <item><b>Registry</b> — <c>[brush=sunset]</c>: looks the name up in a caller-supplied
/// name→<see cref="IBrush"/> map, for complex gradients (custom directions, angles, multi-stop).</item>
/// </list>
/// The declaration site is the run, so the brush samples the run's wrap-invariant 1-D strip — scope is
/// inferred, never stated.
/// </summary>
public static class BrushMarkup
{

    /// <summary>
    /// A <see cref="TextMarkupOptions.BrushResolver"/>: parses inline gradient syntax or, failing that, looks
    /// <paramref name="registry"/> up by name. Returns null (the parser then raises "unrecognized brush") when
    /// neither matches.
    /// </summary>
    public static Func<string, IBrush?> Resolver(IReadOnlyDictionary<string, IBrush>? registry = null) =>
        value =>
        {
            if (registry is not null && registry.TryGetValue(value, out var named)) return named;
            if (TryParseInline(value, out var brush)) return brush;
            return null;
        };

    /// <summary>Build <see cref="TextMarkupOptions"/> wired with the brush <see cref="Resolver"/> (inline
    /// gradients + an optional registry) plus the given default style.</summary>
    public static TextMarkupOptions Options(CellStyle defaultStyle = default,
                                            IReadOnlyDictionary<string, IBrush>? registry = null) =>
        new() { DefaultStyle = BrushedStyle.FromStated(defaultStyle), BrushResolver = Resolver(registry) };

    // Parse "kind:colorA,colorB[,colorC…]" into an IBrush. Returns false for a non-inline value (no
    // recognized kind/colon) so the resolver can fall back to the registry.
    private static bool TryParseInline(string value, out IBrush? brush)
    {
        brush = null;

        int colon = value.IndexOf(':');

        if (colon <= 0)
        {
            if (MarkupColor.TryParseBrush(value, out var solid))
            {
                brush = solid;
                return true;
            }

            return false;
        }

        string kind = value[..colon].ToLowerInvariant();

        var tokens = value[(colon + 1)..]
           .Split(',',
                  StringSplitOptions.RemoveEmptyEntries |
                  StringSplitOptions.TrimEntries);

        if (tokens.Length < 2) return false; // a gradient needs at least two stops

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
                                      _        => null
                                  };

        if (gradient is null) return false;

        brush = gradient;
        return true;
    }
}
