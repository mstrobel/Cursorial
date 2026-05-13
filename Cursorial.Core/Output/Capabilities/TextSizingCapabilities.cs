namespace Cursorial.Output.Capabilities;

/// <summary>
/// Describes a terminal's support for the Kitty text-sizing protocol — OSC 66 sequences that
/// render text wider than its natural width or scaled larger than a single cell. The two
/// sub-features are independently probable per the spec and are surfaced as separate bools.
/// </summary>
/// <remarks>
/// See <see href="https://sw.kovidgoyal.net/kitty/text-sizing-protocol/"/>. The protocol is
/// output-only — applications send <c>ESC ] 66 ; metadata ; text ST</c> and the terminal
/// renders the text in the configured size. Cursor advancement and overstrike semantics for
/// multicell glyphs are defined by the protocol; consumers using these features should rely
/// on the terminal's reported cursor position rather than guessing column counts.
/// </remarks>
/// <param name="Width">
/// True when the terminal honors the <c>w</c> metadata key — rendering a glyph in a fixed
/// number of cells (1–7) regardless of its natural width. Useful for forcing emoji or CJK
/// glyphs into a known column count.
/// </param>
/// <param name="Scale">
/// True when the terminal honors the <c>s</c> metadata key — rendering a glyph in an
/// <c>s × w</c> cell block (1–7 cells tall). Enables double-height text, large titles, and
/// the fractional <c>n/d</c> scaling that builds on the same machinery.
/// </param>
public sealed record TextSizingCapabilities(bool Width,
                                            bool Scale)
{
    /// <summary>A capability set reporting no text-sizing support.</summary>
    public static TextSizingCapabilities None { get; } = new(Width: false,
                                                             Scale: false);
}
