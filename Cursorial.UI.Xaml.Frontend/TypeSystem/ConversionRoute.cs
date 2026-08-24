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
    /// <summary>No mechanism found — a Text value on such a member is a parse-time diagnostic in both
    /// lanes (the G4 close), never a raw string reaching a typed setter at runtime.</summary>
    None = 0,

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

    /// <summary>The undetermined route (providers that predate the probe, or watch-only members).</summary>
    public static ConversionRoute Unknown => default; // Kind == None with no probe run — see XamlMember.Route docs
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
