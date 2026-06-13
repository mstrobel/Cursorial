using System.Collections;

namespace Cursorial.UI;

/// <summary>
/// The <see cref="ResourceDictionary"/> key comparer (CD1): <c>string</c> keys are <b>ordinal</b>
/// (case-sensitive); <see cref="Type"/>, <see cref="DataTemplateKey"/>, and
/// <see cref="ThemeVariantKey"/> compare by value; any other object by its own
/// <see cref="object.Equals(object)"/>/<see cref="object.GetHashCode"/>. <c>"Theme.X"</c> and
/// <c>"theme.x"</c> are distinct; <c>typeof(Button)</c> and <c>new DataTemplateKey(typeof(Button))</c>
/// are distinct (collision-free, C3).
/// </summary>
internal sealed class ResourceKeyComparer : IEqualityComparer<object>
{
    public static readonly ResourceKeyComparer Instance = new();

    private ResourceKeyComparer() {}

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;

        // Ordinal string keys (CD1) — never case-fold.
        if (x is string sx)
            return y is string sy && string.Equals(sx, sy, StringComparison.Ordinal);
        if (y is string)
            return false;

        return x.Equals(y); // Type / DataTemplateKey / ThemeVariantKey / anything else by value
    }

    public int GetHashCode(object obj)
        => obj is string s ? string.GetHashCode(s, StringComparison.Ordinal) : obj.GetHashCode();
}
