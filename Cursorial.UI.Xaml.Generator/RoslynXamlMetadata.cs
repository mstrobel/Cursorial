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
internal sealed class RoslynXamlMetadata : IXamlTypeMetadataProvider, IXamlGenericTypeProvider
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

    /// <summary>The W3 generic closing — the reflection twin's symbol half: resolve the definition
    /// (exact name, then Roslyn's arity-aware match — symbol <c>Name</c> excludes arity, so the resolver
    /// finds `List`1` by "List"; a name-only hit with the wrong arity is rejected), recursively close the
    /// arguments (the <c>x:</c> intrinsics via <c>GetSpecialType</c>), apply the Cursorial suffixes, and
    /// <c>Construct()</c>. The emitter then renders the closed symbol — <c>new T&lt;args&gt;()</c>,
    /// AOT-clean by construction.</summary>
    public XamlTypeResolution TryGetClosedType(in QualifiedTypeName name)
    {
        var closed = ResolveQualified(in name, out var ambiguous);
        if (closed is INamedTypeSymbol named)
            return XamlTypeResolution.Resolved(GetXamlType(named));
        if (closed is not null)
            return XamlTypeResolution.Resolved(GetXamlTypeForSymbol(closed)); // array symbols
        return ambiguous is { Count: > 0 } ? XamlTypeResolution.Ambiguous(ambiguous.ToArray()) : XamlTypeResolution.NotFound();
    }

    private ITypeSymbol? ResolveQualified(in QualifiedTypeName name, out IReadOnlyList<string>? ambiguous)
    {
        ambiguous = null;
        ITypeSymbol? resolved;

        // The x: intrinsics (x:Double/x:String/…) — the schema's BuiltInType twin, over special types.
        // Arity 0 REQUIRED (audit): an argument-bearing intrinsic (x:Double(x:Int32)) must fall through
        // and fail like the reflection lane, never silently discard its arguments.
        if (name.TypeArguments.Count == 0 &&
            string.Equals(name.XmlNamespace, XmlnsNamespaces.Intrinsics, StringComparison.Ordinal) &&
            IntrinsicSpecialType(name.Name) is { } special)
        {
            resolved = _compilation.GetSpecialType(special);
        }
        else if (name.TypeArguments.Count == 0)
        {
            resolved = _resolver.Resolve(name.XmlNamespace, name.Name, out var flat);
            ambiguous = flat;
        }
        else
        {
            // The resolver matches METADATA names, which carry arity — the backtick form is canonical
            // (GenApp.GenericWidget`1); the exact-name fallback covers a using:-mapped exotic. The
            // fallback gets its OWN out variable (audit) so the backtick call's ambiguity candidates
            // survive when the fallback simply finds nothing.
            var definition = _resolver.Resolve(name.XmlNamespace, name.Name + "`" + name.TypeArguments.Count, out var defAmbiguous);
            if (definition is null && defAmbiguous is not { Count: > 0 })
                definition = _resolver.Resolve(name.XmlNamespace, name.Name, out defAmbiguous);
            ambiguous = defAmbiguous;

            if (definition is not { IsGenericType: true } || definition.Arity != name.TypeArguments.Count)
                return null;

            var arguments = new ITypeSymbol[name.TypeArguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                if (ResolveQualified(name.TypeArguments[i], out var argAmbiguous) is not { } argument)
                {
                    ambiguous = argAmbiguous is { Count: > 0 } ? argAmbiguous : ambiguous;
                    return null;
                }
                arguments[i] = argument;
            }

            // Constraint validation BEFORE Construct (audit — the reflection lane's MakeGenericType
            // throws on a violation and reports the positioned CUR2002; Roslyn's Construct validates
            // nothing, so an x:String against `where T : struct` escaped to a raw CS0453 in generated
            // code): the same violation must return null here so the parser's diagnostic — whose
            // message already names the constraint case — fires identically in both lanes.
            var typeParameters = definition.ConstructedFrom.TypeParameters;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!SatisfiesConstraints(typeParameters[i], arguments[i]))
                    return null;
            }

            resolved = definition.ConstructedFrom.Construct(arguments);
        }

        if (resolved is null)
            return null;

        if (name.IsNullable)
        {
            // Parity with the reflection guard (audit): '?' demands a NON-NULLABLE value type — without
            // the second check, Nullable(x:Int32)? closed Nullable<Nullable<int>> and the emitter
            // rendered the syntactically invalid `int??`.
            if (!resolved.IsValueType || resolved.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return null;
            resolved = _compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(resolved);
        }

        if (name.IsArray)
            resolved = _compilation.CreateArrayTypeSymbol(resolved);

        return resolved;
    }

    /// <summary>The Roslyn half of the reflection lane's MakeGenericType constraint enforcement.</summary>
    private bool SatisfiesConstraints(ITypeParameterSymbol parameter, ITypeSymbol argument)
    {
        if (parameter.HasValueTypeConstraint &&
            (!argument.IsValueType || argument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            return false;

        if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType)
            return false;

        if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType)
            return false;

        if (parameter.HasConstructorConstraint && !argument.IsValueType &&
            (argument.IsAbstract ||
             argument is not INamedTypeSymbol ctorable ||
             !ctorable.InstanceConstructors.Any(static c => c is { DeclaredAccessibility: Accessibility.Public, Parameters.Length: 0 })))
            return false;

        foreach (var constraintType in parameter.ConstraintTypes)
        {
            if (!_compilation.HasImplicitConversion(argument, constraintType))
                return false;
        }

        return true;
    }

    private static SpecialType? IntrinsicSpecialType(string name) => name switch
    {
        "String" => SpecialType.System_String,
        "Object" => SpecialType.System_Object,
        "Boolean" => SpecialType.System_Boolean,
        "Char" => SpecialType.System_Char,
        "Byte" => SpecialType.System_Byte,
        "Int16" => SpecialType.System_Int16,
        "Int32" => SpecialType.System_Int32,
        "Int64" => SpecialType.System_Int64,
        "Single" => SpecialType.System_Single,
        "Double" => SpecialType.System_Double,
        "Decimal" => SpecialType.System_Decimal,
        _ => null,
    };

    // An array-symbol XamlType (no members, no activation — used as a type ARGUMENT identity only).
    private XamlType GetXamlTypeForSymbol(ITypeSymbol symbol)
        => new(clrType: new RoslynXamlType(symbol),
               activate: null,
               contentProperty: null,
               isCollection: false,
               requiresInitialize: false,
               memberResolver: static _ => null,
               isMarkupExtension: false);

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
        var isDictionary = SymbolXamlModel.IsResourceDictionary(symbol);
        var isCollection = contentProperty is not null && SymbolXamlModel.ContentIsCollection(symbol, contentProperty) ||
                           isDictionary || // mirror ReflectionXamlMetadata + the emitter: a dictionary is a collection
                           SymbolXamlModel.IsCollectionType(symbol); // …and so is a SELF-LIST element (TransitionCollection, X73/W2b)
        var members = new Dictionary<string, XamlMember?>(StringComparer.Ordinal);

        return new XamlType(
            clrType: new RoslynXamlType(symbol),
            activate: null, // no activation at generator time
            contentProperty: contentProperty,
            isCollection: isCollection,
            requiresInitialize: SymbolXamlModel.RequiresInitialize(symbol), // the parser stamps NeedsBeginInit from this
            memberResolver: name => members.TryGetValue(name, out var m) ? m : members[name] = BuildMember(symbol, name),
            isMarkupExtension: DerivesFromMarkupExtension(symbol));
    }

    private static bool DerivesFromMarkupExtension(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol? t = symbol; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            if (t.ToDisplayString() == "Cursorial.UI.Xaml.MarkupExtension")
                return true;
        return false;
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