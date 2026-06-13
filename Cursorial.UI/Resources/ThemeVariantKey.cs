using Cursorial.Output;

namespace Cursorial.UI;

/// <summary>
/// A <see cref="ResourceDictionary.ThemeDictionaries"/> key (design doc §11.2): a wildcard-bearing
/// <c>(<see cref="ThemeBase"/>?, <see cref="ColorDepth"/>?)</c> pair. A <see langword="null"/> axis
/// is a wildcard (<c>·</c> in the matrix). The all-wildcard <c>(null, null)</c> is rejected by
/// <see cref="Parse"/> — every key constrains at least one axis. Value equality (CD1).
/// </summary>
/// <param name="Base">The light/dark constraint, or <see langword="null"/> to match any base.</param>
/// <param name="Tier">The minimum color tier, or <see langword="null"/> to match any tier.</param>
public readonly record struct ThemeVariantKey(ThemeBase? Base, ColorDepth? Tier)
{
    /// <summary>Whether this key constrains the base axis.</summary>
    public bool HasBase => Base.HasValue;

    /// <summary>Whether this key constrains the tier axis.</summary>
    public bool HasTier => Tier.HasValue;

    /// <summary>
    /// Parses the textual form (CD10): <c>"Dark"</c> → <c>(Dark, null)</c>; <c>"Ansi16"</c> →
    /// <c>(null, Ansi16)</c>; <c>"Dark+Ansi16"</c> → <c>(Dark, Ansi16)</c>. Case-insensitive token
    /// matching; the base token is <c>Dark|Light</c>, the tier token <c>NoColor|Ansi16|Ansi256|Truecolor</c>.
    /// An empty / unparseable string, or a key that would constrain no axis, throws
    /// <see cref="FormatException"/>.
    /// </summary>
    public static ThemeVariantKey Parse(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.IsEmpty)
            throw new FormatException("A ThemeVariantKey must constrain at least one axis; the string was empty.");

        ThemeBase? @base = null;
        ColorDepth? tier = null;

        foreach (var range in Split(text))
        {
            var token = text[range].Trim();
            if (token.IsEmpty)
                throw new FormatException($"Malformed ThemeVariantKey '{text}': empty token between '+' separators.");

            if (TryParseBase(token, out var b))
            {
                if (@base.HasValue)
                    throw new FormatException($"Malformed ThemeVariantKey '{text}': two base tokens.");
                @base = b;
            }
            else if (TryParseTier(token, out var t))
            {
                if (tier.HasValue)
                    throw new FormatException($"Malformed ThemeVariantKey '{text}': two tier tokens.");
                tier = t;
            }
            else
            {
                throw new FormatException($"Malformed ThemeVariantKey '{text}': unrecognized token '{token}'.");
            }
        }

        if (@base is null && tier is null)
            throw new FormatException($"ThemeVariantKey '{text}' constrains no axis; (null, null) is rejected.");

        return new ThemeVariantKey(@base, tier);
    }

    private static bool TryParseBase(ReadOnlySpan<char> token, out ThemeBase value)
    {
        if (token.Equals("Dark", StringComparison.OrdinalIgnoreCase)) { value = ThemeBase.Dark; return true; }
        if (token.Equals("Light", StringComparison.OrdinalIgnoreCase)) { value = ThemeBase.Light; return true; }
        value = default;
        return false;
    }

    private static bool TryParseTier(ReadOnlySpan<char> token, out ColorDepth value)
    {
        if (token.Equals("NoColor", StringComparison.OrdinalIgnoreCase)) { value = ColorDepth.NoColor; return true; }
        if (token.Equals("Ansi16", StringComparison.OrdinalIgnoreCase)) { value = ColorDepth.Ansi16; return true; }
        if (token.Equals("Ansi256", StringComparison.OrdinalIgnoreCase)) { value = ColorDepth.Ansi256; return true; }
        if (token.Equals("Truecolor", StringComparison.OrdinalIgnoreCase)) { value = ColorDepth.Truecolor; return true; }
        value = default;
        return false;
    }

    // Allocation-free '+' split returning Range[] without LINQ; at most two tokens in practice but
    // tolerant of more (the malformed-token path catches them).
    private static List<Range> Split(ReadOnlySpan<char> text)
    {
        var ranges = new List<Range>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '+')
            {
                ranges.Add(new Range(start, i));
                start = i + 1;
            }
        }

        ranges.Add(new Range(start, text.Length));
        return ranges;
    }

    /// <inheritdoc/>
    public override string ToString()
        => (Base, Tier) switch
        {
            (not null, not null) => $"{Base}+{Tier}",
            (not null, null) => $"{Base}",
            (null, not null) => $"{Tier}",
            _ => "(invalid)"
        };
}
