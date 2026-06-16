using System;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The growable backing store the parser writes into, trimmed to the immutable <see cref="XamlDocument"/>
/// arrays at the end (proposal §3.1 — "one allocation per array at the end"). Also the diagnostic
/// sink: under <see cref="XamlDiagnosticMode.ThrowOnFirstError"/> the first error throws; under
/// <see cref="XamlDiagnosticMode.CollectAll"/> errors accumulate.
/// </summary>
internal sealed class XamlDocumentBuilder
{
    private readonly XamlDiagnosticMode _mode;
    private readonly Uri? _source;

    private readonly List<ObjectRecord> _objects = new();
    private readonly List<MemberRecord> _members = new();
    private readonly List<object?> _constants = new();
    private readonly List<string> _strings = new();
    private readonly List<ExtensionRecord> _extensions = new();
    private readonly List<MarkupExtensionNode?> _parsedExtensions = new();
    private readonly List<XamlType?> _resolvedTypes = new();
    private readonly List<XamlMember?> _resolvedMembers = new();
    private readonly List<XamlDiagnostic> _diagnostics = new();

    private readonly Dictionary<string, int> _stringInterning = new(StringComparer.Ordinal);

    // The document-level xmlns prefix → namespace table (the top-level-only policy guarantees one binding
    // per prefix). The default xmlns is stored under the empty-string prefix. The loader's namespace-aware
    // selector resolver consults this to resolve a 'prefix|Type' token / a prefixed Style TargetType.
    private readonly Dictionary<string, string> _namespaces = new(StringComparer.Ordinal);

    public XamlDocumentBuilder(XamlDiagnosticMode mode, Uri? source)
    {
        _mode = mode;
        _source = source;
    }

    public Uri? Source => _source;

    public IReadOnlyList<XamlDiagnostic> Diagnostics => _diagnostics;

    // ── Object/member array building ──────────────────────────────────────────────────────────

    public int ObjectCount => _objects.Count;
    public int MemberCount => _members.Count;

    /// <summary>Reserves an object slot, returning its index; the record is filled later via <see cref="SetObject"/>.</summary>
    public int ReserveObject()
    {
        _objects.Add(default);
        return _objects.Count - 1;
    }

    public void SetObject(int index, ObjectRecord record) => _objects[index] = record;

    public ObjectRecord GetObject(int index) => _objects[index];

    /// <summary>Records a root-element xmlns declaration (<paramref name="prefix"/> = "" for the default xmlns).</summary>
    public void AddNamespaceDeclaration(string prefix, string namespaceUri) => _namespaces[prefix] = namespaceUri;

    public int AddMember(MemberRecord record)
    {
        _members.Add(record);
        return _members.Count - 1;
    }

    // ── Value interning ──────────────────────────────────────────────────────────────────────

    public int AddConstant(object? value)
    {
        _constants.Add(value);
        return _constants.Count - 1;
    }

    public int InternString(string value)
    {
        if (_stringInterning.TryGetValue(value, out int existing))
            return existing;
        _strings.Add(value);
        int index = _strings.Count - 1;
        _stringInterning[value] = index;
        return index;
    }

    public int AddExtension(ExtensionRecord record)
    {
        _extensions.Add(record);
        return _extensions.Count - 1;
    }

    public int AddParsedExtension(MarkupExtensionNode? node)
    {
        _parsedExtensions.Add(node);
        return _parsedExtensions.Count - 1;
    }

    public int AddResolvedType(XamlType? type)
    {
        _resolvedTypes.Add(type);
        return _resolvedTypes.Count - 1;
    }

    public int AddResolvedMember(XamlMember? member)
    {
        _resolvedMembers.Add(member);
        return _resolvedMembers.Count - 1;
    }

    public string ResolvedMemberName(int memberId)
        => memberId >= 0 && memberId < _resolvedMembers.Count ? _resolvedMembers[memberId]?.Name ?? string.Empty : string.Empty;

    public string GetString(int index) => _strings[index];

    // ── Diagnostics ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a diagnostic. Under <see cref="XamlDiagnosticMode.ThrowOnFirstError"/> an error throws
    /// immediately (with the already-collected diagnostics, the new one first); under
    /// <see cref="XamlDiagnosticMode.CollectAll"/> it accumulates and the parse continues.
    /// </summary>
    public void Report(XamlDiagnostic diagnostic)
    {
        if (_mode == XamlDiagnosticMode.ThrowOnFirstError && diagnostic.Severity == XamlDiagnosticSeverity.Error)
        {
            var list = new List<XamlDiagnostic>(_diagnostics.Count + 1) { diagnostic };
            list.AddRange(_diagnostics);
            throw new XamlParseException(list);
        }
        _diagnostics.Add(diagnostic);
    }

    /// <summary>Records an error diagnostic at a position.</summary>
    public void Error(string code, string message, int line, int column)
        => Report(XamlDiagnostic.Error(code, message, _source, line, column));

    /// <summary>True when any error has been collected (CollectAll mode).</summary>
    public bool HasErrors
    {
        get
        {
            foreach (var d in _diagnostics)
                if (d.Severity == XamlDiagnosticSeverity.Error)
                    return true;
            return false;
        }
    }

    // ── Finalization ───────────────────────────────────────────────────────────────────────────

    public XamlDocument Build(Type? rootType, string? rootClassName)
        => new(
            _source,
            rootType,
            rootClassName,
            _objects.ToArray(),
            _members.ToArray(),
            _constants.ToArray(),
            _strings.ToArray(),
            _extensions.ToArray(),
            _parsedExtensions.ToArray(),
            _resolvedTypes.ToArray(),
            _resolvedMembers.ToArray(),
            _diagnostics.ToArray(),
            _namespaces);
}
