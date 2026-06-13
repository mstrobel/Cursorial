using System;
using System.Globalization;
using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Data;

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
                AttachStaticResource(builder, instance, member, doc.Strings[extension.Payload], line, column);
                break;

            case ExtensionKind.DynamicResource:
                AttachDynamicResource(builder, instance, type, member, doc.Strings[extension.Payload], line, column);
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

            default:
                // x:Null/x:Static/x:Type fold at parse (a Folded member, not an Extension) and never reach here.
                throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound,
                    $"Unexpected markup-extension kind '{extension.Kind}' reached the live-attach handler.", line, column);
        }
    }

    // ── {StaticResource} — eager (matrix X113/X114/X121-nested) ──────────────────────────────────

    /// <summary>Resolves a StaticResource key against the ambient scope stack; the value reaches the XD8 assignment.</summary>
    internal object ResolveStaticResource(XamlObjectGraphBuilder builder, string key, int line, int column)
    {
        if (_scopes.TryResolve(key, out var value))
            return value!;

        throw builder.Fatal(XamlDiagnosticCodes.ResourceNotFound,
            $"StaticResource '{key}' was not found.{Environment.NewLine}{_scopes.DescribeSearchedChain()}", line, column);
    }

    private void AttachStaticResource(XamlObjectGraphBuilder builder, object instance, XamlMember? member, string key, int line, int column)
    {
        if (member is null)
            throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound, "StaticResource has no target member.", line, column);

        var value = ResolveStaticResource(builder, key, line, column);
        builder.AssignResolvedValue(member, instance, value, line, column);
    }

    // ── {DynamicResource} — late (matrix X116/X117) ──────────────────────────────────────────────

    private void AttachDynamicResource(XamlObjectGraphBuilder builder, object instance, XamlType type, XamlMember? member, string key, int line, int column)
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
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
                    $"DynamicResource '{key}' could not be installed: {inner.Message}", line, column);
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
        if (member?.Property is not UIProperty property)
            throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                $"Binding target '{member?.Name ?? "(content)"}' is not a bindable UIProperty.", line, column);

        if (instance is not UIObject target)
            throw builder.Fatal(XamlDiagnosticCodes.BindingTargetNotBindable,
                "Binding target is not a UIObject.", line, column);

        var binding = BuildBinding(builder, node, line, column);
        BindingOperations.Install(target, property, binding);
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

    private Binding BuildBinding(XamlObjectGraphBuilder builder, MarkupExtensionNode node, int line, int column)
    {
        // Path: the first positional, or the Path= named arg.
        string path = FirstPositional(node) ?? Named(node, "Path") ?? string.Empty;
        object? fallback = Named(node, "FallbackValue");

        return new Binding(path)
        {
            Source = Named(node, "Source"),
            ElementName = Named(node, "ElementName"),
            RelativeSource = ParseRelativeSource(builder, node, line, column),
            Mode = ParseEnum<BindingMode>(builder, node, "Mode", line, column) ?? BindingMode.Default,
            Converter = ResolveConverter(builder, node, line, column),
            StringFormat = Named(node, "StringFormat"),
            FallbackValue = fallback is null ? UIProperty.UnsetValue : fallback,
        };
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
            // A nested {RelativeSource} with no explicit mode defaults to Self (WPF).
            null when value.IsNested => RelativeSource.Self,
            // An unrecognized mode (e.g. FindAncestor — not in v1) is a hard error, not a silent Self,
            // so a misuse doesn't appear to have worked (P6 review P1-4).
            { Length: > 0 } => throw builder.Fatal(XamlDiagnosticCodes.ConversionFailed,
                $"RelativeSource mode '{mode}' is not supported in v1 (TemplatedParent and Self are).", line, column),
            _ => null,
        };
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
            // {x:Static MyConverters.Instance} as a binding argument value (e.g. Converter={x:Static …}),
            // P6 review P2-13. (The top-level {x:Static} folds at parse; a nested one inside an extension
            // argument reaches here.)
            "Static" => builder.ResolveStaticMember(FirstPositional(node) ?? string.Empty, line, column),
            "Null" => null,
            _ => throw builder.Fatal(XamlDiagnosticCodes.MemberNotFound,
                $"Nested extension '{node.Name}' is not supported as a binding argument value.", line, column),
        };
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
