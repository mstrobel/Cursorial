using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The variant-probe order (design doc §11.2, CD8) — precomputed static tables (8 effective
/// variants, zero per-lookup allocation). For an effective <c>(B, T)</c> the probe order is:
/// <list type="number">
/// <item><c>(B,T) → (B,T−1) → … → (B,NoColor)</c> — exact-base tier descent;</item>
/// <item><c>(·,T) → … → (·,NoColor)</c> — wildcard-base tier descent;</item>
/// <item><c>(B,·)</c> — base-only catch-all, probed <b>last</b>.</item>
/// </list>
/// A tier key declares a <b>minimum</b> capability: descent never ascends (a <c>(B,Truecolor)</c>
/// key serves only Truecolor; <c>(B,Ansi16)</c> serves Ansi16 and above). <c>(B,·)</c> is
/// contractually renderable at every tier including NoColor.
/// </summary>
internal static class ThemeVariantProbe
{
    // Tiers, strongest-first (numeric order is NoColor=0 < Ansi16 < Ansi256 < Truecolor).
    private static readonly ColorDepth[] TiersHighToLow =
    [
        ColorDepth.Truecolor, ColorDepth.Ansi256, ColorDepth.Ansi16, ColorDepth.NoColor
    ];

    // Per-effective-variant precomputed probe sequence. Index = ((int)Base * 4) + tierOrdinal.
    private static readonly ThemeVariantKey[][] Tables = BuildTables();

    /// <summary>The probe sequence for an effective variant (CD8), shared static — never mutate.</summary>
    public static ReadOnlySpan<ThemeVariantKey> Order(ThemeVariant variant)
        => Tables[Index(variant.Base, variant.Tier)];

    private static int Index(ThemeBase @base, ColorDepth tier)
        => (int)@base * 4 + (int)tier;

    private static ThemeVariantKey[][] BuildTables()
    {
        var tables = new ThemeVariantKey[2 * 4][];

        foreach (ThemeBase @base in (ReadOnlySpan<ThemeBase>)[ThemeBase.Dark, ThemeBase.Light])
        {
            foreach (var tier in TiersHighToLow)
            {
                var sequence = new List<ThemeVariantKey>(9);

                // 1. exact-base tier descent: (B,T) → … → (B,NoColor)
                foreach (var t in DescendFrom(tier))
                    sequence.Add(new ThemeVariantKey(@base, t));

                // 2. wildcard-base tier descent: (·,T) → … → (·,NoColor)
                foreach (var t in DescendFrom(tier))
                    sequence.Add(new ThemeVariantKey(null, t));

                // 3. base-only catch-all, last.
                sequence.Add(new ThemeVariantKey(@base, null));

                tables[Index(@base, tier)] = [.. sequence];
            }
        }

        return tables;
    }

    // The tiers from `tier` down to NoColor (descent never ascends).
    private static IEnumerable<ColorDepth> DescendFrom(ColorDepth tier)
    {
        for (var t = (int)tier; t >= 0; t--)
            yield return (ColorDepth)t;
    }
}
