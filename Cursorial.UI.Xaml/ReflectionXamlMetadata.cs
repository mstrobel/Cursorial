using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
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
public sealed class ReflectionXamlMetadata : IXamlTypeMetadataProvider, IXamlStaticResolver, IXamlAttachablePropertyProvider, IXamlOwnMemberProvider, IXamlGenericTypeProvider
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
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The W3 generic closing — the RUC reflective lane; the strict-AOT lane uses the generator's Construct()ed emission.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "MakeGenericType in the RUC lane only; the lowered lane emits closed forms statically.")]
    public XamlTypeResolution TryGetClosedType(in QualifiedTypeName name)
    {
        var closed = ResolveQualified(in name, out var ambiguous);
        if (closed is not null)
            return XamlTypeResolution.Resolved(GetXamlType(closed));
        return ambiguous.Length > 0 ? XamlTypeResolution.Ambiguous(ambiguous) : XamlTypeResolution.NotFound();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The W3 generic closing — the RUC reflective lane.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The RUC lane only.")]
    private Type? ResolveQualified(in QualifiedTypeName name, out string[] ambiguous)
    {
        ambiguous = [];

        Type? resolved;

        if (name.TypeArguments.Count == 0)
        {
            resolved = _schema.Resolve(name.XmlNamespace, name.Name, out ambiguous);

            // The corelib rescue for ARGUMENT positions too (audit — sys:DateTime as a type argument
            // resolved in the Roslyn lane but CUR2002'd here): a clr-namespace:/using: URI whose
            // assembly the schema doesn't hold falls back to the runtime's own resolution.
            if (resolved is null && ambiguous.Length == 0 &&
                XamlSchemaContext.TryDecodeClrNamespace(name.XmlNamespace, out var flatClrNs, out _))
                resolved = Type.GetType(flatClrNs + "." + name.Name);
        }
        else
        {
            // The definition: the exact name first (a using:-mapped exotic), then the CLR backtick-arity
            // form (System.Xaml's resolution rule — the element/argument name is written WITHOUT arity).
            var definition = _schema.Resolve(name.XmlNamespace, name.Name, out ambiguous) is { IsGenericTypeDefinition: true } exact &&
                             exact.GetGenericArguments().Length == name.TypeArguments.Count
                                 ? exact
                                 : _schema.Resolve(name.XmlNamespace, name.Name + "`" + name.TypeArguments.Count, out ambiguous);

            // A clr-namespace:/using: URI whose assembly the schema doesn't hold (scg:List over CoreLib —
            // THE canonical 2009 example) falls back to the runtime's own resolution.
            if (definition is null && XamlSchemaContext.TryDecodeClrNamespace(name.XmlNamespace, out var clrNs, out _))
                definition = Type.GetType(clrNs + "." + name.Name + "`" + name.TypeArguments.Count);

            if (definition is null || !definition.IsGenericTypeDefinition)
                return null;

            var arguments = new Type[name.TypeArguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                if (ResolveQualified(name.TypeArguments[i], out ambiguous) is not { } argument)
                    return null;
                arguments[i] = argument;
            }

            try
            {
                resolved = definition.MakeGenericType(arguments);
            }
            catch (ArgumentException)
            {
                return null; // constraint violation — the caller reports the positioned diagnostic
            }
        }

        if (resolved is null)
            return null;

        // The Cursorial suffix extensions, innermost-first: `x:Double?[]` is double?[].
        if (name.IsNullable)
        {
            if (!resolved.IsValueType || Nullable.GetUnderlyingType(resolved) is not null)
                return null; // '?' demands a non-nullable value type
            resolved = typeof(Nullable<>).MakeGenericType(resolved);
        }

        if (name.IsArray)
            resolved = resolved.MakeArrayType();

        return resolved;
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

        // Force static ctors (and their bases') so every inherited UIProperty registration is present before the
        // settable filter consults the registry — mirrors BuildType; idempotent and cheap after the first run.
        EnsureRegistered(clrType);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (IsSettableMember(clrType, prop)) // read-only SCALARS (Bounds, IsFocused, …) out; read-only COLLECTIONS (Setters, Children) in
                names.Add(prop.Name);
        foreach (var evt in clrType.GetEvents(BindingFlags.Public | BindingFlags.Instance))
            names.Add(evt.Name); // events classify as (settable) event-handler members

        var result = new string[names.Count];
        names.CopyTo(result);
        return result;
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Own-member enumeration for completion-ranking provenance over a resolved XAML type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Own-member enumeration for completion-ranking provenance over a resolved XAML type.")]
    public string[] GetOwnMemberNames(IXamlType targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        // A symbol backend has no runtime type to reflect / query the registry with; return nothing (the host
        // treats every member as inherited). The reflection provider always carries the CLR type.
        if (targetType.UnderlyingSystemType is not { } clrType)
            return Array.Empty<string>();

        EnsureRegistered(clrType);

        var names = new HashSet<string>(StringComparer.Ordinal);

        // (1) Reflection members DECLARED on EXACTLY clrType (DeclaredOnly ⇒ never a purely-inherited base
        //     member), under the same settable filter as GetKnownMemberNames.
        foreach (var prop in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (IsSettableMember(clrType, prop))
                names.Add(prop.Name);
        foreach (var evt in clrType.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            names.Add(evt.Name);

        // (2) Registry members whose EXACT owner is clrType — declared OR AddOwner'd here. This recovers
        //     AddOwner'd members reflection's DeclaringType misses: their CLR wrapper is often absent on the new
        //     owner (e.g. TextElement.Foreground AddOwner'd onto Control, which declares no Foreground wrapper).
        //     Read-only SCALARS are excluded by the shared settable rule; read-only COLLECTIONS stay (content-settable).
        foreach (var property in UIPropertyRegistry.OwnMembersOf(clrType))
            if (IsSettable(property))
                names.Add(property.Name);

        var result = new string[names.Count];
        names.CopyTo(result);
        return result;
    }

    // ── Settable-member classification (Cursorial's property read-only model, NOT reflection CanWrite) ──────
    //
    // A member is XAML-settable — and so appears in GetKnownMemberNames / GetOwnMemberNames — when it is
    // settable in ANY XAML form, the UNION of two disjoint ways:
    //   (a) ATTRIBUTE-settable — a settable SCALAR: a non-read-only UIProperty (regardless of its CLR
    //       wrapper's accessors), or a plain CLR property exposing a PUBLIC setter.
    //   (b) CONTENT-settable — a read-only property populated in place via property-element / content syntax:
    //       a COLLECTION (<Panel.Children>…</Panel.Children>, Style.Setters, StackPanel.Children) OR a
    //       ResourceDictionary (<Button.Resources>…</Button.Resources>). Both are things the loader fills from
    //       child elements — collections through IsCollectionType (ContentPropertyIsCollection / the
    //       collection-fill getter in BuildMember), dictionaries through the ResourceDictionary add-item path
    //       (BuildMember's addDictionaryItem). IsCollectionType deliberately reports ResourceDictionary as
    //       false (it routes to the dictionary populator, not the list one), so content-settability must name
    //       it explicitly — otherwise a read-only Resources vanishes from property-element completion.
    // Read-only SCALARS (Bounds, DesiredSize, IsFocused) are settable in NEITHER form and stay excluded — the
    // #31 fix the attribute path depends on. The union is the complete "known members" set; a caller that
    // needs the attribute-vs-content split (attribute completion wants scalars only; property-element
    // completion wants scalars + read-only collections/dictionaries) recovers it per-member from the resolved
    // XamlMember's collection-ness (the designer host already type-narrows the enumeration this way).

    /// <summary>True when <paramref name="memberType"/> is filled from child elements — a collection (the
    /// loader's <see cref="IsCollectionType"/> signal) or a <see cref="ResourceDictionary"/> — so a read-only
    /// property of this type is still CONTENT-settable via property-element / content syntax.</summary>
    private static bool IsContentSettable(Type memberType)
        => IsCollectionType(memberType) || typeof(ResourceDictionary).IsAssignableFrom(memberType);

    /// <summary>
    /// The XAML-settable rule for a REGISTERED <see cref="UIProperty"/>, shared by both member-enumeration
    /// paths: settable iff it is not <see cref="UIProperty.IsReadOnly"/> (attribute-settable) OR its type is a
    /// collection (content-settable). A read-write <c>StyledProperty</c> stays settable through the property
    /// system (<c>SetValue</c>/XAML) even when its CLR wrapper is get-only; a <c>RegisterReadOnly</c>
    /// styled/attached property and a setter-less <c>RegisterDirect</c> property are read-only — yet a read-only
    /// COLLECTION property (populated via child elements) is still content-settable and enumerable.
    /// </summary>
    private static bool IsSettable(UIProperty property) => !property.IsReadOnly || IsContentSettable(property.PropertyType);

    /// <summary>
    /// The XAML-settable rule for a reflected CLR property member. The property model is authoritative: when the
    /// member is a registered <see cref="UIProperty"/> its <see cref="UIProperty.IsReadOnly"/> flag decides
    /// (regardless of the CLR wrapper's accessors); otherwise it is a plain CLR property, attribute-settable iff
    /// it exposes a PUBLIC setter. Reflection <c>CanWrite</c> is deliberately NOT used — it is
    /// <see langword="true"/> for a non-public setter (e.g. <c>UIElement.IsArrangeValid</c>'s <c>private set</c>),
    /// which is not XAML-settable. Either kind also qualifies when its type is a COLLECTION (content-settable —
    /// <c>Style.Setters</c> / <c>Panel.Children</c> are get-only collections populated as property elements).
    /// </summary>
    private static bool IsSettableMember(Type clrType, PropertyInfo property)
        => UIPropertyRegistry.Find(clrType, property.Name) is { } uiProperty
            ? IsSettable(uiProperty)
            : property.GetSetMethod(nonPublic: false) is not null || IsContentSettable(property.PropertyType);

    /// <summary>
    /// Forces the static constructors of <paramref name="clrType"/> and its bases so every inherited /
    /// <c>AddOwner</c>'d <see cref="UIProperty"/> registration is present before a registry lookup (a derived
    /// type's static ctor does not run its base's). Idempotent; a no-op once the type has initialized.
    /// </summary>
    private static void EnsureRegistered(Type clrType)
    {
        for (Type? t = clrType; t is not null && t != typeof(object); t = t.BaseType)
            RuntimeHelpers.RunClassConstructor(t.TypeHandle);
    }

    /// <inheritdoc/>
    public XamlAttachableMember[] GetAttachableMembers(IXamlType targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        // A symbol backend has no runtime type to query the registry with; return nothing (the host's
        // completion falls back gracefully). The reflection provider always carries the CLR type.
        if (targetType.UnderlyingSystemType is not { } clrType)
            return Array.Empty<XamlAttachableMember>();

        // The registry answers "which attached properties may be set on this target" (HostType assignable
        // from the target) — including read-only ones like TextElement.IsTrimmed (attached on any UIElement,
        // but reported, never assigned). Completion offers only SETTABLE members, so apply the same
        // IsSettable gate the member paths use: a read-only attached SCALAR is dropped; a read-only attached
        // COLLECTION (content-settable via property-element syntax) is admitted. Then reshape each survivor
        // into the host-presentable (owner, name) form.
        var attachable = UIPropertyRegistry.AttachableOnType(clrType);
        if (attachable.Count == 0)
            return Array.Empty<XamlAttachableMember>();

        var result = new List<XamlAttachableMember>(attachable.Count);
        foreach (var property in attachable)
        {
            if (!IsSettable(property))
                continue;

            var owner = property.OwnerType;
            result.Add(new XamlAttachableMember(
                owner.Name, owner.Namespace ?? string.Empty, property.Name, property.TargetsChildElements));
        }

        return result.Count == 0 ? Array.Empty<XamlAttachableMember>() : result.ToArray();
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
        static bool IsSelfListType(Type t)
        {
            // Mirror the frontend's ReflectionXamlType.ComputeIsCollection / the Roslyn twin's
            // ImplementsIList: non-generic IList OR any constructed IList<T> (Styles and
            // UIElementCollection implement ONLY the generic interface — the W2b-audit parity finding:
            // the narrower check made a single-child <Styles> die CUR2104 in this lane while the
            // generated lane lowered it fine).
            if (typeof(System.Collections.IList).IsAssignableFrom(t))
                return true;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IList<>))
                return true;
            foreach (var iface in t.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                    return true;
            }
            return false;
        }

        // The type IS a dictionary when it is one; the type's CONTENT is a collection when its content
        // property is collection-typed (Panel.Children) — the frontend's IsCollection drives the
        // single-Object-vs-Items decision (CommitContentChildren), so it must reflect the content slot.
        // A SELF-LIST (an IList element with no content property — TransitionCollection, X73) counts too:
        // without it a lone child classified Object (memberId -1) and died CUR2104 instead of self-filling
        // (the W2b finding — multi-child self-lists worked, single-child ones never did).
        bool isSelfDictionary = typeof(ResourceDictionary).IsAssignableFrom(clrType);
        bool isSelfList = IsSelfListType(clrType);
        bool contentIsCollection = contentProperty is not null && ContentPropertyIsCollection(clrType, contentProperty);
        bool requiresInit = typeof(ISupportInitialize).IsAssignableFrom(clrType);

        var members = new ConcurrentDictionary<string, XamlMember?>(StringComparer.Ordinal);

        return new XamlType(
            clrType: clrType,
            activate: BuildActivator(clrType),
            contentProperty: contentProperty,
            isCollection: contentIsCollection || isSelfDictionary || isSelfList,
            addItem: null,
            addDictionaryItem: isSelfDictionary ? BuildAddDictionaryItem() : null,
            dictionaryKeyType: isSelfDictionary ? typeof(object) : null,
            requiresInitialize: requiresInit,
            memberResolver: name =>
            {
                if (members.TryGetValue(name, out var cached))
                    return cached;

                var built = BuildMember(clrType, name);

                // A None/Ambiguous route is NOT memoized (audit): the route snapshots the live converter
                // registry, and a permanent cache would turn a later XamlConverters.Register into a
                // frozen CUR2402 — the failing answer stays re-computable so registration heals it. The
                // cost sits entirely on the error path (and null members re-resolve for did-you-mean).
                if (built is null || built.Route.Kind is RouteKind.None or RouteKind.Ambiguous)
                    return built;

                return members.GetOrAdd(name, built);
            },
            isMarkupExtension: typeof(MarkupExtension).IsAssignableFrom(clrType));
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

        // DESIGNER contexts only: materialize without running any constructor — design instances
        // describe their visible state through property assignment, and view-model constructors
        // routinely demand runtime services (a UIApplication, a session). Runtime loading keeps the
        // strict contract: no public parameterless constructor ⇒ not element-activatable (null).
        // Decided at activator-build time: the design host enables the ambient switch at startup,
        // before any type is touched (XamlDesignerContext remarks).
        return XamlDesignerContext.IsDesignMode
            ? () => RuntimeHelpers.GetUninitializedObject(clrType)
            : null;
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

    // The W2e route stamp (xaml-conversion-routes.md CR1): this lane's knowledge — the parse converter,
    // the pre-bridge runtime chain, and ladder convertibility of bridge sources — fed to the SHARED probe
    // whose precedence/ambiguity rules live in the frontend (no lane drift possible on the rules).
    private static readonly IXamlType StringXamlType = new ReflectionXamlType(typeof(string));

    private static ConversionRoute ComputeRoute(Type valueType, ITypeConverter? parseConverter)
        => RouteProbe.Compute(
            new ReflectionXamlType(Nullable.GetUnderlyingType(valueType) ?? valueType), // unwrap like every chain link
            hasParseConverter: parseConverter is not null,
            hasRuntimeConverter: (XamlConverters.For(valueType) ?? XamlConverters.BclConverterForType(valueType)) is not null,
            sourceConvertible: static s => s.UnderlyingSystemType is { } st &&
                                           (XamlConverters.For(st) ?? XamlConverters.BclConverterForType(st)) is not null,
            stringType: StringXamlType);

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
            // A member-level [TypeConverter] on the CLR wrapper property (if any) wins over the property type's
            // (ForMember precedence). An attached property has no instance wrapper → null member → type-level.
            var wrapper = ownerType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var uiConverter = XamlConverters.ForMember(wrapper, uiProperty.PropertyType); // hoisted (audit: once, not twice)
            return new XamlMember(
                name,
                uiProperty.PropertyType,
                property: uiProperty,
                // A collection-typed UIProperty filled by child elements (implicit content / a property element) needs
                // a getter so the collection-fill path can read the current collection and Add — the generator fills
                // the same member through its CLR wrapper (x.Items.Add). Scalar UIProperties keep no getter (they are
                // assigned via SetValue), so only collection members are affected (#7).
                get: IsCollectionType(uiProperty.PropertyType) ? instance => (instance as UIObject)?.GetValue(uiProperty) : null,
                converter: uiConverter,
                isEvent: false,
                isAttachable: uiProperty.IsAttached)
            {
                IsDeferredContent = uiProperty.PropertyType == typeof(ITemplateContent),
                Route = ComputeRoute(uiProperty.PropertyType, uiConverter),
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
            var propConverter = XamlConverters.ForMember(prop, prop.PropertyType); // hoisted (audit)

            return new XamlMember(
                name,
                prop.PropertyType,
                property: null,
                setClr: setClr,
                get: get,
                converter: propConverter, // member [TypeConverter] wins over the type's
                isEvent: false,
                isAttachable: false)
            {
                IsDeferredContent = prop.PropertyType == typeof(ITemplateContent),
                Route = ComputeRoute(prop.PropertyType, propConverter),
            };
        }

        // (4) A public instance FIELD. Some markup extensions (and plain types) expose settable fields; the
        // generator sets them via an object initializer (XamlDataTypeScope.FindMember returns IFieldSymbol), so
        // the reflection loader must too — otherwise `{my:Gauge Threshold=42}` with a `public int Threshold;` field
        // is CUR2002 at load while compiling fine. Excludes readonly/const (not assignable post-construction).
        var fieldInfo = ownerType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (fieldInfo is { IsInitOnly: false, IsLiteral: false })
        {
            var fieldConverter = XamlConverters.ForMember(null, fieldInfo.FieldType); // hoisted (audit)
            return new XamlMember(
                name,
                fieldInfo.FieldType,
                property: null,
                setClr: fieldInfo.SetValue,
                get: fieldInfo.GetValue,
                converter: fieldConverter, // type-level [TypeConverter] (fields rarely carry one)
                isEvent: false,
                isAttachable: false)
            {
                IsDeferredContent = fieldInfo.FieldType == typeof(ITemplateContent),
                Route = ComputeRoute(fieldInfo.FieldType, fieldConverter),
            };
        }

        return null;
    }

    // ── x:Static field/property resolution (the loader's fold-finalize) ──────────────────────────────

    /// <summary>
    /// Resolves an <c>{x:Static Type.Member[.Member…]}</c> path to its value (matrix X26/X122): a public static
    /// field or property on a schema-resolvable type (<c>Colors.Red</c>, <c>Brushes.Red</c>), optionally followed by
    /// a chain of public INSTANCE member accesses (<c>Palette.Colors.Accent</c> — a Cursorial extension over WPF's
    /// <c>Type.Static</c>). The type token is resolved under <paramref name="xmlNamespace"/> — the document-bound
    /// namespace the loader passes after binding the path's prefix itself (P1C). Returns false on an unresolvable path.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "x:Static reflects static + instance members on resolved types; the X5 generator supplies trim-clean values.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "x:Static chains reflect instance members on a resolved value's runtime type; the X5 generator supplies trim-clean values.")]
    public bool TryResolveStatic(string xmlNamespace, string memberPath, out object? value)
    {
        value = null;

        // The TYPE is the FIRST segment (the XAML prefix-mapped name); the first member after it is STATIC, and any
        // deeper segments are INSTANCE member accesses on the running value (Type.Static.Instance.Instance…).
        // Splitting at the FIRST dot (not the last) is what enables the chain — a 2-segment Type.Member path is
        // unaffected (first dot == last dot). Kept byte-identical in shape to ClosedTypeSet.ResolveStaticExpr (X174).
        int firstDot = memberPath.IndexOf('.');
        if (firstDot <= 0)
            return false;

        var type = _schema.Resolve(xmlNamespace, memberPath.Substring(0, firstDot), out _);
        if (type is null)
            return false;

        // Bracket-aware segmentation: members split at dots, an [indexer] binds to the PRECEDING member
        // (never after a dot). Kept byte-identical in shape to XamlPathParser.TryTokenize (X174).
        if (!TryTokenizeStaticMembers(memberPath.Substring(firstDot + 1), out var segments))
            return false;

        // (1) The first member: a public static field or readable property directly on the type.
        if (segments.Count == 0 || segments[0].Key is not null)
            return false;

        object? current;
        if (type.GetField(segments[0].Name!, BindingFlags.Public | BindingFlags.Static) is { } staticField)
            current = staticField.GetValue(null);
        else if (type.GetProperty(segments[0].Name!, BindingFlags.Public | BindingFlags.Static) is { CanRead: true } staticProp)
            current = staticProp.GetValue(null);
        else
            return false;

        // (2) Any remaining segments: public instance members on the running value; indexer segments walk
        // the shared ladder — (a) an unquoted int-parsable key takes Item[int], (b) a key whose last
        // dot-segment names a constant of an Item[TEnum]'s key type (case-insensitive) takes it, (c)
        // anything else takes Item[string] (else Item[object]). Same order as the generator's
        // XamlPathParser.TryResolveIndexer, so the baked and reflected lanes cannot drift. A key the
        // collection does not hold throws in the BAKED lane too — a data error either way — but here it
        // degrades to the standard member-not-found so the loader reports CUR2102 instead of unwinding.
        for (int i = 1; i < segments.Count; i++)
        {
            if (current is null)
                return false;

            var runtimeType = current.GetType();
            var segment = segments[i];

            if (segment.Key is null)
            {
                if (runtimeType.GetProperty(segment.Name!, BindingFlags.Public | BindingFlags.Instance) is { CanRead: true } instanceProp)
                    current = instanceProp.GetValue(current);
                else if (runtimeType.GetField(segment.Name!, BindingFlags.Public | BindingFlags.Instance) is { } instanceField)
                    current = instanceField.GetValue(current);
                else
                    return false;
                continue;
            }

            try
            {
                if (!segment.KeyWasQuoted &&
                    int.TryParse(segment.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intKey) &&
                    runtimeType.GetProperty("Item", [typeof(int)]) is { CanRead: true } intIndexer)
                {
                    current = intIndexer.GetValue(current, [intKey]);
                    continue;
                }

                if (!segment.KeyWasQuoted && TryEnumIndexer(runtimeType, segment.Key, out var enumIndexer, out var enumKey))
                {
                    current = enumIndexer!.GetValue(current, [enumKey]);
                    continue;
                }

                var textual = runtimeType.GetProperty("Item", [typeof(string)])
                              ?? runtimeType.GetProperty("Item", [typeof(object)]);
                if (textual is not { CanRead: true })
                    return false;

                current = textual.GetValue(current, [segment.Key]);
            }
            catch (TargetInvocationException)
            {
                return false; // an absent key degrades to member-not-found (CUR2102), not an unwind
            }
        }

        value = current;
        return true;
    }

    private readonly record struct StaticSegment(string? Name, string? Key, bool KeyWasQuoted);

    private static bool TryTokenizeStaticMembers(string text, out List<StaticSegment> segments)
    {
        segments = [];
        int i = 0, n = text.Length;
        var expectMember = true;

        while (i < n)
        {
            if (expectMember)
            {
                int start = i;
                while (i < n && text[i] != '.' && text[i] != '[')
                    i++;
                if (i == start)
                    return false;
                segments.Add(new StaticSegment(text.Substring(start, i - start), null, false));
                expectMember = false;
                continue;
            }

            if (text[i] == '.')
            {
                if (i + 1 < n && text[i + 1] == '[')
                    return false; // an indexer never follows a dot
                i++;
                expectMember = true;
                continue;
            }

            if (text[i] == '[')
            {
                i++;
                string key;
                bool quoted = false;

                if (i < n && text[i] == '\'')
                {
                    quoted = true;
                    i++;
                    var sb = new StringBuilder();
                    while (i < n && text[i] != '\'')
                    {
                        if (text[i] == '\\' && i + 1 < n)
                            i++;
                        sb.Append(text[i]);
                        i++;
                    }
                    if (i >= n)
                        return false;
                    i++;
                    if (i >= n || text[i] != ']')
                        return false;
                    key = sb.ToString();
                }
                else
                {
                    int start = i;
                    while (i < n && text[i] != ']')
                    {
                        if (text[i] == ',')
                            return false;
                        i++;
                    }
                    if (i >= n || i == start)
                        return false;
                    key = text.Substring(start, i - start).Trim();
                    if (key.Length == 0)
                        return false;
                }

                i++;
                segments.Add(new StaticSegment(null, key, quoted));
                continue;
            }

            return false;
        }

        return !expectMember && segments.Count > 0;
    }

    private static bool TryEnumIndexer(Type runtimeType, string key, out PropertyInfo? indexer, out object? enumKey)
    {
        indexer = null;
        enumKey = null;

        var lastDot = key.LastIndexOf('.');
        var lastSegment = lastDot >= 0 ? key.Substring(lastDot + 1) : key;

        foreach (var property in runtimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name != "Item" || !property.CanRead)
                continue;
            var parameters = property.GetIndexParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
                continue;
            if (!Enum.TryParse(parameters[0].ParameterType, lastSegment, ignoreCase: true, out var parsed))
                continue;

            indexer = property;
            enumKey = parsed;
            return true;
        }

        return false;
    }
}
