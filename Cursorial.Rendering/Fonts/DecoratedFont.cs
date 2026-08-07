using System.Text;

using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// Represents a font that applies a decorative glyph either above or below
/// the rendered text. The decoration appearance and position can be customized.
/// </summary>
public class DecoratedFont : IGlyphFont
{
    const TextAttributes ForbiddenAttributes = TextAttributes.Strikethrough | TextAttributes.Inverse;

    const TextAttributes ForbiddenDecorationAttributes = TextAttributes.Strikethrough |
                                                         TextAttributes.Inverse | 
                                                         TextAttributes.Underline |
                                                         TextAttributes.Overline;
    
    /// <summary>
    /// Represents the default glyph used for text decoration in the <see cref="DecoratedFont"/> class.
    /// The default glyph is set to the Unicode character '▀' (U+2580), which is commonly used for block-style decorations.
    /// </summary>
    /// <remarks>
    /// This static field is utilized by the <see cref="DecoratedFont"/> constructor to provide a default
    /// decoration glyph when no specific value is supplied. It can be combined with various decoration positions
    /// such as <see cref="DecorationPosition.Above"/> or <see cref="DecorationPosition.Below"/> to create styled text.
    /// </remarks>
    private static readonly Rune DefaultDecorationGlyph = new(0x1FB82);

    /// <summary>
    /// Represents a decorated font style where each glyph is underlined
    /// with a "half block" character (▀) positioned below the text.
    /// This decoration enhances the visual appearance by adding an
    /// underline styled with the specified decoration glyph.
    /// </summary>
    public static readonly DecoratedFont HalfBlockUnderline = new("Half-Block Underline", MonospaceFont.Default,  new('\u2580'), position: DecorationPosition.Below);

    /// <summary>
    /// A pre-defined instance of the <see cref="DecoratedFont"/> class that renders
    /// a decorative underline using a "quarter block" character (🮂). The decorative glyph
    /// is positioned below the default glyph rendering area.
    /// </summary>
    public static readonly DecoratedFont QuarterBlockUnderline = new("Quarter-Block Underline", MonospaceFont.Default, new(0x1FB82), position: DecorationPosition.Below);

    /// <summary>
    /// Represents a pre-configured instance of the <see cref="DecoratedFont"/> class,
    /// defining an underline decoration style using an "eighth block" character (▔).
    /// The decoration is positioned below the text by default.
    /// </summary>
    public static readonly DecoratedFont EighthBlockUnderline = new("Eighth-Block Underline", MonospaceFont.Default, new('\u2594'), position: DecorationPosition.Below);

    /// <summary>
    /// A private field that stores a cached string representation of the decoration glyph used in the decorated font.
    /// This field is lazily initialized when the decoration glyph is set, allowing efficient reuse of the string during rendering.
    /// </summary>
    private string? _decoration;

    /// <summary>
    /// Stores the width of the decoration glyph in grapheme clusters.
    /// This value is lazily calculated and cached when the decoration
    /// glyph is first used in rendering. It represents the visual width
    /// of the decoration to ensure proper alignment when applying decorations.
    /// </summary>
    private int? _decorationWidth;

    /// Represents a font that applies a decorative glyph alongside text rendered by an inner glyph font.
    /// This class allows for additional visual elements to be added to text, such as underlines or overlines.
    /// It is built on top of an inner font and renders both the text and the specified decoration.
    /// The decorative glyph is applied in the position specified by the `DecorationPosition`,
    /// either above or below the rendered text.
    public DecoratedFont(string displayName, IGlyphFont inner, Rune decorationGlyph, DecorationPosition position = DecorationPosition.Below)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(inner);

        DisplayName = displayName;
        Inner = inner;
        DecorationGlyph = decorationGlyph != default ? decorationGlyph : DefaultDecorationGlyph;
        Position = position;
    }

    /// <summary>
    /// Gets the inner glyph font utilized by the current decorated font.
    /// This property provides the underlying font that is wrapped or enhanced
    /// by the decorated font, enabling customization of font-rendering behavior.
    /// </summary>
    public IGlyphFont Inner { get; }

    /// <summary>
    /// Gets or sets the character used to render decorative glyphs for the text. The decorative
    /// glyph is applied either above or below the text depending on the <see cref="DecorationPosition"/>
    /// value assigned to the font.
    /// </summary>
    /// <remarks>
    /// Changing the value of this property will reset cached decoration data, including the glyph's
    /// dimensions and other render-specific information. The value defaults to
    /// <see cref="DefaultDecorationGlyph"/> if not explicitly set during construction or through
    /// property assignment.
    /// </remarks>
    public Rune DecorationGlyph
    {
        get;
        set
        {
            field = value;
            _decoration = null;
            _decorationWidth = null;
        }
    }

    /// <summary>
    /// Gets the position of the decoration relative to the text.
    /// </summary>
    /// <remarks>
    /// The decoration position determines whether the decoration glyph is rendered above or below
    /// the text. This value is set during object initialization and cannot be changed thereafter.
    /// </remarks>
    public DecorationPosition Position { get; }

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    /// <remarks>A decorator's repertoire IS its inner face's — this wrapper adds a decoration
    /// row, never a glyph. Inheriting the interface's optimistic default would have a decorated
    /// FIGlet font claim every codepoint while drawing a blank gap for most of them.</remarks>
    public bool HasGlyph(uint codepoint) => Inner.HasGlyph(codepoint);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>NOT a pure forward — the answer depends on <see cref="Position"/>.</b> Read
    /// <see cref="Paint"/>: the inner face is painted at <c>above ? row + 1 : row</c>. With
    /// <see cref="DecorationPosition.Above"/> the decoration takes the anchor row and the GLYPH
    /// slides down one, so every ink row — the baseline row included — is one lower than the
    /// inner face's: <c>Inner.Baseline + 1</c>. With <see cref="DecorationPosition.Below"/> the
    /// glyph stays at the anchor and the extra row is appended underneath, so the baseline is
    /// <c>Inner.Baseline</c>, unchanged, and the growth shows up in <see cref="Descender"/>
    /// instead.
    /// </para>
    /// <para>
    /// Forwarding <c>Inner.Baseline</c> blindly would place a baseline-aligned neighbour one row
    /// too high against an <c>Above</c>-decorated face — sitting on the decoration instead of
    /// beside the glyph — and the error is invisible on the single-row inner faces the bundled
    /// instances use (<see cref="HalfBlockUnderline"/> and friends all wrap
    /// <see cref="MonospaceFont"/>), which is exactly how it would ship.
    /// </para>
    /// </remarks>
    public int Baseline => Inner.Baseline + (Position == DecorationPosition.Above ? 1 : 0);

    /// <inheritdoc cref="Baseline"/>
    public int Ascender => Baseline;

    /// <summary>
    /// <see cref="Inner"/>'s descent, plus the decoration row when it sits
    /// <see cref="DecorationPosition.Below"/> the glyph. Keeps
    /// <c>Ascender + Descender</c> equal to the height <see cref="Measure"/> reports
    /// (<c>Inner.Rows + 1</c>) for both positions.
    /// </summary>
    /// <remarks>
    /// Note that <see cref="Paint"/> draws a <c>Below</c> decoration at a HARD-CODED
    /// <c>row + 1</c> rather than under the last glyph row, so with a multi-row inner face the
    /// decoration currently lands inside the glyph and the extra row <see cref="Measure"/>
    /// reserves at the bottom is left blank. These metrics deliberately describe the box
    /// <see cref="Measure"/> promises — layout reserves against Measure — rather than encoding
    /// that painting bug; fixing the paint anchor is a separate change and does not move the
    /// metrics.
    /// </remarks>
    public int Descender => Inner.Descender + (Position == DecorationPosition.Above ? 0 : 1);

    /// <inheritdoc/>
    public CellStyle EnsureCompatibleStyle(in CellStyle style)
    {
        var attributes = style.Attributes & ~ForbiddenAttributes;

        attributes &= ~(Position switch
                        {
                            DecorationPosition.Below => TextAttributes.Underline,
                            DecorationPosition.Above => TextAttributes.Overline,
                            _                        => TextAttributes.None
                        });

        return style with { Attributes = attributes };
    }

    /// <summary>
    /// <see cref="EnsureCompatibleStyle(in CellStyle)"/> for a delta: the same attributes, forced OFF
    /// rather than masked out — which is what masking a whole style's word already did, said in the
    /// vocabulary that can also say it about a channel it was never told.
    /// </summary>
    private PartialStyle EnsureCompatibleStyle(in PartialStyle style)
    {
        var compatible = style.Clearing(ForbiddenAttributes);

        return Position switch
               {
                   // Underline has its own axis, so it is removed through its own factory rather than
                   // through the flag word — see the decoration style below.
                   DecorationPosition.Below => compatible.RemovingUnderline(),
                   DecorationPosition.Above => compatible.Clearing(TextAttributes.Overline),
                   _                        => compatible
               };
    }

    /// <summary>
    /// Measures the dimensions of the specified text when rendered using the current font,
    /// including any additional decorations that modify the size.
    /// </summary>
    /// <param name="text">The text to measure, provided as a span of characters.</param>
    /// <returns>A <see cref="Size"/> struct representing the number of columns and rows
    /// required to render the provided text, with adjustments for decorations.</returns>
    public Size Measure(ReadOnlySpan<char> text)
    {
        var size = Inner.Measure(text);
        return size with { Rows = size.Rows + 1 };
    }

    /// <summary>
    /// Paints text into a <see cref="CellBuffer"/> at the specified position and applies decoration based on the font style.
    /// </summary>
    /// <param name="buffer">The target <see cref="CellBuffer"/> where the text and decoration should be rendered.</param>
    /// <param name="column">The starting column in the buffer where text and decoration should be painted.</param>
    /// <param name="row">The starting row in the buffer where text and decoration should be painted.</param>
    /// <param name="text">The text to be rendered in the buffer.</param>
    /// <param name="style">The style to be applied to the text and decoration.</param>
    /// <returns>
    /// A <see cref="Size"/> structure representing the number of columns and rows occupied by the rendered text and its decoration.
    /// </returns>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in PartialStyle style)
    {
        var above = Position == DecorationPosition.Above;
        var compatibleStyle = EnsureCompatibleStyle(style);

        // The box covers the decoration row too — it is part of this face's Measure — so it is settled
        // here and the inner face is handed a stamp, exactly as ShadowedFont does and for the same
        // reason: a decorated face's box is bigger than its inner face's, and only this level knows it.
        var ink = GlyphPaint.Ink(buffer, column, row, Measure(text), compatibleStyle);

        var size = Inner.Paint(buffer, column, above ? row + 1 : row, text, ink);

        _decoration ??= DecorationGlyph.ToString();
        _decorationWidth ??= GraphemeWidth.ClusterWidth(_decoration);

        // Underline is split out of the mask because it has its own axis: WithCleared REJECTS it (a
        // flag-word route to a decomposed attribute is the mistake that guard exists to catch), and
        // RemovingUnderline is the factory that owns both halves of it.
        var decoratorStyle = ink.Clearing(ForbiddenDecorationAttributes & ~TextAttributes.Underline)
                                .RemovingUnderline();

        int offset = _decorationWidth.GetValueOrDefault();

        for (int i = 0, n = size.Columns; i < n; i += offset)
        {
            int decorationRow = above ? row : row + 1;
            buffer.Set(column + i, decorationRow, _decoration,
                       GlyphPaint.Over(buffer, column + i, decorationRow, decoratorStyle));
        }

        return size with { Rows = size.Rows + 1 };
    }
}

/// <summary>
/// Represents a decoration position where the decorative element is rendered above the baseline
/// of the associated text or glyph. This position is commonly used for emphasizing text by placing
/// decorative marks or symbols directly above or below the main visual content.
/// </summary>
public enum DecorationPosition
{
    /// <summary>
    /// Specifies that the decoration will be rendered below the text content.
    /// </summary>
    Below,

    /// <summary>
    /// Specifies that the decoration will be rendered above the text content.
    /// </summary>
    Above
}