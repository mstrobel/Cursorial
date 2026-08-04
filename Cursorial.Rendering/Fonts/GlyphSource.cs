using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// A run's glyph source: how its clusters measure and paint (proposal-glyph-runs Phase 1).
/// <see langword="null"/> <see cref="Font"/> with default <see cref="Sizing"/> is the monospace
/// identity — ordinary cell text. A non-default <see cref="Sizing"/> renders via OSC 66 with
/// <see cref="Font"/> as the fallback face on non-supporting terminals; a <see cref="Font"/>
/// with default sizing renders through that font directly (a FIGlet headline run).
/// </summary>
/// <remarks>
/// Immutable and freely shareable — one source typically decorates many runs. The resolved
/// <see cref="Metrics"/> is cached on the instance (metrics are consulted per grapheme cluster
/// during tokenization and splitting, so the resolution must not repeat per cluster).
/// Equality is by <see cref="Font"/> reference + <see cref="Sizing"/> value; the cache does not
/// participate.
/// </remarks>
public sealed record GlyphSource(IGlyphFont? Font, TextSizing Sizing = default)
{
    /// <summary>The monospace identity — what every run without an explicit source uses.</summary>
    public static GlyphSource Default { get; } = new((IGlyphFont?)null);

    public string DisplayName => Sizing is { IsNormal: false } sz 
                                     ? sz.DisplayName
                                     : Font?.DisplayName ?? MonospaceFont.Default.DisplayName;

    public bool IsFiglet => Sizing is { IsNormal: true } && Font is FigletFont;

    /// <summary>
    /// Whether this source's runs paint through the ordinary cell walk. Distinct from having
    /// identity METRICS: a half-height run (<c>s=1, n=1, d=2</c>) measures 1×1 but must still
    /// emit OSC 66 for its visual effect, so the test is "identity face, fully-normal sizing"
    /// (<see cref="MonospaceFont"/> IS the cell walk — routing it through the fallback tree
    /// would only lose per-cell brush sampling).
    /// </summary>
    public bool PaintsAsCells => Font is null or MonospaceFont && Sizing.IsNormal;

    /// <summary>
    /// The advance metrics layout consults for this source's runs. Sizing wins over
    /// <see cref="Font"/> — a scaled run's cell footprint is the protocol's, whatever face may
    /// paint the fallback (on a non-OSC-66 terminal <see cref="Content.ScaledText"/> paints
    /// within the same reserved cells, degrading to its monospace face when the fallback font
    /// cannot fit the band).
    /// </summary>
    public GlyphMetrics Metrics => field ??= Resolve();

    private GlyphMetrics Resolve()
    {
        // Keyed on IsNormal, not the footprint: a footprint-identity FRACTIONAL sizing
        // (s=1:n=1:d=2) still paints through OSC 66, so its metrics must be the scaled identity
        // — using an explicit fallback Font's metrics here would measure a face the terminal
        // never paints.
        if (!Sizing.IsNormal)
            return MonospaceFont.Default.GetScaledMetrics(Sizing);

        return Font?.GetMetrics() ?? GlyphMetrics.Monospace;
    }

    /// <summary>Value equality over <see cref="Font"/> + <see cref="Sizing"/> ONLY — the lazily
    /// resolved metrics cache must not participate: the compiler-synthesized record equality
    /// compares every field, so a resolved and an unresolved copy of the same source would
    /// compare unequal and the hash would MUTATE when the cache filled.</summary>
    public bool Equals(GlyphSource? other)
        => other is not null && Equals(Font, other.Font) && Sizing.Equals(other.Sizing);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Font, Sizing);

    /// <summary>
    /// The source layout should ACTUALLY use on a terminal with <paramref name="capabilities"/> —
    /// the same decision tree <see cref="Content.ScaledText"/> walks at paint, settled at layout
    /// time so measurement and paint agree. A sizing the terminal supports (or that changes no
    /// footprint) resolves to itself; an unsupported one falls back to the explicit
    /// <see cref="Font"/>, or the bundled default face for the sizing (tier parity with the old
    /// block path: a scale-2 headline on a dumb terminal measures AND paints as a FIGlet
    /// headline, not as clipped monospace).
    /// </summary>
    public GlyphSource ResolveFor(OutputCapabilities capabilities)
    {
        if (Sizing.IsNormal || Sizing.IsSupported(capabilities))
            return this;

        return new GlyphSource(Font ?? Content.ScaledText.PickDefaultFallback(Sizing, isMultiLine: false));
    }
}
