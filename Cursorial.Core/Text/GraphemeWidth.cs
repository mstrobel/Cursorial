using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cursorial.Text;

/// <summary>
/// Compute the display width of Unicode text in terminal cells.
/// </summary>
/// <remarks>
/// <para>
/// "Width" is the number of monospace cells the terminal allocates when rendering the text:
/// 0 for combining marks and zero-width characters, 1 for ordinary printable characters, 2 for
/// East Asian wide / fullwidth glyphs and most emoji. A <see cref="StringWidth(ReadOnlySpan{char})"/> call
/// iterates grapheme clusters (via <see cref="StringInfo"/>) so emoji-with-modifiers and other
/// multi-codepoint glyphs are measured as a single unit.
/// </para>
/// <para>
/// <b>Accuracy.</b> The wide-character ranges below are hand-coded to cover the common majority:
/// Hangul, CJK Unified Ideographs (Planes 0–3), Compatibility Ideographs, Fullwidth Forms, the
/// major emoji blocks, <b>and the UTS #51 <c>Emoji_Presentation=Yes</c> codepoints scattered
/// through the legacy symbol blocks</b> (⌚ ⏰ ☕ ⚡ ⛔ ✅ ❌ ❗ ⭐ ⭕ …) — default-emoji glyphs that
/// modern terminals render two cells wide even though wcwidth-era tables call them narrow.
/// Recently added codepoints outside these ranges will report width 1 instead of 2. A full
/// <c>EastAsianWidth.txt</c>-backed table can drop in later without breaking the public
/// surface — the API is just <c>int</c> in / <c>int</c> out.
/// </para>
/// <para>
/// <b>Variation selectors.</b> <c>U+FE0F</c> (VS16) forces emoji presentation and is treated as
/// retroactively bumping the preceding base codepoint to width 2; <c>U+FE0E</c> (VS15) forces
/// text presentation and pins the preceding base at width 1. Both are themselves zero-width.
/// </para>
/// </remarks>
public static class GraphemeWidth
{
    /// <summary>
    /// Width of a single Rune codepoint without considering its surrounding cluster context.
    /// Returns 0 for combining marks, control characters, format controls, and variation
    /// selectors; 2 for East Asian wide / fullwidth glyphs and default-emoji ranges; 1 for
    /// everything else printable.
    /// </summary>
    /// <param name="rune">A rune representing a scalar value (0–0x10FFFF, excluding surrogates).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CodepointWidth(Rune rune)
    {
        return CodepointWidth(rune.Value);
    }

    /// <summary>
    /// Width of a single Unicode codepoint without considering its surrounding cluster context.
    /// Returns 0 for combining marks, control characters, format controls, and variation
    /// selectors; 2 for East Asian wide / fullwidth glyphs and default-emoji ranges; 1 for
    /// everything else printable.
    /// </summary>
    /// <param name="codepoint">A Unicode scalar value (0–0x10FFFF, excluding surrogates).</param>
    public static int CodepointWidth(int codepoint)
    {
        // ASCII fast path — printable ASCII is overwhelmingly the common case.
        if ((uint) codepoint < 0x80)
        {
            return codepoint switch
                   {
                       < 0x20 => 0, // C0 controls.
                       0x7F   => 0, // DEL.
                       _      => 1
                   };
        }

        if (codepoint < 0)
            throw new ArgumentOutOfRangeException(nameof(codepoint), "Codepoint must be non-negative.");

        if (codepoint > 0x10FFFF)
            throw new ArgumentOutOfRangeException(nameof(codepoint), "Codepoint exceeds Unicode maximum.");

        // Latin-1 controls (C1) are zero-width.
        if (codepoint is >= 0x80 and < 0xA0) return 0;

        // Soft hyphen and other format controls — render as zero width.
        // Use Rune.GetUnicodeCategory for everything that needs a category check.
        if (!Rune.IsValid(codepoint)) return 0; // Surrogate halves — should not occur - defensive.
        var category = Rune.GetUnicodeCategory(new Rune(codepoint));

        switch (category)
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
                return 0;
        }

        // Wide / fullwidth ranges. Ordered to short-circuit the BMP fast-path before reaching
        // for the supplementary-plane checks.
        if (IsWide(codepoint)) return 2;

        return 1;
    }

    /// <summary>
    /// Width of a grapheme cluster — a span of UTF-16 code units representing one user-visible
    /// glyph. Honors VS16/VS15 retroactive width adjustments and treats ZWJ joiners + combining
    /// marks as zero-width continuations of the base.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for passing exactly one grapheme cluster. Use
    /// <see cref="StringWidth(ReadOnlySpan{char})"/> for arbitrary text where cluster boundaries
    /// aren't known.
    /// </remarks>
    public static int ClusterWidth(ReadOnlySpan<char> cluster)
    {
        if (cluster.IsEmpty) return 0;

        int width = 0;
        bool seenBase = false;
        int i = 0;

        while (i < cluster.Length)
        {
            if (Rune.DecodeFromUtf16(cluster[i..], out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                // Invalid sequence — skip the offending code unit and continue. Defensive only;
                // well-formed input never reaches this branch.
                i++;
                continue;
            }

            i += consumed;

            int cp = rune.Value;

            if (cp == 0xFE0F)
            {
                // Emoji presentation selector — bump width to at least 2.
                width = Math.Max(width, 2);
                continue;
            }

            if (cp == 0xFE0E)
            {
                // Text presentation selector — pin to 1 if the base was 2 by default, but we
                // want text rendering.
                if (!seenBase) continue;
                width = Math.Min(width, 1);
                continue;
            }

            if (cp == 0x200D)
            {
                // ZWJ — continuation of the joined sequence, doesn't add width.
                continue;
            }

            int w = CodepointWidth(cp);

            if (!seenBase)
            {
                width = w;
                seenBase = true;
            }
            else
            {
                // Subsequent codepoints in a cluster are typically combining marks (width 0).
                // If a second base sneaks in (malformed cluster), keep the max.
                width = Math.Max(width, w);
            }
        }

        return width;
    }

    /// <summary>
    /// Total display width of <paramref name="text"/>. Enumerates grapheme clusters via
    /// <see cref="StringInfo"/> so multi-codepoint glyphs are measured as a single unit.
    /// </summary>
    public static int StringWidth(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0;

        int total = 0;
        var enumerator = text.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            total += ClusterWidth(cluster);
        }

        return total;
    }

    /// <summary>
    /// Calculates the number of grapheme clusters in the specified text string.
    /// A grapheme cluster is the smallest unit of a writing system that displays as a single character
    /// (e.g., letters, emojis, and composed characters).
    /// </summary>
    /// <param name="text">The input string for which grapheme clusters are to be counted.</param>
    /// <returns>The total number of grapheme clusters in the provided string.</returns>
    public static int ClusterCount(ReadOnlySpan<char> text)
    {
        int clusters = 0;
        var enumerator = text.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
            clusters++;

        return clusters;
    }

    /// <summary>
    /// True for East Asian Wide / Fullwidth codepoints and default-emoji ranges. The table
    /// below is intentionally partial: it covers the major blocks used by ~95% of real-world
    /// text. Codepoints outside these ranges fall through to width 1, which is the conservative
    /// answer for unknown text.
    /// </summary>
    private static bool IsWide(int cp)
    {
        // BMP ranges.
        if (cp <= 0xFFFF)
        {
            return cp switch
                   {
                       >= 0x1100 and <= 0x115F => true, // Hangul Jamo
                       0x2329 or 0x232A        => true, // Left/right-pointing angle bracket

                       // Emoji_Presentation=Yes codepoints in the legacy symbol blocks (UTS #51):
                       // default-emoji glyphs modern terminals render two cells wide even though
                       // they predate the emoji blocks (⌚⏰☔✅❌⭐⭕ …). Shared with the
                       // compositor's emoji-stomp classifier — one source, no drift. Their absence
                       // measured ✅/❌ as width 1 → per-glyph cursor drift in the incremental diff.
                       _ when IsEmojiPresentationScalar(cp) => true,

                       >= 0x2E80 and <= 0x303E => true, // CJK Radicals, Kangxi Radicals, Ideographic Description, CJK Symbols and Punctuation (subset)
                       >= 0x3041 and <= 0x33FF => true, // Hiragana / Katakana / Bopomofo / Hangul Compatibility Jamo / Enclosed CJK / CJK Compatibility
                       >= 0x3400 and <= 0x4DBF => true, // CJK Extension A
                       >= 0x4E00 and <= 0x9FFF => true, // CJK Unified Ideographs
                       >= 0xA000 and <= 0xA4CF => true, // Yi Syllables / Yi Radicals
                       >= 0xA960 and <= 0xA97F => true, // Hangul Jamo Extended-A
                       >= 0xAC00 and <= 0xD7A3 => true, // Hangul Syllables
                       >= 0xF900 and <= 0xFAFF => true, // CJK Compatibility Ideographs
                       >= 0xFE10 and <= 0xFE19 => true, // Vertical Forms
                       >= 0xFE30 and <= 0xFE4F => true, // CJK Compatibility Forms
                       >= 0xFE50 and <= 0xFE6F => true, // Small Form Variants (some narrow but treated wide for simplicity)
                       >= 0xFF00 and <= 0xFF60 => true, // Fullwidth Forms
                       >= 0xFFE0 and <= 0xFFE6 => true, // Fullwidth signs
                       _                       => false
                   };
        }

        // Supplementary planes. Scripts and the Enclosed Ideographic block are EAW=W; everything
        // emoji-shaped delegates to the UTS #51 Emoji=Yes table (one source with the compositor's
        // emoji-stomp classifier). That union — never a whole-block blanket — is what fixed both
        // directions of drift: 🅱/🛠-class pictographs (Emoji=Yes, EAW=N/A) render two cells on
        // emoji-font terminals and were MISSING; alchemical/chess/Arrows-C symbols (EAW=N,
        // Emoji=No) render one cell and were wrongly claimed wide.
        return cp switch
               {
                   >= 0x16FE0 and <= 0x16FFF => true,  // Tangut/Kana supplement marks (EAW=W)
                   >= 0x17000 and <= 0x187FF => true,  // Tangut
                   >= 0x18800 and <= 0x18D0F => true,  // Tangut Components / Khitan Small Script / Tangut Supplement
                   >= 0x1AFF0 and <= 0x1AFFF => true,  // Kana Extended-B
                   >= 0x1B000 and <= 0x1B2FF => true,  // Kana Supplement / Extended-A / Small Kana / Nüshu
                   >= 0x1F200 and <= 0x1F265 => true,  // Enclosed Ideographic Supplement (🈁🈚🈯…; EAW=W incl. the non-emoji squared CJK)
                   >= 0x20000 and <= 0x2FFFD => true,  // CJK Extension B, C, D, E, F, I (Plane 2)
                   >= 0x30000 and <= 0x3FFFD => true,  // CJK Extension G, H (Plane 3)
                   _                         => IsEmojiPresentationScalar(cp)
               };
    }

    /// <summary>
    /// True when <paramref name="codepoint"/> is an <b>emoji-presentation</b> scalar: the
    /// UTS #51 <c>Emoji_Presentation=Yes</c> set in the legacy BMP symbol blocks, plus the full
    /// <c>Emoji=Yes</c> ranges in the supplementary plane (emoji-data.txt, Unicode 16 — precise
    /// ranges, not block blankets). These glyphs render as COLOR BITMAPS on emoji-font terminals
    /// and therefore ignore the SGR foreground — the property both consumers care about: the
    /// width table measures them 2 cells (an emoji font draws even the EAW=N/A members like
    /// 🅱 and 🛠 two cells wide), and the compositor stomps them under a covering layer (a
    /// foreground-tinted "hidden" emoji still renders in full color, straight through). BMP
    /// <c>Emoji=Yes</c>-but-text-default glyphs (bare ❤, ☺) stay out: they measure 1 cell and
    /// tint like text on most terminals, and widening them would change BMP width behavior.
    /// </summary>
    public static bool IsEmojiPresentationScalar(int codepoint) => codepoint switch
    {
        0x231A or 0x231B => true,          // ⌚ ⌛
        >= 0x23E9 and <= 0x23EC => true,   // ⏩..⏬
        0x23F0 or 0x23F3 => true,          // ⏰ ⏳
        0x25FD or 0x25FE => true,          // ◽ ◾
        0x2614 or 0x2615 => true,          // ☔ ☕
        >= 0x2648 and <= 0x2653 => true,   // ♈..♓ (zodiac)
        0x267F => true,                    // ♿
        0x2693 => true,                    // ⚓
        0x26A1 => true,                    // ⚡
        0x26AA or 0x26AB => true,          // ⚪ ⚫
        0x26BD or 0x26BE => true,          // ⚽ ⚾
        0x26C4 or 0x26C5 => true,          // ⛄ ⛅
        0x26CE => true,                    // ⛎
        0x26D4 => true,                    // ⛔
        0x26EA => true,                    // ⛪
        0x26F2 or 0x26F3 => true,          // ⛲ ⛳
        0x26F5 => true,                    // ⛵
        0x26FA => true,                    // ⛺
        0x26FD => true,                    // ⛽
        0x2705 => true,                    // ✅
        0x270A or 0x270B => true,          // ✊ ✋
        0x2728 => true,                    // ✨
        0x274C => true,                    // ❌
        0x274E => true,                    // ❎
        >= 0x2753 and <= 0x2755 => true,   // ❓ ❔ ❕
        0x2757 => true,                    // ❗
        >= 0x2795 and <= 0x2797 => true,   // ➕ ➖ ➗
        0x27B0 => true,                    // ➰
        0x27BF => true,                    // ➿
        0x2B1B or 0x2B1C => true,          // ⬛ ⬜
        0x2B50 => true,                    // ⭐
        0x2B55 => true,                    // ⭕

        // ── the supplementary plane: the UTS #51 Emoji=Yes ranges (emoji-data.txt, Unicode 16) ──
        // Precise, not a block blanket: the width table consumes this too, and a blanket claimed
        // 2 cells for EAW=N non-emoji neighbors (alchemical, chess, playing cards, Arrows-C).
        0x1F004 => true,                   // 🀄
        0x1F0CF => true,                   // 🃏
        0x1F170 or 0x1F171 => true,        // 🅰 🅱
        0x1F17E or 0x1F17F => true,        // 🅾 🅿
        0x1F18E => true,                   // 🆎
        >= 0x1F191 and <= 0x1F19A => true, // 🆑..🆚
        >= 0x1F1E6 and <= 0x1F1FF => true, // regional indicators (a pair composes one 2-cell flag)
        0x1F201 or 0x1F202 => true,        // 🈁 🈂
        0x1F21A => true,                   // 🈚
        0x1F22F => true,                   // 🈯
        >= 0x1F232 and <= 0x1F23A => true, // 🈲..🈺
        0x1F250 or 0x1F251 => true,        // 🉐 🉑
        >= 0x1F300 and <= 0x1F321 => true, // Misc Symbols and Pictographs …
        >= 0x1F324 and <= 0x1F393 => true,
        0x1F396 or 0x1F397 => true,
        >= 0x1F399 and <= 0x1F39B => true,
        >= 0x1F39E and <= 0x1F3F0 => true,
        >= 0x1F3F3 and <= 0x1F3F5 => true,
        >= 0x1F3F7 and <= 0x1F4FD => true,
        >= 0x1F4FF and <= 0x1F53D => true,
        >= 0x1F549 and <= 0x1F54E => true,
        >= 0x1F550 and <= 0x1F567 => true,
        0x1F56F or 0x1F570 => true,
        >= 0x1F573 and <= 0x1F57A => true,
        0x1F587 => true,
        >= 0x1F58A and <= 0x1F58D => true,
        0x1F590 => true,
        0x1F595 or 0x1F596 => true,
        0x1F5A4 or 0x1F5A5 => true,
        0x1F5A8 => true,
        0x1F5B1 or 0x1F5B2 => true,
        0x1F5BC => true,
        >= 0x1F5C2 and <= 0x1F5C4 => true,
        >= 0x1F5D1 and <= 0x1F5D3 => true,
        >= 0x1F5DC and <= 0x1F5DE => true,
        0x1F5E1 or 0x1F5E3 or 0x1F5E8 or 0x1F5EF or 0x1F5F3 => true,
        >= 0x1F5FA and <= 0x1F64F => true, // … through the Emoticons
        >= 0x1F680 and <= 0x1F6C5 => true, // Transport and Map Symbols …
        >= 0x1F6CB and <= 0x1F6D2 => true,
        >= 0x1F6D5 and <= 0x1F6D7 => true,
        >= 0x1F6DC and <= 0x1F6E5 => true,
        0x1F6E9 => true,
        0x1F6EB or 0x1F6EC => true,
        0x1F6F0 => true,
        >= 0x1F6F3 and <= 0x1F6FC => true,
        >= 0x1F7E0 and <= 0x1F7EB => true, // 🟠..🟫 colored circles/squares
        0x1F7F0 => true,                   // 🟰
        >= 0x1F90C and <= 0x1F93A => true, // Supplemental Symbols and Pictographs …
        >= 0x1F93C and <= 0x1F945 => true,
        >= 0x1F947 and <= 0x1F9FF => true,
        >= 0x1FA70 and <= 0x1FA7C => true, // Symbols and Pictographs Extended-A …
        >= 0x1FA80 and <= 0x1FA89 => true,
        >= 0x1FA8F and <= 0x1FAC6 => true,
        >= 0x1FACE and <= 0x1FADC => true,
        >= 0x1FADF and <= 0x1FAE9 => true,
        >= 0x1FAF0 and <= 0x1FAF8 => true,
        _ => false
    };

    /// <summary>
    /// True when <paramref name="cluster"/> renders with <b>emoji presentation</b> — a color
    /// bitmap immune to the SGR foreground: a default-emoji base
    /// (<see cref="IsEmojiPresentationScalar"/>) or any base forced to emoji by VS16 (U+FE0F);
    /// VS15 (U+FE0E) forces text presentation and wins. ZWJ sequences, skin tones, and
    /// regional-indicator flag pairs classify through their member scalars.
    /// </summary>
    public static bool IsEmojiPresentation(ReadOnlySpan<char> cluster)
    {
        var sawEmoji = false;
        var i = 0;

        while (i < cluster.Length)
        {
            if (Rune.DecodeFromUtf16(cluster[i..], out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                i++;
                continue;
            }

            i += consumed;

            switch (rune.Value)
            {
                case 0xFE0E:
                    return false; // explicit text presentation — renders in the foreground color

                case 0xFE0F:
                    sawEmoji = true;
                    break;

                default:
                    sawEmoji |= IsEmojiPresentationScalar(rune.Value);
                    break;
            }
        }

        return sawEmoji;
    }

    /// <summary>
    /// True for codepoints whose East Asian Width is <b>Ambiguous</b> (EAW=A) — glyphs the
    /// Unicode standard says render as width 1 in a non-East-Asian context but width 2 in an
    /// East-Asian one. Box-drawing rules, block elements, geometric shapes (▲▼), arrows, Greek,
    /// Cyrillic, and a swathe of Latin-1 / general-punctuation symbols all fall here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CodepointWidth(int)"/> resolves ambiguous codepoints to 1 — the correct default
    /// for the common (non-East-Asian) case. But a terminal configured to treat ambiguous width
    /// as <em>wide</em> (a CJK locale, or an explicit "ambiguous = double-width" setting in VTE /
    /// GNOME Terminal, etc.) renders them across 2 cells. When the renderer emits a run of such
    /// glyphs assuming width 1, the per-glyph disagreement accumulates and the row overflows the
    /// right margin. This predicate lets the renderer detect width-uncertain glyphs and defend
    /// the cursor position (re-anchor with an explicit move after each one) so the drift can't
    /// compound — see <c>FrameRenderer.EmitDiff</c>, gated on
    /// <c>TextSizingCapabilities.WideGlyphs == false</c> just like the wide-glyph neighbor-fill
    /// defense.
    /// </para>
    /// <para>
    /// The table covers the contiguous BMP blocks that are wholly or predominantly EAW=A and
    /// that actually appear in terminal UIs. It over-approximates a few blocks (treating the
    /// whole block as ambiguous when a handful of members are not) — that is deliberately safe:
    /// the only consequence of a false positive is one extra cursor-move on a terminal already
    /// flagged as unreliable for wide glyphs. ASCII (&lt; 0x80) is never ambiguous and returns
    /// false immediately, so plain prose stays on the fast contiguous-run path.
    /// </para>
    /// </remarks>
    public static bool IsAmbiguousWidth(int codepoint)
    {
        if ((uint) codepoint < 0x80) return false; // ASCII is unambiguously narrow.

        // A codepoint the width table already treats as WIDE is by definition not width-
        // UNCERTAIN — the block ranges below over-approximate, and the Emoji_Presentation
        // codepoints folded into the wide table (☔ ♈ ⚡ ✅ ❌ ◽ …) live inside them. This guard
        // keeps the wide and ambiguous sets disjoint by construction (the renderer must never
        // apply the ambiguous defense to a glyph it already emits as two cells).
        if (IsWide(codepoint)) return false;

        return codepoint switch
               {
                   0x00A1 or 0x00A4 or 0x00A7 or 0x00A8 or 0x00AA or 0x00AD or 0x00AE => true, // Latin-1 symbols (scattered EAW=A)
                   >= 0x00B0 and <= 0x00B4 => true, // ° ± ² ³ ´
                   >= 0x00B6 and <= 0x00BF => true, // ¶ · … ¿  (and the fraction / ordinal run)
                   0x00C6 or 0x00D0 or 0x00D7 or 0x00D8 => true, // Æ Ð × Ø
                   >= 0x00E0 and <= 0x00FF => true, // accented Latin small letters (predominantly EAW=A)
                   >= 0x0391 and <= 0x03C9 => true, // Greek letters (α … Ω)
                   >= 0x0401 and <= 0x0451 => true, // Cyrillic (Ё … ё) — predominantly EAW=A
                   >= 0x2010 and <= 0x203B => true, // General Punctuation (dashes, quotes, bullets, †‡•…‰)
                   0x203E => true, // Overline
                   >= 0x2070 and <= 0x209F => true, // Superscripts and Subscripts (⁰¹²…ₙ — used by [font=super/subscript])
                   >= 0x2153 and <= 0x215E => true, // Vulgar fractions
                   >= 0x2160 and <= 0x216B => true, // Roman numerals (uppercase)
                   >= 0x2170 and <= 0x2179 => true, // Roman numerals (lowercase)
                   >= 0x2190 and <= 0x21FF => true, // Arrows
                   >= 0x2200 and <= 0x22FF => true, // Mathematical Operators
                   >= 0x2460 and <= 0x24FF => true, // Enclosed Alphanumerics (①②…)
                   >= 0x2500 and <= 0x257F => true, // Box Drawing (─ ═ ┼ …) — the horizontal-rule glyphs
                   >= 0x2580 and <= 0x259F => true, // Block Elements (▀ ▄ █ ░▒▓ …)
                   >= 0x25A0 and <= 0x25FF => true, // Geometric Shapes (■ ● ▲ ▼ …)
                   >= 0x2600 and <= 0x26FF => true, // Miscellaneous Symbols (predominantly EAW=A)
                   >= 0x2700 and <= 0x27BF => true, // Dingbats (predominantly EAW=A)
                   >= 0xE000 and <= 0xF8FF => true, // Private Use Area (EAW=A) — Nerd Font icons live here;
                                                    // terminals routinely render PUA glyphs double-wide, so
                                                    // the re-CUP defense must cover them (the options UI's
                                                    // Nerd Font probe row is exactly such a run)
                   _ => false
               };
    }
}