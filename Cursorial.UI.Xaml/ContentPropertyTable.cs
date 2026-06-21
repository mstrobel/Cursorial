using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Cursorial.UI.Xaml;

/// <summary>
/// Content-property metadata, attribute-driven (matrix X70): the framework types carry
/// <c>Cursorial.Markup.[ContentProperty("Name")]</c> (in <c>Cursorial.Shared</c>, which every layer can
/// reference) on the relevant base types; the attribute is <c>Inherited</c>, so a subclass picks up its
/// nearest decorated ancestor's content property. App types decorate the same way. Matched on the attribute's
/// simple name (so any equivalently named attribute is honored). A type with no such attribute — directly or
/// inherited — has no content property and rejects implicit content (<c>CUR2104</c>).
/// </summary>
internal static class ContentPropertyTable
{
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reads a [ContentProperty]-shaped attribute by name on a resolved XAML type.")]
    public static string? For(Type clrType)
    {
        // An [ContentProperty("Name")] attribute (inherited — the nearest decorated ancestor wins), matched by
        // attribute simple name so the framework's Cursorial.Markup attribute or any equivalent is honored.
        foreach (var attr in clrType.GetCustomAttributes(inherit: true))
        {
            var attrType = attr.GetType();
            if (attrType.Name != "ContentPropertyAttribute")
                continue;
            var nameProp = attrType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp?.GetValue(attr) is string name && name.Length > 0)
                return name;
        }

        return null;
    }
}
