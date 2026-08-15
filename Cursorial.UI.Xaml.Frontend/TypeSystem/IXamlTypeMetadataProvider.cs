

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
    /// <para>
    /// The set is the UNION of the members settable in EITHER XAML form — attribute-settable scalars AND
    /// content-settable read-only collections populated as property elements
    /// (<c>&lt;Panel.Children&gt;…</c>, <c>Style.Setters</c>) — so it is the complete "known members" list
    /// for a member spelled either way. Read-only SCALARS (<c>Bounds</c>, <c>DesiredSize</c>) are settable in
    /// neither form and are absent. A caller that must split the two (attribute completion wants scalars only;
    /// property-element completion wants scalars + collections) recovers the distinction per-member from the
    /// resolved <see cref="XamlMember"/>'s value type / collection-ness.
    /// </para>
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
/// (<c>{x:Static co:CellStyle.Default}</c> → the <c>co:</c> declaration's namespace) and passes the provider a
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
/// An OPTIONAL companion seam to <see cref="IXamlTypeMetadataProvider"/> — the exact shape of
/// <see cref="IXamlStaticResolver"/>: it enumerates the attached properties that may be <em>set on</em> a
/// target type, so a designer / completion host can offer <c>Owner.Property</c> attributes (e.g.
/// <c>Grid.Column</c>) on a child element without reaching into the framework's property registry itself.
/// It is a SEPARATE interface rather than a member on <see cref="IXamlTypeMetadataProvider"/> so it stays
/// non-breaking on netstandard2.0 — that runtime has no default interface methods (a body on an interface
/// member is <c>CS8701</c> there), so an added interface method cannot carry an empty default and would break
/// every existing provider. Existing providers keep working untouched; a provider that CAN answer opts in by
/// implementing this. The runtime reflection provider (<c>ReflectionXamlMetadata</c>) implements it over the
/// property registry; the build-time symbol provider does not (generated/AOT completion is not the host's
/// path — the host drives the runtime reflection provider). A consumer probes for the capability
/// (<c>provider is IXamlAttachablePropertyProvider ap</c>) and simply offers no attachable completion when it
/// is absent, mirroring the loader's <c>{x:Static}</c> probe (<c>XamlObjectGraphBuilder</c>).
/// </summary>
public interface IXamlAttachablePropertyProvider
{
    /// <summary>
    /// The attached properties declarable on <paramref name="targetType"/> — every attached property whose
    /// host (target) type is assignable from it — as <see cref="XamlAttachableMember"/>s. Empty when none
    /// apply, or when the provider cannot map the type (e.g. a symbol backend whose
    /// <see cref="IXamlType.UnderlyingSystemType"/> is <see langword="null"/>). The set reflects only the
    /// attached properties whose owners have registered (the registry is populated as owner types initialize);
    /// forcing owner registration is the consumer's concern.
    /// </summary>
    XamlAttachableMember[] GetAttachableMembers(IXamlType targetType);
}

/// <summary>
/// An OPTIONAL companion seam to <see cref="IXamlTypeMetadataProvider"/> — the exact shape of
/// <see cref="IXamlAttachablePropertyProvider"/>: it enumerates a type's <em>own</em> settable member names —
/// those DECLARED on the type or brought onto it via <c>AddOwner</c>, but <em>not</em> the purely-inherited
/// members it merely receives from its base types. A completion host uses it to rank suggestions: a member the
/// element itself introduces (band 2) is more relevant than one inherited from a distant base, so the host tags
/// a candidate as "own" by simple set-membership against this result (the names are aligned with
/// <see cref="IXamlTypeMetadataProvider.GetKnownMemberNames"/>). It is a SEPARATE interface rather than a member
/// on <see cref="IXamlTypeMetadataProvider"/> so it stays non-breaking on netstandard2.0 — that runtime has no
/// default interface methods (a body on an interface member is <c>CS8701</c> there), so an added interface method
/// cannot carry an empty default and would break every existing provider. Existing providers keep working
/// untouched; a provider that CAN answer opts in by implementing this. The runtime reflection provider
/// (<c>ReflectionXamlMetadata</c>) implements it over reflection + the property registry; the build-time symbol
/// provider does not (generated/AOT completion is not the host's path — the host drives the runtime reflection
/// provider). A consumer probes for the capability (<c>provider is IXamlOwnMemberProvider om</c>) and simply
/// treats every member as inherited when it is absent, mirroring the loader's <c>{x:Static}</c> probe.
/// </summary>
public interface IXamlOwnMemberProvider
{
    /// <summary>
    /// The settable member names DECLARED or <c>AddOwner</c>'d on <em>exactly</em> <paramref name="targetType"/>
    /// — never a member purely inherited from a base type. The result honors the same XAML-settable filter as
    /// <see cref="IXamlTypeMetadataProvider.GetKnownMemberNames"/> (a read-only SCALAR appears in neither; a
    /// read-only content COLLECTION appears in both), so it is a provenance subset the host can intersect with
    /// the known-member set. A type's own attached-property declaration is an own member only when it is
    /// self-usable — a CHILD-only attached property (<c>Grid.Row</c>) is not, though it stays attachable on
    /// children. Empty when none apply, or when the
    /// provider cannot map the type (e.g. a symbol backend whose <see cref="IXamlType.UnderlyingSystemType"/> is
    /// <see langword="null"/>). Registry-backed members require their owner type to have registered; forcing that
    /// registration is the provider's concern.
    /// </summary>
    string[] GetOwnMemberNames(IXamlType targetType);
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

/// <summary>
/// One attached property a host can offer on a target element, decomposed so a designer can both PRESENT it in
/// XAML syntax (<c>Owner.Property</c>, e.g. <c>Grid.Column</c>) and RESOLVE the owner's xmlns prefix.
/// <see cref="OwnerName"/> is the owner's simple (XAML) name; <see cref="OwnerNamespace"/> is its CLR namespace.
/// </summary>
/// <remarks>
/// The CLR namespace is carried deliberately rather than returning a bare <c>"Owner.Property"</c> string: it is
/// the join key back to an xmlns URI (through the xmlns→CLR-namespace map the host already holds), which the
/// host needs to emit the correct prefix when the owner is NOT under the document's default namespace — either a
/// third-party attached property, or the common case where the document binds the Cursorial URI to a non-default
/// prefix (<c>c:Grid.Column</c>). It also disambiguates two owners that share a simple name across namespaces. It
/// is expressed as plain strings (never <see cref="System.Type"/>), so a symbol backend could supply the same
/// shape without loading the runtime type.
/// </remarks>
/// <param name="OwnerName">The simple name of the type which owns the attached attachable member.</param>
/// <param name="OwnerNamespace">The CLR namespace of the type which owns the attached attachable member.</param>
/// <param name="PropertyName">The unqualified name of the attachable member.</param>
/// <param name="TargetsChildElements">
/// Whether the owner INTENDS this property to be attached on its child elements — a container reading
/// per-child layout/config (<c>Grid.Row</c>, <c>DockPanel.Dock</c>, <c>Canvas.Left</c>, <c>Ribbon.ButtonSize</c>),
/// mirroring <c>UIProperty.TargetsChildElements</c>. A completion host uses it to lift ONLY these to the
/// parent-context band when the element sits inside such a container: a broadly-hosted attached SERVICE
/// (<c>NameScope</c>, <c>ToolTipService</c>, owner <c>UIElement</c>) is attachable everywhere but is not
/// child-positioning, so it stays out of that band. <see langword="false"/> when unreported.
/// </param>
public readonly record struct XamlAttachableMember(
    string OwnerName, string OwnerNamespace, string PropertyName, bool TargetsChildElements = false)
{
    /// <summary>The XAML-presentable qualified form — <c>Owner.Property</c> (e.g. <c>Grid.Column</c>).</summary>
    public string QualifiedName => OwnerName + "." + PropertyName;
}