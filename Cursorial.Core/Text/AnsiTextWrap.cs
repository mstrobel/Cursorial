using System.Globalization;
using System.Text;

namespace Cursorial.Text;

/// <summary>
/// Options governing <see cref="AnsiTextWrap.Wrap(string, int, in WrapOptions)"/>. The
/// default-constructed value (<c>default(WrapOptions)</c> or <c>new WrapOptions()</c>) means
/// "all defaults"; pass an explicit value to override.
/// </summary>
/// <remarks>
/// Record-struct positional defaults only apply when the parameterized constructor is invoked.
/// Default-constructing the struct zero-initializes the fields, so the parameter defaults below
/// don't fire — the property getters compensate by returning the documented default value when
/// the underlying field is unset.
/// </remarks>
public readonly record struct WrapOptions
{
    private readonly bool _breakLongWordsSet;
    private readonly bool _breakLongWords;
    private readonly string? _newLine;
    private readonly bool _trimTrailingSpacesSet;
    private readonly bool _trimTrailingSpaces;

    public WrapOptions(
        bool BreakLongWords = true,
        string NewLine = "\n",
        bool TrimTrailingSpaces = true)
    {
        _breakLongWordsSet = true;
        _breakLongWords = BreakLongWords;
        _newLine = NewLine;
        _trimTrailingSpacesSet = true;
        _trimTrailingSpaces = TrimTrailingSpaces;
    }

    /// <summary>
    /// When a single word exceeds the column limit, break it at the column boundary rather
    /// than overflowing. Default to true.
    /// </summary>
    public bool BreakLongWords
    {
        get => _breakLongWordsSet ? _breakLongWords : true;
        init { _breakLongWords = value; _breakLongWordsSet = true; }
    }

    /// <summary>
    /// Line separator inserted between wrapped lines. Default <c>"\n"</c>; raw-mode terminal
    /// output (with OPOST disabled) typically needs <c>"\r\n"</c>.
    /// </summary>
    public string NewLine
    {
        get => _newLine ?? "\n";
        init => _newLine = value;
    }

    /// <summary>
    /// Drop runs of trailing ASCII spaces from each wrapped line so the line ends flush against
    /// the column limit. Default true.
    /// </summary>
    public bool TrimTrailingSpaces
    {
        get => _trimTrailingSpacesSet ? _trimTrailingSpaces : true;
        init { _trimTrailingSpaces = value; _trimTrailingSpacesSet = true; }
    }
}

/// <summary>
/// Word-wrap text while passing ANSI escape sequences through untouched. Width is measured by
/// rendered glyphs only — CSI (SGR / cursor / mode), OSC (hyperlinks, text sizing), DCS, and SS3
/// sequences contribute no columns but are preserved in their original positions in the output.
/// </summary>
/// <remarks>
/// <para>
/// Grapheme cluster boundaries (per <see cref="StringInfo"/>) drive the width accounting, so
/// emoji-with-modifiers, ZWJ joined sequences, and combining marks measure as a single unit
/// with the width that
/// <see cref="GraphemeWidth.ClusterWidth(System.ReadOnlySpan{char})"/> reports.
/// </para>
/// <para>
/// SGR state crosses wrap boundaries naturally: an SGR sequence emitted before the break stays
/// in effect on the next line because the escape passes through with no reset injected. If a
/// caller wants every wrapped line to start with a clean SGR state, they should pre-emit
/// <c>SGR 0</c> after each break themselves; the wrapper deliberately does not assume the
/// caller's preferred reset policy.
/// </para>
/// </remarks>
public static class AnsiTextWrap
{
    /// <summary>Convenience overload using default <see cref="WrapOptions"/>.</summary>
    public static string Wrap(string text, int columns)
        => Wrap(text, columns, new WrapOptions());

    /// <summary>
    /// Word-wrap <paramref name="text"/> to at most <paramref name="columns"/> rendered cells
    /// per line, preserving any embedded ANSI escape sequences.
    /// </summary>
    public static string Wrap(string text, int columns, in WrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

        // options.NewLine is non-null by construction (the getter falls back to "\n" when the
        // backing field is unset, so default(WrapOptions) is usable directly).
        if (text.Length == 0) return text;

        var output = new StringBuilder(text.Length + text.Length / Math.Max(columns, 1));
        // Buffers the in-progress current line. We rewrite the tail of the line when we need
        // to back up to a word boundary, so it has to be StringBuilder-shaped rather than
        // streamed directly.
        var line = new StringBuilder();
        int lineWidth = 0;
        int lastBreakOpportunityIndex = -1;   // Index in `line` immediately AFTER the last
                                              // whitespace run we could break on.
        int lastBreakOpportunityWidth = -1;

        int i = 0;
        while (i < text.Length)
        {
            int escapeLength = MeasureEscapeSequence(text, i);
            if (escapeLength > 0)
            {
                line.Append(text, i, escapeLength);
                i += escapeLength;
                continue;
            }

            // Handle hard line breaks present in the source.
            if (text[i] == '\n')
            {
                CommitLine(output, line, options);
                output.Append(options.NewLine);
                line.Clear();
                lineWidth = 0;
                lastBreakOpportunityIndex = -1;
                lastBreakOpportunityWidth = -1;
                i++;
                continue;
            }
            if (text[i] == '\r')
            {
                // CR (alone) or CR-LF: treat as a single line break.
                CommitLine(output, line, options);
                output.Append(options.NewLine);
                line.Clear();
                lineWidth = 0;
                lastBreakOpportunityIndex = -1;
                lastBreakOpportunityWidth = -1;
                if (i + 1 < text.Length && text[i + 1] == '\n') i += 2;
                else i++;
                continue;
            }

            // Extract one grapheme cluster from this point.
            int clusterLength = NextGraphemeClusterLength(text, i);
            int clusterWidth = GraphemeWidth.ClusterWidth(text.AsSpan(i, clusterLength));

            bool isWhitespace = IsWrapWhitespace(text, i, clusterLength);

            if (lineWidth + clusterWidth > columns && lineWidth > 0)
            {
                // Need to wrap before adding this cluster.
                if (lastBreakOpportunityIndex >= 0)
                {
                    // Break at the last whitespace boundary.
                    string overflow = line.ToString(
                        lastBreakOpportunityIndex,
                        line.Length - lastBreakOpportunityIndex);
                    line.Length = lastBreakOpportunityIndex;
                    CommitLine(output, line, options);
                    output.Append(options.NewLine);
                    line.Clear();
                    line.Append(overflow);
                    lineWidth -= lastBreakOpportunityWidth;
                    lastBreakOpportunityIndex = -1;
                    lastBreakOpportunityWidth = -1;
                }
                else if (options.BreakLongWords)
                {
                    // Force-break at the current position.
                    CommitLine(output, line, options);
                    output.Append(options.NewLine);
                    line.Clear();
                    lineWidth = 0;
                }
                // else: let the long word overflow the limit (caller opted out of breaking).
            }

            line.Append(text, i, clusterLength);
            lineWidth += clusterWidth;
            i += clusterLength;

            if (isWhitespace)
            {
                lastBreakOpportunityIndex = line.Length;
                lastBreakOpportunityWidth = lineWidth;
            }
        }

        CommitLine(output, line, options);
        return output.ToString();
    }

    /// <summary>
    /// If position <paramref name="index"/> in <paramref name="text"/> starts an ANSI escape
    /// sequence (ESC followed by recognizable framing), return its length in UTF-16 code units.
    /// Returns 0 otherwise. Covers CSI, OSC, DCS, SS3, and ESC + intermediate(s) + final.
    /// </summary>
    public static int MeasureEscapeSequence(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)index >= (uint)text.Length) return 0;
        if (text[index] != '\x1b') return 0;

        int i = index + 1;
        if (i >= text.Length) return 1; // Lone trailing ESC.

        char introducer = text[i];
        i++;

        switch (introducer)
        {
            case '[':
                // CSI: parameter / intermediate bytes (0x20-0x3F), then final (0x40-0x7E).
                while (i < text.Length && text[i] is >= (char)0x20 and < (char)0x40) i++;
                if (i < text.Length && text[i] is >= (char)0x40 and <= (char)0x7E) i++;
                return i - index;

            case ']':
                // OSC: body until BEL or ESC \.
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == '\x07') { i++; break; }
                    if (c == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                return i - index;

            case 'P': // DCS
            case 'X': // SOS
            case '^': // PM
            case '_': // APC
                // Body until ESC \.
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                return i - index;

            case 'O':
                // SS3: one more byte.
                if (i < text.Length) i++;
                return i - index;

            default:
                // ESC + optional intermediates + final byte.
                while (i < text.Length && text[i] is >= (char)0x20 and <= (char)0x2F) i++;
                if (i < text.Length && text[i] is >= (char)0x30 and <= (char)0x7E) i++;
                return i - index;
        }
    }

    private static int NextGraphemeClusterLength(string text, int index)
    {
        // Single-call grapheme advance. StringInfo doesn't expose a no-allocation form yet, so
        // use the index-stepping helper and recover the length from the position delta.
        int next = StringInfo.GetNextTextElementLength(text, index);
        return next > 0 ? next : 1; // Defensive: should not be zero for valid input.
    }

    private static bool IsWrapWhitespace(string text, int index, int length)
    {
        if (length == 0) return false;
        char c = text[index];
        return c is ' ' or '\t';
    }

    private static void CommitLine(StringBuilder output, StringBuilder line, in WrapOptions options)
    {
        if (options.TrimTrailingSpaces)
        {
            while (line.Length > 0 && (line[^1] == ' ' || line[^1] == '\t'))
            {
                line.Length--;
            }
        }
        output.Append(line);
    }
}
