using System.Reflection;

using Cursorial.UI.Data;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The X2 markup-extension live-attach handler (matrix §10): resolves a parsed
/// <see cref="ExtensionRecord"/> against the type system and routes the result through the
/// deferred-value seams (matrix XD7) — never a sentinel through <c>SetValue</c>:
/// <list type="bullet">
/// <item><c>{StaticResource key}</c> resolves <b>eagerly</b> against the ambient resource stack; an
/// ordinary value reaches the XD8 assignment (matrix X113).</item>
/// <item><c>{DynamicResource key}</c> on a direct property installs a live producer via
/// <see cref="ResourceExtensions.SetResourceReference{T}"/> (X116); on a <c>Setter.Value</c> it stores a
/// <see cref="ResourceReference"/> carrier (X117).</item>
/// <item><c>{Binding …}</c> builds the S2 <see cref="Binding"/> and applies it via
/// <see cref="BindingOperations.Install(UIObject, UIProperty, BindingBase)"/> (X118/X119/X121).</item>
/// <item><c>{TemplateBinding prop}</c> builds the S2 <see cref="TemplateBinding"/> and applies it at build
/// (X127/X160); the parse-time template-body restriction already passed in the frontend (X56).</item>
/// <item><c>{x:Static}</c>/<c>{x:Type}</c>/<c>{x:Null}</c> fold at parse and never reach this handler.</item>
/// <item>a custom <see cref="MarkupExtension"/> subtype activates, has its members set, and returns
/// <c>ProvideValue(services)</c> (X125/X126).</item>
/// </list>
/// </summary>
internal sealed class XamlMarkupExtensionHandler : IXamlMarkupExtensionHandler
{
    private readonly XamlResourceScopeStack _scopes;

    internal XamlMarkupExtensionHandler(XamlResourceScopeStack scopes) => _scopes = scopes;

    public void Attach(
        XamlObjectGraphBuilder builder,
        object instance,
        XamlType type,
        XamlMember? member,
        in ExtensionRecord extension,
        XamlDocument doc,
        int line,
        int column)
    {
        switch (extension.Kind)
        {
            case ExtensionKind.StaticResource:
                AttachStaticResource(builder, instance, member, ResolveResourceKey(builder, in extension, doc, line, column), line, column);
                break;

            case ExtensionKind.DynamicResource:
                AttachDynamicResource(builder, instance, type, member, ResolveResourceKey(builder, in extension, doc, line, column), line, column);
                break;

            case ExtensionKind.Binding:
                AttachBinding(builder, instance, member, doc.ParsedExtensions[extension.Payload]!, line, column);
                break;

            case ExtensionKind.TemplateBinding:
                AttachTemplateBinding(builder, instance, member, doc.ParsedExtensions[extension.Payload]!, line, column);
                break;

            case ExtensionKind.Custom:
                AttachCustom(builder, instance, member, doc.ParsedExtensions[extension.Payload]!, line, column);
                break;

            case ExtensionKind.Reference:
                AttachReference(builder, instance, member, doc.Strings[extension.Payload], line, column);
                break;

            default:
                // x:Null/x:Static/x:Type fold at parse (a Folded member, not an Extension) and never reach here.
                throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound,
                    $"Unexpected markup-extension kind '{extension.Kind}' reached the live-attach handler.", line, column);
        }
    }

    // ── Resource key resolution (literal string or a nested key extension, XD7a) ─────────────────

    /// <summary>
    /// The resource key for a <c>{StaticResource}</c>/<c>{DynamicResource}</c>: a literal string (the
    /// common form, X44/X57) or — when the key is itself a markup extension
    /// (<c>{DynamicResource {x:Static ThemeKeys.X}}</c>, X44a/X57a) — the nested extension resolved to
    /// its value at instantiate (<c>{x:Static}</c>→the member value; <c>{StaticResource}</c>→the keyed
    /// value; the resource system is object-keyed). A null-resolving nested key is rejected here with a
    /// position-carrying diagnostic so a bare <see cref="ArgumentNullException"/> never escapes
    /// downstream (XD7a / risk-1).
    /// </summary>
    internal object ResolveResourceKey(XamlObjectGraphBuilder builder, in ExtensionRecord ext, XamlDocument doc, int line, int column)
    {
        if (!ext.PayloadIsParsedExtension)
            return doc.Strings[ext.Payload];

        var resolved = ResolveNestedExtension(builder, doc.ParsedExtensions[ext.Payload]!, line, column);
        return resolved ?? throw builder.Fatal(XamlDiagnosticCodes.ResourceNotFound,
            "A {StaticResource}/{DynamicResource} key markup extension resolved to null; a resource key must be non-null.",
            line, column);
    }

    // ── {StaticResource} — eager (matrix X113/X114/X121-nested) ──────────────────────────────────

    /// <summary>Resolves a StaticResource key against the ambient scope stack; the value reaches the XD8 assignment.</summary>
    internal object ResolveStaticResource(XamlObjectGraphBuilder builder, object key, int line, int column)
    {
        if (_scopes.TryResolve(key, out var value))
            return value!;

        throw builder.Fatal(XamlDiagnosticCodes.ResourceNotFound,
            $"StaticResource '{key}' was not found.{Environment.NewLine}{_scopes.DescribeSearchedChain()}", line, column);
    }

    private void AttachStaticResource(XamlObjectGraphBuilder builder, object instance, XamlMember? member, object key, int line, int column)
    {
        if (member is null)
            throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound, "StaticResource has no target member.", line, column);

        var value = ResolveStaticResource(builder, key, line, column);
        builder.AssignResolvedValue(member, instance, value, line, column);
    }

    // ── {DynamicResource} — late (matrix X116/X117) ──────────────────────────────────────────────

    // ReSharper disable once UnusedParameter.Local
    private void AttachDynamicResource(XamlObjectGraphBuilder builder, object instance, XamlType type, XamlMember? member, object key, int line, int column)
    {
        if (member is null)
            throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound, "DynamicResource has no target member.", line, column);

        // On a styled (non-direct) UIProperty of a UIElement, install the live producer (XD7 — never a
        // sentinel through SetValue). A DynamicResource cannot bind a direct property: there is no store
        // slot for a producer to occupy. The producer is generic over StyledProperty<T>; the loader is
        // already [RequiresDynamicCode], so dispatch by the property's runtime type.
        if (instance is UIElement element && member.Property is UIProperty { IsDirect: false } styled)
        {
            try
            {
                SetResourceReferenceDynamic(element, styled, key);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
                    $"DynamicResource '{key}' could not be installed: {inner.Message}", line, column, inner);
            }
            return;
        }

        throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
            $"{{DynamicResource}} on '{member.Name}' is not supported: only styled (non-direct) UIProperties on a " +
            "UIElement support dynamic resources. Use {StaticResource} for a load-time value, or a Binding.", line, column);
    }

    /// <summary>The public nested-extension resolver (custom-extension argument values, matrix X126).</summary>
    internal object? ResolveNestedExtensionPublic(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
        => ResolveNestedExtension(builder, node, line, column);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The loader is RequiresDynamicCode; SetResourceReference dispatches by the property's runtime type.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "The loader is RequiresUnreferencedCode; SetResourceReference dispatches by the property's runtime type.")]
    private static void SetResourceReferenceDynamic(UIElement element, UIProperty property, object key)
    {
        // SetResourceReference<T>(this UIElement, StyledProperty<T>, object) — bridge a non-generic
        // UIProperty by closing the generic over the property's value type.
        var method = typeof(ResourceExtensions)
            .GetMethod(nameof(ResourceExtensions.SetResourceReference), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(property.PropertyType);
        method.Invoke(null, [element, property, key]);
    }

    // ── {Binding} / {TemplateBinding} — live (matrix X118–X121, X127) ────────────────────────────

    private void AttachBinding(XamlObjectGraphBuilder builder, object instance, XamlMember? member, MarkupExtensionNode node, int line, int column)
    {
        // A member typed as a Binding DESCRIPTOR (e.g. DataCondition.Binding) rather than a bindable
        // UIProperty: {Binding …} yields the Binding OBJECT itself, assigned as the member value (WPF
        // parity — a Binding in a Binding-typed slot is the descriptor, not a live install). Its consumer
        // (the StyleEngine's When watch) arms it later; an ElementName stays on the descriptor for runtime
        // namescope resolution rather than being resolved to a concrete source here.
        if (member is { Property: null } && typeof(BindingBase).IsAssignableFrom(member.SystemType()))
        {
            builder.AssignResolvedValue(member, instance, BuildBinding(builder, node, line, column), line, column);
            return;
        }

        if (member?.Property is not UIProperty property)
            throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                $"Binding target '{member?.Name ?? "(content)"}' is not a bindable UIProperty.", line, column);

        if (instance is not UIObject target)
            throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                "Binding target is not a UIObject.", line, column);

        // {Binding ElementName=name} and {Binding Source={x:Reference name}} both anchor on a named element in the
        // active name scope — the document scope, or, inside a DataTemplate / ControlTemplate, that template
        // instance's own scope. Resolve the name to the concrete element and build the binding with it as the
        // Source, reusing x:Reference's backward(now)/forward(end-of-(sub)tree defer) resolution. This avoids the
        // runtime ElementName lookup racing the namescope attach (which the loader / DataTemplate.Build performs
        // only AFTER building the subtree), so it resolves identically at the document level and inside templates,
        // in either part order. (Binding source/anchor properties are init-only, so the element must be known at
        // construction — hence build-with-source, not mutate-after.)
        if (NamedElementAnchor(node) is { } name)
        {
            if (builder.ResolveName(name) is { } resolved)
                BindingOperations.Install(target, property, BuildBinding(builder, node, line, column, resolved));
            else
                builder.DeferNameResolution(name, line, column,
                    element => BindingOperations.Install(target, property, BuildBinding(builder, node, line, column, element)));
            return;
        }

        BindingOperations.Install(target, property, BuildBinding(builder, node, line, column));
    }

    // The name a binding anchors on, from ElementName=name OR Source={x:Reference name} (both resolve a named
    // element in the active scope); null when the binding has neither.
    private static string? NamedElementAnchor(MarkupExtensionNode node)
    {
        if (Named(node, "ElementName") is { Length: > 0 } elementName)
            return elementName;
        if (node.FindNamed("Source") is { IsNested: true } source && StripPrefix(source.Nested!.Name) == "Reference")
            return FirstPositional(source.Nested!);
        return null;
    }

    private void AttachTemplateBinding(XamlObjectGraphBuilder builder, object instance, XamlMember? member, MarkupExtensionNode node, int line, int column)
    {
        if (member?.Property is not UIProperty property)
            throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                $"TemplateBinding target '{member?.Name ?? "(content)"}' is not a bindable UIProperty.", line, column);

        if (instance is not UIObject target)
            throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                "TemplateBinding target is not a UIObject.", line, column);

        // The single positional argument names the source property on the templated parent.
        string sourcePropName = FirstPositional(node)
            ?? throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                "TemplateBinding requires a source property name.", line, column);

        var sourceProperty = builder.ResolveTemplateBindingSource(target, property, sourcePropName, line, column);
        var templateBinding = new TemplateBinding(sourceProperty);
        BindingOperations.Install(target, property, templateBinding);
    }

    // Builds a Binding from the markup node. <paramref name="namedElementSource"/>, when supplied, is the element a
    // resolved ElementName / Source={x:Reference} anchor points at — it becomes the binding's Source directly
    // (binding source/anchor properties are init-only, so the anchor must be resolved before construction).
    private Binding BuildBinding(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column, object? namedElementSource = null)
    {
        // Path: the first positional, or the Path= named arg.
        string path = FirstPositional(node) ?? Named(node, "Path") ?? string.Empty;
        object? fallback = Named(node, "FallbackValue");

        return new Binding
        {
            // Preprocess the path NOW against the xmlns-aware resolver (design doc §6.3 — the load-time
            // "preprocessor"): a `(prefix:Type.Member)` type-qualified segment resolves its owner against the
            // document's root xmlns table, baked in once. A bare `(Grid.Row)` still resolves via the registry
            // default. Type qualification is not assumed to be attached — it may be a regular property qualified
            // for disambiguation/clarity; only the OWNER TYPE resolves here, the member stays runtime-resolved.
            Path = new PropertyPath(path, builder.PathTypeResolver),
            Source = namedElementSource ?? ParseSourceValue(builder, node, line, column),
            // An ElementName is resolved to a concrete Source in the install path (namedElementSource);
            // in the descriptor path (no resolved anchor) it rides the descriptor for runtime resolution.
            ElementName = namedElementSource is null ? Named(node, "ElementName") : null,
            RelativeSource = ParseRelativeSource(builder, node, line, column),
            Mode = ParseEnum<BindingMode>(builder, node, "Mode", line, column) ?? BindingMode.Default,
            Converter = ResolveConverter(builder, node, line, column),
            StringFormat = Named(node, "StringFormat"),
            FallbackValue = fallback ?? UIProperty.UnsetValue,
        };
    }

    // Resolves a Binding's non-anchor Source argument (the ElementName / Source={x:Reference} anchor forms are
    // resolved to a concrete element by AttachBinding and passed as namedElementSource). Source may be a plain
    // value, or a nested {StaticResource key} / {x:Static member} resolved eagerly to a source object.
    private object? ParseSourceValue(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
    {
        if (node.FindNamed("Source") is not { } sourceArg)
            return null;

        return sourceArg.IsNested
            ? ResolveNestedExtension(builder, sourceArg.Nested!, line, column)
            : sourceArg.Text;
    }

    private static RelativeSource? ParseRelativeSource(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
    {
        if (node.FindNamed("RelativeSource") is not { } value)
            return null;

        // A nested {RelativeSource Self|TemplatedParent} (the common form), {RelativeSource Mode=…}, or
        // a bare text value.
        string? mode = value.IsNested
            ? FirstPositional(value.Nested!) ?? Named(value.Nested!, "Mode")
            : value.Text;

        return mode switch
        {
            "TemplatedParent" => RelativeSource.TemplatedParent,
            "Self" => RelativeSource.Self,
            // {RelativeSource FindAncestor, AncestorType={x:Type T}[, AncestorLevel=n]} — the nth logical ancestor
            // assignable to AncestorType (the engine already implements FindAncestor; this is the XAML front).
            "FindAncestor" when value.IsNested => BuildFindAncestor(builder, value.Nested!, line, column),
            // A nested {RelativeSource} with no explicit mode defaults to Self (WPF).
            null when value.IsNested => RelativeSource.Self,
            // An unrecognized mode (or a bare FindAncestor without AncestorType) is a hard error, not a silent Self,
            // so a misuse doesn't appear to have worked (P6 review P1-4).
            { Length: > 0 } => throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
                $"RelativeSource mode '{mode}' is not supported (Self, TemplatedParent, and FindAncestor are).", line, column),
            _ => null,
        };
    }

    // {RelativeSource FindAncestor, AncestorType={x:Type T} | T [, AncestorLevel=n]} → a FindAncestor anchor.
    private static RelativeSource BuildFindAncestor(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
    {
        if (node.FindNamed("AncestorType") is not { } typeArg)
            throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
                "RelativeSource FindAncestor requires an AncestorType (e.g. AncestorType={x:Type ScrollViewer}).", line, column);

        // AncestorType may be a nested {x:Type T} (WPF) or a bare type token T (Avalonia TypeConverter form).
        var typeName = typeArg.IsNested
            ? (typeArg.Nested!.PositionalArguments.Count > 0 ? typeArg.Nested.PositionalArguments[0].Text : null)
            : typeArg.Text;
        if (string.IsNullOrEmpty(typeName))
            throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed, "RelativeSource AncestorType is empty.", line, column);

        var ancestorType = (Type) builder.ResolveTypeToken(typeName!, line, column);
        var level = node.FindNamed("AncestorLevel") is { Text: { } lvl } && int.TryParse(lvl, out var n) && n > 0 ? n : 1;
        return new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = ancestorType, AncestorLevel = level };
    }

    private IValueConverter? ResolveConverter(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
    {
        var arg = node.FindNamed("Converter");
        if (arg is not { } value)
            return null;

        // A nested {StaticResource StatusToBrush} resolves eagerly to the converter (matrix X121).
        if (value.IsNested)
        {
            var resolved = ResolveNestedExtension(builder, value.Nested!, line, column);
            if (resolved is IValueConverter converter)
                return converter;
            throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
                $"The binding Converter resolved to '{resolved?.GetType().Name ?? "null"}', not an IValueConverter.", line, column);
        }

        return null;
    }

    private object? ResolveNestedExtension(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
    {
        string name = StripPrefix(node.Name);
        return name switch
        {
            "StaticResource" => ResolveStaticResource(builder, FirstPositional(node) ?? string.Empty, line, column),
            // {x:Static MyConverters.Instance} as a binding argument value (e.g., Converter={x:Static …}),
            // P6 review P2-13. (The top-level {x:Static} folds at parse; a nested one inside an extension
            // argument reaches here.)
            "Static" => builder.ResolveStaticMember(FirstPositional(node) ?? string.Empty, line, column),
            // {x:Type T} as a resource KEY ({StaticResource {x:Type Calendar}} — control themes key by {x:Type},
            // and a Style.BasedOn references one). Resolves to the System.Type key.
            "Type" => builder.ResolveTypeToken(FirstPositional(node) ?? string.Empty, line, column),
            "Null" => null,
            _ => throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound,
                $"Nested extension '{node.Name}' is not supported as a binding argument value.", line, column),
        };
    }

    // ── {x:Reference Name} (XAML2009 namescope reference) ────────────────────────────────────────
    private void AttachReference(XamlObjectGraphBuilder builder, object instance, XamlMember? member, string name, int line, int column)
    {
        if (member is null)
            throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound, "{x:Reference} has no target member.", line, column);

        // Backward reference (the named element is already built) → assign now; a forward reference (not yet in
        // the name scope) defers to the builder's end-of-(sub)tree pass, which resolves it once the scope is full.
        // ResolveName uses the active scope (document, or a template slice's own scope — so {x:Reference} works
        // inside a DataTemplate/ControlTemplate too).
        if (builder.ResolveName(name) is { } element)
            builder.AssignResolvedValue(member, instance, element, line, column);
        else
            builder.DeferReference(instance, member, name, line, column);
    }

    // ── Custom MarkupExtension (matrix X125/X126) ────────────────────────────────────────────────

    private void AttachCustom(XamlObjectGraphBuilder builder, object instance, XamlMember? member, MarkupExtensionNode node, int line, int column)
    {
        if (member is null)
            throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound, "A custom markup extension has no target member.", line, column);

        var extension = builder.ActivateCustomExtension(node, member, line, column);
        var services = new XamlServiceProvider(builder, instance, member, _scopes, line, column);
        object? value = extension.ProvideValue(services);
        builder.AssignResolvedValue(member, instance, value, line, column);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string? FirstPositional(MarkupExtensionNode node)
        => node.PositionalArguments.Count > 0 && node.PositionalArguments[0].Text is { } t ? t : null;

    private static string? Named(MarkupExtensionNode node, string name)
        => node.FindNamed(name) is { Text: { } t } ? t : null;

    private TEnum? ParseEnum<TEnum>(XamlObjectGraphBuilder builder, MarkupExtensionNode node, string argName, int line, int column)
        where TEnum : struct, Enum
    {
        if (Named(node, argName) is not { } text)
            return null;
        // Enum names in XAML are case-insensitive (WPF/Avalonia parity, P6 review P1-6) — `Mode=twoway`
        // matches `TwoWay`.
        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var value))
            return value;
        throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
            $"'{text}' is not a member of {typeof(TEnum).Name} (binding argument '{argName}').", line, column);
    }

    private static string StripPrefix(string name)
    {
        int colon = name.IndexOf(':');
        return colon >= 0 ? name.Substring(colon + 1) : name;
    }
}
