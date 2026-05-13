namespace Cursorial.Output;

/// <summary>
/// The maximum color fidelity an output channel supports. Values are ordered from least to
/// most capable so a numeric comparison (<c>depth &gt;= ColorDepth.Ansi256</c>) is meaningful.
/// </summary>
public enum ColorDepth
{
    /// <summary>No color information is honored. Output is monochrome.</summary>
    NoColor = 0,

    /// <summary>The classic 8 ANSI colors plus 8 bright variants (SGR 30–37, 40–47, 90–97, 100–107).</summary>
    Ansi16 = 1,

    /// <summary>The 256-color palette via SGR 38;5;n / 48;5;n.</summary>
    Ansi256 = 2,

    /// <summary>24-bit RGB color via SGR 38;2;r;g;b / 48;2;r;g;b.</summary>
    Truecolor = 3
}