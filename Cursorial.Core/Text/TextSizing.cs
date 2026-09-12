using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Text;

/// <summary>
/// Vertical alignment of glyphs within a multicell block under the Kitty text-sizing protocol.
/// Applies when the cell-block is taller than the glyph (e.g. <c>s=2:n=1:d=2</c> renders text
/// at half-height inside a double-tall block).
/// </summary>
public enum TextSizingVerticalAlignment : byte
{
    /// <summary>Anchor at the top of the cell-block (the spec default, <c>v=0</c>).</summary>
    Top = 0,

    /// <summary>Anchor at the bottom (<c>v=1</c>).</summary>
    Bottom = 1,

    /// <summary>Center vertically within the cell-block (<c>v=2</c>).</summary>
    Center = 2,
}

/// <summary>Horizontal alignment of glyphs within a multicell block under the Kitty text-sizing protocol.</summary>
public enum TextSizingHorizontalAlignment : byte
{
    /// <summary>Anchor at the left of the cell-block (the spec default, <c>h=0</c>).</summary>
    Left = 0,

    /// <summary>Anchor at the right (<c>h=1</c>).</summary>
    Right = 1,

    /// <summary>Center horizontally within the cell-block (<c>h=2</c>).</summary>
    Center = 2,
}

/// <summary>
/// Parameters of a Kitty text-sizing OSC 66 emission. Default-constructed instance is "normal
/// text" (no scaling, auto width) and produces an empty metadata block on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Width is a SPAN width.</b> The <c>w</c> key is the width in cells of the <i>whole text of
/// one OSC 66 sequence</i> — not a per-cluster advance (maintainer-verified against kitty,
/// 2026-09-12; the earlier "unsupported by decision" stance rested on the per-cluster misreading).
/// The terminal renders the sequence's text in exactly <c>w × s</c> columns, packing the glyphs
/// with natural spacing. Combined with the fractional scale <c>n/d</c> that is what puts
/// <b>several glyphs in one cell</b>: a superscript <c>12</c> at <c>n=1:d=2</c> with <c>w=1</c>
/// renders both digits in a single cell. Without <c>w</c> the fractional scale only shrinks the
/// glyph inside its normal cell block.
/// </para>
/// <para>
/// <b>Packing.</b> <see cref="Packed"/> asks the writer to compute <c>w</c> for each sequence it
/// emits — the packed natural width, <c>⌈natural · n/d⌉</c> cells, chunking the text so no
/// sequence exceeds the spec's seven cells (<see cref="TextSizingPacking"/>). That is the mode a
/// read-only sub/superscript run wants. An explicit <see cref="Width"/> pins one sequence's width
/// instead (a wide glyph forced into two cells, a glyph the terminal would otherwise mis-measure).
/// </para>
/// <para>
/// <b>Footprint.</b> Every sizing still has a whole-cell footprint on the outside — the buffer,
/// the caret and layout speak whole cells — see <see cref="SpanColumns"/>. What changes with
/// packing is how much text fits inside those cells.
/// </para>
/// </remarks>
/// <param name="Scale">
/// Overall scale factor <c>s</c>. Range 1–7 per the spec. Default 1 (no scale). Text splits
/// into cells as normal text would, each cell an <c>s×s</c> block — a cluster occupies its
/// natural width × <c>s</c> columns by <c>s</c> rows.
/// </param>
/// <param name="Width">
/// The width in cells <c>w</c> of one emitted sequence's whole text. Range 0–7; 0 (the default)
/// lets the terminal compute the width from the text as for normal text. Non-zero renders the
/// sequence in exactly <c>w × s</c> columns whatever the text's natural width. See the remarks.
/// </param>
/// <param name="Numerator">
/// Fractional-scale numerator <c>n</c>. Range 0–15. Used with <see cref="Denominator"/> to
/// render text at a fraction of its natural size (e.g. <c>n=1:d=2</c> = half size inside the
/// <see cref="Scale"/>-tall block; with <see cref="Packed"/> or a <see cref="Width"/>, two
/// half-size glyphs share a cell).
/// </param>
/// <param name="Denominator">
/// Fractional-scale denominator <c>d</c>. Range 0–15. MUST be greater than <see cref="Numerator"/>
/// when both are non-zero, per the spec.
/// </param>
/// <param name="Vertical">Vertical alignment within the block (a superscript sits <see cref="TextSizingVerticalAlignment.Top"/>, a subscript <see cref="TextSizingVerticalAlignment.Bottom"/>).</param>
/// <param name="Horizontal">Horizontal alignment within the block.</param>
/// <param name="Packed">
/// Compute <c>w</c> per emitted sequence as the packed natural width of its text (see the remarks);
/// ignored when <see cref="Width"/> is non-zero. Requires the terminal's <c>w</c> support like an
/// explicit width does.
/// </param>
[TypeConverter(typeof(TextSizingConverter))]
public readonly record struct TextSizing(
    byte Scale = 1,
    byte Width = 0,
    byte Numerator = 0,
    byte Denominator = 0,
    TextSizingVerticalAlignment Vertical = TextSizingVerticalAlignment.Top,
    TextSizingHorizontalAlignment Horizontal = TextSizingHorizontalAlignment.Left,
    bool Packed = false)
{
    /// <summary>Spec defaults — no scaling, auto width, top-left alignment.</summary>
    public static TextSizing Normal => default;

    public static TextSizing Double => new(Scale: 2);

    /// <summary>True when every parameter is at its spec-default and the metadata block would be empty.</summary>
    public bool IsNormal => Scale is 0 or 1 &&
                            Width is 0 &&
                            !Packed &&
                            Numerator is 0 &&
                            Denominator is 0 &&
                            Vertical is TextSizingVerticalAlignment.Top &&
                            Horizontal is TextSizingHorizontalAlignment.Left;

    /// <summary>The effective scale (<c>s</c>; the record default 0 reads as 1).</summary>
    public int EffectiveScale => Scale == 0 ? 1 : Scale;

    /// <summary>Whether a fractional scale <c>n/d</c> is in effect (both non-zero).</summary>
    public bool IsFractional => Numerator != 0 && Denominator != 0;

    /// <summary>Whether the emitted sequences carry a <c>w</c> key — an explicit <see cref="Width"/> or <see cref="Packed"/>.</summary>
    public bool UsesWidth => Width != 0 || Packed;

    /// <summary>A superscript at <paramref name="numerator"/>/<paramref name="denominator"/> size, packed so several glyphs share a cell (the default: half size).</summary>
    public static TextSizing Superscript(byte numerator = 1, byte denominator = 2)
        => new(Numerator: numerator, Denominator: denominator, Vertical: TextSizingVerticalAlignment.Top, Packed: true);

    /// <summary>A subscript at <paramref name="numerator"/>/<paramref name="denominator"/> size, packed so several glyphs share a cell (the default: half size).</summary>
    public static TextSizing Subscript(byte numerator = 1, byte denominator = 2)
        => new(Numerator: numerator, Denominator: denominator, Vertical: TextSizingVerticalAlignment.Bottom, Packed: true);

    /// <summary>A glyph forced into exactly <paramref name="cells"/> cells (1–7) at normal scale — the wide-glyph guarantee for a terminal whose own width tables disagree with the buffer's.</summary>
    public static TextSizing FixedWidth(byte cells) => new(Width: cells);

    public string DisplayName => GetDisplayName();

    private string GetDisplayName()
    {
        if (IsNormal) return "Normal";

        string name;
        if (Numerator > 0 && Denominator > 0)
        {
            if (Scale > 1) name = $"Scaled {(Scale - 1) + (decimal)Numerator / Denominator:P0}";
            else if (Vertical is TextSizingVerticalAlignment.Top) name = $"{(decimal)Numerator/Denominator:P0} Superscript";
            else if (Vertical is TextSizingVerticalAlignment.Bottom) name = $"{(decimal)Numerator/Denominator:P0} Subscript";
            else name = $"Scaled {(decimal)Numerator/Denominator:P0}";
        }
        else if (Scale > 1)
        {
            name = $"Scaled {Scale:P0}";
        }
        else
        {
            name = "Normal";
        }

        if (Width != 0) return $"{name}, {Width} cells";
        return Packed ? $"{name}, packed" : name;
    }

    public bool IsSupported(OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // Scale > 1 and a fractional scale need the s-key; an explicit width or packing needs the
        // w-key. A sizing that exercises neither renders identically to plain text — a no-op
        // fragment is not useful, so it reports unsupported and the higher-level fallback fires
        // (a MonospaceFont is the better choice there).
        bool needsScale = Scale != 0 && Scale != 1 || IsFractional;
        bool needsWidth = UsesWidth;

        if (needsScale && !capabilities.TextSizing.Scale) return false;
        if (needsWidth && !capabilities.TextSizing.Width) return false;

        return needsScale || needsWidth;
    }

    /// <summary>
    /// The whole-cell footprint of one line of <paramref name="text"/> under this sizing: the columns the
    /// emitted sequences occupy (<see cref="Width"/> × <see cref="Scale"/> for an explicit width, the
    /// packed chunk widths summed × <see cref="Scale"/> when <see cref="Packed"/>, else the natural width
    /// × <see cref="Scale"/>) by <see cref="Scale"/> rows. This is the unit layout, the caret and the
    /// buffer see; the packing inside is the terminal's.
    /// </summary>
    public (int Columns, int Rows) SpanSize(ReadOnlySpan<char> text) => (SpanColumns(text), EffectiveScale);

    /// <summary>The columns one line of <paramref name="text"/> occupies under this sizing (see <see cref="SpanSize"/>).</summary>
    public int SpanColumns(ReadOnlySpan<char> text)
    {
        int scale = EffectiveScale;
        if (Width != 0)
            return Width * scale;

        if (Packed)
            return TextSizingPacking.PackedCells(this, text) * scale;

        return GraphemeWidth.StringWidth(text) * scale;
    }

    public (int Columns, int Rows) GetGlyphSize()
    {
        // Cell footprint per the OSC 66 spec: with w=0 ("auto"), text splits into cells as
        // normal text would, and each cell becomes an s×s block — so a cluster occupies
        // (natural width × s, s). Scale=0 is the record-struct default; treat as 1.
        //
        // The FRACTIONAL scale (n/d) deliberately does not participate: per the spec, "the
        // fractional scale does not affect the number of cells the text occupies, instead it
        // just adjusts the rendered font size within those cells" — s=2:n=1:d=2 still occupies
        // 2×2 cells per cluster (half-size glyphs, the spacing makes up the difference).
        // (Maintainer-verified against kitty, 2026-08-02; the previous n/d division here was a
        // misreading that collapsed half-height sizing to a zero footprint.)

        int unitSize = Scale == 0 ? 1 : Scale;

        return (unitSize, unitSize);
    }

    // ───────────────────────────── string form ─────────────────────────────

    /// <summary>The canonical metadata form (<c>s=2:n=1:d=2:v=2:h=1:w=1:packed</c>; <c>Normal</c> for the default) — see <see cref="TextSizingConverter"/>.</summary>
    public override string ToString()
    {
        if (IsNormal)
            return "Normal";

        var parts = new List<string>(7);
        if (Scale is not (0 or 1)) parts.Add($"s={Scale}");
        if (Numerator != 0) parts.Add($"n={Numerator}");
        if (Denominator != 0) parts.Add($"d={Denominator}");
        if (Vertical != TextSizingVerticalAlignment.Top) parts.Add($"v={(int)Vertical}");
        if (Horizontal != TextSizingHorizontalAlignment.Left) parts.Add($"h={(int)Horizontal}");
        if (Width != 0) parts.Add($"w={Width}");
        if (Packed) parts.Add("packed");
        return string.Join(':', parts);
    }

    /// <summary>Parses the named or metadata string form (see <see cref="TextSizingConverter"/>).</summary>
    /// <exception cref="FormatException"><paramref name="text"/> is not a valid sizing.</exception>
    public static TextSizing Parse(string text)
    {
        if (TryParse(text, out var sizing, out var error))
            return sizing;

        throw new FormatException(error);
    }

    /// <summary>Tries to parse the named or metadata string form (see <see cref="TextSizingConverter"/>).</summary>
    public static bool TryParse(string? text, out TextSizing sizing)
        => TryParse(text, out sizing, out _);

    /// <summary>Tries to parse the named or metadata string form, reporting why it failed.</summary>
    public static bool TryParse(string? text, out TextSizing sizing, [NotNullWhen(false)] out string? error)
    {
        sizing = Normal;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "A text sizing must not be empty (use 'Normal').";
            return false;
        }

        byte scale = 1, width = 0, numerator = 0, denominator = 0;
        var vertical = TextSizingVerticalAlignment.Top;
        var horizontal = TextSizingHorizontalAlignment.Left;
        bool packed = false;

        foreach (var rawToken in text.Split([' ', '\t', ':', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.ToLowerInvariant();
            int eq = token.IndexOf('=');
            if (eq > 0)
            {
                var key = token[..eq];
                var value = token[(eq + 1)..];
                if (!ApplyKey(key, value, ref scale, ref width, ref numerator, ref denominator, ref vertical, ref horizontal, ref packed, out error))
                    return false;
                continue;
            }

            switch (token)
            {
                case "normal" or "default" or "1x":
                    break;
                case "double":
                    scale = 2;
                    break;
                case "triple":
                    scale = 3;
                    break;
                case "superscript" or "super" or "sup":
                    if (!IsFractionSet(numerator, denominator)) (numerator, denominator) = (1, 2);
                    vertical = TextSizingVerticalAlignment.Top;
                    packed = true;
                    break;
                case "subscript" or "sub":
                    if (!IsFractionSet(numerator, denominator)) (numerator, denominator) = (1, 2);
                    vertical = TextSizingVerticalAlignment.Bottom;
                    packed = true;
                    break;
                case "packed" or "pack":
                    packed = true;
                    break;
                case "top":
                    vertical = TextSizingVerticalAlignment.Top;
                    break;
                case "bottom":
                    vertical = TextSizingVerticalAlignment.Bottom;
                    break;
                case "center" or "middle" or "vcenter":
                    vertical = TextSizingVerticalAlignment.Center;
                    break;
                case "left":
                    horizontal = TextSizingHorizontalAlignment.Left;
                    break;
                case "right":
                    horizontal = TextSizingHorizontalAlignment.Right;
                    break;
                case "hcenter":
                    horizontal = TextSizingHorizontalAlignment.Center;
                    break;
                default:
                    if (token.Length >= 2 && token[^1] == 'x' && TryByte(token[..^1], out var s) && s is >= 1 and <= 7)
                    {
                        scale = s;
                        break;
                    }

                    int slash = token.IndexOf('/');
                    if (slash > 0 && TryByte(token[..slash], out var n) && TryByte(token[(slash + 1)..], out var d) && n >= 1 && d >= 1)
                    {
                        if (n >= d)
                        {
                            error = $"The fraction '{rawToken}' must be below 1 (the spec requires d > n).";
                            return false;
                        }

                        (numerator, denominator) = (n, d);
                        break;
                    }

                    error = $"Unrecognized text-sizing token '{rawToken}'. Expected a name (Normal, Double, 2x…7x, Superscript, Subscript, Packed, Top/Bottom/Center, Left/Right/HCenter), a fraction (1/2), or a key (s=, n=, d=, v=, h=, w=).";
                    return false;
            }
        }

        if (numerator == 0 ^ denominator == 0)
        {
            error = "A fractional scale needs both n and d.";
            return false;
        }

        if (numerator != 0 && numerator >= denominator)
        {
            error = "The fractional scale n/d must be below 1 (the spec requires d > n).";
            return false;
        }

        sizing = new TextSizing(scale, width, numerator, denominator, vertical, horizontal, packed);
        return true;
    }

    private static bool IsFractionSet(byte n, byte d) => n != 0 && d != 0;

    private static bool TryByte(string text, out byte value) => byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool ApplyKey(
        string key, string value,
        ref byte scale, ref byte width, ref byte numerator, ref byte denominator,
        ref TextSizingVerticalAlignment vertical, ref TextSizingHorizontalAlignment horizontal, ref bool packed,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (key)
        {
            case "s" or "scale":
                if (!TryByte(value, out scale) || scale is < 1 or > 7) return Fail($"s must be 1–7, got '{value}'.", out error);
                return true;
            case "w" or "width":
                if (!TryByte(value, out width) || width > 7) return Fail($"w must be 0–7, got '{value}'.", out error);
                return true;
            case "n":
                if (!TryByte(value, out numerator) || numerator > 15) return Fail($"n must be 0–15, got '{value}'.", out error);
                return true;
            case "d":
                if (!TryByte(value, out denominator) || denominator > 15) return Fail($"d must be 0–15, got '{value}'.", out error);
                return true;
            case "v":
                if (TryByte(value, out var v) && v <= 2) { vertical = (TextSizingVerticalAlignment)v; return true; }
                switch (value)
                {
                    case "top": vertical = TextSizingVerticalAlignment.Top; return true;
                    case "bottom": vertical = TextSizingVerticalAlignment.Bottom; return true;
                    case "center" or "middle": vertical = TextSizingVerticalAlignment.Center; return true;
                }
                return Fail($"v must be 0–2 or top/bottom/center, got '{value}'.", out error);
            case "h":
                if (TryByte(value, out var h) && h <= 2) { horizontal = (TextSizingHorizontalAlignment)h; return true; }
                switch (value)
                {
                    case "left": horizontal = TextSizingHorizontalAlignment.Left; return true;
                    case "right": horizontal = TextSizingHorizontalAlignment.Right; return true;
                    case "center": horizontal = TextSizingHorizontalAlignment.Center; return true;
                }
                return Fail($"h must be 0–2 or left/right/center, got '{value}'.", out error);
            case "p" or "packed":
                packed = value is "1" or "true" or "yes";
                return true;
            default:
                return Fail($"Unknown text-sizing key '{key}'.", out error);
        }

        static bool Fail(string message, out string? error)
        {
            error = message;
            return false;
        }
    }
}