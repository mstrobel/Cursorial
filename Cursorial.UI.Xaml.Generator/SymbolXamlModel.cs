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
    /// <summary>One XAML-settable member resolved from symbols (a property, an event, or an attached property).</summary>
    public readonly record struct MemberModel(
        string Name,
        ITypeSymbol ValueType,
        bool CanWrite,
        bool CanRead,
        bool IsEvent,
        INamedTypeSymbol? RegisteredFieldOwner,
        bool IsAttached = false,
        // An init-only CLR property setter (settable in an object initializer, not post-construction). The
        // generated provider can't emit a compiled `t.Prop = v` for it (CS8852) — it sets it reflectively.
        bool IsInitOnly = false,
        // True when the member OR its value type carries a Cursorial.Markup [TypeConverter]/[ValueSerializer]. The
        // emitter then emits a runtime XamlConverters.ForMember(...) call (identical to the reflection provider, so
        // zero drift + no baking accessibility traps) instead of the AOT-clean pure-ladder For(typeof(T)).
        bool UsesAttributeConverter = false);

    /// <summary>
    /// The XAML-settable members of a type (most-derived first, deduped by name): public instance properties
    /// and events, PLUS registered properties exposed only as a <c>&lt;Name&gt;Property</c> static field with
    /// no instance CLR wrapper — i.e. attached properties like <c>Grid.Row</c>. The reflection provider
    /// resolves the latter through <c>UIPropertyRegistry.Find</c> (rung 1 of its BuildMember ladder); the
    /// symbol model has no registry, so it mirrors that by reading the <c>&lt;Name&gt;Property</c> field
    /// convention. WITHOUT this pass a symbol-backed parse of <c>&lt;Button Grid.Row="0"/&gt;</c> would emit a
    /// false CUR2102.
    /// </summary>
    public static IEnumerable<MemberModel> EnumerateMembers(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        // Pass 1 — public instance properties + events (CLR ladder rungs 2-3). A registered StyledProperty
        // that ALSO has an instance CLR wrapper (e.g. Width/WidthProperty) is caught here and annotated with
        // its RegisteredFieldOwner, so the emitter bakes `property: <Owner>.WidthProperty` (rung-1 parity).
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
                                                 registeredOwner,
                                                 IsInitOnly: prop.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: true },
                                                 UsesAttributeConverter: MemberHasConverterAttribute(prop) || TypeHasMarkupConverterAttribute(prop.Type));
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

        // Pass 2 — registered UIProperty fields with NO instance CLR wrapper (attached properties, rung 1).
        // Run after pass 1 so an instance-backed registered property (Width) is never double-yielded.
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var symbol in t.GetMembers())
            {
                if (symbol is not IFieldSymbol field || !IsRegisteredPropertyField(field, out var attached, out var valueType))
                    continue;

                var memberName = field.Name.Substring(0, field.Name.Length - PropertyFieldSuffix.Length);
                if (memberName.Length > 0 && seen.Add(memberName))
                {
                    yield return new MemberModel(memberName,
                                                 valueType,
                                                 CanWrite: true,
                                                 CanRead: true,
                                                 IsEvent: false,
                                                 RegisteredFieldOwner: t,
                                                 IsAttached: attached,
                                                 UsesAttributeConverter: TypeHasMarkupConverterAttribute(valueType));
                }
            }
        }
    }

    // True when the MEMBER carries a converter/serializer attribute — Cursorial.Markup [TypeConverter]/
    // [ValueSerializer] OR the BCL System.ComponentModel.TypeConverter (member-level interop). The emitter then
    // emits a runtime XamlConverters.ForMember(...) call (identical to the reflection provider — same precedence,
    // accessibility, string-name ctor, BCL adaptation; zero drift, no `new T()` baking that could fail to compile).
    private static bool MemberHasConverterAttribute(ISymbol symbol)
    {
        // Walk the overridden-property chain — the reflection provider reads member attributes with inherit:true
        // (System.Attribute.GetCustomAttribute / GetCustomAttributes), but Roslyn's GetAttributes() is direct-only,
        // so an attribute on a base virtual that a derived member overrides must be picked up here too (zero drift).
        for (var s = symbol; s is not null; s = (s as IPropertySymbol)?.OverriddenProperty)
        {
            if (s.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString()
                    is "Cursorial.Markup.TypeConverterAttribute" or "Cursorial.Markup.ValueSerializerAttribute"
                    or "System.ComponentModel.TypeConverterAttribute"))
                return true;
        }
        return false;
    }

    // As above for a value TYPE — walks the base chain (the attribute is Inherited; GetAttributes() is direct-only)
    // and unwraps Nullable<T> first (mirroring XamlConverters.ForMember's `underlying` — a `MyEnum?` member must see
    // MyEnum's [TypeConverter]). Cursorial.Markup only: a TYPE-level BCL [TypeConverter] is the loader's ConvertText
    // fallback (after the ladder), NOT flagged here — else every primitive member (int/double/… all carry a BCL
    // TypeConverter) would lose its AOT-clean baked For(typeof(T)) and the ladder would be bypassed.
    private static bool TypeHasMarkupConverterAttribute(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            if (t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString()
                    is "Cursorial.Markup.TypeConverterAttribute" or "Cursorial.Markup.ValueSerializerAttribute"))
                return true;
        }
        return false;
    }

    private const string PropertyFieldSuffix = "Property";

    // A `public static readonly <Name>Property` field of UIProperty type → its value type is the UIProperty's
    // last generic argument (AttachedProperty<int>→int, DirectProperty<TOwner,T>→T). `attached` is true for an
    // AttachedProperty<T> (the loader assigns it via SetValue on the attached registration, IsAttachable).
    private static bool IsRegisteredPropertyField(IFieldSymbol field, out bool attached, out ITypeSymbol valueType)
    {
        attached = false;
        valueType = null!;

        if (!field.IsStatic || field.DeclaredAccessibility != Accessibility.Public)
            return false;
        if (!field.Name.EndsWith(PropertyFieldSuffix, System.StringComparison.Ordinal) || field.Name.Length == PropertyFieldSuffix.Length)
            return false;
        if (!IsUIPropertyType(field.Type))
            return false;
        if (field.Type is not INamedTypeSymbol { IsGenericType: true } named || named.TypeArguments.Length < 1)
            return false;

        valueType = named.TypeArguments[named.TypeArguments.Length - 1];
        attached = IsAttachedPropertyType(field.Type);
        return true;
    }

    private static bool IsAttachedPropertyType(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.OriginalDefinition.ToDisplayString().StartsWith("Cursorial.UI.AttachedProperty", System.StringComparison.Ordinal))
                return true;
        }
        return false;
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

    /// <summary>The content property name from an inherited <c>[ContentProperty("Name")]</c> attribute, or null.</summary>
    public static string? ResolveContentProperty(INamedTypeSymbol type)
    {
        // The attribute is Inherited, but Roslyn's GetAttributes() returns only directly-declared attributes —
        // so walk the base chain and take the nearest decorated ancestor (mirroring the reflection provider's
        // GetCustomAttributes(inherit:true)). Matched by attribute simple name, like the loader.
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var attr in t.GetAttributes())
            {
                // ReSharper disable once MergeIntoPattern - explicit indexing (not a list pattern) — netstandard2.0 has no System.Index.
                if (attr.AttributeClass?.Name == "ContentPropertyAttribute" &&
                    attr.ConstructorArguments.Length == 1 &&
                    attr.ConstructorArguments[0].Value is string name &&
                    name.Length > 0)
                {
                    return name;
                }
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

    /// <summary>True when the type is (or derives from) <c>Cursorial.UI.ResourceDictionary</c> — a keyed
    /// dictionary the loader fills via <c>Add(key, value)</c> (the type-level dictionary flag).</summary>
    public static bool IsResourceDictionary(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            if (t.ToDisplayString() == "Cursorial.UI.ResourceDictionary")
                return true;
        }
        return false;
    }

    /// <summary>True when the type — or any base type — implements
    /// <c>System.ComponentModel.ISupportInitialize</c>, directly or transitively (e.g. via
    /// <c>ISupportInitializeNotification</c>, which extends it). The loader brackets such a type's member
    /// sets with <c>BeginInit</c>/<c>EndInit</c>. Walks the base-class chain AND, at each level, the
    /// interface-extension hierarchy explicitly — recursing each interface's own bases via
    /// <c>Interfaces</c> rather than trusting <c>AllInterfaces</c> to flatten it — so the transitive and
    /// inherited cases resolve the same way the reflection provider's <c>IsAssignableFrom</c> does.</summary>
    public static bool RequiresInitialize(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (var iface in current.Interfaces)
            {
                if (InterfaceIsOrExtendsSupportInitialize(iface))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ISupportInitializeNotification extends ISupportInitialize, so a type implementing only the notification
    // form still requires init — match by recursing each interface's directly-extended bases (Interfaces),
    // walking the interface hierarchy independently of the class hierarchy.
    private static bool InterfaceIsOrExtendsSupportInitialize(INamedTypeSymbol iface)
    {
        if (iface.Name == "ISupportInitialize" &&
            iface.ContainingNamespace?.ToDisplayString() == "System.ComponentModel")
        {
            return true;
        }

        foreach (var baseInterface in iface.Interfaces)
        {
            if (InterfaceIsOrExtendsSupportInitialize(baseInterface))
            {
                return true;
            }
        }

        return false;
    }

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

        var hasRequiredProperties = type.GetMembers().OfType<IPropertySymbol>().Any(p => p.IsRequired);

        return type.InstanceConstructors.Any(
            c => c.Parameters.Length == 0 &&
                 c.DeclaredAccessibility == Accessibility.Public &&
                 (!hasRequiredProperties || c.GetAttributes()
                                             .Any(a => a.AttributeClass?.ToDisplayString() ==
                                                       "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")));
    }
}