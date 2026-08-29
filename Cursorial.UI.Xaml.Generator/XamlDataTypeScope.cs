// frontend node graph (internals via InternalsVisibleTo)

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

    /// <summary>
    /// The <c>x:DataType</c> scope a <c>DataTemplate</c> establishes for its content: its <c>DataType</c>,
    /// gated on <paramref name="obj"/> actually being a DataTemplate. The path validator pushes this over the
    /// template subtree so a <c>{Binding}</c> inside a <c>&lt;DataTemplate DataType="vm:Foo"&gt;</c> validates
    /// against <c>Foo</c> even when the content root omits a (redundant) <c>x:DataType</c> — the build-time twin of
    /// the runtime template DataContext being an instance of <c>DataType</c>. Null when the object is not a
    /// DataTemplate, or the <c>DataType</c> is absent / unresolvable.
    /// </summary>
    public static INamedTypeSymbol? ForDataTemplate(XamlDocument document, in ObjectRecord obj, XamlSymbolResolver resolver)
        => IsDataTemplate(TypeSymbolOf(document, obj.TypeId)) ? DataTemplateDataType(document, in obj, resolver) : null;

    /// <summary>
    /// The DataTemplate's <c>DataType</c> as a resolved symbol (mirrors the loader's <c>TryGetDataType</c>): the
    /// <c>x:DataType</c> directive, or a CLR <c>DataType</c> member (a folded <c>{x:Type}</c> constant or a Text
    /// token), resolved via the document xmlns. Ungated — the caller owns the DataTemplate check (the lowering
    /// emitter gates on <see cref="IsDataTemplate"/> at its own call sites; <see cref="ForDataTemplate"/> gates for
    /// the validator). Null when absent / unresolvable.
    /// </summary>
    public static INamedTypeSymbol? DataTemplateDataType(XamlDocument document, in ObjectRecord obj, XamlSymbolResolver resolver)
    {
        var members = document.Members;

        for (int m = obj.MemberStart; m < obj.MemberStart + obj.MemberCount; m++)
        {
            ref readonly var member = ref members[m];

            if (member is { Kind: XamlValueKind.Directive, DirectiveKind: (int) XamlDirectiveKind.DataType })
                return ResolveToken(document, document.Strings[member.ValueIndex], resolver);

            if (member.MemberId >= 0 && document.ResolvedMembers[member.MemberId]?.Name == "DataType")
            {
                if (member.Kind == XamlValueKind.Folded && document.Constants[member.ValueIndex] is XamlTypeReference tr)
                    return ResolveToken(document, tr.TypeName, resolver); // {x:Type Foo}
                if (member.Kind == XamlValueKind.Text)
                    return ResolveToken(document, document.Strings[member.ValueIndex], resolver);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="type"/> is or derives from <c>Cursorial.UI.Controls.DataTemplate</c>.</summary>
    public static bool IsDataTemplate(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t is { Name: "DataTemplate", ContainingNamespace.Name: "Controls" })
                return true;

        return false;
    }

    private static INamedTypeSymbol? TypeSymbolOf(XamlDocument document, int typeId)
        => typeId >= 0 && document.ResolvedTypes[typeId]?.ClrType is RoslynXamlType { Symbol: INamedTypeSymbol s } ? s : null;

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

        var result = ns is null ? null : resolver.Resolve(ns, local, out _);
        if (result is not null)
            return result;

        const string xType = "{x:Type ";

        if (token.StartsWith(xType) && token.EndsWith("}"))
        {
            return ResolveToken(document,
                                token.Substring(xType.Length, token.Length - xType.Length - 1).Trim(),
                                resolver);
        }

        return null;
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

    /// <summary>
    /// True when a property/field member can be assigned through OUTSIDE a constructor — a public,
    /// non-init-only setter, or a non-readonly/non-const field. (An <c>init</c>-only setter is excluded:
    /// a lowered <c>__s.Prop = __v</c> assignment to it is CS8852, so an init-only leaf must degrade to a
    /// null setter ⇒ OneWay, mirroring the engine's read-only-leaf path — B152.)
    /// </summary>
    public static bool IsWritable(ISymbol member) => member switch
    {
        IPropertySymbol property => property is { SetMethod: { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } },
        IFieldSymbol field => field is { IsReadOnly: false, IsConst: false },
        _ => false,
    };

    /// <summary>
    /// True when the only public way to assign the member is an <c>init</c> setter — i.e. it must be set in an
    /// object initializer (<c>new T { Prop = v }</c>), never post-construction (<c>__e.Prop = v</c> is CS8852).
    /// </summary>
    public static bool IsInitOnlySettable(ISymbol member)
        => member is IPropertySymbol { SetMethod: { DeclaredAccessibility: Accessibility.Public, IsInitOnly: true } };

    /// <summary>True when a property/field member can be read through publicly (a public getter / any field).</summary>
    public static bool IsReadable(ISymbol member) => member switch
    {
        IPropertySymbol property => property is { GetMethod.DeclaredAccessibility: Accessibility.Public },
        IFieldSymbol => true,
        _ => false,
    };

    /// <summary>
    /// True when the registered <c>&lt;Name&gt;Property</c> static field on <paramref name="owner"/> is a
    /// <c>StyledProperty&lt;T&gt;</c> — the precondition for <c>SetResourceReference&lt;T&gt;</c> ({DynamicResource})
    /// and for a typed zero-box binding push. A direct/attached property (or a base <c>UIProperty</c> field) is not.
    /// </summary>
    public static bool IsStyledProperty(INamedTypeSymbol owner, string name)
    {
        foreach (var member in owner.GetMembers(name + "Property"))
            if (member is IFieldSymbol { IsStatic: true, Type: INamedTypeSymbol { OriginalDefinition.Name: "StyledProperty" } })
                return true;

        return false;
    }

    /// <summary>
    /// True when the registered <c>&lt;Name&gt;Property</c> field is a non-direct STYLED property — a
    /// <c>StyledProperty&lt;T&gt;</c> or its <c>AttachedProperty&lt;T&gt;</c> subclass — the precondition for
    /// <c>SetResourceReference&lt;T&gt;</c> (<c>{DynamicResource}</c>), which the loader accepts for ANY non-direct
    /// property (the handler's <c>IsDirect: false</c> gate). A <c>DirectProperty</c> or a base <c>UIProperty</c>
    /// field is not — the loader Fatals (BindingTargetNotBindable) on those, so the generator keeps them fenced.
    /// </summary>
    public static bool IsNonDirectStyledProperty(INamedTypeSymbol owner, string name)
    {
        foreach (var member in owner.GetMembers(name + "Property"))
            if (member is IFieldSymbol { IsStatic: true, Type: INamedTypeSymbol fieldType })
                for (var t = fieldType; t is not null; t = t.BaseType)
                    if (t.OriginalDefinition.Name is "StyledProperty") // AttachedProperty<T> : StyledProperty<T>; DirectProperty : UIProperty
                        return true;

        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> is or derives from <c>Cursorial.UI.UIElement</c> — the receiver
    /// constraint for the resource extension methods (<c>FindResource</c> / <c>SetResourceReference</c>), so a
    /// resource markup on a non-element object (a brush/pen inside a resource value) bails instead of emitting
    /// an uncompilable call.
    /// </summary>
    public static bool IsUIElement(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t is { Name: "UIElement", ContainingNamespace.Name: "UI" } &&
                t.ContainingNamespace.ContainingNamespace is { Name: "Cursorial", ContainingNamespace.IsGlobalNamespace: true })
                return true;

        return false;
    }
}
