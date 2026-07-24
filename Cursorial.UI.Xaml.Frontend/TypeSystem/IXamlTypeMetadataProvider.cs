using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Resolves XAML local names + xmlns URIs to <see cref="XamlType"/>s and registers the xmlns→CLR
/// namespace map. The default implementation (<c>ReflectionXamlMetadata</c>, in the loader assembly)
/// is reflection-backed and honestly trim/AOT-annotated; the X5 generator emits a trim-clean
/// provider against this same seam (matrix XD16). The frontend parser is driven entirely by this
/// interface — it never reflects.
/// </summary>
public interface IXamlTypeMetadataProvider
{
    /// <summary>
    /// Resolves the <see cref="XamlType"/> for a local name within an xmlns namespace, or <c>null</c>
    /// when no such type exists in the mapped CLR namespaces. An <i>ambiguous</i> local name (two
    /// matching types) is reported through the returned <see cref="XamlTypeResolution"/> so the parser
    /// can emit <c>CUR2001</c> with the candidate list. A pure-syntax frontend test may pass a stub provider
    /// (or none) — type-resolution rows inject the real loader provider.
    /// </summary>
    XamlTypeResolution TryGetType(string xmlNamespace, string localName);

    /// <summary>
    /// Returns the CLR namespaces mapped to an xmlns URI (for did-you-mean suggestions over a
    /// namespace's known type names). Empty when the URI is unmapped.
    /// </summary>
    string[] GetClrNamespaces(string xmlNamespace);

    /// <summary>
    /// Returns the known XAML-visible type names within an xmlns URI, for Levenshtein did-you-mean
    /// suggestions on a miss (matrix X19). May be empty if the provider cannot enumerate.
    /// </summary>
    string[] GetKnownTypeNames(string xmlNamespace);

    /// <summary>
    /// Returns the XAML-settable member names of <paramref name="type"/> (public instance properties +
    /// events), for a did-you-mean suggestion on a member-not-found diagnostic (P6 review P2-16). Takes the
    /// abstract <see cref="IXamlType"/> so a symbol backend can enumerate from a Roslyn symbol. May return
    /// an empty array when the provider cannot enumerate (no suggestion is offered).
    /// </summary>
    string[] GetKnownMemberNames(IXamlType type);
}

/// <summary>
/// An OPTIONAL companion seam to <see cref="IXamlTypeMetadataProvider"/>: resolves an
/// <c>{x:Static Type.Member}</c> path to its runtime value. The loader's fold-finalize checks for this on
/// the active metadata provider; a provider that does not implement it simply cannot resolve <c>x:Static</c>
/// (the loader reports <c>CUR</c> member-not-found). <c>ReflectionXamlMetadata</c> implements it reflectively;
/// the X5 generated provider bakes a switch over the document's referenced statics (AOT-clean). It is a
/// SEPARATE interface rather than a member on <see cref="IXamlTypeMetadataProvider"/> so it stays
/// non-breaking on netstandard2.0 (which has no default interface methods) — existing providers keep working
/// and opt in by implementing it.
/// </summary>
/// <remarks>
/// The single method is xmlns-QUALIFIED (P1C): the loader binds the document prefix itself
/// (<c>{x:Static co:Colors.Red}</c> → the <c>co:</c> declaration's namespace) and passes the provider a
/// prefix-FREE path, so providers never see raw prefixes and two documents binding the same prefix to
/// different namespaces cannot collide. There is deliberately no unqualified (default-namespace-only)
/// overload — such a method silently resolves prefixed paths in the wrong namespace, the exact bug class
/// this seam is kept qualified to avoid.
/// </remarks>
public interface IXamlStaticResolver
{
    /// <summary>
    /// Resolves a prefix-free <c>Type.Member</c> path (e.g. <c>"Colors.Red"</c>) whose type is declared under
    /// <paramref name="xmlNamespace"/> (a Cursorial uri or <c>clr-namespace:</c> form) to its value, or
    /// returns <c>false</c> on an unresolvable path.
    /// </summary>
    bool TryResolveStatic(string xmlNamespace, string memberPath, out object? value);
}

/// <summary>
/// The outcome of a type resolution: the resolved type, an ambiguity, or a miss.
/// </summary>
public readonly struct XamlTypeResolution
{
    private XamlTypeResolution(XamlType? type, string[]? ambiguousCandidates)
    {
        Type = type;
        AmbiguousCandidates = ambiguousCandidates;
    }

    /// <summary>The resolved type, or <c>null</c> on miss/ambiguity.</summary>
    public XamlType? Type { get; }

    /// <summary>The candidate full names when the local name is ambiguous; otherwise <c>null</c>.</summary>
    public string[]? AmbiguousCandidates { get; }

    /// <summary>True when a single type resolved.</summary>
    public bool IsResolved => Type is not null;

    /// <summary>True when two or more types matched the local name.</summary>
    public bool IsAmbiguous => AmbiguousCandidates is not null;

    /// <summary>A successful resolution.</summary>
    public static XamlTypeResolution Resolved(XamlType type) => new(type, null);

    /// <summary>An ambiguous resolution carrying the candidate full names.</summary>
    public static XamlTypeResolution Ambiguous(string[] candidates) => new(null, candidates);

    /// <summary>A miss (no matching type).</summary>
    public static XamlTypeResolution NotFound() => default;
}