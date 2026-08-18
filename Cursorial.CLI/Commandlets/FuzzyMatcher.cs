namespace Cursorial.CLI.Commandlets;

/// <summary>
/// The `filter` commandlet's match engine: a case-insensitive subsequence match — every query
/// character appears in the item, in order — ranked by where the match starts, then by item length
/// (tighter items first), then by original order (a stable ranking). Pure and static, so the
/// scoring is unit-testable without a host.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Whether every character of <paramref name="query"/> appears in <paramref name="item"/> in
    /// order, case-insensitively. <paramref name="firstMatchIndex"/> reports where the first query
    /// character landed (0 for an empty query, and on failure).
    /// </summary>
    public static bool TryMatch(string item, string query, out int firstMatchIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(query);

        firstMatchIndex = 0;

        var queryIndex = 0;
        for (var i = 0; i < item.Length && queryIndex < query.Length; i++)
        {
            if (char.ToUpperInvariant(item[i]) != char.ToUpperInvariant(query[queryIndex]))
                continue;
            if (queryIndex == 0)
                firstMatchIndex = i;
            queryIndex++;
        }

        if (queryIndex == query.Length)
            return true;

        firstMatchIndex = 0;
        return false;
    }

    /// <summary>
    /// The matching items, best first: by first-match index, then item length, then original order
    /// (ties keep the input order). A null/empty query matches everything, in original order.
    /// </summary>
    public static IReadOnlyList<string> Filter(IReadOnlyList<string> items, string? query)
    {
        ArgumentNullException.ThrowIfNull(items);

        // No query, no ranking signal: everything matches, in original order (the length tiebreak
        // must not reorder an unfiltered list).
        if (string.IsNullOrEmpty(query))
            return items;

        var ranked = new List<(string Item, int First, int Order)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (TryMatch(items[i], query, out var first))
                ranked.Add((items[i], first, i));
        }

        // List.Sort is unstable; the original-order component makes the ranking total, so ties are stable by construction.
        ranked.Sort(static (a, b) =>
        {
            var byFirst = a.First.CompareTo(b.First);
            if (byFirst != 0)
                return byFirst;

            var byLength = a.Item.Length.CompareTo(b.Item.Length);
            return byLength != 0 ? byLength : a.Order.CompareTo(b.Order);
        });

        var result = new string[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
            result[i] = ranked[i].Item;
        return result;
    }
}
