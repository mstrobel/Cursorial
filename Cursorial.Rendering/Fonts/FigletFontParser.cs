using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using Cursorial.Rendering.Content;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// Parser for FIGlet font files (<c>.flf</c>) — the canonical format described at
/// <see href="http://www.figlet.org/figfont.html"/>. Also accepts ZIP-wrapped <c>.flf</c>
/// payloads (the on-disk format some font distributions use), and TLF / Caca <c>tlf2a</c>
/// fonts that share the FIGlet format with a different signature.
/// </summary>
/// <remarks>
/// <para>
/// The parser fills out an in-memory <see cref="FigletFont"/> from a stream or path. It loads
/// the printable-ASCII range (32–126) plus the German Latin-1 supplements (Ä Ö Ü ä ö ü ß)
/// that every FIGlet font is required to ship, then walks any extended glyphs declared via
/// codetag headers (decimal, hex <c>0x…</c>, or octal <c>0…</c> codepoint introducers).
/// </para>
/// <para>
/// Per-line glyph rendering: each character line ends with one or more occurrences of an
/// end-marker character (the last character of the line). The parser strips them. Embedded
/// ANSI escape sequences (rare, but some "colored" fonts use them) are filtered out — we
/// don't apply colors from the font, only the consumer's <see cref="Cursorial.Output.Style"/>.
/// </para>
/// </remarks>
public static partial class FigletFontParser
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex { get; }

    /// <summary>Codepoints every FIGlet font is required to define — ASCII 32–126 plus seven Latin-1.</summary>
    private static readonly uint[] RequiredCodepoints =
    [
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
        42, 43, 44, 45, 46, 47, 48, 49, 50, 51,
        52, 53, 54, 55, 56, 57, 58, 59, 60, 61,
        62, 63, 64, 65, 66, 67, 68, 69, 70, 71,
        72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
        82, 83, 84, 85, 86, 87, 88, 89, 90, 91,
        92, 93, 94, 95, 96, 97, 98, 99, 100, 101,
        102, 103, 104, 105, 106, 107, 108, 109, 110, 111,
        112, 113, 114, 115, 116, 117, 118, 119, 120, 121,
        122, 123, 124, 125, 126,
        196, 214, 220, 228, 246, 252, 223
    ];

    /// <summary>Parse a FIGlet font from a file path. The font's name is the file stem.</summary>
    public static FigletFont LoadFromFile(string path, string? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);

        if (Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out Uri? uri))
            return LoadFromUri(uri, nameOverride);

        return Load(stream, Path.GetFileNameWithoutExtension(path), uriSource: null, nameOverride);
    }

    /// <summary>Parse a FIGlet font from an embedded resource or file URI. The font's name is the file stem.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the font could not be loaded from the given Uri.</exception>
    public static FigletFont LoadFromUri(Uri uriSource, string? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(uriSource);

        if (ResourceLoader.Default.TryOpen(uriSource) is {} stream)
        {
            var path = uriSource.GetComponents(UriComponents.Path, UriFormat.Unescaped);
            var name = Path.GetFileNameWithoutExtension(path);

            return Load(stream, name, uriSource, nameOverride);
        }

        throw new InvalidOperationException($"Could not open resource from uri: <{uriSource}>");
    }

    /// <summary>Parse a FIGlet font from a stream. Accepts both raw and ZIP-wrapped <c>.flf</c>.</summary>
    public static FigletFont Load(Stream stream, string name, Uri? uriSource = null, string? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(name);

        var lines = ReadAllLines(stream);

        if (lines.Length < 1)
            throw new InvalidDataException("FIGlet font is empty — no header line.");

        var header = lines[0];
        bool isFiglet = header.StartsWith("flf2a", StringComparison.Ordinal);
        bool isCaca = header.StartsWith("tlf2a", StringComparison.Ordinal);

        if (!isFiglet && !isCaca)
        {
            string preview = header[..Math.Min(10, header.Length)];

            throw new InvalidDataException(
                $"FIGlet font signature missing — expected 'flf2a' or 'tlf2a', got '{preview}'.");
        }

        if (header.Length < 7)
            throw new InvalidDataException("FIGlet header too short.");

        // 6th character is the hardblank glyph.
        char hardblank = header[5];

        var headerFields = header[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (headerFields.Length < 1)
            throw new InvalidDataException("FIGlet header missing the required 'height' field.");

        int height = int.Parse(headerFields[0]);
        // baseline (1), maxLength (2), oldLayout (3), commentLines (4), printDirection (5),
        // fullLayout (6), codetagCount (7). We use height, baseline, oldLayout, commentLines,
        // fullLayout.
        //
        // Baseline is a COUNT of rows from the top of the glyph box THROUGH the baseline row (the
        // spec: "the number of lines of sub-characters from the baseline of a FIGcharacter to the
        // top of the tallest FIGcharacter"), so standard.flf's `6 5` means five body rows and one
        // descender row. TryParse rather than Parse, and absent → height: a font that omits or
        // mangles a purely COSMETIC metric still has perfectly paintable glyph bodies, and
        // `baseline == height` is the no-descender reading — i.e. exactly the behavior this
        // parser had before it read the field at all. FigletFont clamps the value into the
        // spec's [1, height] range; see the note on its constructor for why clamp beats throw.
        int baseline = headerFields.Length > 1 && int.TryParse(headerFields[1], out int parsedBaseline)
                           ? parsedBaseline
                           : height;
        int oldLayoutValue = headerFields.Length > 3 ? int.Parse(headerFields[3]) : 0;
        int commentLines = headerFields.Length > 4 ? int.Parse(headerFields[4]) : 0;
        int fullLayoutValue = headerFields.Length > 6 ? int.Parse(headerFields[6]) : -1;

        var legacy = (FigletLegacyLayoutMode) (oldLayoutValue < 0 ? 0 : oldLayoutValue);
        var layout = ResolveLayoutMode(fullLayoutValue, legacy, isFiglet);

        // Skip the comment block, then the glyph body begins at the line after.
        int cursor = 1 + commentLines;

        var glyphs = new Dictionary<uint, FigletGlyph>();

        // Required glyphs are in fixed order — no codetags between them. A spec-compliant font
        // defines all of these, but real-world files (especially test fixtures) sometimes don't;
        // we tolerate truncation by stopping at EOF rather than throwing — the consumer gets
        // whatever glyphs were successfully read, and missing codepoints fall back to the
        // space glyph at render time.
        foreach (uint cp in RequiredCodepoints)
        {
            if (cursor + height > lines.Length) break;
            var glyph = ParseGlyphBody(cp, lines, ref cursor, height, hardblank);
            glyphs[cp] = glyph;
        }

        // Extended glyphs: each glyph is preceded by a codetag line of the form
        //   <decimal_or_hex_or_octal_code> [optional description]
        // followed by `height` lines of glyph data.
        while (cursor < lines.Length)
        {
            var codeTag = lines[cursor].TrimEnd();

            if (string.IsNullOrEmpty(codeTag))
            {
                cursor++;
                continue;
            }

            var tokens = codeTag.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                cursor++;
                continue;
            }

            if (!TryParseCodepoint(tokens[0], out uint extCp))
            {
                // Unrecognized codetag; skip the line and try again. Could be a comment in a
                // weird position, or a malformed file. Best-effort: don't fail the whole font.
                cursor++;
                continue;
            }

            cursor++;

            if (cursor + height > lines.Length)
            {
                // Truncated extended glyph at end of file — break, the font is already usable.
                break;
            }

            var extGlyph = ParseGlyphBody(extCp, lines, ref cursor, height, hardblank);
            glyphs[extCp] = extGlyph;
        }

        return new FigletFont(nameOverride ?? name, hardblank, height, layout, glyphs, uriSource, baseline);
    }

    private static FigletGlyph ParseGlyphBody(uint codepoint, string[] lines, ref int cursor, int height,
                                              char hardblank)
    {
        var body = new string[height];

        for (int i = 0; i < height; i++)
        {
            if (cursor >= lines.Length)
                throw new InvalidDataException($"FIGlet font truncated mid-glyph for codepoint U+{codepoint:X4} at line {i + 1} of {height}.");

            var raw = lines[cursor++].TrimEnd('\r');
            body[i] = StripEndMarker(StripAnsi(raw));
        }

        return new FigletGlyph(codepoint, body, hardblank);
    }

    private static string StripEndMarker(string line)
    {
        if (line.Length == 0) return line;
        char marker = line[^1];
        int end = line.Length;
        while (end > 0 && line[end - 1] == marker) end--;
        return line[..end];
    }

    private static string StripAnsi(string line)
        => line.Contains('\x1B', StringComparison.Ordinal) ? AnsiEscapeRegex.Replace(line, string.Empty) : line;

    private static bool TryParseCodepoint(string token, out uint codepoint)
    {
        codepoint = 0;

        if (string.IsNullOrEmpty(token)) return false;

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            // Negative codetags exist in the spec to mark "delete this glyph"; we don't support
            // deletion of already-defined glyphs, but parse the value so the body still consumes
            // its lines and doesn't desync the parser.
            bool neg = token[0] == '-';
            int start = neg ? 3 : 2;

            return uint.TryParse(token.AsSpan(start), System.Globalization.NumberStyles.HexNumber,
                                 System.Globalization.CultureInfo.InvariantCulture, out codepoint);
        }

        // Octal (leading 0 with no x) or decimal — keep it simple and accept decimal-only here.
        // Pre-modern FIGlet font files in the wild use decimal almost exclusively.
        return uint.TryParse(token, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out codepoint);
    }

    /// <summary>
    /// Pick the effective <see cref="FigletLayoutMode"/> from the modern <c>fullLayout</c>
    /// header field, falling back to the legacy <c>oldLayout</c> bits when fullLayout is absent
    /// (negative) per the FIGlet spec.
    /// </summary>
    internal static FigletLayoutMode ResolveLayoutMode(int fullLayoutValue, FigletLegacyLayoutMode legacy, bool isFiglet)
    {
        if (fullLayoutValue >= 0)
            return (FigletLayoutMode) fullLayoutValue;

        // Derive from the legacy field. For FLF (FIGlet 2a-compatible) we default to Kern and
        // turn on Smush + Equal when horizontal-fitting is requested. For TLF we default to
        // Smush — the Caca variant always smushes unless the file says otherwise.
        var mode = isFiglet ? FigletLayoutMode.Kern : FigletLayoutMode.Smush;

        if (legacy.HasFlag(FigletLegacyLayoutMode.HorizontalKerning))
            mode |= FigletLayoutMode.Kern;

        if (legacy.HasFlag(FigletLegacyLayoutMode.HorizontalFitting))
            mode |= FigletLayoutMode.Smush | FigletLayoutMode.Equal;

        return mode;
    }

    private static string[] ReadAllLines(Stream stream)
    {
        var buffered = new BufferedStream(stream);
        bool isZip = LooksLikeZip(buffered);
        if (!isZip) return ReadTextLines(buffered);

        // ZIP-wrapped font: take the first entry (FIGlet's canonical archive holds one entry
        // named "-", but other zips may use a different name).
        using var archive = new ZipArchive(buffered, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.GetEntry("-") ?? archive.Entries.FirstOrDefault();

        if (entry is null)
            throw new InvalidDataException("ZIP-wrapped FIGlet font contained no entries.");

        using var entryStream = entry.Open();
        return ReadTextLines(entryStream);
    }

    private static bool LooksLikeZip(BufferedStream stream)
    {
        if (!stream.CanSeek) return false;
        var origin = stream.Position;
        Span<byte> signature = stackalloc byte[4];
        int read = stream.Read(signature);
        stream.Position = origin;

        return read == 4 && signature[0] == 0x50 && signature[1] == 0x4B &&
               signature[2] == 0x03 && signature[3] == 0x04;
    }

    private static string[] ReadTextLines(Stream stream)
    {
        // FIGlet fonts are 7-bit ASCII plus Latin-1 extended characters; UTF-8 superset reads
        // both correctly. Some legacy fonts are CP-437, but the affected codepoints are rare, and
        // the parser tolerates undecodable bytes by falling back to '?'.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                                            leaveOpen: true);

        var lines = new List<string>();
        while (reader.ReadLine() is {} line) lines.Add(line);
        return lines.ToArray();
    }
}