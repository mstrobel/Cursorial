namespace Cursorial.Rendering.Text;

/// <summary>
/// Built-in <see cref="IGlyphMap"/> implementations and helpers for constructing custom maps
/// from substitution tables. All built-ins are stateless and safe to share across threads.
/// </summary>
/// <remarks>
/// <para>
/// The built-ins cover the typography transforms most commonly requested in terminal UIs:
/// fullwidth Latin (used in CJK contexts and decorative banners), mathematical double-struck
/// (set-theory notation), small-caps Latin (subtle emphasis), and the Unicode super/subscript
/// blocks (chemical formulae, math). Coverage is best-effort within Unicode's actual support;
/// graphemes outside a map's table pass through unchanged.
/// </para>
/// </remarks>
public static class GlyphMaps
{
    /// <summary>Identity map — every grapheme is returned unchanged.</summary>
    public static IGlyphMap Identity { get; } = new IdentityMap();

    /// <summary>
    /// Fullwidth Latin and digit forms (U+FF01–U+FF5E). ASCII printable characters and digits
    /// map to their double-cell-width cousins; ASCII space maps to the ideographic space
    /// (U+3000) which most terminals render at two cells wide.
    /// </summary>
    public static IGlyphMap Fullwidth { get; } = new FullwidthMap();

    /// <summary>
    /// Mathematical double-struck letters (𝔸 𝕒 𝟘). Uses the contiguous Plane-1 range
    /// (U+1D538+) plus the BMP-encoded letters Unicode keeps in the "Letterlike Symbols" block
    /// (ℂ ℍ ℕ ℙ ℚ ℝ ℤ).
    /// </summary>
    public static IGlyphMap DoubleStruck { get; } = new DoubleStruckMap();

    /// <summary>
    /// Small-capital Latin lowercase letters (ᴀ ʙ ᴄ …). Uppercase and non-letters pass through.
    /// Coverage matches Unicode's available small-caps inventory; a handful of letters fall back
    /// to ASCII when no canonical small-cap form exists.
    /// </summary>
    public static IGlyphMap SmallCaps { get; } = new SmallCapsMap();

    /// <summary>
    /// Superscript digits and common operators (⁰ ¹ ² ⁺ ⁻ ⁽ ⁾). Letters and other characters
    /// pass through unchanged.
    /// </summary>
    public static IGlyphMap Superscript { get; } = new SuperscriptMap();

    /// <summary>
    /// Subscript digits and common operators (₀ ₁ ₂ ₊ ₋ ₍ ₎). Letters and other characters
    /// pass through unchanged.
    /// </summary>
    public static IGlyphMap Subscript { get; } = new SubscriptMap();

    /// <summary>
    /// Construct a custom <see cref="IGlyphMap"/> from a substitution dictionary. Graphemes not
    /// present in the table pass through unchanged. The supplied dictionary is captured by
    /// reference; mutating it after construction changes the map's behavior — pass a frozen
    /// snapshot for shareable maps.
    /// </summary>
    public static IGlyphMap From(IReadOnlyDictionary<string, string> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new DictionaryMap(table);
    }

    private sealed class IdentityMap : IGlyphMap
    {
        public string Map(ReadOnlySpan<char> grapheme) => grapheme.ToString();
    }

    private sealed class DictionaryMap(IReadOnlyDictionary<string, string> table) : IGlyphMap
    {
        public string Map(ReadOnlySpan<char> grapheme)
        {
            // ReadOnlyDictionary doesn't offer a span-keyed lookup; allocate the string only on
            // miss after a fast-path probe via Lookup<TKey,TValue>.GetAlternateLookup once that's
            // wired up by callers using FrozenDictionary. For now: allocate once.
            var key = grapheme.ToString();
            return table.TryGetValue(key, out var result) ? result : key;
        }
    }

    private sealed class FullwidthMap : IGlyphMap
    {
        public string Map(ReadOnlySpan<char> grapheme)
        {
            if (grapheme.Length != 1) return grapheme.ToString();
            char c = grapheme[0];
            if (c == ' ') return "　";                       // ideographic space
            if (c is >= '!' and <= '~') return ((char) (0xFF00 + (c - 0x20))).ToString();
            return grapheme.ToString();
        }
    }

    private sealed class DoubleStruckMap : IGlyphMap
    {
        // Uppercase letters with BMP-encoded "Letterlike Symbol" exceptions; the rest live
        // contiguously in Plane 1 (U+1D538..). Each Plane-1 codepoint is a UTF-16 surrogate
        // pair, so the map returns a 2-char string.
        public string Map(ReadOnlySpan<char> grapheme)
        {
            if (grapheme.Length != 1) return grapheme.ToString();
            char c = grapheme[0];

            return c switch
            {
                'C' => "ℂ", 'H' => "ℍ", 'N' => "ℕ", 'P' => "ℙ",
                'Q' => "ℚ", 'R' => "ℝ", 'Z' => "ℤ",
                >= 'A' and <= 'Z' => char.ConvertFromUtf32(0x1D538 + (c - 'A')),
                >= 'a' and <= 'z' => char.ConvertFromUtf32(0x1D552 + (c - 'a')),
                >= '0' and <= '9' => char.ConvertFromUtf32(0x1D7D8 + (c - '0')),
                _ => grapheme.ToString(),
            };
        }
    }

    private sealed class SmallCapsMap : IGlyphMap
    {
        public string Map(ReadOnlySpan<char> grapheme)
        {
            if (grapheme.Length != 1) return grapheme.ToString();
            return grapheme[0] switch
            {
                'a' => "ᴀ", 'b' => "ʙ", 'c' => "ᴄ", 'd' => "ᴅ",
                'e' => "ᴇ", 'f' => "ꜰ", 'g' => "ɢ", 'h' => "ʜ",
                'i' => "ɪ", 'j' => "ᴊ", 'k' => "ᴋ", 'l' => "ʟ",
                'm' => "ᴍ", 'n' => "ɴ", 'o' => "ᴏ", 'p' => "ᴘ",
                'q' => "ꞯ", 'r' => "ʀ", 's' => "ꜱ", 't' => "ᴛ",
                'u' => "ᴜ", 'v' => "ᴠ", 'w' => "ᴡ", 'x' => "x",
                'y' => "ʏ", 'z' => "ᴢ",
                _ => grapheme.ToString(),
            };
        }
    }

    private sealed class SuperscriptMap : IGlyphMap
    {
        public string Map(ReadOnlySpan<char> grapheme)
        {
            if (grapheme.Length != 1) return grapheme.ToString();
            return grapheme[0] switch
            {
                '0' => "⁰", '1' => "¹", '2' => "²", '3' => "³",
                '4' => "⁴", '5' => "⁵", '6' => "⁶", '7' => "⁷",
                '8' => "⁸", '9' => "⁹",
                '+' => "⁺", '-' => "⁻", '=' => "⁼",
                '(' => "⁽", ')' => "⁾",
                _ => grapheme.ToString(),
            };
        }
    }

    private sealed class SubscriptMap : IGlyphMap
    {
        public string Map(ReadOnlySpan<char> grapheme)
        {
            if (grapheme.Length != 1) return grapheme.ToString();
            return grapheme[0] switch
            {
                '0' => "₀", '1' => "₁", '2' => "₂", '3' => "₃",
                '4' => "₄", '5' => "₅", '6' => "₆", '7' => "₇",
                '8' => "₈", '9' => "₉",
                '+' => "₊", '-' => "₋", '=' => "₌",
                '(' => "₍", ')' => "₎",
                _ => grapheme.ToString(),
            };
        }
    }
}
