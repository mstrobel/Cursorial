using System.Collections;
using System.ComponentModel;

using Cursorial.Markup;
using Cursorial.UI.Controls;

// ReSharper disable UnusedParameter.Local
// ReSharper disable once UnusedMember.Local

namespace Cursorial.UI.Xaml;

/// <summary>
/// Stage 2 (matrix §§6–9): walks the immutable <see cref="XamlDocument"/> node graph into a live object
/// graph. Activates objects (parameterless ctor / a supplied root instance), sets members through the
/// XD4 ladder (registered <c>UIProperty</c> via <c>SetValue</c> at <see cref="BindingPriority.LocalValue"/>,
/// else the CLR setter), runs the terminal converter ladder on <c>Text</c> values, fills content /
/// implicit collections / dictionaries / attached properties, registers <c>x:Name</c> in the document
/// namescope, and folds access-key literals on flagged object slots (XD11). Markup-extension live attach
/// (X2) routes through the optional <see cref="IXamlMarkupExtensionHandler"/> seam; deferred content (X3)
/// through <see cref="IXamlDeferredContentFactory"/>.
/// </summary>
internal sealed class XamlObjectGraphBuilder
{
    private readonly XamlDocument _doc;
    private readonly XamlLoaderOptions _options;
    private readonly Uri? _source;
    private readonly NameScopeDictionary _documentScope = new();
    private readonly Dictionary<string, int> _registeredNames = new(StringComparer.Ordinal);
    private readonly XamlResourceScopeStack _scopes;
    private readonly XamlMarkupExtensionHandler _extensionHandler;
    private readonly IXamlDeferredContentFactory _deferredContentFactory;

    // The namespace-aware selector resolver (lazy — only Styles need it): binds 'prefix|Type' selector
    // tokens + prefixed TargetTypes against the document's root xmlns table (#23).
    private XamlSelectorTypeResolver? _selectorResolver;
    private XamlPathTypeResolver? _pathResolver;

    // The object whose member set is currently in progress and the document root (the
    // IRootObjectProvider surface — matrix X125).
    private object? _currentObject;
    private object? _rootObject;

    /// <summary>
    /// In template-build mode (a per-<c>Build</c> instantiation of a deferred slice, matrix §13.2): the
    /// build context whose <see cref="TemplateBuildContext.NameScope"/> receives <c>x:Name</c> parts and
    /// whose <see cref="TemplateBuildContext.TemplatedParent"/> anchors <c>{TemplateBinding}</c>. Null in
    /// the ordinary document walk.
    /// </summary>
    internal TemplateBuildContext? TemplateContext { get; init; }

    /// <summary>The captured definition-site lexical scope a template build resolves StaticResource against (matrix X162).</summary>
    internal CapturedScopeChain CapturedTemplateScope { get; init; }

    /// <summary>The URI of the source Xaml document.</summary>
    internal Uri? Source => _source;

    private bool InTemplateBuild => TemplateContext is not null;

    internal XamlObjectGraphBuilder(XamlDocument doc, XamlLoaderOptions options, Uri? source, IResourceScope? ambient = null)
    {
        _doc = doc;
        _options = options;
        _source = source;
        _scopes = new XamlResourceScopeStack(ambient ?? ResourceScopes.ForApplication());
        _extensionHandler = new XamlMarkupExtensionHandler(_scopes);
        _deferredContentFactory = new XamlDeferredContentFactory(_options);
    }

    internal NameScopeDictionary DocumentScope => _documentScope;

    /// <summary>The document root object (the <see cref="IRootObjectProvider"/> surface, matrix X125).</summary>
    internal object? RootObject => _rootObject;

    /// <summary>The lexical ambient resource stack (StaticResource / deferred-entry capture).</summary>
    internal XamlResourceScopeStack Scopes => _scopes;

    /// <summary>Builds the document, returning the root object. <paramref name="rootInstance"/> populates the root instead of activating.</summary>
    internal object Build(object? rootInstance)
    {
        if (!_doc.HasObjects)
            throw Fatal(XamlDiagnosticCodes.EmptyDocument, "The document has no root element.", 1, 1);

        var root = InstantiateObject(0, rootInstance);
        _rootObject = root;

        // The whole tree is built and the document name scope is fully populated — resolve any forward name
        // reference ({x:Reference}, or a {Binding ElementName=…}/{Binding Source={x:Reference …}} whose named
        // element appeared later in the document).
        ResolveDeferredReferences();

        // Attach the document namescope to a UIElement root (matrix X101/XD15) for runtime FindName / code-first
        // ElementName resolution.
        if (root is UIElement rootElement)
            NameScope.SetNameScope(rootElement, _documentScope);

        return root;
    }

    private readonly List<DeferredNameResolution> _deferredReferences = [];

    private readonly record struct DeferredNameResolution(string Name, int Line, int Column, Action<object> Assign);

    /// <summary>The name scope the current build resolves <c>x:Name</c> references against — the per-build template
    /// scope inside a template slice (matrix X155), else the document scope.</summary>
    private INameScope ResolutionScope => TemplateContext?.NameScope ?? _documentScope;

    /// <summary>Finds the element registered under <paramref name="name"/> in the current build's name scope (the
    /// document scope, or a template slice's per-build scope). Null when not (yet) registered — a forward reference.</summary>
    internal object? ResolveName(string name) => ResolutionScope.Find(name);

    /// <summary>Records an <c>{x:Reference Name}</c> direct-value reference whose named element was not yet built (a
    /// forward reference); <see cref="ResolveDeferredReferences"/> assigns it after the (sub)tree is built.</summary>
    internal void DeferReference(object target, XamlMember member, string name, int line, int column)
        => DeferNameResolution(name, line, column, element => AssignResolvedValue(member, target, element, line, column));

    /// <summary>Records a forward name reference resolved to its element at end-of-(sub)tree (generalizes
    /// <see cref="DeferReference"/> for the binding <c>ElementName</c> / <c>Source={x:Reference}</c> cases, which
    /// set the resolved element as the binding's source and install it).</summary>
    internal void DeferNameResolution(string name, int line, int column, Action<object> assign)
        => _deferredReferences.Add(new DeferredNameResolution(name, line, column, assign));

    /// <summary>Resolves forward name references now that the (sub)tree's name scope is full.</summary>
    private void ResolveDeferredReferences()
    {
        foreach (var reference in _deferredReferences)
        {
            var element = ResolutionScope.Find(reference.Name)
                ?? throw Fatal(XamlDiagnosticCodes.ReferenceNotFound,
                    $"Could not resolve the x:Name reference '{reference.Name}' — no element with that name exists in this scope.",
                    reference.Line, reference.Column);

            reference.Assign(element);
        }
    }

    /// <summary>
    /// Instantiates a deferred template slice rooted at <paramref name="sliceHead"/> in template-build
    /// mode (matrix §13.2): a fresh subtree, names registered into the template scope, StaticResource
    /// resolved against the captured definition-site chain (matrix X162).
    /// </summary>
    internal object BuildTemplateSlice(int sliceHead)
    {
        // Push the captured definition-site lexical chain so {StaticResource} inside the body resolves
        // against the template's enclosing definition scope, not the instantiation site (matrix X162/X163).
        _scopes.PushChain(CapturedTemplateScope);
        var root = InstantiateObject(sliceHead);
        // Resolve forward name references INSIDE the template against the per-build template name scope (the slice's
        // counterpart of Build's resolution pass) — so an ElementName / {x:Reference} binding works in a DataTemplate
        // exactly as at the document level, regardless of part order.
        ResolveDeferredReferences();
        return root;
    }

    // ── Object instantiation ─────────────────────────────────────────────────────────────────────

    private object InstantiateObject(int objectIndex, object? existingInstance = null)
    {
        ref readonly var record = ref _doc.Objects[objectIndex];
        var type = record.TypeId >= 0 ? _doc.ResolvedTypes[record.TypeId] : null;
        int line = LineInfo.Line(record.PackedLineInfo);
        int column = LineInfo.Column(record.PackedLineInfo);

        // <x:Array Type="T"> builds a T[] from its items (XD27); TypeId is the ELEMENT type T, not the
        // object's own type, so this must precede the type-null throw and the normal activation path.
        if (record.HasFlag(ObjectFlags.IsArray))
            return BuildArray(objectIndex, in record, type, line, column);

        // A markup extension in element form consumed as a dictionary or collection ENTRY (no target member):
        // produce its context-independent value — a folded constant, a resolved StaticResource, or a
        // DynamicResource carrier (resource aliasing). In a member VALUE position it never reaches here (the
        // Object-member case attaches it to the target member instead).
        if (record.HasFlag(ObjectFlags.IsMarkupExtension))
            return ProvideExtensionEntryValue(in record, line, column)!;

        if (type is null)
            throw Fatal(XamlDiagnosticCodes.TypeNotFound, "The element's type did not resolve.", line, column);

        // A built-in primitive element initializes from its content text (XD28): <x:String>hi</x:String> → "hi",
        // <x:Int32>5</x:Int32> → 5. It has no parameterless-activate-then-set-content path (a value type would
        // activate to its default and then reject the content text), so intercept before activation.
        if (existingInstance is null && XamlSchemaContext.IsBuiltInType(type.SystemType()))
            return BuildInitTextPrimitive(objectIndex, in record, type, line, column);

        // A <Setter> is construction-immutable (no parameterless ctor): build it from its Property/Value
        // members directly (matrix X117/X129 — Setters in style/resource dictionaries).
        if (existingInstance is null && type.SystemType() == typeof(Setter))
            return BuildSetter(objectIndex, in record, line, column);

        object instance = existingInstance ?? ActivateSpecialOrDefault(objectIndex, in record, type, line, column);

        // Designer/tooling opt-in: remember where this object came from. Template builds flow
        // through here too, registering against the template's own defining document.
        if (_options.TrackSourceInfo)
            XamlSourceRegistry.Register(instance, _source ?? _doc.SourceUri, line, column);

        // A self-ResourceDictionary (the root <ResourceDictionary> or a <Foo.Resources> dictionary element)
        // loads via the dedicated keyed/merged/themed/deferred path (matrix §11/§12).
        if (instance is ResourceDictionary selfDict)
        {
            LoadResourceDictionary(objectIndex, in record, selfDict, line, column);
            return instance;
        }

        bool needsInit = (instance is ISupportInitialize) && record.HasFlag(ObjectFlags.NeedsBeginInit);
        if (needsInit)
            ((ISupportInitialize)instance).BeginInit();

        var previousObject = _currentObject;
        _currentObject = instance;
        IDisposable? deferScope = instance is UIObject uiObject ? uiObject.DeferNotifications() : null;
        int scopeDepth = _scopes.Depth;
        try
        {
            ApplyMembers(objectIndex, in record, instance, type, line, column);
        }
        finally
        {
            _scopes.PopDownTo(scopeDepth);
            deferScope?.Dispose();
            _currentObject = previousObject;
            if (needsInit)
                ((ISupportInitialize)instance).EndInit();
        }

        // A <DataCondition> with no Binding: the Xaml init lane bypasses the ctors' RequireReflectionLane guard, so a
        // Binding-less condition would otherwise load clean and then throw an opaque ArgumentNullException deep in
        // style arming (BindingOperations.Watch). Reject it at the authoring site with a positioned diagnostic —
        // the DataCondition analog of the Setter "requires a resolvable Property" guard.
        if (instance is DataCondition { Binding: null })
            throw Fatal(XamlDiagnosticCodes.MemberNotFound, "A <DataCondition> requires a Binding.", line, column);

        return instance;
    }

    private object Activate(XamlType type, int line, int column)
    {
        if (type.Activate is { } activate)
            return activate();
        throw Fatal(XamlDiagnosticCodes.NoParameterlessConstructor,
            $"Type '{type.ClrType.Name}' has no usable public parameterless constructor.", line, column);
    }

    /// <summary>
    /// Builds a <c>T[]</c> for an <c>&lt;x:Array Type="T"&gt;</c> object (XD27): <paramref name="elementType"/>
    /// is the element type T (the object's <see cref="ObjectRecord.TypeId"/>), and the single <c>Items</c>
    /// member (synthetic <c>MemberId == -1</c>) holds the array's elements, walked depth-first by subtree length.
    /// </summary>
    private object BuildArray(int objectIndex, in ObjectRecord record, XamlType? elementType, int line, int column)
    {
        if (elementType is null)
            throw Fatal(XamlDiagnosticCodes.TypeNotFound, "The x:Array element type did not resolve.", line, column);

        Type clr = elementType.SystemType();

        int first = -1, count = 0;
        for (int i = 0; i < record.MemberCount; i++)
        {
            var m = _doc.Members[record.MemberStart + i];
            if (m.Kind == XamlValueKind.Items) { first = m.ValueIndex; count = m.ItemCount; break; }
        }

        var array = Array.CreateInstance(clr, count);
        if (count > 0)
        {
            int k = 0;
            foreach (var childIndex in EnumerateItems(first, count))
            {
                var item = InstantiateObject(childIndex);

                // Let Array.SetValue be the arbiter — it accepts reference assignability AND primitive widening
                // (an int item into a long[]/double[]), which IsInstanceOfType would reject. A genuinely
                // incompatible item surfaces as a positioned CUR2401.
                try
                {
                    array.SetValue(item, k);
                }
                catch (Exception ex) when (ex is InvalidCastException or ArgumentException)
                {
                    ref readonly var childRecord = ref _doc.Objects[childIndex];
                    throw Fatal(XamlDiagnosticCodes.ConversionFailed,
                        $"x:Array element {k} is a '{item?.GetType().Name ?? "null"}', not assignable to the array type '{clr.Name}'.",
                        LineInfo.Line(childRecord.PackedLineInfo), LineInfo.Column(childRecord.PackedLineInfo), ex);
                }

                k++;
            }
        }

        RegisterSpecialObjectName(objectIndex, in record, array);
        return array;
    }

    /// <summary>
    /// Builds a built-in primitive value from its content text (XD28): the element's synthetic content-text
    /// member (or the empty string when absent) is converted to the built-in's CLR type through the same
    /// converter ladder a member value uses (XD3 fold-equivalence).
    /// </summary>
    private object BuildInitTextPrimitive(int objectIndex, in ObjectRecord record, XamlType type, int line, int column)
    {
        Type clr = type.SystemType();

        string text = string.Empty;
        for (int i = 0; i < record.MemberCount; i++)
        {
            var m = _doc.Members[record.MemberStart + i];
            if (m.Kind == XamlValueKind.Text && m.MemberId < 0) { text = _doc.Strings[m.ValueIndex]; break; }
        }

        object value = ConvertInitText(clr, text, line, column);
        RegisterSpecialObjectName(objectIndex, in record, value);
        return value;
    }

    /// <summary>Converts a built-in primitive element's initialization text to its CLR value (XD28).</summary>
    private object ConvertInitText(Type clr, string text, int line, int column)
    {
        if (clr == typeof(string) || clr == typeof(object))
            return text;

        var converter = XamlConverters.For(clr);
        if (converter is null)
            return text; // no converter — return the raw string (matches ConvertText's fall-through)

        var ctx = new XamlValueContext(_options.ConverterCulture, targetMember: null, clr, _source, line, column);
        return converter.ConvertFromString(text, in ctx)
            ?? throw Fatal(XamlDiagnosticCodes.ConversionFailed, $"Could not convert '{text}' to '{clr.Name}'.", line, column);
    }

    /// <summary>Registers an <c>x:Name</c> on a special-built object (an x:Array / built-in primitive) into the
    /// active name scope — the directive path the normal <see cref="ApplyMembers"/> loop runs for ordinary
    /// objects (x:Key is consumed by the parent dictionary path, not here).</summary>
    private void RegisterSpecialObjectName(int objectIndex, in ObjectRecord record, object instance)
    {
        if (!record.HasFlag(ObjectFlags.HasName))
            return;

        for (int i = 0; i < record.MemberCount; i++)
        {
            var m = _doc.Members[record.MemberStart + i];
            if (m.Kind == XamlValueKind.Directive && (XamlDirectiveKind) m.DirectiveKind == XamlDirectiveKind.Name)
            {
                RegisterName(_doc.Strings[m.ValueIndex], instance, objectIndex,
                             LineInfo.Line(m.PackedLineInfo), LineInfo.Column(m.PackedLineInfo));
                return;
            }
        }
    }

    /// <summary>
    /// Activates an object, handling the <c>Style</c> ⇒ <c>init</c>-only <see cref="Selector"/> mapping
    /// (matrix X139/DEV, #23): a <c>TargetType="Button"</c> reconstructs the <see cref="Style"/> from an
    /// EXACT-type selector (the resolved CLR type, namespace-aware), and an explicit <c>Selector="..."</c>
    /// is parsed with the namespace-aware resolver (so a <c>prefix|Type</c> token binds the document xmlns).
    /// </summary>
    private object ActivateSpecialOrDefault(int objectIndex, in ObjectRecord record, XamlType type, int line, int column)
    {
        if (type.SystemType() == typeof(Style))
        {
            // An explicit Selector is the matcher and WINS; a co-present TargetType is only the frontend's
            // Setter-property resolution hint (already consumed at parse), not a second selector. TargetType
            // alone synthesizes the exact-type selector.
            if (TryGetStyleStringMember(in record, "Selector", out string selectorText))
                return new Style(BuildSelector(selectorText, line, column));

            if (TryGetStyleStringMember(in record, "TargetType", out string targetTypeName))
                return new Style(BuildTargetTypeSelector(targetTypeName, line, column));
        }

        return Activate(type, line, column);
    }

    /// <summary>The namespace-aware selector resolver over the document's root xmlns table + loader metadata.</summary>
    private XamlSelectorTypeResolver SelectorResolver
        => _selectorResolver ??= new XamlSelectorTypeResolver(_doc.Namespaces, _options.MetadataProvider);

    /// <summary>The namespace-aware binding-path type resolver — resolves a <c>(prefix:Type.Member)</c> attached
    /// segment's owner against the document's root xmlns table + loader metadata (bare tokens fall back to the
    /// registry). Handed to every XAML-authored <c>Binding</c> so prefixed attached-property paths resolve.</summary>
    internal Cursorial.UI.Data.IPathTypeResolver PathTypeResolver
        => _pathResolver ??= new XamlPathTypeResolver(_doc.Namespaces, _options.MetadataProvider);

    /// <summary>The service provider handed to a context-dependent <see cref="ITypeConverter"/> at load
    /// (<see cref="XamlValueContext.Services"/>) — exposes the xmlns-aware <see cref="PathTypeResolver"/> so a
    /// <c>PropertyPath</c>-typed member value resolves its type qualifications against the document's namespaces.
    /// Cached: one instance per document build.</summary>
    private IServiceProvider ConversionServices
        => _conversionServices ??= new ConversionServiceProvider(PathTypeResolver);

    private ConversionServiceProvider? _conversionServices;

    // The minimal load-time IServiceProvider for converters — resolves the binding-path type resolver only.
    private sealed class ConversionServiceProvider(Cursorial.UI.Data.IPathTypeResolver pathResolver) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(Cursorial.UI.Data.IPathTypeResolver) ? pathResolver : null;
    }

    /// <summary>
    /// Builds a Style's selector from its <c>TargetType</c> as an EXACT-type selector (the resolved CLR type),
    /// namespace-aware via the document xmlns table + metadata. A <c>prefix:</c>-qualified name MUST bind a
    /// declared (root) xmlns — an unbound prefix is a positioned error (CUR2003), mirroring the
    /// <c>prefix|Type</c> selector form and never silently stripping to the default namespace; an unresolvable
    /// prefixed type is CUR2002. An UNprefixed name resolves in the document's default xmlns (honoring a root
    /// re-declaration), falling back to a simple-name parse (the default selector resolver — registry-known /
    /// exported element types) for a name the metadata cannot resolve.
    /// </summary>
    private Selector BuildTargetTypeSelector(string targetTypeName, int line, int column)
    {
        int colon = targetTypeName.IndexOf(':');
        if (colon > 0)
        {
            string prefix = targetTypeName.Substring(0, colon);
            string local = targetTypeName.Substring(colon + 1);
            if (!_doc.Namespaces.TryGetValue(prefix, out var ns))
                throw Fatal(XamlDiagnosticCodes.UndeclaredPrefix,
                    $"Unbound xmlns prefix '{prefix}' in Style TargetType '{targetTypeName}'.", line, column);

            var prefixed = _options.MetadataProvider.TryGetType(ns, local);
            if (prefixed.IsResolved)
                return Selectors.OfType(null, prefixed.Type!.SystemType());

            throw Fatal(XamlDiagnosticCodes.TypeNotFound,
                $"Style TargetType '{targetTypeName}' was not found in namespace '{ns}'.", line, column);
        }

        string defaultNs = _doc.Namespaces.TryGetValue(string.Empty, out var dns) ? dns : XamlSchemaContext.CursorialUiNamespace;
        var resolution = _options.MetadataProvider.TryGetType(defaultNs, targetTypeName);
        if (resolution.IsResolved)
            return Selectors.OfType(null, resolution.Type!.SystemType());

        return BuildSelector(targetTypeName, line, column); // simple-name fallback, errors wrapped as XAML diagnostics
    }

    /// <summary>Parses an explicit <c>Selector="..."</c> with the namespace-aware resolver, surfacing a parse failure as a XAML diagnostic.</summary>
    private Selector BuildSelector(string text, int line, int column)
    {
        try
        {
            return Selector.Parse(text.Trim(), SelectorResolver);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw Fatal(XamlDiagnosticCodes.ConversionFailed, $"'{text}' is not a valid selector: {ex.Message}", line, column, ex);
        }
    }

    private bool TryGetStyleStringMember(in ObjectRecord record, string memberName, out string value)
    {
        value = string.Empty;
        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            if (member.MemberId < 0 || member.Kind != XamlValueKind.Text)
                continue;
            if (_doc.ResolvedMembers[member.MemberId]?.Name == memberName)
            {
                value = _doc.Strings[member.ValueIndex];
                return true;
            }
        }
        return false;
    }

    // ── Member application ───────────────────────────────────────────────────────────────────────

    private void ApplyMembers(int objectIndex, in ObjectRecord record, object instance, XamlType type, int objLine, int objColumn)
    {
        // Pass 1: fill any Resources member first and push it onto the lexical scope stack so a
        // descendant's {StaticResource} resolves against it (matrix XD9 — Resources is processed before
        // content regardless of declaration order, WPF-faithful).
        ApplyResourcesFirst(objectIndex, in record, instance, type);

        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            int line = LineInfo.Line(member.PackedLineInfo);
            int column = LineInfo.Column(member.PackedLineInfo);

            if (IsResourcesMember(in member, instance))
                continue; // already filled in pass 1

            switch (member.Kind)
            {
                case XamlValueKind.Directive:
                    ApplyDirective(in member, instance, objectIndex, line, column);
                    break;

                case XamlValueKind.Event:
                    // Events bind to the root instance (X2/recorded-deferred at X1); no-op here.
                    break;

                case XamlValueKind.Folded:
                    ApplyValue(in member, instance, type, _doc.Constants[member.ValueIndex], line, column);
                    break;

                case XamlValueKind.Text:
                    // Style.TargetType and Style.Selector were consumed at activation (the type-selector
                    // mapping + the namespace-aware selector parse, X139/#23).
                    if (instance is Style && (IsMemberNamed(in member, "TargetType") || IsMemberNamed(in member, "Selector")))
                        break;
                    ApplyTextMember(in member, instance, type, _doc.Strings[member.ValueIndex], line, column);
                    break;

                case XamlValueKind.Object:
                {
                    // A markup extension in element form as a scalar member value (<Setter.Value><DynamicResource
                    // …/></Setter.Value>): attach it to THIS member exactly as the curly form would, rather than
                    // instantiating an object.
                    ref readonly var childRecord = ref _doc.Objects[member.ValueIndex];
                    if (childRecord.HasFlag(ObjectFlags.IsMarkupExtension))
                    {
                        ApplyExtensionMemberObject(in member, in childRecord, instance, type, line, column);
                        break;
                    }

                    var child = InstantiateObject(member.ValueIndex);
                    ApplyValue(in member, instance, type, child, line, column);
                    break;
                }

                case XamlValueKind.Items:
                    ApplyItems(in member, instance, type, line, column);
                    break;

                case XamlValueKind.Extension:
                    ApplyExtension(in member, instance, type, line, column);
                    break;

                case XamlValueKind.Deferred:
                    ApplyDeferred(in member, instance, type, line, column);
                    break;
            }
        }
    }

    private bool IsMemberNamed(in MemberRecord member, string name)
        => member.MemberId >= 0 && _doc.ResolvedMembers[member.MemberId]?.Name == name;

    private void ApplyDirective(in MemberRecord member, object instance, int objectIndex, int line, int column)
    {
        var kind = (XamlDirectiveKind)member.DirectiveKind;
        if (kind == XamlDirectiveKind.Name)
        {
            string name = _doc.Strings[member.ValueIndex];
            RegisterName(name, instance, objectIndex, line, column);
        }
        // x:Key is consumed by the dictionary-add path; x:Class / x:DataType are recorded only.
    }

    private void RegisterName(string name, object instance, int objectIndex, int line, int column)
    {
        ref readonly var record = ref _doc.Objects[objectIndex];

        // x:Name inside a resource dictionary has no namescope (matrix X104) — the frontend already
        // reports CUR2304 at parse; defensively skip here.
        if (record.HasFlag(ObjectFlags.InResourceDictionary))
            return;

        // In template-build mode, parts register ONLY in the per-build template name scope (matrix X155),
        // never the document scope.
        if (TemplateContext is { } context)
        {
            if (instance is UIElement part)
                context.RegisterName(name, part);
            return;
        }

        if (_registeredNames.TryGetValue(name, out int firstLine))
        {
            throw Fatal(XamlDiagnosticCodes.DuplicateName,
                $"The name '{name}' is already registered in this namescope (first defined at line {firstLine}).", line, column);
        }

        _registeredNames[name] = line;
        if (instance is UIElement element)
            element.Name = name;
        _documentScope.Register(name, instance);
    }

    // ── Value assignment (XD4) ─────────────────────────────────────────────────────────────────────

    private void ApplyTextMember(in MemberRecord member, object instance, XamlType type, string text, int line, int column)
    {
        var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
        if (resolved is null)
        {
            // A content-text member on a type with no content property → CUR2104.
            if (type.ContentProperty is null)
                throw Fatal(XamlDiagnosticCodes.NoContentProperty,
                    $"Type '{type.ClrType.Name}' has no content property and cannot accept implicit text content.", line, column);
            return;
        }

        // Classes="accent primary" — the style-class set is a read-only ClassSet (no setter); split the
        // space-separated names and Add each (static class assignment, Avalonia parity). A repeated name is a
        // harmless no-op (ClassSet.Add dedups). Works the same on a template instance's element.
        if (instance is UIElement classTarget && resolved.SystemType() == typeof(ClassSet))
        {
            foreach (var className in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                classTarget.Classes.Add(className);
            return;
        }

        object? value = ConvertText(resolved, text, instance, line, column);
        Assign(resolved, instance, value, line, column);
    }

    private void ApplyValue(in MemberRecord member, object instance, XamlType type, object? value, int line, int column)
    {
        var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
        if (resolved is null)
        {
            if (type.ContentProperty is null)
                throw Fatal(XamlDiagnosticCodes.NoContentProperty,
                    $"Type '{type.ClrType.Name}' has no content property and cannot accept implicit content.", line, column);
            return;
        }

        // A single child of a read-only collection member (e.g. one <Style> under <Panel.Styles>):
        // the frontend can't classify it as Items (it has no runtime collection knowledge), so the
        // loader fills the existing collection rather than setting it.
        if (value is not null && IsReadOnlyCollectionMember(resolved, instance, value, out var collection))
        {
            BindCollectionAdders(collection!, out var addItem, out _);
            addItem?.Invoke(collection!, value);
            return;
        }

        Assign(resolved, instance, value, line, column);
    }

    private static bool IsReadOnlyCollectionMember(XamlMember member, object instance, object value, out object? collection)
    {
        collection = null;

        // The value IS assignable to the member type (incl. a UIProperty-backed member): assign it.
        if (member.Property is null && member.SystemType().IsInstanceOfType(value))
            return false;
        if (member.Property is UIProperty p && p.PropertyType.IsInstanceOfType(value))
            return false;

        // The value is NOT the collection itself — fill the existing collection the getter returns
        // (a single <Style> under <Panel.Styles>, etc.). Requires a getter.
        if (member.Get is null)
            return false;

        var existing = member.Get(instance);
        if (existing is ResourceDictionary or IList || (existing is not null && IsGenericList(existing)))
        {
            collection = existing;
            return true;
        }
        return false;
    }

    private void ApplyItems(in MemberRecord member, object instance, XamlType type, int line, int column)
    {
        var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
        int first = member.ValueIndex;
        int count = member.ItemCount;

        // Resolve the collection to fill: a read-only collection member's existing value (the getter
        // path) or the implicit content collection (Panel.Children / a dictionary).
        object collection = ResolveCollection(resolved, instance, type, line, column, out var addItem, out var addDictionaryItem);

        int childIndex = first;
        for (int k = 0; k < count; k++, childIndex += _doc.Objects[childIndex].SubtreeLength)
        {
            ref readonly var childRecord = ref _doc.Objects[childIndex];

            // A keyed dictionary item.
            if (addDictionaryItem is not null && TryGetKey(in childRecord, out string key))
            {
                var keyValue = ConvertDictionaryKey(collection, type, key, line, column);
                var item = InstantiateObject(childIndex);
                addDictionaryItem(collection, keyValue, item);
            }
            else
            {
                var item = InstantiateObject(childIndex);
                if (addItem is null)
                    throw Fatal(XamlDiagnosticCodes.NoCollectionAccess,
                        $"Member has no fillable collection on '{type.ClrType.Name}'.", line, column);
                addItem(collection, item);
            }
        }
    }

    /// <summary>
    /// The object indices of an <c>Items</c> run, walking by <see cref="ObjectRecord.SubtreeLength"/> (the
    /// depth-first SoA layout — item <c>k</c> is NOT at <c>first + k</c> when items carry subtrees).
    /// </summary>
    private IEnumerable<int> EnumerateItems(int first, int count)
    {
        int index = first;
        for (int k = 0; k < count; k++)
        {
            yield return index;
            index += _doc.Objects[index].SubtreeLength;
        }
    }

    private object ResolveCollection(
        XamlMember? member, object instance, XamlType type, int line, int column,
        out Action<object, object?>? addItem, out Action<object, object, object?>? addDictionaryItem)
    {
        addItem = null;
        addDictionaryItem = null;

        // A self-collection instance (a ResourceDictionary / IList element whose own children fill it,
        // with no content-property member): fill the instance directly (matrix X73).
        if (member is null && (instance is ResourceDictionary || instance is IList || IsGenericList(instance)))
        {
            BindCollectionAdders(instance, out addItem, out addDictionaryItem);
            return instance;
        }

        // Implicit content collection (Panel.Children etc.): the member is the content property.
        if (member is null || member.Name == type.ContentProperty)
        {
            if (member is not null && member.Get is { } getOwn && getOwn(instance) is { } ownedCollection)
            {
                BindCollectionAdders(ownedCollection, out addItem, out addDictionaryItem);
                return ownedCollection;
            }
        }

        if (member is not null)
        {
            // A read-only collection property (get-object): fill the existing collection.
            if (member.Get is { } get && get(instance) is { } existing)
            {
                BindCollectionAdders(existing, out addItem, out addDictionaryItem);
                return existing;
            }

            // A settable collection member typed as a collection: read it back if a getter exists, else
            // it must already be a collection instance — but with neither getter nor setter it's CUR2105.
            throw Fatal(XamlDiagnosticCodes.NoCollectionAccess,
                $"Collection member '{member.Name}' on '{type.ClrType.Name}' has no getter to fill.", line, column);
        }

        throw Fatal(XamlDiagnosticCodes.NoContentProperty,
            $"Type '{type.ClrType.Name}' has no content collection for implicit items.", line, column);
    }

    private static void BindCollectionAdders(object collection, out Action<object, object?>? addItem, out Action<object, object, object?>? addDictionaryItem)
    {
        addItem = null;
        addDictionaryItem = null;

        switch (collection)
        {
            case ResourceDictionary:
                addDictionaryItem = static (dict, key, value) => ((ResourceDictionary)dict).Add(key, value);
                break;
            case UIElementCollection uiCollection:
                addItem = (_, item) => uiCollection.Add((UIElement)item!);
                break;
            case IList list:
                addItem = (_, item) => list.Add(item);
                break;
            default:
                // A generic IList<T> without IList — fall back to a reflective Add at fill time.
                addItem = InvokeGenericAdd;
                break;
        }
    }

    private static bool IsGenericList(object instance)
    {
        foreach (var iface in instance.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                return true;
        }
        return false;
    }

    private static void InvokeGenericAdd(object collection, object? item)
    {
        foreach (var iface in collection.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(ICollection<>))
            {
                iface.GetMethod("Add")!.Invoke(collection, [item]);
                return;
            }
        }
        throw new InvalidOperationException($"'{collection.GetType().Name}' is not a fillable collection.");
    }

    private void ApplyExtension(in MemberRecord member, object instance, XamlType type, int line, int column)
    {
        // Live markup extensions (Binding/StaticResource/DynamicResource/TemplateBinding/custom) attach
        // through the handler seam (matrix §10 / XD7).
        var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
        ref readonly var ext = ref _doc.Extensions[member.ValueIndex];
        _extensionHandler.Attach(this, instance, type, resolved, in ext, _doc, line, column);
    }

    /// <summary>The value slot of a markup-extension element object (element-form extension): the single
    /// <see cref="XamlValueKind.Extension"/>/<see cref="XamlValueKind.Folded"/> member (an x:Key directive,
    /// when present, is a separate member consumed by the dictionary path).</summary>
    private ref readonly MemberRecord ExtensionValueMember(in ObjectRecord record)
    {
        for (int i = 0; i < record.MemberCount; i++)
        {
            ref readonly var m = ref _doc.Members[record.MemberStart + i];
            if (m.Kind is XamlValueKind.Extension or XamlValueKind.Folded)
                return ref m;
        }

        return ref _doc.Members[record.MemberStart + record.MemberCount - 1]; // always present by construction
    }

    /// <summary>
    /// Applies an element-form markup extension (<paramref name="extObject"/>) as the value of
    /// <paramref name="member"/>: a folded intrinsic assigns its constant; a live extension attaches to the
    /// target member exactly as the curly attribute form does — the target is <paramref name="member"/>'s
    /// resolved member, the extension record is the synthetic object's value slot.
    /// </summary>
    private void ApplyExtensionMemberObject(in MemberRecord member, in ObjectRecord extObject, object instance, XamlType type, int line, int column)
    {
        ref readonly var value = ref ExtensionValueMember(in extObject);
        if (value.Kind == XamlValueKind.Folded)
        {
            ApplyValue(in member, instance, type, _doc.Constants[value.ValueIndex], line, column);
            return;
        }

        var attach = new MemberRecord(member.MemberId, XamlValueKind.Extension, value.ValueIndex, 0, member.PackedLineInfo);
        ApplyExtension(in attach, instance, type, line, column);
    }

    /// <summary>
    /// Produces the standalone value of an element-form markup extension used as a dictionary/collection ENTRY
    /// (no target member): a folded constant, a resolved <c>StaticResource</c>, or a <c>DynamicResource</c>
    /// carrier (<see cref="ResourceReference"/> — resource aliasing). Binding/TemplateBinding/custom have no
    /// meaning without a target and are rejected with a positioned diagnostic.
    /// </summary>
    private object? ProvideExtensionEntryValue(in ObjectRecord record, int line, int column)
    {
        ref readonly var value = ref ExtensionValueMember(in record);
        if (value.Kind == XamlValueKind.Folded)
            return _doc.Constants[value.ValueIndex];

        ref readonly var ext = ref _doc.Extensions[value.ValueIndex];
        switch (ext.Kind)
        {
            case ExtensionKind.StaticResource:
                return _extensionHandler.ResolveStaticResource(this,
                    _extensionHandler.ResolveResourceKey(this, in ext, _doc, line, column), line, column);

            case ExtensionKind.DynamicResource:
                return new ResourceReference(_extensionHandler.ResolveResourceKey(this, in ext, _doc, line, column));

            case ExtensionKind.Custom:
                // A custom extension standing alone (a dictionary/collection entry): ProvideValue with no
                // target member — the produced value is stored (and receives the entry's x:Key).
                return _extensionHandler.ProvideStandaloneCustomValue(this, _doc.ParsedExtensions[ext.Payload]!, line, column);

            default:
                throw Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                    $"A {ext.Kind} markup extension has no target as a standalone dictionary or collection entry " +
                    "(only x:Null/x:Type/x:Static, StaticResource, DynamicResource, and custom extensions may stand alone).", line, column);
        }
    }

    private void ApplyDeferred(in MemberRecord member, object instance, XamlType type, int line, int column)
    {
        var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
        if (resolved is null)
            return;

        // Capture the lexical resource chain at the template's DEFINITION site (matrix X162/X163 — the
        // template's StaticResource resolution is WPF-faithfully lexical, not instantiation-site).
        var content = _deferredContentFactory.Create(_doc, member.ValueIndex, _options, _source, _scopes.CaptureChain());
        Assign(resolved, instance, content, line, column);
    }

    // ── The XD4 assignment + the converter ladder ─────────────────────────────────────────────────

    private void Assign(XamlMember member, object instance, object? value, int line, int column)
    {
        // Resolve a folded {x:Static} placeholder to its value (the loader's fold-finalize, X122).
        value = ResolveStaticReference(value, line, column);

        // Access-key folding (XD11): an OBJECT-typed content slot keeps the raw string — the runtime
        // ContentControl.GetAccessText() folds it on demand (the three-identical-producers rule), so the
        // loader must NOT replace the value (matrix X68 stores "Hi", X165 derives AccessText from "_Run").
        // A genuinely AccessText-typed member folds by type here.
        if (value is string accessLiteral && member.SystemType() == typeof(AccessText))
            value = AccessText.Parse(accessLiteral);

        if (member.Property is UIProperty uiProperty)
        {
            // The registered UIProperty path: SetValue at LocalValue (XD8).
            if (instance is UIObject uiObject)
            {
                uiObject.SetValue(uiProperty, value, BindingPriority.LocalValue);
                return;
            }
            throw Fatal(XamlDiagnosticCodes.MemberNotFound,
                $"Member '{member.Name}' is a UIProperty but the target is not a UIObject.", line, column);
        }

        if (member.SetClr is { } setClr)
        {
            setClr(instance, value);
            return;
        }

        throw Fatal(XamlDiagnosticCodes.NoCollectionAccess,
            $"Member '{member.Name}' has no setter.", line, column);
    }

    private object? ConvertText(XamlMember member, string text, object instance, int line, int column)
    {
        // A string-typed slot keeps the raw text (the access-key fold runs in Assign).
        var memberType = member.SystemType();
        if (memberType == typeof(string) || memberType == typeof(object))
            return text;

        // A System.Type-valued member (e.g. ControlTemplate.TargetType) takes a type TOKEN resolved via the
        // xmlns — there is no string→Type value converter. The token honors its xmlns prefix / the document's
        // default xmlns (the same resolution as the implicit DataTemplate key). An unresolved token falls through
        // to the converter ladder (→ raw string → the CLR setter rejects it, as before).
        if (memberType == typeof(Type))
        {
            if (TryResolveTypeToken(text, out var resolved, out var unboundPrefix))
                return resolved;
            // An unbound prefix is unambiguously an author error — report it precisely (parity with the
            // DataTemplate DataType path) rather than falling through to a raw CLR-setter type mismatch. A
            // bound-but-unresolved token still falls through to the converter ladder (the documented behavior).
            if (unboundPrefix is not null)
                throw Fatal(XamlDiagnosticCodes.UndeclaredPrefix, $"Unbound xmlns prefix '{unboundPrefix}' in type '{text}'.", line, column);
        }

        // member.Converter is the provider's resolved converter (member attributes + the ladder, via ForMember);
        // its last fallback is a BCL System.ComponentModel.TypeConverter for the member's type (after the ladder,
        // so our curated converters keep precedence) — gives XAML compatibility with any [TypeConverter]-bearing
        // type (and BCL defaults: enums, Guid, …). Resolved here at load (not on a baked path), uniform for both
        // providers (a non-ladder type's baked converter is null → this fallback runs identically).
        var converter = member.Converter ?? XamlConverters.For(memberType) ?? XamlConverters.BclConverterForType(memberType);
        if (converter is null)
            return text; // no converter — pass the raw string (CLR setter may accept it)

        var ctx = new XamlValueContext(_options.ConverterCulture, member, memberType, _source, line, column, ConversionServices);
        return converter.ConvertFromString(text, in ctx);
    }

    private object? ResolveStaticReference(object? value, int line, int column)
    {
        if (value is not XamlStaticReference staticRef)
            return value;

        return ResolveStaticMember(staticRef.MemberPath, line, column);
    }

    /// <summary>
    /// Resolves an <c>{x:Static Type.Member}</c> member path to its value (the loader's fold-finalize).
    /// Used both for a parse-folded <see cref="XamlStaticReference"/> placeholder and for a nested
    /// <c>{x:Static …}</c> reached as a markup-extension argument value (matrix X122 / P6 review P2-13).
    /// </summary>
    internal object? ResolveStaticMember(string memberPath, int line, int column)
    {
        // The optional x:Static seam (IXamlStaticResolver) — ReflectionXamlMetadata resolves it reflectively,
        // the X5 generated provider via a baked switch. No hard-cast to a concrete provider type, so x:Static
        // works under the generated/AOT provider too (and the loader no longer statically references the
        // reflection provider here — one of the two AOT blocker sites closed).
        var provider = _options.MetadataProvider;

        if (provider is IXamlQualifiedStaticResolver qualified)
        {
            // The xmlns-aware seam (P1C): bind the document prefix here — {x:Static co:Colors.Red}
            // resolves Colors.Red under the co: declaration's namespace, an unprefixed path under the
            // document default xmlns — and hand the provider a prefix-free path.
            string path;
            string ns;
            int colon = memberPath.IndexOf(':');
            if (colon > 0)
            {
                var prefix = memberPath.Substring(0, colon);
                path = memberPath.Substring(colon + 1);
                if (!_doc.Namespaces.TryGetValue(prefix, out ns!))
                {
                    throw Fatal(XamlDiagnosticCodes.MemberNotFound,
                        $"Could not resolve {{x:Static {memberPath}}}: xmlns prefix '{prefix}' is not declared.", line, column);
                }
            }
            else
            {
                path = memberPath;
                ns = _doc.Namespaces.TryGetValue(string.Empty, out var dns) ? dns : XamlSchemaContext.CursorialUiNamespace;
            }

            if (qualified.TryResolveStatic(ns, path, out var qualifiedResolved))
                return qualifiedResolved;
        }
        else if (provider is IXamlStaticResolver resolver && resolver.TryResolveStatic(memberPath, out var resolved))
        {
            return resolved;
        }

        throw Fatal(XamlDiagnosticCodes.MemberNotFound,
            $"Could not resolve {{x:Static {memberPath}}}.", line, column);
    }

    // ── x:Key conversion (XD10) ──────────────────────────────────────────────────────────────────

    private bool TryGetKey(in ObjectRecord record, out string key)
    {
        key = string.Empty;
        if (!record.HasFlag(ObjectFlags.HasKey))
            return false;

        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            if (member is { Kind: XamlValueKind.Directive, DirectiveKind: (int)XamlDirectiveKind.Key })
            {
                key = _doc.Strings[member.ValueIndex];
                return true;
            }
        }
        return false;
    }

    // A plain ResourceDictionary key is the literal string — UNLESS it is an {x:Type T} / {x:Static M}
    // markup extension, which resolves to its VALUE at load (a Type / member value). The frontend stores
    // x:Key verbatim (directives are not ME-folded at parse), so the resolution happens here — what lets a
    // Type-keyed ControlTheme dictionary author its entries as `<Style x:Key="{x:Type Button}">` (mirrors
    // the XD7a resource-KEY resolution). A themed dictionary's variant key goes through ThemeVariantKey
    // (FillThemeDictionaries, C-8), not this path.
    private object ConvertDictionaryKey(object collection, XamlType type, string key, int line, int column)
        => ResolveDictionaryKey(key, line, column);

    private object ResolveDictionaryKey(string key, int line, int column)
    {
        if (!MarkupExtensionParser.LooksLikeExtension(key))
            return key;

        MarkupExtensionNode node;
        try
        {
            node = MarkupExtensionParser.Parse(key, _source, line, column);
        }
        catch (XamlParseException ex)
        {
            Exception? inner = ex.InnerException;

            while (inner is XamlParseException)
                inner = inner.InnerException;

            throw Fatal(ex.Code, ex.Diagnostics[0].Message, line, column, inner);
        }

        var name = node.Name;
        var colon = name.IndexOf(':');
        if (colon >= 0)
            name = name[(colon + 1)..];
        var arg = node.PositionalArguments.Count > 0 ? node.PositionalArguments[0].Text : null;

        if (name == "Type" && arg is { } typeName)
            return ResolveTypeToken(typeName, line, column);

        if (name == "Static" && arg is { } memberPath)
            return ResolveStaticMember(memberPath, line, column)
                   ?? throw Fatal(XamlDiagnosticCodes.MemberNotFound, $"x:Key {{x:Static {memberPath}}} resolved to null.", line, column);

        throw Fatal(XamlDiagnosticCodes.UnsupportedIntrinsic,
            $"An x:Key markup extension must be {{x:Type T}} or {{x:Static M}}; '{node.Name}' is not supported as a key.", line, column);
    }

    // Resolves an {x:Type T} token to its <see cref="System.Type"/> — shared by the x:Key path, a nested
    // {x:Type} key inside a {StaticResource}/{DynamicResource} (control themes key by {x:Type}, and a
    // Style.BasedOn references one), and RelativeSource AncestorType. Delegates to the prefix-aware
    // TryResolveTypeToken (an xmlns-prefixed token — vm:LayerModel — resolves against its bound namespace,
    // not just the default UI xmlns), reporting an unbound prefix distinctly. Mirrors the generator's
    // XamlDataTypeScope.ResolveToken.
    internal object ResolveTypeToken(string typeName, int line, int column)
        => ResolveTypeToken(typeName, "x:Type", line, column);

    // ── Resource dictionaries (matrix §11/§12) ─────────────────────────────────────────────────────

    /// <summary>True when a member fills a <c>Resources</c> dictionary (a <see cref="IResourceHost"/> or a type with a <c>ResourceDictionary Resources</c> getter, e.g. <c>ControlTemplate</c>).</summary>
    private bool IsResourcesMember(in MemberRecord member, object instance)
    {
        if (member.MemberId < 0)
            return false;
        var resolved = _doc.ResolvedMembers[member.MemberId];
        return resolved is { Name: "Resources" } && TryGetResourcesDictionary(instance, resolved) is not null;
    }

    /// <summary>Reads the <c>Resources</c> dictionary off an instance (the read-only getter), or null.</summary>
    private static ResourceDictionary? TryGetResourcesDictionary(object instance, XamlMember member)
    {
        if (instance is IResourceHost host)
            return host.Resources;
        if (member.Get?.Invoke(instance) is ResourceDictionary dict)
            return dict;
        return null;
    }

    /// <summary>
    /// Fills any <c>Resources</c> member first and pushes it onto the lexical scope stack so a
    /// descendant's <c>{StaticResource}</c> resolves against it (matrix XD9). A no-op when the element
    /// has no Resources member.
    /// </summary>
    private void ApplyResourcesFirst(int objectIndex, in ObjectRecord record, object instance, XamlType type)
    {
        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            if (!IsResourcesMember(in member, instance))
                continue;

            int line = LineInfo.Line(member.PackedLineInfo);
            int column = LineInfo.Column(member.PackedLineInfo);
            var resolved = _doc.ResolvedMembers[member.MemberId]!;
            var dict = TryGetResourcesDictionary(instance, resolved);
            if (dict is null)
                return;

            // Push the scope FIRST so a resource entry's own StaticResource (referencing an earlier
            // sibling) resolves against this dictionary (matrix XD9 — same-dictionary back-reference).
            _scopes.Push(dict);

            // A <Foo.Resources> body is either a single nested <ResourceDictionary> or a run of keyed
            // resource entries (the implicit content). A single keyed child is an entry, not a nested dict.
            switch (member.Kind)
            {
                case XamlValueKind.Object:
                    AddOrMergeResourceChild(member.ValueIndex, dict, line, column);
                    break;
                case XamlValueKind.Items:
                    foreach (int idx in EnumerateItems(member.ValueIndex, member.ItemCount))
                        AddOrMergeResourceChild(idx, dict, line, column);
                    break;
            }
            return;
        }
    }

    /// <summary>Loads a self-<see cref="ResourceDictionary"/> object (the root or a <c>.Resources</c> child) from its node-graph children.</summary>
    private void LoadResourceDictionary(int objectIndex, in ObjectRecord record, ResourceDictionary dict, int line, int column)
    {
        var previousObject = _currentObject;
        _currentObject = dict;
        int scopeDepth = _scopes.Depth;
        _scopes.Push(dict);
        try
        {
            FillResourceDictionaryMembers(objectIndex, in record, dict, line, column);
        }
        finally
        {
            _scopes.PopDownTo(scopeDepth);
            _currentObject = previousObject;
        }
    }

    /// <summary>
    /// The body of a <c>&lt;ResourceDictionary&gt;</c>: <c>Source=</c>, <c>MergedDictionaries</c>,
    /// <c>ThemeDictionaries</c>, and keyed entries (matrix X129–X135).
    /// </summary>
    private void FillResourceDictionaryMembers(int objectIndex, in ObjectRecord record, ResourceDictionary dict, int line, int column)
    {
        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            int mLine = LineInfo.Line(member.PackedLineInfo);
            int mColumn = LineInfo.Column(member.PackedLineInfo);
            var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
            string? memberName = resolved?.Name;

            switch (memberName)
            {
                case "Source":
                    // Setting Source routes through ResourceDictionary.LoadCallback (matrix X131/C-9).
                    SetResourceSource(dict, in member, mLine, mColumn);
                    break;

                case "MergedDictionaries":
                    FillMergedDictionaries(dict, in member, mLine, mColumn);
                    break;

                case "ThemeDictionaries":
                    FillThemeDictionaries(dict, in member, mLine, mColumn);
                    break;

                case "Styles":
                    FillStyles(dict, in member, mLine, mColumn);
                    break;

                default:
                    // A directive (x:Key on the dictionary element itself when nested) or a keyed entry
                    // run is the implicit content; otherwise ignore.
                    if (member.Kind == XamlValueKind.Items)
                    {
                        foreach (int idx in EnumerateItems(member.ValueIndex, member.ItemCount))
                            AddOrMergeResourceChild(idx, dict, mLine, mColumn);
                    }
                    else if (member.Kind == XamlValueKind.Object)
                    {
                        AddOrMergeResourceChild(member.ValueIndex, dict, mLine, mColumn);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// A child of a <c>&lt;Foo.Resources&gt;</c> body: a nested <c>&lt;ResourceDictionary&gt;</c> whose
    /// entries fold into <paramref name="target"/> (the <c>.Resources</c> getter owns one dictionary), or
    /// a single keyed resource entry.
    /// </summary>
    private void AddOrMergeResourceChild(int childIndex, ResourceDictionary target, int line, int column)
    {
        ref readonly var child = ref _doc.Objects[childIndex];
        var childType = child.TypeId >= 0 ? _doc.ResolvedTypes[child.TypeId] : null;

        if (childType is not null && typeof(ResourceDictionary).IsAssignableFrom(childType.SystemType()))
        {
            // <Foo.Resources><ResourceDictionary>…</ResourceDictionary></Foo.Resources>: fold the inner
            // dictionary's members into the host's own Resources dictionary.
            FillResourceDictionaryMembers(childIndex, in child, target, line, column);
            return;
        }

        AddDictionaryEntry(target, childIndex, line, column);
    }

    private void SetResourceSource(ResourceDictionary dict, in MemberRecord member, int line, int column)
    {
        // A folded constant that is ALREADY a Uri is used as-is: its OriginalString still carries
        // the author's casing, which round-tripping through ToString() (canonical, lowercased
        // authority) would destroy — and the authority names an assembly, not a DNS host.
        var uri = member.Kind switch
        {
            XamlValueKind.Text when _doc.Strings[member.ValueIndex] is { Length: > 0 } text
                => new Uri(text, UriKind.RelativeOrAbsolute),
            XamlValueKind.Folded when _doc.Constants[member.ValueIndex] is Uri folded
                => folded,
            XamlValueKind.Folded when _doc.Constants[member.ValueIndex]?.ToString() is { Length: > 0 } folded
                => new Uri(folded, UriKind.RelativeOrAbsolute),
            _ => null,
        };
        if (uri is null)
            return;
        try
        {
            // A relative reference ("Resources.xaml", "../Theme/Base.xaml") resolves against the
            // CONTAINING document's URI — cursorial://, embedded://, and file:// alike — so linked
            // dictionaries move with their documents instead of baking machine paths. Composition
            // goes through XamlUriUtil so the authority keeps its ORIGINAL casing (System.Uri
            // lowercases hosts; ours name assemblies).
            if (!uri.IsAbsoluteUri && (_source ?? _doc.SourceUri) is { IsAbsoluteUri: true } baseUri)
                uri = CursorialUri.ResolveRelative(baseUri, uri.OriginalString);

            dict.Source = uri;
        }
        catch (InvalidOperationException ex)
        {
            // No loader installed (the P5 state, matrix X133) — surface as a conversion failure with position.
            throw Fatal(XamlDiagnosticCodes.ConversionFailed, ex.Message, line, column, ex);
        }
    }

    private void FillMergedDictionaries(ResourceDictionary dict, in MemberRecord member, int line, int column)
    {
        if (member.Kind is XamlValueKind.Items)
        {
            foreach (int idx in EnumerateItems(member.ValueIndex, member.ItemCount))
                dict.MergedDictionaries.Add((ResourceDictionary)InstantiateObject(idx));
        }
        else if (member.Kind == XamlValueKind.Object)
        {
            dict.MergedDictionaries.Add((ResourceDictionary)InstantiateObject(member.ValueIndex));
        }
    }

    // The theme-styles channel (design doc §11.8 #3 / C100): <ResourceDictionary.Styles> populates the
    // dictionary's Styles collection — selector styles, NOT keyed entries (no x:Key). Each <Style> attaches
    // (seal-on-attach). Consumed only from UIApplication.Theme by the StyleEngine at Theme(2).
    private void FillStyles(ResourceDictionary dict, in MemberRecord member, int line, int column)
    {
        var styles = dict.Styles ??= [];
        if (member.Kind is XamlValueKind.Items)
        {
            foreach (int idx in EnumerateItems(member.ValueIndex, member.ItemCount))
                styles.Add((Style)InstantiateObject(idx));
        }
        else if (member.Kind == XamlValueKind.Object)
        {
            styles.Add((Style)InstantiateObject(member.ValueIndex));
        }
    }

    private void FillThemeDictionaries(ResourceDictionary dict, in MemberRecord member, int line, int column)
    {
        void AddTheme(int childIndex)
        {
            ref readonly var child = ref _doc.Objects[childIndex];
            if (!TryGetKey(in child, out string keyText))
                throw Fatal(XamlDiagnosticCodes.NameInResourceDictionary,
                    "A ThemeDictionaries entry requires an x:Key naming the variant.", line, column);

            ThemeVariantKey variantKey;
            try
            {
                variantKey = ThemeVariantKey.Parse(keyText);
            }
            catch (FormatException ex)
            {
                throw Fatal(XamlDiagnosticCodes.ConversionFailed,
                    $"'{keyText}' is not a valid theme-variant key: {ex.Message}", line, column, ex);
            }

            var sub = (ResourceDictionary)InstantiateObject(childIndex);
            dict.ThemeDictionaries[variantKey] = sub;
        }

        if (member.Kind is XamlValueKind.Items)
        {
            foreach (int idx in EnumerateItems(member.ValueIndex, member.ItemCount))
                AddTheme(idx);
        }
        else if (member.Kind == XamlValueKind.Object)
        {
            AddTheme(member.ValueIndex);
        }
    }

    /// <summary>
    /// Adds one keyed entry to a dictionary as a DEFERRED node-graph slice (matrix X141/XD18) — no
    /// instantiation at load. The entry realizes on first lookup, capturing the dictionary's lexical
    /// scope (the definition-site chain, matrix X143).
    /// </summary>
    private void AddDictionaryEntry(ResourceDictionary dict, int childIndex, int line, int column)
    {
        ref readonly var child = ref _doc.Objects[childIndex];

        object key;
        if (TryGetKey(in child, out string explicitKey))
        {
            // Resolve an {x:Type T} / {x:Static M} key to its value (the deferred-entry path; mirrors the
            // immediate-add ConvertDictionaryKey) — what makes `<Style x:Key="{x:Type Button}">` a Type key.
            key = ResolveDictionaryKey(explicitKey, line, column);
        }
        else if (TryGetImplicitKey(childIndex, in child, out var implicitKey))
        {
            key = implicitKey;
        }
        else
        {
            throw Fatal(XamlDiagnosticCodes.NameInResourceDictionary,
                "A resource dictionary entry requires an x:Key (or an implicit Style.TargetType / DataTemplate.DataType key).", line, column);
        }

        dict.SetDeferred(key, new XamlDeferredResourceEntry(this, childIndex, _scopes.CaptureChain()));
    }

    /// <summary>
    /// The implicit key for an unkeyed dictionary entry (matrix X137/X138): a <c>Style</c> with a
    /// <c>TargetType</c> keys by the type-selector form; a <c>DataTemplate</c> with a <c>DataType</c>
    /// keys by <see cref="DataTemplateKey"/>.
    /// </summary>
    private bool TryGetImplicitKey(int childIndex, in ObjectRecord child, out object key)
    {
        key = string.Empty;
        var type = child.TypeId >= 0 ? _doc.ResolvedTypes[child.TypeId] : null;
        if (type is null)
            return false;

        if (type.SystemType() == typeof(Style) && TryGetStyleStringMember(in child, "TargetType", out string targetTypeName))
        {
            // The implicit Style key is the target-type selector (the styling engine matches on Selector;
            // the dictionary key is the type-selector form, matrix X137).
            key = $"Style:{targetTypeName}";
            return true;
        }

        if (typeof(DataTemplate).IsAssignableFrom(type.SystemType()) && TryGetDataType(in child, out var dataType))
        {
            key = new DataTemplateKey(dataType);
            return true;
        }

        return false;
    }

    private bool TryGetDataType(in ObjectRecord record, out Type dataType)
    {
        dataType = typeof(object);
        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            // A DataType resolution error reports the DataType member's OWN position (not the enclosing
            // dictionary/.Resources member), so the diagnostic points at the offending attribute (audit finding).
            int line = LineInfo.Line(member.PackedLineInfo);
            int column = LineInfo.Column(member.PackedLineInfo);

            // DataType may be the x:DataType directive or a CLR DataType member with a folded Type value.
            if (member is { Kind: XamlValueKind.Directive, DirectiveKind: (int)XamlDirectiveKind.DataType })
            {
                dataType = ResolveTypeToken(_doc.Strings[member.ValueIndex], "DataTemplate DataType", line, column);
                return true;
            }
            if (member.MemberId >= 0 && _doc.ResolvedMembers[member.MemberId]?.Name == "DataType")
            {
                if (member.Kind == XamlValueKind.Folded && _doc.Constants[member.ValueIndex] is Type t)
                {
                    dataType = t;
                    return true;
                }
                if (member.Kind == XamlValueKind.Text)
                {
                    dataType = ResolveTypeToken(_doc.Strings[member.ValueIndex], "DataTemplate DataType", line, column);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves a (possibly xmlns-prefixed) type token against the document's namespace map — the same
    /// resolution <see cref="BuildTargetTypeSelector"/> uses, so a type-valued member honors
    /// <c>using:</c>/<c>clr-namespace:</c> prefixes (and a root-redeclared default xmlns) rather than assuming
    /// the Cursorial UI namespace. Returns <see langword="false"/> on an unbound prefix or unresolved type
    /// (the caller decides whether that is fatal or a fall-through), reporting <paramref name="unboundPrefix"/>
    /// so a precise diagnostic can distinguish the two.
    /// </summary>
    private bool TryResolveTypeToken(string token, out Type type, out string? unboundPrefix)
    {
        type = typeof(object);
        unboundPrefix = null;

        int colon = token.IndexOf(':');
        if (colon > 0)
        {
            string prefix = token.Substring(0, colon);
            string local = token.Substring(colon + 1);
            if (!_doc.Namespaces.TryGetValue(prefix, out var ns))
            {
                unboundPrefix = prefix;
                return false;
            }

            var prefixed = _options.MetadataProvider.TryGetType(ns, local);
            if (!prefixed.IsResolved)
                return false;

            type = prefixed.Type!.SystemType();
            return true;
        }

        string defaultNs = _doc.Namespaces.TryGetValue(string.Empty, out var dns) ? dns : XamlSchemaContext.CursorialUiNamespace;
        var resolution = _options.MetadataProvider.TryGetType(defaultNs, token);
        if (!resolution.IsResolved)
            return false;

        type = resolution.Type!.SystemType();
        return true;
    }

    /// <summary>The fatal-on-miss wrapper over <see cref="TryResolveTypeToken"/> (for sites where the token MUST
    /// resolve — e.g. an implicit <c>DataTemplate</c> key); <paramref name="usage"/> names the site.</summary>
    private Type ResolveTypeToken(string token, string usage, int line, int column)
    {
        if (TryResolveTypeToken(token, out var type, out var unboundPrefix))
            return type;

        throw unboundPrefix is not null
            ? Fatal(XamlDiagnosticCodes.UndeclaredPrefix, $"Unbound xmlns prefix '{unboundPrefix}' in {usage} '{token}'.", line, column)
            : Fatal(XamlDiagnosticCodes.TypeNotFound, $"{usage} '{token}' was not found.", line, column);
    }

    /// <summary>
    /// Realizes a deferred dictionary entry (matrix X142): pushes the captured definition-site lexical
    /// scope (matrix X143 — a nested StaticResource resolves against the defining dictionary then the
    /// enclosing chain), then instantiates the slice's object subtree.
    /// </summary>
    internal object RealizeDeferredEntry(int childIndex, CapturedScopeChain capturedScope)
    {
        int depth = _scopes.Depth;
        _scopes.PushChain(capturedScope);
        try
        {
            return InstantiateObject(childIndex);
        }
        finally
        {
            _scopes.PopDownTo(depth);
        }
    }

    // ── Setters (matrix X117/X129) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a construction-immutable <see cref="Setter"/> from its <c>Property</c>/<c>Value</c> members
    /// (matrix X117/X129). <c>Property</c> resolved at parse against the lexical Style <c>TargetType</c>
    /// (so its member carries the TARGET <see cref="UIProperty"/>); a folded/text <c>Value</c> is the
    /// constant; a <c>{DynamicResource}</c> <c>Value</c> stores a <see cref="ResourceReference"/> carrier.
    /// </summary>
    private object BuildSetter(int objectIndex, in ObjectRecord record, int line, int column)
    {
        UIProperty? property = null;
        object? value = UIProperty.UnsetValue;

        for (int i = 0; i < record.MemberCount; i++)
        {
            var member = _doc.Members[record.MemberStart + i];
            var resolved = member.MemberId >= 0 ? _doc.ResolvedMembers[member.MemberId] : null;
            if (resolved is null)
                continue;

            // The parse-time Setter resolution rewrote the "Property" member's MemberId to the TARGET
            // property (it carries a UIProperty); the literal property NAME is its Text value.
            if (resolved.Property is UIProperty targetProperty && member.Kind == XamlValueKind.Text)
            {
                property = targetProperty;
                continue;
            }

            if (resolved.Name == "Value")
            {
                switch (member.Kind)
                {
                    case XamlValueKind.Folded:
                        value = _doc.Constants[member.ValueIndex];
                        break;
                    case XamlValueKind.Text:
                        value = _doc.Strings[member.ValueIndex];
                        break;
                    case XamlValueKind.Object:
                        // An inline object Setter.Value — e.g. <Setter Property="ItemsPanel"><ItemsPanelTemplate>…
                        // (its deferred Content is applied by InstantiateObject's member pass). Without this the
                        // value stayed UnsetValue and the setter was silently a no-op.
                        value = InstantiateObject(member.ValueIndex);
                        break;
                    case XamlValueKind.Extension:
                    {
                        ref readonly var ext = ref _doc.Extensions[member.ValueIndex];
                        // The key is a literal string or a nested key extension ({x:Static …}) — XD7a.
                        if (ext.Kind == ExtensionKind.DynamicResource)
                            value = new ResourceReference(_extensionHandler.ResolveResourceKey(this, in ext, _doc, line, column));
                        else if (ext.Kind == ExtensionKind.StaticResource)
                            value = _extensionHandler.ResolveStaticResource(this, _extensionHandler.ResolveResourceKey(this, in ext, _doc, line, column), line, column);
                        break;
                    }
                }
            }
        }

        if (property is null)
            throw Fatal(XamlDiagnosticCodes.MemberNotFound, "A Setter requires a resolvable Property.", line, column);

        // A bare Type-typed Setter.Value token ("Button") resolves to the System.Type here: the generator bakes
        // typeof(T) (SetterValueExpr), and StyleSetterConverter has no xmlns context at Seal, so the token must be
        // resolved at load — against the document's namespaces — rather than deferred as an unconvertible string.
        if (property.PropertyType == typeof(Type) && value is string typeToken && typeToken.Length > 0)
            value = ResolveTypeToken(typeToken, line, column);

        return new Setter(property, value);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────────────────────────

    internal XamlParseException Fatal(string code, string message, int line, int column, Exception? inner = null)
        => new(XamlDiagnostic.Error(code, message, _source, Math.Max(line, 1), Math.Max(column, 1)), inner);

    // ── X2/X3 handler-facing helpers ───────────────────────────────────────────────────────────

    /// <summary>Assigns a markup-extension result (already resolved to a value) through the XD8 ladder.</summary>
    internal void AssignResolvedValue(XamlMember member, object instance, object? value, int line, int column)
    {
        // A custom-extension or StaticResource result targeting a typed slot runs the converter ladder
        // when the value is a string and the slot isn't string/object (matrix X121-style flexibility).
        if (value is string text && member.SystemType() is var mt && mt != typeof(string) && mt != typeof(object))
            value = ConvertText(member, text, instance, line, column);

        Assign(member, instance, value, line, column);
    }

    /// <summary>
    /// Resolves a <c>{TemplateBinding sourceName}</c> source property to a registered <see cref="UIProperty"/>
    /// on the target's runtime type (matrix X127/X160). The source name resolves through the metadata
    /// provider against the target's type (the templated parent shares the property identity). A
    /// TYPE-QUALIFIED source (<c>Owner.Prop</c>, <c>ns:Owner.Prop</c>, or WPF's optional-parens
    /// <c>(Owner.Prop)</c>) names its declaring type explicitly — an attached property, or one declared on a
    /// base/other type — and resolves the owner through that xmlns-aware token.
    /// </summary>
    internal UIProperty ResolveTemplateBindingSource(UIObject target, UIProperty targetProperty, string sourceName, int line, int column)
    {
        var name = sourceName.Trim();
        if (name.Length > 1 && name[0] == '(' && name[^1] == ')')
            name = name[1..^1].Trim();

        // Type-qualified: split off the OWNER (everything before the last dot — a possibly prefixed type
        // token) and find the property on that resolved owner type.
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1)
        {
            var ownerToken = name[..dot];
            var propName = name[(dot + 1)..];

            if (!TryResolveTypeToken(ownerToken, out var ownerType, out var unboundPrefix))
                throw unboundPrefix is not null
                    ? Fatal(XamlDiagnosticCodes.UndeclaredPrefix,
                        $"Unbound xmlns prefix '{unboundPrefix}' in TemplateBinding source '{sourceName}'.", line, column)
                    : Fatal(XamlDiagnosticCodes.TypeNotFound,
                        $"TemplateBinding source owner type '{ownerToken}' was not found.", line, column);

            return UIPropertyRegistry.Find(ownerType, propName)
                   ?? throw Fatal(XamlDiagnosticCodes.MemberNotFound,
                       $"TemplateBinding source property '{propName}' is not a registered UIProperty on '{ownerType.Name}'.", line, column);
        }

        // The source lives on the TEMPLATED PARENT (matrix X160) — resolve against its runtime type when
        // a template build is in progress, falling back to the target part's type.
        var sourceOwner = TemplateContext?.TemplatedParent?.GetType() ?? target.GetType();

        var prop = UIPropertyRegistry.Find(sourceOwner, sourceName)
                   ?? UIPropertyRegistry.Find(target.GetType(), sourceName);
        if (prop is not null)
            return prop;

        // Fall back to the target property itself when the source name matches it (Background→Background).
        if (string.Equals(targetProperty.Name, sourceName, StringComparison.Ordinal))
            return targetProperty;

        throw Fatal(XamlDiagnosticCodes.MemberNotFound,
            $"TemplateBinding source property '{sourceName}' is not a registered UIProperty.", line, column);
    }

    /// <summary>
    /// Activates a custom <see cref="MarkupExtension"/> from its parsed node (matrix X125/X126): resolves
    /// the extension type, activates it (positional args mapped by the ctor-arity / declaration-order
    /// convention), and sets its named members.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Custom markup-extension members are set by reflection in the RequiresUnreferencedCode loader.")]
    internal MarkupExtension ActivateCustomExtension(MarkupExtensionNode node, XamlMember member, int line, int column)
    {
        var xamlType = ResolveExtensionType(node, line, column);
        var instance = xamlType.Activate?.Invoke()
            ?? throw Fatal(XamlDiagnosticCodes.NoParameterlessConstructor,
                $"Markup extension '{node.Name}' has no usable public parameterless constructor.", line, column);

        if (instance is not MarkupExtension extension)
            throw Fatal(XamlDiagnosticCodes.MemberNotFound,
                $"Type '{xamlType.ClrType.Name}' is used as a markup extension but does not derive from MarkupExtension.", line, column);

        // Positional args → the primary positional member by the ctor-arity convention (matrix X126):
        // map each positional to a writable property by declaration order (skipping the base members).
        if (node.PositionalArguments.Count > 0)
            ApplyPositionalArguments(extension, xamlType, node, line, column);

        // Named args → members on the extension.
        foreach (var named in node.NamedArguments)
        {
            var extMember = xamlType.TryGetMember(named.Name)
                ?? throw Fatal(XamlDiagnosticCodes.MemberNotFound,
                    $"No member '{named.Name}' on markup extension '{xamlType.ClrType.Name}'.", line, column);
            object? value = named.Value.IsNested
                ? ResolveExtensionArgument(named.Value.Nested!, line, column)
                : ConvertText(extMember, named.Value.Text ?? string.Empty, extension, line, column);
            Assign(extMember, extension, value, line, column);
        }

        return extension;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Custom markup-extension constructor argument mapping in the RequiresUnreferencedCode loader.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Custom markup-extension constructor argument mapping in the RequiresUnreferencedCode loader.")]
    private void ApplyPositionalArguments(MarkupExtension extension, XamlType xamlType, MarkupExtensionNode node, int line, int column)
    {
        var extType = extension.GetType();
        var props = extType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        // The authoritative ordering, if the WPF [ConstructorArgument] convention is in use: the
        // arity-matching constructor's parameter names, each mapped to the property carrying
        // [ConstructorArgument(paramName)] (P6 review P1-3). ([ConstructorArgument] is property-only —
        // AttributeTargets.Property — so a field can never be a positional target, matching the generator.)
        // Absent that, declaration order of the writable public properties.
        var ordered = ResolveConstructorArgumentOrder(extType, props, node.PositionalArguments.Count);

        if (ordered is not null)
        {
            for (int i = 0; i < ordered.Length && i < node.PositionalArguments.Count; i++)
                AssignPositional(extension, xamlType, ordered[i], node.PositionalArguments[i], line, column);
            return;
        }

        int taken = 0;
        foreach (var arg in node.PositionalArguments)
        {
            // Skip non-writable properties.
            while (taken < props.Length && !props[taken].CanWrite)
                taken++;
            if (taken >= props.Length)
                break;
            AssignPositional(extension, xamlType, props[taken++], arg, line, column);
        }
    }

    /// <summary>
    /// The property ordering implied by the WPF <see cref="ConstructorArgumentAttribute"/> convention:
    /// the public constructor whose arity matches <paramref name="positionalCount"/>, with each
    /// parameter name resolved to the property carrying <c>[ConstructorArgument(paramName)]</c>. Returns
    /// <c>null</c> when no constructor matches or any parameter has no companion attributed property (the
    /// caller falls back to declaration order).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Custom markup-extension constructor argument mapping in the RequiresUnreferencedCode loader.")]
    private static System.Reflection.PropertyInfo[]? ResolveConstructorArgumentOrder(
        Type extType, System.Reflection.PropertyInfo[] props, int positionalCount)
    {
        if (positionalCount == 0)
            return null;

        System.Reflection.ParameterInfo[]? matchParams = null;
        foreach (var ctor in extType.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var ps = ctor.GetParameters();
            if (ps.Length == positionalCount)
            {
                matchParams = ps;
                break;
            }
        }
        if (matchParams is null)
            return null;

        var result = new System.Reflection.PropertyInfo[positionalCount];
        for (int i = 0; i < positionalCount; i++)
        {
            string paramName = matchParams[i].Name ?? string.Empty;
            System.Reflection.PropertyInfo? match = null;
            foreach (var prop in props)
            {
                var attr = (ConstructorArgumentAttribute?)Attribute.GetCustomAttribute(prop, typeof(ConstructorArgumentAttribute));
                if (attr is not null && string.Equals(attr.ArgumentName, paramName, StringComparison.Ordinal))
                {
                    match = prop;
                    break;
                }
            }
            if (match is null)
                return null; // not fully annotated — fall back to declaration order
            result[i] = match;
        }
        return result;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Custom markup-extension constructor argument mapping in the RequiresUnreferencedCode loader.")]
    private void AssignPositional(
        MarkupExtension extension, XamlType xamlType, System.Reflection.PropertyInfo prop,
        MarkupExtensionArgumentValue arg, int line, int column)
    {
        var extMember = xamlType.TryGetMember(prop.Name);
        object? value = arg.IsNested
            ? ResolveExtensionArgument(arg.Nested!, line, column)
            : extMember is not null
                ? ConvertText(extMember, arg.Text ?? string.Empty, extension, line, column)
                : arg.Text;
        if (extMember is not null)
            Assign(extMember, extension, value, line, column);
        else
            prop.SetValue(extension, value);
    }

    private object? ResolveExtensionArgument(MarkupExtensionNode nested, int line, int column)
        => _extensionHandler.ResolveNestedExtensionPublic(this, nested, line, column);

    private XamlType ResolveExtensionType(MarkupExtensionNode node, int line, int column)
    {
        string name = node.Name;
        int colon = name.IndexOf(':');
        if (colon >= 0)
            name = name.Substring(colon + 1);

        // The xmlns the extension name's prefix bound to at PARSE (the parser stamps it — the reader
        // scope is long gone here). Without it a prefixed project extension ({v:EnumItemsSource …})
        // could only be probed in the default UI namespace and was unresolvable at load while
        // resolving fine at parse. Hand-built nodes (no stamp) keep the default-UI fallback.
        var xmlNamespace = node.ResolvedNamespace ?? XamlSchemaContext.CursorialUiNamespace;

        // Resolve in markup-extension position: prefer the WPF "Extension"-suffixed type so a `{Foo}` whose suffixed
        // twin `FooExtension` exists binds the extension even when a same-named non-extension type (e.g. an `Icon`
        // CONTROL alongside `IconExtension`) is also addressable. Fall back to the bare name for extensions authored
        // without the suffix; a bare name resolving to a non-MarkupExtension surfaces a clear diagnostic in ActivateCustomExtension.
        var resolution = _options.MetadataProvider.TryGetType(xmlNamespace, name + "Extension");
        if (!resolution.IsResolved)
            resolution = _options.MetadataProvider.TryGetType(xmlNamespace, name);
        if (resolution.IsResolved)
            return resolution.Type!;

        throw Fatal(XamlDiagnosticCodes.TypeNotFound,
            $"Markup extension type '{node.Name}' was not found.", line, column);
    }
}

/// <summary>
/// The X2 markup-extension attach seam (matrix §10–12): the loader hands a parsed extension record to
/// the handler, which resolves it against the type system and attaches the result through the
/// <c>IDeferredValue.AttachTo</c> / <c>SetResourceReference</c> / <c>BindingOperations</c> routes (never
/// a sentinel through <c>SetValue</c> — XD7). Null at X1.
/// </summary>
internal interface IXamlMarkupExtensionHandler
{
    void Attach(XamlObjectGraphBuilder builder, object instance, XamlType type, XamlMember? member, in ExtensionRecord extension, XamlDocument doc, int line, int column);
}

/// <summary>
/// The X3 deferred-content seam (matrix §13): wraps a node-graph slice as an <c>ITemplateContent</c>-shaped
/// object, capturing the lexical resource chain at the definition site (matrix X162).
/// </summary>
internal interface IXamlDeferredContentFactory
{
    object Create(XamlDocument doc, int sliceHead, XamlLoaderOptions options, Uri? source, CapturedScopeChain capturedScope);
}
