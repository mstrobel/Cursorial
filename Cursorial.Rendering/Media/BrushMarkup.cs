using System.Diagnostics.CodeAnalysis;

using Cursorial.Rendering.Text;

namespace Cursorial.Rendering.Media;

/// <summary>
/// The brush vocabulary behind <see cref="TextMarkup"/>'s <c>[fg=VALUE]</c> / <c>[bg=VALUE]</c> tags. Two
/// authoring styles, one resolved <see cref="IBrush"/> the parser states on the wrapped runs' own carriers:
/// <list type="bullet">
/// <item><b>Inline</b> — <c>[fg=linear:#f92672,#66d9ef]</c> / <c>[fg=radial:red,black]</c> /
/// <c>[fg=conic:0,4,2]</c>: a gradient kind plus a comma-separated color list (hex, palette index, or named
/// color), evenly spaced, with the brush's default geometry — or a bare color token, a solid. This grammar
/// is syntax, not names: the parser owns it, no options required.</item>
/// <item><b>Registry</b> — <c>[fg=sunset]</c>: looks the name up in a caller-supplied
/// name→<see cref="IBrush"/> map, for complex gradients (custom directions, angles, multi-stop). Names
/// resolve BEFORE the inline grammar, so a registered name overrides a built-in one at exactly the width
/// of the name.</item>
/// </list>
/// The declaration site is the run, so the brush samples the run's wrap-invariant 1-D strip — scope is
/// inferred, never stated.
/// </summary>
public static class BrushMarkup
{

    /// <summary>
    /// A <see cref="TextMarkupOptions.BrushResolver"/>: looks <paramref name="registry"/> up by name first,
    /// then parses the inline grammar. Returns null (the parser then falls through to its own built-in
    /// grammar and, failing that, raises "unrecognized color or brush") when neither matches.
    /// </summary>
    public static Func<string, IBrush?> Resolver(IReadOnlyDictionary<string, IBrush>? registry = null) =>
        value =>
        {
            if (registry is not null && registry.TryGetValue(value, out var named)) return named;
            if (TryParseInline(value, out var brush)) return brush;
            return null;
        };

    /// <summary>Build <see cref="TextMarkupOptions"/> wired with the brush <see cref="Resolver"/> (an
    /// optional named registry) plus the given default style. Callers holding a resolved
    /// <see cref="Cursorial.Output.CellStyle"/> adapt it via <see cref="BrushedStyle.FromStated"/>.</summary>
    public static TextMarkupOptions Options(BrushedStyle defaultStyle = default,
                                            IReadOnlyDictionary<string, IBrush>? registry = null) =>
        new() { DefaultStyle = defaultStyle, BrushResolver = Resolver(registry) };

    // Parse "kind:colorA,colorB[,colorC…]" into an IBrush, or a bare color token into a solid. Returns
    // false for a value that is neither (no recognized kind/colon, unparseable color) so the caller —
    // TextMarkup's [fg]/[bg] fallback, or Resolver after a registry miss — can reject it.
    internal static bool TryParseInline(string value, [NotNullWhen(true)] out IBrush? brush)
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
