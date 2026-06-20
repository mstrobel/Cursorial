using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The additive content-property metadata (matrix X70 decision): the v1 framework types ship without
/// the frontend's <c>[ContentProperty]</c> attribute (it lives in the netstandard2.0 frontend, which
/// <c>Cursorial.UI</c> cannot reference), so the loader supplies the same mapping through this static
/// table keyed by the nearest base type with a known content property. App types may additionally
/// carry a <c>[ContentProperty("...")]</c> attribute (matched by attribute simple name), honored first.
/// A type with neither has no content property and rejects implicit content (<c>CUR2104</c>).
/// </summary>
internal static class ContentPropertyTable
{
    // Base-type → content-property mapping, most-derived first (walked against the runtime type chain).
    private static readonly (Type Base, string Property)[] Known =
    {
        (typeof(ContentControl), nameof(ContentControl.Content)),
        (typeof(Decorator), "Child"),               // Border : Decorator
        (typeof(Popup), "Child"),                   // <Popup>child</Popup> → Popup.Child (WPF [ContentProperty("Child")] parity)
        (typeof(Panel), nameof(Panel.Children)),
        (typeof(ItemsControl), nameof(ItemsControl.Items)),
        (typeof(ControlTemplate), "Content"),
        (typeof(DataTemplate), "Content"),
        (typeof(Style), nameof(Style.Setters)),     // <Style>'s implicit content is its Setters (WPF parity)
        (typeof(Drawing.Media.GradientBrush), nameof(Drawing.Media.GradientBrush.Stops)), // <LinearGradientBrush><GradientStop/>…
    };

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reads a [ContentProperty]-shaped attribute by name on a resolved XAML type.")]
    public static string? For(Type clrType)
    {
        // (1) An explicit [ContentProperty("Name")] attribute (matched by attribute simple name so an
        //     app's frontend attribute or any equivalently named attribute is honored).
        foreach (var attr in clrType.GetCustomAttributes(inherit: true))
        {
            var attrType = attr.GetType();
            if (attrType.Name != "ContentPropertyAttribute")
                continue;
            var nameProp = attrType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp?.GetValue(attr) is string name && name.Length > 0)
                return name;
        }

        // (2) The known base-type table.
        foreach (var (baseType, property) in Known)
        {
            if (baseType.IsAssignableFrom(clrType))
                return property;
        }

        return null;
    }
}
