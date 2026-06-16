using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A Levenshtein-distance "did you mean" suggester for type-not-found diagnostics (matrix X19). We
/// own the resolver, so a miss can suggest the nearest known type name. Returns an empty string when
/// nothing is close enough (distance &gt; a third of the longer name, or no candidates).
/// </summary>
internal static class DidYouMean
{
    /// <summary>Returns the closest candidate to <paramref name="name"/>, or the empty string.</summary>
    public static string Suggest(string name, string[]? candidates)
    {
        if (candidates is null || candidates.Length == 0 || name.Length == 0)
            return string.Empty;

        string best = string.Empty;
        int bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            int d = Distance(name, candidate);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        // Only suggest when the edit distance is small relative to the name length.
        int threshold = Math.Max(2, Math.Max(name.Length, best.Length) / 3);
        return bestDistance <= threshold ? best : string.Empty;
    }

    private static int Distance(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        if (la == 0) return lb;
        if (lb == 0) return la;

        var prev = new int[lb + 1];
        var curr = new int[lb + 1];

        for (int j = 0; j <= lb; j++)
            prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= lb; j++)
            {
                // case-insensitive comparison so "bordr" → "Border" scores well
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[lb];
    }
}
