using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// The build-time, symbol-backed <see cref="IXamlTypeMetadataProvider"/> (design doc WS-X4.3): resolves
/// XAML names to Roslyn symbols (via <see cref="XamlSymbolResolver"/>) and builds <see cref="XamlType"/> /
/// <see cref="XamlMember"/>s with <see cref="RoslynXamlType"/> identities, so the frontend parser runs
/// <i>inside</i> the generator — the SAME parser the loader uses. The parse then yields semantic
/// <c>CUR2xxx</c> diagnostics (type/member-not-found, ambiguity) and a fully resolved node graph whose
/// resolved-type table carries the element symbols (the basis for typed <c>x:Name</c> field codegen and
/// <c>x:DataType</c> path checking).
/// </summary>
/// <remarks>
/// Member/type facts come from the shared <see cref="SymbolXamlModel"/> — the same source the
/// <see cref="MetadataProviderEmitter"/> bakes from — so the build-time parse provider cannot drift from
/// the emitted runtime provider. It never loads <c>Cursorial.UI</c> into the compiler (no
/// <see cref="System.Type"/>); the generated provider's <c>typeof(...)</c> references resolve at the
/// consumer's compile. The parse must run with <c>FoldConstants = false</c> (there are no runtime values
/// to fold at generator time).
/// </remarks>
internal sealed class RoslynXamlMetadata : IXamlTypeMetadataProvider
{
    private const string StyleTypeName = "Cursorial.UI.Style";

    private readonly Compilation _compilation;
    private readonly XamlSymbolResolver _resolver;
    private readonly Dictionary<ITypeSymbol, XamlType> _typeCache = new(SymbolEqualityComparer.Default);
    private RoslynXamlType? _systemTypeRef;

    public RoslynXamlMetadata(Compilation compilation)
    {
        _compilation = compilation;
        _resolver = new XamlSymbolResolver(compilation);
    }

    public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
    {
        var symbol = _resolver.Resolve(xmlNamespace, localName, out var ambiguous);

        if (symbol is not null)
            return XamlTypeResolution.Resolved(GetXamlType(symbol));

        if (ambiguous is { Count: > 0 })
            return XamlTypeResolution.Ambiguous(ambiguous.ToArray());

        return XamlTypeResolution.NotFound();
    }

    public string[] GetClrNamespaces(string xmlNamespace) => _resolver.CandidateNamespaces(xmlNamespace).ToArray();

    // No cheap whole-namespace type enumeration at generator time — a type-not-found diagnostic without a
    // did-you-mean suggestion is acceptable (the suggestion is optional, matrix X19).
    public string[] GetKnownTypeNames(string xmlNamespace) => Array.Empty<string>();

    public string[] GetKnownMemberNames(IXamlType type)
        => type is RoslynXamlType { Symbol: INamedTypeSymbol named }
               ? SymbolXamlModel.EnumerateMembers(named).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray()
               : Array.Empty<string>();

    private XamlType GetXamlType(INamedTypeSymbol symbol)
    {
        if (_typeCache.TryGetValue(symbol, out var cached))
            return cached;

        var built = BuildType(symbol);
        _typeCache[symbol] = built;
        return built;
    }

    private XamlType BuildType(INamedTypeSymbol symbol)
    {
        var contentProperty = SymbolXamlModel.ResolveContentProperty(symbol);
        var isCollection = contentProperty is not null && SymbolXamlModel.ContentIsCollection(symbol, contentProperty);
        var members = new Dictionary<string, XamlMember?>(StringComparer.Ordinal);

        return new XamlType(
            clrType: new RoslynXamlType(symbol),
            activate: null, // no activation at generator time
            contentProperty: contentProperty,
            isCollection: isCollection,
            memberResolver: name => members.TryGetValue(name, out var m) ? m : members[name] = BuildMember(symbol, name));
    }

    private XamlMember? BuildMember(INamedTypeSymbol owner, string name)
    {
        // Rung 0 — Style.TargetType is synthetic: a Style matches via a Selector, not a TargetType property,
        // so the reflection provider injects a Type-typed member here. Mirror it, or an enclosed Setter's
        // target-type resolution (X64/X66) fails (CUR2110) and TargetType itself is CUR2102.
        if (name == "TargetType" && string.Equals(owner.ToDisplayString(), StyleTypeName, StringComparison.Ordinal))
            return new XamlMember(name, SystemTypeRef);

        if (SymbolXamlModel.FindMember(owner, name) is not {} m)
            return null;

        return new XamlMember(
                   name: m.Name,
                   valueType: new RoslynXamlType(m.ValueType),
                   // A non-null Property marks a registered UIProperty (XD4 rule 1); the parser only checks
                   // null-ness, never the value (it carries the field-owner symbol, never a runtime UIProperty).
                   property: m.RegisteredFieldOwner,
                   isEvent: m.IsEvent,
                   isAttachable: m.IsAttached)
               {
                   IsDeferredContent = SymbolXamlModel.IsDeferredContent(m.ValueType)
               };
    }

    // The value-type identity for the synthetic Style.TargetType member (System.Type; falls back to object).
    // Only its non-collection-ness matters at parse time — the loader reads the TargetType VALUE string.
    private RoslynXamlType SystemTypeRef
        => _systemTypeRef ??= new RoslynXamlType(
               (ITypeSymbol?) _compilation.GetTypeByMetadataName("System.Type") ??
               _compilation.GetSpecialType(SpecialType.System_Object)
           );
}