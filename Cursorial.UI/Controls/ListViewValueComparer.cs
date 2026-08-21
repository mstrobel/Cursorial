using System.Globalization;

namespace Cursorial.UI.Controls;

/// <summary>
/// The comparer behind <see cref="ListView.IsBuiltInSortEnabled"/>: projects each item through the
/// column's sort path and orders the projections.
/// </summary>
/// <remarks>
/// Deliberately forgiving, because it runs against arbitrary view-model items: a null key sorts first, an
/// <see cref="IComparable"/> key compares natively, and anything else degrades to an
/// <see cref="StringComparer.CurrentCultureIgnoreCase"/> comparison of <c>ToString()</c> rather than
/// throwing mid-sort and leaving the list half-permuted. This is the convenience path — a host that needs
/// real ordering semantics handles <see cref="ListView.Sorting"/> and sorts its own collection.
/// </remarks>
internal sealed class ListViewValueComparer(string? memberPath, ListViewSortDirection direction) : IComparer<object?>
{
    private readonly int _sign = direction == ListViewSortDirection.Descending ? -1 : 1;

    public int Compare(object? x, object? y) => _sign * CompareKeys(KeyOf(x), KeyOf(y));

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reflective value-path over a runtime object's properties (ItemsSource sort/compare); a trimmed member yields a null key — degrade, not crash.")]

    private object? KeyOf(object? item)
    {
        if (item is null || memberPath is not { Length: > 0 } path)
            return item;

        var current = item;

        foreach (var segment in path.Split('.'))
        {
            if (current is null)
                return null;

            var property = current.GetType().GetProperty(segment);
            if (property is null)
                return null;

            current = property.GetValue(current);
        }

        return current;
    }

    private static int CompareKeys(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        if (left is IComparable comparable && left.GetType() == right.GetType())
        {
            try
            {
                return comparable.CompareTo(right);
            }
            catch (ArgumentException)
            {
                // Mismatched runtime types behind a shared interface — fall through to the text compare.
            }
        }

        return string.Compare(
            Convert.ToString(left, CultureInfo.CurrentCulture),
            Convert.ToString(right, CultureInfo.CurrentCulture),
            StringComparison.CurrentCultureIgnoreCase);
    }
}
