using System.Collections.Concurrent;
using System.Reflection;

namespace Cursorial.UI.Controls;

/// <summary>
/// Per-control-type cache of <see cref="TemplatePartAttribute"/> declarations (design doc §12.2):
/// reflection runs once per type, the result is memoized. Includes inherited declarations (the
/// attribute is <c>Inherited = true</c>), with empty-name anchors filtered out.
/// </summary>
internal static class TemplatePartCache
{
    private static readonly ConcurrentDictionary<Type, TemplatePartAttribute[]> Cache = new();
    private static readonly TemplatePartAttribute[] Empty = [];

    /// <summary>The declared template parts for <paramref name="controlType"/> (inherited included), memoized.</summary>
    internal static TemplatePartAttribute[] For(Type controlType)
        => Cache.GetOrAdd(controlType, static t =>
        {
            var attrs = t.GetCustomAttributes<TemplatePartAttribute>(inherit: true)
                         .Where(static a => !string.IsNullOrEmpty(a.Name))
                         .ToArray();
            return attrs.Length == 0 ? Empty : attrs;
        });
}
