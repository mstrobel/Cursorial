using System.Reflection;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// Convenience accessors for FIGlet fonts. Bundles five public-domain fonts from the canonical
/// FIGlet 2.x distribution (all by Glenn Chappell, 1993) as embedded resources — load any of
/// them as <see cref="FigletFont"/> instances via the static properties below. Custom fonts
/// load via <see cref="Load"/> or <see cref="LoadFromFile"/>.
/// </summary>
/// <remarks>
/// The bundled fonts are loaded lazily on first access and cached for the life of the
/// process. They are intentionally a small curated set — covering the canonical look
/// (<see cref="Standard"/>), a slanted variant (<see cref="Slant"/>), a compact small face
/// (<see cref="Small"/>), a large block face (<see cref="Big"/>), and a minimal two-cell
/// face (<see cref="Mini"/>). Anything beyond these load from disk via <see cref="LoadFromFile"/>.
/// </remarks>
public static class FigletFonts
{
    private static readonly Lazy<FigletFont> s_standard = new(() => LoadEmbedded("standard.flf"));
    private static readonly Lazy<FigletFont> s_slant = new(() => LoadEmbedded("slant.flf"));
    private static readonly Lazy<FigletFont> s_small = new(() => LoadEmbedded("small.flf"));
    private static readonly Lazy<FigletFont> s_big = new(() => LoadEmbedded("big.flf"));
    private static readonly Lazy<FigletFont> s_mini = new(() => LoadEmbedded("mini.flf"));

    /// <summary>
    /// The canonical "Standard" FIGlet font (Glenn Chappell &amp; Ian Chai, 1993). 6 rows tall;
    /// the recognizable look most people associate with FIGlet output.
    /// </summary>
    public static FigletFont Standard => s_standard.Value;

    /// <summary>
    /// A slanted (italic-feel) variant of <see cref="Standard"/> (Glenn Chappell, 1993).
    /// </summary>
    public static FigletFont Slant => s_slant.Value;

    /// <summary>
    /// A compact 5-row face derived from <see cref="Standard"/> (Glenn Chappell, 1993). Good
    /// for sub-headings where the full-size standard font is too tall.
    /// </summary>
    public static FigletFont Small => s_small.Value;

    /// <summary>
    /// A large 8-row block face (Glenn Chappell, 1993). Suited to big titles where vertical
    /// space is plentiful.
    /// </summary>
    public static FigletFont Big => s_big.Value;

    /// <summary>
    /// A minimal 4-row face (Glenn Chappell, 1993). Useful for fitting "headline" text into
    /// tight UI affordances.
    /// </summary>
    public static FigletFont Mini => s_mini.Value;

    /// <summary>
    /// Load a FIGlet font from a stream. Equivalent to <see cref="FigletFontParser.Load"/>;
    /// exposed here so callers don't need to import the parser namespace for the common case.
    /// </summary>
    public static FigletFont Load(Stream stream, string name)
        => FigletFontParser.Load(stream, name);

    /// <summary>
    /// Load a FIGlet font from a <c>.flf</c> file path. The font's <see cref="FigletFont.Name"/>
    /// is taken from the file stem.
    /// </summary>
    public static FigletFont LoadFromFile(string path)
        => FigletFontParser.LoadFromFile(path);

    private static FigletFont LoadEmbedded(string resourceLeaf)
    {
        var assembly = typeof(FigletFonts).Assembly;
        var resourceName = $"Cursorial.Rendering.Fonts.Embedded.{resourceLeaf}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded FIGlet font '{resourceName}' is missing from the assembly. " +
                               "This indicates a packaging error in Cursorial.Rendering.");

        return FigletFontParser.Load(stream, Path.GetFileNameWithoutExtension(resourceLeaf));
    }
}