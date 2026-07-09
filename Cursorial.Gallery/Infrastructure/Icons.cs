using Cursorial.Gallery.ViewModels;
using Cursorial.UI.Themes;

namespace Cursorial.Gallery.Infrastructure;

internal static class Icons
{
    internal static IconCarrier IconCut() => Nf("\U000F0190",            "✁", "✂️");            // nf-md-content_cut U+F0190 · floor U+2701 ✁
    internal static IconCarrier IconCopy() => Nf("\U000F018F",           "⧉", "🗒");           // nf-md-content_copy U+F018F · floor U+29C9 ⧉
    internal static IconCarrier IconPaste() => Nf("\U000F0192",          "▤", "📋");          // nf-md-content_paste U+F0192 · floor U+25A4 ▤
    internal static IconCarrier IconBold() => Nf("\U000F0264",           "✱", "🅱");           // nf-md-format_bold U+F0264 · floor U+2731 ✱
    internal static IconCarrier IconItalic() => Nf("\U000F0277",         "⟋", "✍️");         // nf-md-format_italic U+F0277 · floor U+27CB ⟋
    internal static IconCarrier IconInlineCode() => Nf("\U000F0174",     "{", "💻");     // nf-md-code_tags U+F0174 · floor U+0060 `
    internal static IconCarrier IconUndo() => Nf("\U000F054C",           "↶", "↩️");           // nf-md-undo U+F054C · floor U+21B6 ↶
    internal static IconCarrier IconRedo() => Nf("\U000F044E",           "↷", "↪️");           // nf-md-redo U+F044E · floor U+21B7 ↷
    internal static IconCarrier IconSelectAll() => Nf("\U000F0486",      "⬚", "🔲");      // nf-md-select_all U+F0486 · floor U+2B1A ⬚
    internal static IconCarrier IconInsertRowAbove() => Nf("\U000F04F4", "↥", "⬆️"); // nf-md-table_row_plus_before U+F04F4 · floor U+21A5 ↥
    internal static IconCarrier IconInsertRowBelow() => Nf("\U000F04F3", "↧", "⬇️"); // nf-md-table_row_plus_after U+F04F3 · floor U+21A7 ↧
    internal static IconCarrier IconInsertColLeft() => Nf("\U000F04ED",  "↤", "⬅️");  // nf-md-table_column_plus_before U+F04ED · floor U+21A4 ↤
    internal static IconCarrier IconInsertColRight() => Nf("\U000F04EC", "↦", "➡️"); // nf-md-table_column_plus_after U+F04EC · floor U+21A6 ↦
    internal static IconCarrier IconDeleteRow() => Nf("\U000F04F5",      "⊖", "❌");       // nf-md-table_row_remove U+F04F5 · floor U+2296 ⊖
    internal static IconCarrier IconDeleteCol() => Nf("\U000F04EE",      "⊘", "❌");       // nf-md-table_column_remove U+F04EE · floor U+2298 ⊘
    internal static IconCarrier IconDeleteTable() => Nf("\U000F0A76",    "⊗", "🗑️");   // nf-md-table_remove U+F0A76 · floor U+2297 ⊗
    internal static IconCarrier IconMoveRowUp() => Nf("\U000F0739",      "↑", "🔼");      // nf-md-arrow_up_bold_box_outline U+F0739 · floor U+2191 ↑
    internal static IconCarrier IconMoveRowDown() => Nf("\U000F0730",    "↓", "🔽");    // nf-md-arrow_down_bold_box_outline U+F0730 · floor U+2193 ↓
    internal static IconCarrier IconMoveColLeft() => Nf("\U000F0733",    "←", "◀️");    // nf-md-arrow_left_bold_box_outline U+F0733 · floor U+2190 ←
    internal static IconCarrier IconMoveColRight() => Nf("\U000F0736",   "→", "▶️");   // nf-md-arrow_right_bold_box_outline U+F0736 · floor U+2192 →
    internal static IconCarrier IconAlignLeft() => Nf("\U000F0262",      "⇤", "⬅️");      // nf-md-format_align_left U+F0262 · floor U+21E4 ⇤
    internal static IconCarrier IconAlignCenter() => Nf("\U000F0260",    "↹", "↔️");    // nf-md-format_align_center U+F0260 · floor U+21B9 ↹
    internal static IconCarrier IconAlignRight() => Nf("\U000F0263",     "⇥", "➡️");     // nf-md-format_align_right U+F0263 · floor U+21E5 ⇥
    internal static IconCarrier IconClearCell() => Nf("\U000F01FE",      "∅", "🧹");      // nf-md-eraser U+F01FE · floor U+2205 ∅ (ledger row added)
    internal static IconCarrier IconRaw() => Nf("\U000F0694",            "⌗", "⌨️");            // nf-md-code_tags_check U+F0694 · floor U+2317 ⌗
    internal static IconCarrier IconWrap() => Nf("\U000F05B6",           "↵", "↩️");           // nf-md-wrap U+F05B6 · floor U+21B5 ↵
    internal static IconCarrier IconTruncate() => Nf("\U000F0D0E",       "…", "✂️");       // nf-md-format_text_wrapping_clip U+F0D0E · floor U+2026 … (ledger row added)
    internal static IconCarrier IconSettings() => Nf("\U0000E690",       "⚙", "⚙️");       // nf-md-format_text_wrapping_clip U+F0D0E · floor U+2026 … (ledger row added)
    internal static IconCarrier IconFind() => Nf("\U000F0349",           "⌕", "🔍");           // nf-md-format_text_wrapping_clip U+F0D0E · floor U+2026 … (ledger row added)
    
    // Builds a fresh tiered Icon: the Nerd Font Glyph (width-1), the color-Emoji tier (opt-in caps-emoji — a 2-wide
    // emoji is fine here; the width-1 discipline is the Text tier's), and the width-1 Unicode Text floor. Image tier
    // stays null (PNGs procured in M5+). GlyphWidth is 1 for every nf-md icon.
    private static IconCarrier Nf(string glyph, string text, string emoji)
        => new() { Glyph = glyph, GlyphWidth = 1, Text = text, Emoji = emoji };
}
