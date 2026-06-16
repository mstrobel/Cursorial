using System.Globalization;

namespace Cursorial.UI.Controls;

/// <summary>
/// A parsed access-key label (design doc §12.5): the display <see cref="Text"/> (underscores
/// stripped), the mnemonic <see cref="Key"/>, and the <see cref="KeyIndex"/> grapheme to underline.
/// Matching is simple-case-folded (<c>char.ToLowerInvariant</c>).
/// </summary>
/// <param name="Text">The display text (access-key underscores removed).</param>
/// <param name="Key">The mnemonic character (folded at match time); <c>'\0'</c> when there is none.</param>
/// <param name="KeyIndex">The grapheme index of the mnemonic in <see cref="Text"/>; <c>-1</c> when there is none.</param>
public readonly record struct AccessText(string Text, char Key, int KeyIndex)
{
    /// <summary>Whether the label carries a mnemonic.</summary>
    public bool HasKey => KeyIndex >= 0;

    /// <summary>
    /// Parses an access-key label (design doc §12.5): <c>"_File"</c> → <c>("File",'F',0)</c>;
    /// <c>"__"</c> is a literal <c>'_'</c>; the mnemonic must be a BMP letter/digit, else the
    /// underscore stays literal with NO key (<b>never throws</b>).
    /// </summary>
    public static AccessText Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var display = new System.Text.StringBuilder(s.Length);
        var key = '\0';
        var keyCodeUnitOffset = -1; // the UTF-16 offset of the mnemonic in `display` (converted to a cluster index below)

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '_')
            {
                display.Append(c);
                continue;
            }

            // A trailing underscore or a doubled underscore is a literal '_'.
            if (i + 1 >= s.Length)
            {
                display.Append('_');
                continue;
            }

            var next = s[i + 1];
            if (next == '_')
            {
                display.Append('_');
                i++; // consume the second underscore (the literal escape)
                continue;
            }

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (keyCodeUnitOffset < 0 && (char.IsLetterOrDigit(next) && next <= '￿'))
            {
                // The mnemonic: record the code-unit offset, drop the underscore.
                keyCodeUnitOffset = display.Length;
                key = next;
                display.Append(next);
                i++; // consume the mnemonic char (already appended)
                continue;
            }

            // A leading underscore before a non-letter/digit stays literal with no key (e.g. "_!").
            display.Append('_');
        }

        var text = display.ToString();
        // KeyIndex is a GRAPHEME-CLUSTER index (the AccessTextPresenter underlines the i-th cluster), so
        // convert the recorded code-unit offset to a cluster index — they diverge when a multi-char
        // grapheme (a surrogate pair / combining sequence) precedes the mnemonic.
        var keyIndex = keyCodeUnitOffset < 0 ? -1 : ClusterIndexAtCodeUnit(text, keyCodeUnitOffset);
        return new AccessText(text, key, keyIndex);
    }

    // Converts a UTF-16 code-unit offset into the grapheme-cluster index of the cluster that starts at
    // (or contains) it — the unit AccessTextPresenter underlines. ASCII/BMP-only prefixes make these
    // coincide; a surrogate pair before the mnemonic makes them diverge by one per such grapheme.
    private static int ClusterIndexAtCodeUnit(string text, int codeUnitOffset)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var clusterIndex = 0;
        while (enumerator.MoveNext())
        {
            var elementOffset = enumerator.ElementIndex; // code-unit offset of this cluster's start
            var clusterLength = ((string)enumerator.Current).Length;
            if (codeUnitOffset >= elementOffset && codeUnitOffset < elementOffset + clusterLength)
                return clusterIndex;
            clusterIndex++;
        }

        return clusterIndex; // offset at/after the end — clamp to the cluster count
    }

    /// <summary>Wraps <paramref name="s"/> verbatim with no access-key parsing (no mnemonic).</summary>
    public static AccessText Literal(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new AccessText(s, '\0', -1);
    }

    /// <summary>The explicit <c>string → AccessText</c> conversion (parsing is lossy, hence explicit — doc §12.5).</summary>
    public static explicit operator AccessText(string s) => Parse(s);

    /// <summary>Whether <paramref name="candidate"/> matches the mnemonic, simple-case-folded.</summary>
    public bool Matches(char candidate)
        => HasKey && char.ToLowerInvariant(candidate) == char.ToLowerInvariant(Key);

    /// <inheritdoc/>
    public override string ToString()
        => HasKey ? $"AccessText('{Text}', key='{Key}'@{KeyIndex})" : $"AccessText('{Text}')";

    // ReSharper disable once UnusedMember.Local
    private string DebuggerDisplay => ToString();
}
