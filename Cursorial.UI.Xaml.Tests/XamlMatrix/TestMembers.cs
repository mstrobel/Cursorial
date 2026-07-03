using Cursorial.UI;
using Cursorial.UI.Xaml;
using Cursorial.Rendering;
using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// The static, reflection-free member table for the X0 test types. Each entry carries the member's
/// value type, whether it is a registered <c>UIProperty</c> (the opaque <c>Property</c> marker), its
/// converter, and the deferred-content flag. The frontend reads these to classify a member's value
/// (fold / text / extension / deferred) and to honor the XD4 resolution order.
/// </summary>
internal static class TestMembers
{
    // A sentinel standing in for "this member maps to a registered UIProperty" (XD4 rule 1). The
    // loader supplies the real UIProperty at X1; the frontend only needs non-null vs null.
    private static readonly object PropertyMarker = new();

    public static Dictionary<string, XamlMember> For(Type owner, string? contentProperty, bool deferredContent)
    {
        var table = new Dictionary<string, XamlMember>(StringComparer.Ordinal);

        // Common element members.
        Add(table, "Width", typeof(int), TestConverters.IntCell);
        Add(table, "Height", typeof(int), TestConverters.IntCell);
        Add(table, "Margin", typeof(Margins), TestConverters.Margins);
        Add(table, "Visibility", typeof(Visibility), TestConverters.ForEnum<Visibility>());
        Add(table, "IsEnabled", typeof(bool), TestConverters.Bool);
        Add(table, "Opacity", typeof(double), TestConverters.Double);

        // Object-typed content slots (context-dependent — never fold at parse).
        if (owner == typeof(UIControls.Button) || owner == typeof(UIControls.ButtonBase) || owner == typeof(UIControls.Label))
            Add(table, "Content", typeof(object), converter: null, isDeferred: false);
        if (owner == typeof(UIControls.Border))
            Add(table, "Child", typeof(object), converter: null);
        if (owner == typeof(UIControls.TextBlock))
            Add(table, "Text", typeof(string), converter: null);
        if (owner == typeof(UIControls.StackPanel) || owner == typeof(UIControls.Grid))
        {
            Add(table, "Children", typeof(UIElementCollection), converter: null);
            Add(table, "Styles", typeof(object), converter: null);
        }
        if (owner == typeof(UIControls.Grid))
        {
            Add(table, "RowDefinitions", typeof(UIControls.RowDefinitionCollection), converter: null);
            Add(table, "ColumnDefinitions", typeof(UIControls.ColumnDefinitionCollection), converter: null);
            // Attached members on Grid (X75): Grid.Row / Grid.Column.
            AddAttached(table, "Row", typeof(int), TestConverters.IntCell);
            AddAttached(table, "Column", typeof(int), TestConverters.IntCell);
        }
        if (owner == typeof(UIControls.RowDefinition) || owner == typeof(UIControls.ColumnDefinition))
            Add(table, "Height", typeof(UIControls.GridLength), TestConverters.GridLength);

        // Click event for the event-rejection rows (X152).
        if (owner == typeof(UIControls.Button) || owner == typeof(UIControls.ButtonBase))
            AddEvent(table, "Click");

        // The deferred ITemplateContent member on templates.
        if (deferredContent && contentProperty is not null)
            AddDeferred(table, contentProperty);

        // Style / Setter members.
        if (owner == typeof(Style))
        {
            Add(table, "TargetType", typeof(Type), converter: null);
            Add(table, "Setters", typeof(object), converter: null);
        }
        if (owner == typeof(Setter))
        {
            Add(table, "Property", typeof(string), converter: null);
            Add(table, "Value", typeof(object), converter: null);
        }

        return table;
    }

    private static void Add(Dictionary<string, XamlMember> table, string name, Type valueType, ITypeConverter? converter, bool isDeferred = false)
        => table[name] = new XamlMember(name, valueType, property: PropertyMarker, converter: converter) { IsDeferredContent = isDeferred };

    private static void AddAttached(Dictionary<string, XamlMember> table, string name, Type valueType, ITypeConverter? converter)
        => table[name] = new XamlMember(name, valueType, property: PropertyMarker, converter: converter, isAttachable: true);

    private static void AddEvent(Dictionary<string, XamlMember> table, string name)
        => table[name] = new XamlMember(name, typeof(Delegate), isEvent: true);

    private static void AddDeferred(Dictionary<string, XamlMember> table, string name)
        => table[name] = new XamlMember(name, typeof(UIControls.ITemplateContent)) { IsDeferredContent = true };
}
