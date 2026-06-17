using System;
using System.Collections.Generic;

using Cursorial.UI.Xaml; // frontend node graph (internals via InternalsVisibleTo)

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// WS-B3 — build-time <c>x:DataType</c> binding-path validation (binding-matrix B184). For each
/// DataContext-relative <c>{Binding}</c> whose lexical scope declares an <c>x:DataType</c>, it walks the
/// binding's dotted path against the declared type's members (symbol-only, no runtime <see cref="System.Type"/>)
/// and reports a <c>CURG2001</c> assist when a segment doesn't resolve — catching a typo'd path at build time.
/// </summary>
/// <remarks>
/// Conservative by design (an assist, not a gate — matrix WS-X4.4): it validates only bindings that are
/// DataContext-relative (no <c>Source</c>/<c>RelativeSource</c>/<c>ElementName</c>) and have a simple dotted
/// path; an indexer / attached / method-bearing path, or a hop into a non-named type (array, pointer, type
/// parameter), stops the walk WITHOUT a diagnostic (the runtime reflective binding still applies). The
/// <c>x:DataType</c> lexical scope is reconstructed from the depth-first node graph via a subtree-bounded stack,
/// so a binding inside a <c>DataTemplate</c> uses that template's <c>x:DataType</c>, not an outer one.
/// </remarks>
internal static class BindingPathValidator
{
    /// <summary>Validates every x:DataType-scoped binding path; <paramref name="report"/> is (message, line, column).</summary>
    public static void Validate(XamlDocument document, XamlSymbolResolver resolver, Action<string, int, int> report)
    {
        var objects = document.Objects;
        var members = document.Members;
        var strings = document.Strings;
        var extensions = document.Extensions;
        var parsed = document.ParsedExtensions;

        var scope = new Stack<(int SubtreeEnd, INamedTypeSymbol DataType)>();

        for (int i = 0; i < objects.Length; i++)
        {
            ref readonly var obj = ref objects[i];

            while (scope.Count > 0 && scope.Peek().SubtreeEnd <= i)
                scope.Pop();

            // An x:DataType on this object establishes the DataContext type for its whole subtree.
            if (ResolveDataType(document, in obj, resolver) is { } declared)
                scope.Push((i + obj.SubtreeLength, declared));

            if (scope.Count == 0)
                continue;

            var dataType = scope.Peek().DataType;

            for (int m = obj.MemberStart; m < obj.MemberStart + obj.MemberCount; m++)
            {
                ref readonly var member = ref members[m];
                if (member.Kind != XamlValueKind.Extension)
                    continue;

                ref readonly var ext = ref extensions[member.ValueIndex];
                if (ext.Kind != ExtensionKind.Binding)
                    continue;

                if (parsed[ext.Payload] is { } node)
                    ValidateBinding(node, dataType, report);
            }
        }
    }

    private static INamedTypeSymbol? ResolveDataType(XamlDocument document, in ObjectRecord obj, XamlSymbolResolver resolver)
    {
        var members = document.Members;
        for (int m = obj.MemberStart; m < obj.MemberStart + obj.MemberCount; m++)
        {
            ref readonly var member = ref members[m];
            if (member.Kind == XamlValueKind.Directive && member.DirectiveKind == (int)XamlDirectiveKind.DataType)
                return ResolveTypeName(document, document.Strings[member.ValueIndex], resolver);
        }
        return null;
    }

    // Resolves an x:DataType token ("vm:FileItem" / "FileItem") via the document xmlns table.
    private static INamedTypeSymbol? ResolveTypeName(XamlDocument document, string token, XamlSymbolResolver resolver)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        string prefix = string.Empty, local = token;
        int colon = token.IndexOf(':');
        if (colon > 0)
        {
            prefix = token.Substring(0, colon);
            local = token.Substring(colon + 1);
        }

        if (!document.Namespaces.TryGetValue(prefix, out var ns))
            ns = prefix.Length == 0 ? XamlSymbolResolver.CursorialUiNamespace : null!;
        if (ns is null)
            return null;

        return resolver.Resolve(ns, local, out _);
    }

    private static void ValidateBinding(MarkupExtensionNode node, INamedTypeSymbol dataType, Action<string, int, int> report)
    {
        // Only DataContext-relative bindings carry an x:DataType-validatable path.
        if (node.FindNamed("Source") is not null || node.FindNamed("RelativeSource") is not null || node.FindNamed("ElementName") is not null)
            return;

        string? path = node.PositionalArguments.Count > 0 ? node.PositionalArguments[0].Text : node.FindNamed("Path")?.Text;
        if (string.IsNullOrEmpty(path) || path == ".")
            return; // binds to the DataContext itself — nothing to walk

        // Conservative: indexers / attached-property / method-call paths are not walked (reflective at runtime).
        if (path!.IndexOf('[') >= 0 || path.IndexOf('(') >= 0)
            return;

        INamedTypeSymbol current = dataType;
        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0)
                return;

            if (FindMemberType(current, segment) is not { } memberType)
            {
                report($"Binding path member '{segment}' was not found on x:DataType '{current.Name}'.", node.Line, node.Column);
                return;
            }

            if (memberType is INamedTypeSymbol named)
                current = named;
            else
                return; // can't walk into an array / pointer / type parameter — stop without a diagnostic
        }
    }

    private static ITypeSymbol? FindMemberType(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(name))
            {
                if (member.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (member is IPropertySymbol property)
                    return property.Type;
                if (member is IFieldSymbol field)
                    return field.Type;
            }
        }
        return null;
    }
}
