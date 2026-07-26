using System;
using System.Collections.Generic;

namespace Cursorial.UI.Dialogs;

/// <summary>
/// One row of a file dialog's "Save as type" / "Files of type" selector — a human label plus the glob pattern
/// the listing is filtered by. This is the Win32 <c>OPENFILENAME.lpstrFilter</c> pair and the WPF
/// <c>FileDialog.Filter</c> half-pair (<c>"PNG image|*.png"</c>) spelled as a record instead of a
/// pipe-delimited string, so a caller cannot get the two halves out of step. The design page's dropdown is
/// this type verbatim: <c>PNG image (*.png)</c>, <c>JPEG image (*.jpg;*.jpeg)</c>, <c>SVG vector (*.svg)</c>,
/// <c>All files (*.*)</c>.
/// <para>
/// <b>Why <see cref="Pattern"/> is a string and not a list.</b> Records compare by value, and a caller must be
/// able to write <c>result.Filter == FileDialogFilter.AllFiles</c> — the same affordance
/// <see cref="TaskDialogButton"/>'s well-known statics give. An <c>IReadOnlyList&lt;string&gt;</c> member would
/// compare by REFERENCE inside the synthesized equality, so two filters spelled identically would not be
/// equal and the well-known statics would stop being comparable. The semicolon-separated form is also exactly
/// what the selector shows, so there is one spelling of the pattern in the whole system rather than two.
/// </para>
/// </summary>
/// <param name="Label">The human-readable format name, e.g. <c>"PNG image"</c> — shown without the pattern in
/// prose and with it in the selector (see <see cref="DisplayText"/>).</param>
/// <param name="Pattern">
/// One or more <c>;</c>-separated globs, e.g. <c>"*.png"</c> or <c>"*.jpg;*.jpeg"</c>. <c>*</c> matches any run
/// of characters and <c>?</c> exactly one; matching is ordinal-case-insensitive, because a file dialog that
/// hides <c>PHOTO.PNG</c> from an <c>*.png</c> filter is simply broken.
/// </param>
public sealed record FileDialogFilter(string Label, string Pattern)
{
    /// <summary>
    /// The well-known "All Files (*.*)" row. A <see langword="static"/> singleton so value equality lets a
    /// caller test <c>result.Filter == FileDialogFilter.AllFiles</c> — and so the dialogs can fall back to it
    /// for a caller that supplied no filters at all without inventing a second, unequal spelling of it.
    /// </summary>
    public static FileDialogFilter AllFiles { get; } = new("All Files", "*.*");

    /// <summary>The selector's row text — <c>"PNG image (*.png)"</c>. Also what <see cref="ToString"/> returns,
    /// so a plain <see cref="Controls.ComboBox"/> over these needs no item template.</summary>
    public string DisplayText => $"{Label} ({Pattern})";

    /// <summary>
    /// The extension a <see cref="FileSaveDialog"/> appends to an extension-less typed name — the leading
    /// <c>'.'</c> included (<c>".png"</c>) — or <see langword="null"/> when the first glob names no single
    /// extension (<c>"*.*"</c>, <c>"*"</c>, <c>"*.?g"</c>). Derived from the FIRST glob only: a multi-glob
    /// filter like <c>"*.jpg;*.jpeg"</c> has a preferred spelling, and it is the one written first.
    /// </summary>
    public string? DefaultExtension
    {
        get
        {
            foreach (var glob in EnumerateGlobs(Pattern))
            {
                // "*.png" is the only shape that names an extension: a leading star, a dot, then a wholly
                // LITERAL tail. "*.*" and "*.?g" name a family, not an extension, and appending either to a
                // typed name would produce a file called "report.*".
                return glob.Length > 2 && glob[0] == '*' && glob[1] == '.'
                       && glob.AsSpan(2).IndexOfAny('*', '?') < 0
                    ? glob[1..]
                    : null;
            }

            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> passes this filter. Directories are never tested — a filter narrows
    /// which FILES are offered, and hiding the folders that lead to them would make the dialog unusable.
    /// </summary>
    /// <param name="fileName">The entry's display name, not a full path.</param>
    public bool Matches(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        foreach (var glob in EnumerateGlobs(Pattern))
        {
            if (MatchesGlob(glob, fileName))
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => DisplayText;

    private static IEnumerable<string> EnumerateGlobs(string pattern)
    {
        foreach (var part in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    // ───────────────────────────── the glob matcher ─────────────────────────────
    //
    // Deliberately hand-written rather than translated into a Regex: the patterns are caller-supplied (and, via
    // a "Custom…" filter row, potentially user-supplied), and a translated Regex over such input is a
    // catastrophic-backtracking surface. This is the classic two-pointer wildcard match with a SINGLE rewind
    // anchor, which bounds the whole thing at O(pattern × candidate) with no allocation. `*` spans any run
    // INCLUDING a dot — so "*.*" matches "archive.tar.gz" — and `?` spans exactly one character.
    private static bool MatchesGlob(string pattern, string candidate)
    {
        // ── "*.*" is match-ALL, not "names with a dot" ────────────────────────────────────────────
        // The bug this exists to prevent: a lexically honest glob engine reads "*.*" as "anything, a dot,
        // anything" and therefore hides every extension-less file — LICENSE, Makefile, README — from the
        // "All Files" filter, which is precisely the filter that promises to hide nothing. "*.*" is a Win32
        // filter idiom, not a glob, and every file dialog since has read it as "everything".
        if (pattern is "*" or "*.*")
            return true;

        var p = 0;
        var c = 0;
        var starPattern = -1;
        var starCandidate = 0;

        while (c < candidate.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], candidate[c])))
            {
                p++;
                c++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starCandidate = c;
            }
            else if (starPattern >= 0)
            {
                // Backtrack: the last star swallows one more character. The only rewind in the algorithm.
                p = starPattern + 1;
                c = ++starCandidate;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    private static bool Same(char left, char right)
        => left == right || char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
}
