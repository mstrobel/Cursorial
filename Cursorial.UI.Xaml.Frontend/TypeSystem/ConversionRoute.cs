using System;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The mechanism a member's TEXT value converts through (design doc <c>xaml-conversion-routes.md</c>
/// CR1/CR2 — the W2e route vocabulary). Computed ONCE per member by the shared <see cref="RouteProbe"/>
/// over backend-answered capability queries and stored as <see cref="XamlMember.Route"/>; both lanes
/// execute the same recorded decision, so conversion precedence cannot drift between the runtime loader
/// and the X4 generator.
/// </summary>
public enum RouteKind : byte
{
    /// <summary>The probe has not run (a provider that predates it, the symbol lane's conservative
    /// default until its converter set becomes queryable metadata, watch-only members) — consumers make
    /// NO route-based decisions and today's behavior stands.</summary>
    Unknown = 0,

    /// <summary>The probe ran and found NO mechanism — a Text value on such a member is a positioned
    /// parse-time diagnostic (the G4 close), never a raw string reaching a typed setter at runtime.</summary>
    None,

    /// <summary>A converter the provider supplied on the member (the ladder's curated rows, member/type
    /// attributes, the BCL fallback — everything <c>XamlMember.Converter</c> or the loader's runtime
    /// chain resolves ahead of the bridge).</summary>
    Converter,

    /// <summary>Free-form text accepted verbatim (string/object slots).</summary>
    RawText,

    /// <summary>A context-bound mechanism executed at load/build against live scope (Selector, the CR5
    /// parse-resolved <c>UIProperty</c> token, <c>System.Type</c> tokens).</summary>
    Contextual,

    /// <summary>The CR7 bridge: an implicit conversion operator from <see cref="ConversionRoute.SourceType"/>.</summary>
    ImplicitOp,

    /// <summary>The CR7 bridge: an explicit conversion operator from <see cref="ConversionRoute.SourceType"/>.</summary>
    ExplicitOp,

    /// <summary>The CR7 bridge: a public single-parameter constructor from <see cref="ConversionRoute.SourceType"/>.</summary>
    Constructor,

    /// <summary>The CR7 bridge: <c>static T Parse(string)</c>.</summary>
    ParseMethod,

    /// <summary>Two viable candidates of one bridge kind — a LOUD parse-time ambiguity diagnostic (the
    /// CR3 rule), never a silent pick.</summary>
    Ambiguous,
}

/// <summary>A member's recorded conversion route (immutable; see <see cref="RouteKind"/>).</summary>
public readonly struct ConversionRoute(RouteKind kind, IXamlType? sourceType = null)
{
    /// <summary>The mechanism kind.</summary>
    public RouteKind Kind { get; } = kind;

    /// <summary>The bridge route's source type S (null for non-bridge kinds).</summary>
    public IXamlType? SourceType { get; } = sourceType;

    /// <summary>The un-probed route (<see cref="RouteKind.Unknown"/> — the default).</summary>
    public static ConversionRoute Unknown => default;
}

/// <summary>
/// The shared W2e route probe (CR1/CR3): applies the pinned precedence and one-viable-per-kind rule over
/// backend-answered facts. Lives in the netstandard2.0 frontend so the DECISION cannot drift between the
/// reflection loader and the X4 generator — a provider supplies its lane's knowledge (parse converter,
/// runtime-chain resolvability, source-type convertibility) and the probe does the rest.
/// </summary>
public static class RouteProbe
{
    /// <summary>
    /// Computes a member's conversion route. <paramref name="hasParseConverter"/>: the member carries a
    /// parse-time <see cref="ITypeConverter"/>. <paramref name="hasRuntimeConverter"/>: the lane's
    /// pre-bridge runtime chain (ladder + BCL rung) resolves the value type — null means UNKNOWN (the
    /// symbol lane until its converter set is queryable metadata) and yields
    /// <see cref="RouteKind.Unknown"/> rather than a guess. <paramref name="sourceConvertible"/> answers
    /// whether a bridge candidate's source type converts from text (null ⇒ bridge kinds undecidable ⇒
    /// Unknown). <paramref name="stringType"/> is the lane's identity for <c>string</c> (the
    /// assignable-from-string check — an <c>IComparable</c>-typed member legitimately accepts raw text).
    /// </summary>
    public static ConversionRoute Compute(
        IXamlType valueType,
        bool hasParseConverter,
        bool? hasRuntimeConverter,
        Func<IXamlType, bool>? sourceConvertible,
        IXamlType? stringType)
    {
        var fullName = valueType.FullName;

        // The contextual mechanisms — resolved against live scope, never through a text converter:
        // Selector (activation-built), UIProperty (the CR5 parse-resolved token), System.Type (the
        // xmlns-resolved token), ClassSet (the loader's split-and-Add special case).
        if (fullName is "Cursorial.UI.Selector" or "Cursorial.UI.UIProperty" or "System.Type" or "Cursorial.UI.ClassSet")
            return new ConversionRoute(RouteKind.Contextual);

        if (fullName is "System.String" or "System.Object")
            return new ConversionRoute(RouteKind.RawText);

        if (hasParseConverter)
            return new ConversionRoute(RouteKind.Converter);

        if (hasRuntimeConverter is null)
            return ConversionRoute.Unknown;
        if (hasRuntimeConverter is true)
            return new ConversionRoute(RouteKind.Converter);

        // A member type ASSIGNABLE from string legitimately accepts the raw text (IComparable,
        // IEnumerable<char>, …) — the historical passthrough stays a real route, not an error.
        if (stringType is not null && valueType.IsAssignableFrom(stringType))
            return new ConversionRoute(RouteKind.RawText);

        if (sourceConvertible is null)
            return ConversionRoute.Unknown;

        // The CR7 bridge kinds in CR3 order; within a kind exactly one viable candidate, else Ambiguous.
        var candidates = valueType.GetConversionRouteCandidates();

        foreach (var kind in BridgeKindOrder)
        {
            IXamlType? viable = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.Kind != kind || !sourceConvertible(candidate.SourceType))
                    continue;
                if (viable is not null)
                    return new ConversionRoute(RouteKind.Ambiguous);
                viable = candidate.SourceType;
            }

            if (viable is not null)
                return new ConversionRoute(kind, viable);
        }

        return new ConversionRoute(RouteKind.None);
    }

    private static readonly RouteKind[] BridgeKindOrder =
        [RouteKind.ImplicitOp, RouteKind.ExplicitOp, RouteKind.Constructor, RouteKind.ParseMethod];
}

/// <summary>
/// A single bridge-route candidate a backend enumerates for <see cref="IXamlType.GetConversionRouteCandidates"/>:
/// the source parameter type of one implicit/explicit operator, single-parameter constructor, or
/// <c>static T Parse(string)</c> declared on the type.
/// </summary>
public readonly struct ConversionRouteCandidate(RouteKind kind, IXamlType sourceType)
{
    /// <summary>Which bridge kind the candidate is (ImplicitOp/ExplicitOp/Constructor/ParseMethod).</summary>
    public RouteKind Kind { get; } = kind;

    /// <summary>The candidate's source parameter type.</summary>
    public IXamlType SourceType { get; } = sourceType;
}
