using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// The shared symbol-fact layer (mirrors <c>ReflectionXamlMetadata</c>'s <c>BuildType</c>/<c>BuildMember</c>
/// ladder) used by BOTH the build-time parse provider (<see cref="RoslynXamlMetadata"/>) and the
/// generated-provider emitter (<see cref="MetadataProviderEmitter"/>). One source of truth so the two
/// cannot drift — the dual-run gate (matrix X174) verifies the emitted provider against the reflection
/// provider, and the parse provider must agree with the emitter or the generated tree would diverge from
/// what the build-time diagnostics saw.
/// </summary>
internal static class SymbolXamlModel
{
    // The loader's ContentPropertyTable, by base-type full name (most-derived first).
    private static readonly (string BaseType, string Property)[] ContentPropertyTable =
    [
        ("Cursorial.UI.Controls.ContentControl", "Content"),
        ("Cursorial.UI.Controls.Decorator", "Child"),
        ("Cursorial.UI.Popup", "Child"),
        ("Cursorial.UI.Controls.Panel", "Children"),
        ("Cursorial.UI.Controls.ControlTemplate", "Content"),
        ("Cursorial.UI.Controls.DataTemplate", "Content"),
        ("Cursorial.UI.Style", "Setters"),
        ("Cursorial.Drawing.Media.GradientBrush", "Stops")
    ];

    /// <summary>One XAML-settable member resolved from symbols (a property or an event).</summary>
    public readonly record struct MemberModel(
        string Name, 
        ITypeSymbol ValueType,
        bool CanWrite,
        bool CanRead,
        bool IsEvent,
        INamedTypeSymbol? RegisteredFieldOwner);

    /// <summary>The public instance properties + events of a type (most-derived first, deduped by name).</summary>
    public static IEnumerable<MemberModel> EnumerateMembers(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var symbol in t.GetMembers())
            {
                if (symbol.DeclaredAccessibility != Accessibility.Public || symbol.IsStatic)
                    continue;

                if (symbol is IPropertySymbol { IsIndexer: false } prop && seen.Add(prop.Name))
                {
                    var registeredOwner = FindRegisteredPropertyOwner(type, prop.Name);

                    yield return new MemberModel(prop.Name,
                                                 prop.Type,
                                                 CanWrite: prop.SetMethod is { DeclaredAccessibility: Accessibility.Public },
                                                 CanRead: prop.GetMethod is { DeclaredAccessibility: Accessibility.Public },
                                                 IsEvent: false,
                                                 registeredOwner);
                }
                else if (symbol is IEventSymbol evt && seen.Add(evt.Name))
                {
                    yield return new MemberModel(evt.Name,
                                                 evt.Type,
                                                 CanWrite: false,
                                                 CanRead: false,
                                                 IsEvent: true,
                                                 RegisteredFieldOwner: null);
                }
            }
        }
    }

    /// <summary>Resolves a single member by name (null on miss) — the per-member resolution the parser drives.</summary>
    public static MemberModel? FindMember(INamedTypeSymbol type, string name)
    {
        foreach (var member in EnumerateMembers(type))
        {
            if (string.Equals(member.Name, name, System.StringComparison.Ordinal))
                return member;
        }

        return null;
    }

    /// <summary>The type that declares a <c>public static &lt;name&gt;Property</c> UIProperty field (the registration convention).</summary>
    public static INamedTypeSymbol? FindRegisteredPropertyOwner(INamedTypeSymbol type, string memberName)
    {
        var fieldName = memberName + "Property";

        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            var field = t.GetMembers(fieldName)
                         .OfType<IFieldSymbol>()
                         .FirstOrDefault(f => f is { IsStatic: true, DeclaredAccessibility: Accessibility.Public } &&
                                              IsUIPropertyType(f.Type));

            if (field is not null)
                return t;
        }

        return null;
    }

    private static bool IsUIPropertyType(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = t.OriginalDefinition.ToDisplayString();

            if (name.StartsWith("Cursorial.UI.StyledProperty", System.StringComparison.Ordinal) ||
                name.StartsWith("Cursorial.UI.DirectProperty", System.StringComparison.Ordinal) ||
                name.StartsWith("Cursorial.UI.AttachedProperty", System.StringComparison.Ordinal) ||
                name == "Cursorial.UI.UIProperty")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The content property name ([ContentProperty] attribute, then the known base-type table), or null.</summary>
    public static string? ResolveContentProperty(INamedTypeSymbol type)
    {
        // (1) An explicit [ContentProperty("Name")] attribute (matched by simple name).
        foreach (var attr in type.GetAttributes())
        {
            // NOTE: explicit indexing (not a list pattern) — netstandard2.0 has no System.Index.
            if (attr.AttributeClass?.Name == "ContentPropertyAttribute"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string name && name.Length > 0)
            {
                return name;
            }
        }

        // (2) The known base-type table (most-derived first).
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            var full = t.ToDisplayString();

            foreach (var (baseType, property) in ContentPropertyTable)
            {
                if (full == baseType)
                    return property;
            }
        }

        return null;
    }

    /// <summary>True when the type's CONTENT property is collection-typed (the type-level fill flag on XamlType).</summary>
    public static bool ContentIsCollection(INamedTypeSymbol type, string contentProperty)
    {
        var prop = FindMember(type, contentProperty);

        if (prop is not { ValueType: {} ct })
            return false;

        if (ct.SpecialType == SpecialType.System_String || ct.SpecialType == SpecialType.System_Object)
            return false;

        if (ct.Name == "ITemplateContent")
            return false;

        return ImplementsIList(ct);
    }

    /// <summary>
    /// True when a value TYPE is collection-shaped — the per-member Object-vs-Items signal
    /// (<see cref="IXamlType.IsCollection"/>). Mirrors <c>ReflectionXamlType.ComputeIsCollection</c>:
    /// string/object are never collections; an <c>IList</c>/<c>IList&lt;T&gt;</c> or a
    /// <c>ResourceDictionary</c> (matched by name) is.
    /// </summary>
    public static bool IsCollectionType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String || type.SpecialType == SpecialType.System_Object)
            return false;

        if (ImplementsIList(type))
            return true;

        if (type.Name == "ResourceDictionary")
            return true;

        return false;
    }

    /// <summary>True when the value type is the deferred-content contract (<c>ITemplateContent</c>).</summary>
    public static bool IsDeferredContent(ITypeSymbol type) => type.Name == "ITemplateContent";

    private static bool ImplementsIList(ITypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IList_T)
                return true;

            if (iface.ToDisplayString() == "System.Collections.IList")
                return true;
        }

        return type.ToDisplayString() == "System.Collections.IList";
    }

    /// <summary>True when the type has a usable public parameterless activation (a default ctor / value type).</summary>
    public static bool CanActivate(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
            return false;

        if (type.IsValueType)
            return type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

        return type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
    }
}