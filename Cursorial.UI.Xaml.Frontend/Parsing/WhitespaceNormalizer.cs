using System.Text;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The documented (non-East-Asian) WPF-faithful whitespace rule (matrix XD19): element text is
/// trimmed at both ends; an interior newline+indent run collapses to a single space. Attribute
/// values are verbatim and never pass through here; <c>xml:space="preserve"</c> bypasses this.
/// </summary>
internal static class WhitespaceNormalizer
{
    /// <summary>
    /// Normalizes element text per XD19: trim ends, collapse any run of whitespace that contains a
    /// newline to a single space. A run of plain spaces with no newline (rare in element bodies) is
    /// preserved so attribute-like inner spacing is not mangled, matching System.Xaml's collapse on
    /// line breaks. A whitespace-only body normalizes to the empty string (matrix X35 — dropped).
    /// </summary>
    public static string NormalizeElementText(string text)
    {
        if (text.Length == 0)
            return text;

        // Whitespace-only → dropped.
        bool allWhitespace = true;
        foreach (char ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                allWhitespace = false;
                break;
            }
        }
        if (allWhitespace)
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        int n = text.Length;

        // Skip leading whitespace.
        while (i < n && char.IsWhiteSpace(text[i]))
            i++;

        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                // Consume the whitespace run; note whether it contains a line break.
                bool sawNewline = false;
                int runStart = i;
                while (i < n && char.IsWhiteSpace(text[i]))
                {
                    if (text[i] is '\n' or '\r')
                        sawNewline = true;
                    i++;
                }

                // If this is trailing whitespace, drop it entirely.
                if (i >= n)
                    break;

                // A newline-bearing run collapses to one space; a pure-space run is preserved verbatim
                // (System.Xaml collapses on line breaks; interior single spaces between words are kept).
                if (sawNewline)
                    sb.Append(' ');
                else
                    sb.Append(text, runStart, i - runStart);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
