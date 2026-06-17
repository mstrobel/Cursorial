using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

using Cursorial.UI.Controls;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The default, reflection-backed <see cref="IXamlTypeMetadataProvider"/> (matrix XD16): the <b>only</b>
/// place runtime reflection lives (activation / CLR setters-getters / events / <c>x:Static</c>), cached
/// per type. Registered <c>UIProperty</c> members resolve through <c>UIPropertyRegistry</c> (no
/// reflection); the static <c>[ContentProperty]</c> table is the additive content-property metadata
/// (matrix X70 — the v1 framework types ship without the frontend attribute, so the provider supplies
/// the same mapping). Honestly trim/AOT-annotated; the X5 generated provider is the supported trimmed
/// mode (deferred).
/// </summary>
[RequiresUnreferencedCode("Resolves XAML types, members, converters, and x:Static fields by reflection.")]
[RequiresDynamicCode("Compiles activation/setter thunks; AOT falls back to Activator/MethodInfo.Invoke.")]
public sealed class ReflectionXamlMetadata : IXamlTypeMetadataProvider
{
    /// <summary>The process-wide default instance over <see cref="XamlSchemaContext.Default"/>.</summary>
    public static ReflectionXamlMetadata Instance { get; } = new(XamlSchemaContext.Default);

    private readonly XamlSchemaContext _schema;
    private readonly ConcurrentDictionary<Type, XamlType> _typeCache = new();

    /// <summary>Creates a provider over <paramref name="schema"/>.</summary>
    public ReflectionXamlMetadata(XamlSchemaContext schema)
        => _schema = schema ?? throw new ArgumentNullException(nameof(schema));

    /// <inheritdoc/>
    public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
    {
        var clrType = _schema.Resolve(xmlNamespace, localName, out var ambiguous);
        if (clrType is not null)
            return XamlTypeResolution.Resolved(GetXamlType(clrType));
        if (ambiguous.Length > 0)
            return XamlTypeResolution.Ambiguous(ambiguous);
        return XamlTypeResolution.NotFound();
    }

    /// <inheritdoc/>
    public string[] GetClrNamespaces(string xmlNamespace)
    {
        var namespaces = _schema.GetClrNamespaces(xmlNamespace);
        var result = new string[namespaces.Count];
        for (int i = 0; i < namespaces.Count; i++)
            result[i] = namespaces[i];
        return result;
    }

    /// <inheritdoc/>
    public string[] GetKnownTypeNames(string xmlNamespace) => _schema.GetKnownTypeNames(xmlNamespace);

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Member-name enumeration for a did-you-mean diagnostic over a resolved XAML type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Member-name enumeration for a did-you-mean diagnostic over a resolved XAML type.")]
    public string[] GetKnownMemberNames(IXamlType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.UnderlyingSystemType is not { } clrType)
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            names.Add(prop.Name);
        foreach (var evt in clrType.GetEvents(BindingFlags.Public | BindingFlags.Instance))
            names.Add(evt.Name);

        var result = new string[names.Count];
        names.CopyTo(result);
        return result;
    }

    /// <summary>The cached <see cref="XamlType"/> for a resolved CLR type (the dual-provider drift surface).</summary>
    public XamlType GetXamlType(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return _typeCache.GetOrAdd(clrType, BuildType);
    }

    private XamlType BuildType(Type clrType)
    {
        // Force the type's static constructor — and its base types' — so every inherited UIProperty
        // registration populates the registry before any member lookup (a derived type's static ctor
        // does NOT run its base's; a member like ContentControl.Content is registered in the base).
        for (Type? t = clrType; t is not null && t != typeof(object); t = t.BaseType)
            RuntimeHelpers.RunClassConstructor(t.TypeHandle);

        var contentProperty = ContentPropertyTable.For(clrType);

        // The type IS a dictionary when it is one; the type's CONTENT is a collection when its content
        // property is collection-typed (Panel.Children) — the frontend's IsCollection drives the
        // single-Object-vs-Items decision (CommitContentChildren), so it must reflect the content slot.
        bool isSelfDictionary = typeof(ResourceDictionary).IsAssignableFrom(clrType);
        bool contentIsCollection = contentProperty is not null && ContentPropertyIsCollection(clrType, contentProperty);
        bool requiresInit = typeof(ISupportInitialize).IsAssignableFrom(clrType);

        var members = new ConcurrentDictionary<string, XamlMember?>(StringComparer.Ordinal);

        return new XamlType(
            clrType: clrType,
            activate: BuildActivator(clrType),
            contentProperty: contentProperty,
            isCollection: contentIsCollection || isSelfDictionary,
            addItem: null,
            addDictionaryItem: isSelfDictionary ? BuildAddDictionaryItem() : null,
            dictionaryKeyType: isSelfDictionary ? typeof(object) : null,
            requiresInitialize: requiresInit,
            memberResolver: name => members.GetOrAdd(name, n => BuildMember(clrType, n)));
    }

    // ── Activation ───────────────────────────────────────────────────────────────────────────────

    private static Func<object>? BuildActivator(Type clrType)
    {
        if (clrType.IsAbstract || clrType.IsInterface)
            return null;
        // Nullable<T> is a value type but Activator.CreateInstance boxes its default to a null reference (the
        // thunk's ! would hand a null downstream); it can't be an element target anyway, so reject it.
        if (Nullable.GetUnderlyingType(clrType) is not null)
            return null;
        // A value type's implicit parameterless constructor is NOT surfaced by GetConstructor(Type.EmptyTypes)
        // (it returns null), but Activator.CreateInstance always default-constructs one. Record structs (e.g.,
        // Drawing.Pen) are thus element-authorable; their init members are set via reflection SetValue on the
        // boxed instance the builder holds, which mutates the box in place (boxed-struct-safe).
        if (clrType.IsValueType || clrType.GetConstructor(Type.EmptyTypes) is not null)
            return () => Activator.CreateInstance(clrType)!;
        return null;
    }

    // ── Collections / dictionaries ─────────────────────────────────────────────────────────────────

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Reads the content property's type on a resolved XAML type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reads the content property's type on a resolved XAML type.")]
    private static bool ContentPropertyIsCollection(Type clrType, string contentProperty)
    {
        var uiProperty = UIPropertyRegistry.Find(clrType, contentProperty);
        var contentType = uiProperty?.PropertyType
            ?? clrType.GetProperty(contentProperty, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;

        if (contentType is null)
            return false;
        if (contentType == typeof(string) || contentType == typeof(object))
            return false;
        if (typeof(ITemplateContent).IsAssignableFrom(contentType))
            return false;
        return IsCollectionType(contentType);
    }

    private static bool IsCollectionType(Type clrType)
    {
        if (typeof(ResourceDictionary).IsAssignableFrom(clrType))
            return false; // handled as a dictionary
        if (clrType == typeof(string))
            return false;
        if (typeof(IList).IsAssignableFrom(clrType))
            return true;
        foreach (var iface in clrType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                return true;
        }
        return false;
    }

    private static Action<object, object, object?> BuildAddDictionaryItem()
        => static (dict, key, value) => ((ResourceDictionary)dict).Add(key, value);

    // ── Members ────────────────────────────────────────────────────────────────────────────────────

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Member resolution over a resolved XAML type; X5 generator supplies trim-clean members.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Member resolution over a resolved XAML type; X5 generator supplies trim-clean members.")]
    private XamlMember? BuildMember(Type ownerType, string name)
    {
        // (0) Style.TargetType: the styling object uses a Selector, not a TargetType property (matrix
        //     X139/DEV). Expose a synthetic Type-typed member so the frontend resolves it; the loader
        //     maps it to a type-selector at activation.
        if (ownerType == typeof(Style) && name == "TargetType")
            return new XamlMember(name, typeof(Type));

        // (1) A registered UIProperty (the XD4 first rung — reflection-free lookup).
        var uiProperty = UIPropertyRegistry.Find(ownerType, name);
        if (uiProperty is not null)
        {
            return new XamlMember(
                name,
                uiProperty.PropertyType,
                property: uiProperty,
                converter: XamlConverters.For(uiProperty.PropertyType),
                isEvent: false,
                isAttachable: uiProperty.IsAttached)
            {
                IsDeferredContent = uiProperty.PropertyType == typeof(ITemplateContent),
            };
        }

        // (2) A CLR event.
        var evt = ownerType.GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
        if (evt is not null)
            return new XamlMember(name, evt.EventHandlerType ?? typeof(Delegate), isEvent: true);

        // (3) A CLR property (setter delegate, or getter for read-only collections).
        var prop = ownerType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null)
        {
            Action<object, object?>? setClr = prop.CanWrite ? prop.SetValue : null;
            Func<object, object?>? get = prop.CanRead ? prop.GetValue : null;

            return new XamlMember(
                name,
                prop.PropertyType,
                property: null,
                setClr: setClr,
                get: get,
                converter: XamlConverters.For(prop.PropertyType),
                isEvent: false,
                isAttachable: false)
            {
                IsDeferredContent = prop.PropertyType == typeof(ITemplateContent),
            };
        }

        return null;
    }

    // ── x:Static field/property resolution (the loader's fold-finalize) ──────────────────────────────

    /// <summary>
    /// Resolves an <c>{x:Static Type.Member}</c> path to its value (matrix X26/X122): a public static
    /// field or property on a type the schema can resolve (<c>Colors.Red</c>, <c>Brushes.Red</c>, …).
    /// Returns false on an unresolvable path.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "x:Static reflects a static member on a resolved type; X5 generator supplies trim-clean values.")]
    public bool TryResolveStatic(string memberPath, out object? value)
    {
        value = null;
        int dot = memberPath.LastIndexOf('.');
        if (dot <= 0)
            return false;

        var typeName = memberPath.Substring(0, dot);
        var memberName = memberPath.Substring(dot + 1);

        var type = _schema.Resolve(XamlSchemaContext.CursorialUiNamespace, typeName, out _);
        if (type is null)
            return false;

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
        if (field is not null)
        {
            value = field.GetValue(null);
            return true;
        }

        var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
        if (prop is { CanRead: true })
        {
            value = prop.GetValue(null);
            return true;
        }

        return false;
    }
}
