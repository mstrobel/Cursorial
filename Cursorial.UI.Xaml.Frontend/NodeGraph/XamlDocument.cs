using System;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The immutable, resolved, folded node graph produced by stage 1 (<c>XamlLoader.Parse</c>). Owns
/// flat struct arrays with children contiguous depth-first — any subtree is
/// <c>(startIndex, SubtreeLength)</c>, which <i>is</i> a deferred-content slice (proposal §3.1).
/// Immutable after parse; thread-safe to share across <c>Load</c>s and template <c>Build</c>s
/// (matrix XD20). The internal array shape is surfaced to tests via <c>InternalsVisibleTo</c> — the
/// content is the contract, the member names are implementation freedom (matrix §0.2).
/// </summary>
public sealed class XamlDocument
{
    internal XamlDocument(
        Uri? sourceUri,
        Type? rootType,
        string? rootClassName,
        ObjectRecord[] objects,
        MemberRecord[] members,
        object?[] constants,
        string[] strings,
        ExtensionRecord[] extensions,
        MarkupExtensionNode?[] parsedExtensions,
        XamlType?[] resolvedTypes,
        XamlMember?[] resolvedMembers,
        IReadOnlyList<XamlDiagnostic> diagnostics,
        IReadOnlyDictionary<string, string> namespaces,
        XamlDesignInfo? designInfo = null)
    {
        SourceUri = sourceUri;
        _rootType = rootType;
        RootClassName = rootClassName;
        Objects = objects;
        Members = members;
        Constants = constants;
        Strings = strings;
        Extensions = extensions;
        ParsedExtensions = parsedExtensions;
        ResolvedTypes = resolvedTypes;
        ResolvedMembers = resolvedMembers;
        Diagnostics = diagnostics;
        Namespaces = namespaces;
        DesignInfo = designInfo;
    }

    private readonly Type? _rootType;

    /// <summary>The document source URI, if known.</summary>
    public Uri? SourceUri { get; }

    /// <summary>
    /// The resolved CLR type of the root element. Throws if the root type did not resolve (a
    /// <c>CUR2002</c>/<c>CUR2001</c> diagnostic was recorded under <c>CollectAll</c>).
    /// </summary>
    public Type RootType
        => _rootType ?? throw new InvalidOperationException(
            "The document root type did not resolve; inspect Diagnostics for the failure.");

    /// <summary>True when the root type resolved.</summary>
    public bool HasRootType => _rootType is not null;

    /// <summary>The <c>x:Class</c> code-behind class name on the root, or <c>null</c>.</summary>
    public string? RootClassName { get; }

    /// <summary>All diagnostics collected during the parse (empty on a clean parse).</summary>
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Design-time metadata from the root's <c>d:</c> attributes, or <c>null</c> when the document
    /// declares none. Runtime loading never reads this; designer hosts do.
    /// </summary>
    public XamlDesignInfo? DesignInfo { get; }

    // ── Internal node-graph arrays (the contract surface for tests) ────────────────────────────

    internal ObjectRecord[] Objects { get; }
    internal MemberRecord[] Members { get; }
    internal object?[] Constants { get; }
    internal string[] Strings { get; }
    internal ExtensionRecord[] Extensions { get; }
    internal MarkupExtensionNode?[] ParsedExtensions { get; }
    internal XamlType?[] ResolvedTypes { get; }
    internal XamlMember?[] ResolvedMembers { get; }

    /// <summary>
    /// The document-level xmlns prefix → namespace table captured from the ROOT element (the top-level-only
    /// policy makes it unambiguous — CUR2004 rejects a non-root declaration). The default xmlns is keyed by
    /// the empty string. The loader's namespace-aware selector resolver consults this for a <c>prefix|Type</c>
    /// selector token and a prefixed Style <c>TargetType</c>.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Namespaces { get; }

    /// <summary>The root object record (index 0), or a default when the document is empty.</summary>
    internal ref readonly ObjectRecord Root => ref Objects[0];

    /// <summary>True when the document parsed at least one object.</summary>
    internal bool HasObjects => Objects.Length > 0;
}
