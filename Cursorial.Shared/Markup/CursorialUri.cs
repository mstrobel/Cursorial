using System;

namespace Cursorial.Markup;

/// <summary>
/// URI plumbing for the <c>cursorial://</c>/<c>embedded://</c> resource scheme, where the
/// AUTHORITY names an assembly. <see cref="System.Uri"/> canonicalizes authorities to lowercase
/// (correct for DNS hosts, wrong for assembly simple names — <c>Assembly.Load</c> probes by file
/// name on case-sensitive file systems), so composition here re-applies the base URI's original
/// authority casing to the result.
/// </summary>
public static class CursorialUri
{
    public const string EmbeddedScheme = "embedded";
    public const string CursorialScheme = "cursorial";

    /// <summary>
    /// Determines whether <paramref name="uri"/> is a <c>cursorial://</c> or <c>embedded://</c> URI.
    /// </summary>
    /// <param name="uri">The URI to test.</param>
    /// <returns>
    /// <c>true</c> if the URI is a <c>cursorial://</c> or <c>embedded://</c> URI; otherwise, <c>false</c>.
    /// </returns>
    public static bool IsCursorialUri(Uri? uri) => uri?.Scheme is CursorialScheme or EmbeddedScheme;

    /// <summary>Resolves <paramref name="relative"/> against <paramref name="baseUri"/>, preserving the authority's original casing.</summary>
    public static Uri ResolveRelative(Uri baseUri, string relative)
    {
        var composed = new Uri(baseUri, relative);
        if (!composed.IsAbsoluteUri)
            return composed;

        var original = GetOriginalAuthority(baseUri);
        if (original is null || string.Equals(original, composed.Host, StringComparison.Ordinal))
            return composed;

        // Rebuild with the base's original authority; the composed path (with ../ folding done
        // by System.Uri) is case-preserved already — only the authority gets canonicalized.
        return new Uri($"{composed.Scheme}://{original}{composed.AbsolutePath}", UriKind.Absolute);
    }

    /// <summary>The authority exactly as written in the URI's original string, or null when unavailable.</summary>
    public static string? GetOriginalAuthority(Uri uri)
    {
        var s = uri.OriginalString;
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return null;
        var start = schemeEnd + 3;
        var end = s.IndexOf('/', start);
        var authority = end < 0 ? s.Substring(start) : s.Substring(start, end - start);
        return authority.Length == 0 ? null : authority;
    }
}
