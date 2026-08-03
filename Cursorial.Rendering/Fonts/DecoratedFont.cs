using System.Text;

using Cursorial.Output;
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
    public Style EnsureCompatibleStyle(in Style style)
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
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style)
    {
        var above = Position == DecorationPosition.Above;
        var compatibleStyle = EnsureCompatibleStyle(style);
        
        var size = Inner.Paint(buffer, column, above ? row + 1 : row, text, compatibleStyle);

        _decoration ??= DecorationGlyph.ToString();
        _decorationWidth ??= GraphemeWidth.ClusterWidth(_decoration);

        var decoratorStyle = compatibleStyle with { Attributes = compatibleStyle.Attributes & ~ForbiddenDecorationAttributes };
        
        int offset = _decorationWidth.GetValueOrDefault();

        for (int i = 0, n = size.Columns; i < n; i += offset)
        {
            if (above)
                buffer.Set(column + i, row, _decoration, decoratorStyle);
            else
                buffer.Set(column + i, row + 1, _decoration, decoratorStyle);
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