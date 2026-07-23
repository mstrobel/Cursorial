using Cursorial.Rendering.Content;

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
    private static readonly Lazy<FigletFont> s_standard = new(() => LoadEmbedded("Fonts/Embedded/standard.flf"));
    private static readonly Lazy<FigletFont> s_slant = new(() => LoadEmbedded("Fonts/Embedded/slant.flf"));
    private static readonly Lazy<FigletFont> s_small = new(() => LoadEmbedded("Fonts/Embedded/small.flf"));
    private static readonly Lazy<FigletFont> s_big = new(() => LoadEmbedded("Fonts/Embedded/big.flf"));
    private static readonly Lazy<FigletFont> s_mini = new(() => LoadEmbedded("Fonts/Embedded/mini.flf"));

    private static readonly Lazy<FigletFont> s_ansiShadow = new(() => LoadEmbedded("Fonts/Embedded/ansi-shadow.flf"));
    private static readonly Lazy<FigletFont> s_hp2640LargeType = new(() => LoadEmbedded("Fonts/Embedded/hp2640-largetype.flf"));
    private static readonly Lazy<FigletFont> s_miniWi = new(() => LoadEmbedded("Fonts/Embedded/miniwi.flf"));
    private static readonly Lazy<FigletFont> s_cga = new(() => LoadEmbedded("Fonts/Embedded/phm-cga.flf"));
    private static readonly Lazy<FigletFont> s_lcdMatrix = new(() => LoadEmbedded("Fonts/Embedded/phm-lcdmatrix.flf"));
    private static readonly Lazy<FigletFont> s_led = new(() => LoadEmbedded("Fonts/Embedded/phm-leds.flf"));
    private static readonly Lazy<FigletFont> s_roman = new(() => LoadEmbedded("Fonts/Embedded/roman.flf"));
    private static readonly Lazy<FigletFont> s_smallSlant = new(() => LoadEmbedded("Fonts/Embedded/small-slant.flf"));

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
    /// A classic large block face with a shadow (Glenn Chappell, 1993).
    /// </summary>
    public static FigletFont AnsiShadow => s_ansiShadow.Value;
    
    /// <summary>
    /// Recreation of the HP6440 terminals' Large Character Set, by Philippe Majerus.
    /// </summary>
    public static FigletFont Hp2640LargeType => s_hp2640LargeType.Value;
    
    /// <summary>
    /// Recreation of the 'miniwi' bitmap font.
    /// </summary>
    public static FigletFont MiniWi => s_miniWi.Value;
    
    /// <summary>
    /// Recreaton of the IBM CGA bold, straight from the CP437 ROM, by Philippe Majerus.
    /// </summary>
    public static FigletFont CGA => s_cga.Value;
    
    /// <summary>
    /// Inspired by the Motorola MC6847 character generator, Tatung Einstein TC-01, TRS-80, and other
    /// computers of the 1980s, this font features LCD-like 6×8 semi-pixels.
    /// </summary>
    public static FigletFont LCDMatrix => s_lcdMatrix.Value;
    
    /// <summary>
    /// This font looks like a LED marquee or low-resolution LCD matrix displays. By Philippe Majerus.
    /// </summary>
    public static FigletFont LED => s_led.Value;
    
    /// <summary>
    /// The classic FIGlet font "Roman" by Nick Miners, circa 1994.
    /// </summary>
    public static FigletFont Roman => s_roman.Value;

    
    /// <summary>
    /// A smaller variant of the popular <see cref="Slant"/> font (Glenn Chappell, 1993).
    /// </summary>
    public static FigletFont SmallSlant => s_smallSlant.Value;

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

    private static FigletFont LoadEmbedded(string resourceLeaf, string? nameOverride = null)
    {
        var uri = ResourceLoader.Embedded(typeof(FigletFonts).Assembly, resourceLeaf);
        using var stream = ResourceLoader.Default.Open(uri);
        return FigletFontParser.LoadFromUri(uri, nameOverride);
    }
}