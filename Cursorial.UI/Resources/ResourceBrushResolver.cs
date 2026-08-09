using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.UI.Themes;

using R = Cursorial.UI.ResourceExtensions;

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
    private static readonly Dictionary<byte, string> Ansi16ThemeKeys =
        new()
        {
            { 0, ThemeKeys.AnsiBlack },
            { 1, ThemeKeys.AnsiRed },
            { 2, ThemeKeys.AnsiGreen },
            { 3, ThemeKeys.AnsiYellow },
            { 4, ThemeKeys.AnsiBlue },
            { 5, ThemeKeys.AnsiMagenta },
            { 6, ThemeKeys.AnsiCyan },
            { 7, ThemeKeys.AnsiWhite },
            { 8, ThemeKeys.AnsiLightBlack },
            { 9, ThemeKeys.AnsiLightRed },
            { 10, ThemeKeys.AnsiLightGreen },
            { 11, ThemeKeys.AnsiLightYellow },
            { 12, ThemeKeys.AnsiLightBlue },
            { 13, ThemeKeys.AnsiLightMagenta },
            { 14, ThemeKeys.AnsiLightCyan },
            { 15, ThemeKeys.AnsiLightWhite }
        };

    private static readonly Func<string, object?> ApplicationLookup =
        s => R.WalkApplicationTail(s, R.ResolveVariant(null), null, out var o) ? o : null;

    private static Func<string, object?> ElementLookup(UIElement scope)
        => s => scope.TryFindResource(s, out var resolved) ? resolved : null;

    /// <summary>A brush resolver that walks the application tail.</summary>
    public static Func<string, object?> ForApplication { get; } = CreateCore(ApplicationLookup);

    /// <summary>Creates a brush resolver over <paramref name="scope"/>'s chain.</summary>
    public static Func<string, object?> Create(UIElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return CreateCore(ElementLookup(scope));
    }

    private static Func<string, object?> CreateCore(Func<string, object?> innerLookup)
    {
        ArgumentNullException.ThrowIfNull(innerLookup);

        var inline = BrushMarkup.Resolver();

        return value =>
               {
                   // 1. Inline gradient grammar (linear:/radial:/conic:) — shared with text markup.
                   if (inline(value) is BrushedStyle { Foreground: {} f } brushedStyle)
                   {
                       if (f is SolidColorBrush { Color: { Kind: ColorKind.Palette, PaletteIndex: var i } } &&
                           MarkupColor.IsThemePaletteIndex(i) &&
                           Ansi16ThemeKeys.TryGetValue(i, out var themeKey) &&
                           innerLookup(themeKey) is IBrush themeBrush)
                       {
                           brushedStyle = brushedStyle with { Foreground = themeBrush };
                       }

                       return brushedStyle;
                   }

                   // 2. A bare resource name resolved through the element's chain to an IBrush.
                   if (innerLookup(value) is IBrush brush)
                       return new BrushedStyle(brush);

                   return null; // unknown — the parser raises "Unrecognized brush"
               };
    }
}