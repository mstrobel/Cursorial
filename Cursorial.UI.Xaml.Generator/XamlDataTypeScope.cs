using Cursorial.UI.Xaml; // frontend node graph (internals via InternalsVisibleTo)

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// Shared <c>x:DataType</c> resolution + symbol member-walk for the build-time binding workstreams (WS-B3):
/// the path validator (<see cref="BindingPathValidator"/>) and the lowering emitter
/// (<see cref="LoweringEmitter"/>) both reconstruct the <c>x:DataType</c> lexical scope and walk a binding
/// path against the declared type's members from Roslyn symbols only (no runtime <see cref="System.Type"/>).
/// One source so the validator and the emitter cannot disagree about what a path resolves to.
/// </summary>
internal static class XamlDataTypeScope
{
    /// <summary>The CLR type symbol of an <c>x:DataType</c> declared directly on <paramref name="obj"/>, or null.</summary>
    public static INamedTypeSymbol? ForObject(XamlDocument document, in ObjectRecord obj, XamlSymbolResolver resolver)
    {
        var members = document.Members;

        for (int m = obj.MemberStart; m < obj.MemberStart + obj.MemberCount; m++)
        {
            ref readonly var member = ref members[m];

            if (member.Kind == XamlValueKind.Directive && member.DirectiveKind == (int) XamlDirectiveKind.DataType)
                return ResolveToken(document, document.Strings[member.ValueIndex], resolver);
        }

        return null;
    }

    /// <summary>Resolves an <c>x:DataType</c> token (<c>"vm:FileItem"</c> / <c>"FileItem"</c>) via the document xmlns table.</summary>
    public static INamedTypeSymbol? ResolveToken(XamlDocument document, string token, XamlSymbolResolver resolver)
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

        return ns is null ? null : resolver.Resolve(ns, local, out _);
    }

    /// <summary>Finds a public property/field member by name, walking base types (stops at <see cref="object"/>).</summary>
    public static ISymbol? FindMember(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (var member in t.GetMembers(name))
            {
                if (member.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (member is IPropertySymbol or IFieldSymbol)
                    return member;
            }

        return null;
    }

    /// <summary>The value type of a property/field member symbol, or null for anything else.</summary>
    public static ITypeSymbol? MemberType(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => null,
    };

    /// <summary>True when a property/field member can be assigned through (a public setter / a non-readonly field).</summary>
    public static bool IsWritable(ISymbol member) => member switch
    {
        IPropertySymbol property => property is { SetMethod.DeclaredAccessibility: Accessibility.Public },
        IFieldSymbol field => field is { IsReadOnly: false, IsConst: false },
        _ => false,
    };

    /// <summary>True when a property/field member can be read through publicly (a public getter / any field).</summary>
    public static bool IsReadable(ISymbol member) => member switch
    {
        IPropertySymbol property => property is { GetMethod.DeclaredAccessibility: Accessibility.Public },
        IFieldSymbol => true,
        _ => false,
    };
}
