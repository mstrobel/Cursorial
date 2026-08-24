using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

// ReSharper disable UnusedParameter.Local
// ReSharper disable UnusedMember.Local
// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The stage-1 parser: a single <see cref="XmlReader"/> pass that produces the immutable
/// <see cref="XamlDocument"/> node graph. Resolves types/members through the injected
/// <see cref="IXamlTypeMetadataProvider"/>, parses + folds markup extensions, folds context-free
/// literals, and emits <c>CUR1xxx</c>/<c>CUR2xxx</c> diagnostics with 1-based line+column on every
/// node. Pure and thread-safe (a fresh parser per parse); no instantiation, no <c>Cursorial.UI</c>
/// dependency.
/// </summary>
internal sealed class XamlParser
{
    private readonly XmlReader _reader;
    private readonly IXmlLineInfo _lineInfo;
    private readonly XamlParseOptions _options;
    private readonly XamlDocumentBuilder _builder;

    private string? _rootClassName;
    private Type? _rootType;

    // Markup compatibility (mc:) + design-time (d:) state. Ignorable namespaces are URIs (the
    // root's mc:Ignorable prefixes, resolved at declaration scope); design values are captured
    // from the ROOT element only and surface on XamlDocument.DesignInfo.
    private HashSet<string>? _ignorableNamespaces;
    private bool _hasDesignInfo;
    private int? _designWidth;
    private int? _designHeight;
    private XamlType? _designDataContextType;
    private XamlDocument? _designDataContextContent;
    private string? _designDataContextStaticPath;

    // The lexical Style.TargetType stack: a Style pushes its resolved target type before its body is
    // walked so an enclosed Setter can resolve Property/Value against it (matrix X64/X66).
    private readonly Stack<XamlType?> _styleTargetStack = new();

    // The enclosing-object-element AMBIENT stack (W2 CR5, barrier semantics from the W2b audit): every
    // object element pushes its resolved type around its body walk, so a child's end-of-object
    // UIProperty-token resolution can use the NEAREST ENCLOSING element as the ambient target — the
    // <Border><Transition.Transitions><DoubleTransition Property="Opacity"> case (Border is the ambient).
    // BARRIER frames stop the walk where the lexical tree stops being the runtime tree: a deferred
    // template boundary (the body attaches to a templated parent, not the document ancestors), a
    // resource-dictionary boundary (an entry attaches wherever it is consumed — resolution against the
    // RD HOST would make a shared resource parse host-dependently), and Style (a selector-only style has
    // no lexical target; its TargetType rides _styleTargetStack, the walk's FALLBACK).
    private readonly record struct AmbientFrame(XamlType? Type, bool IsBarrier);

    private readonly Stack<AmbientFrame> _elementTypeStack = new();

    // Fragment mode (a design-data subtree re-parsed through XmlReader.ReadSubtree): the subtree
    // reader SYNTHESIZES in-scope xmlns declarations lazily — a prefix first used on a NESTED
    // element gets its declaration emitted there, which the top-level-only policy (CUR2004) would
    // reject even though no user wrote it. Fragment parsing records such declarations instead.
    private readonly bool _isFragment;

    private XamlParser(XmlReader reader, XamlParseOptions options, Uri? source, bool isFragment = false)
    {
        _reader = reader;
        _lineInfo = reader as IXmlLineInfo ?? throw new InvalidOperationException("XmlReader must implement IXmlLineInfo.");
        _options = options;
        _isFragment = isFragment;
        _builder = new XamlDocumentBuilder(options.DiagnosticMode, source);
    }

    /// <summary>Parses XAML text into a document.</summary>
    public static XamlDocument Parse(string xml, XamlParseOptions options, Uri? source)
    {
        using var stringReader = new StringReader(xml);
        return Parse(stringReader, options, source);
    }

    /// <summary>Parses XAML from a stream into a document.</summary>
    public static XamlDocument Parse(Stream xml, XamlParseOptions options, Uri? source)
    {
        using var streamReader = new StreamReader(xml);
        return Parse(streamReader, options, source);
    }

    /// <summary>Parses XAML from a text reader into a document (the reader is consumed, not materialized).</summary>
    public static XamlDocument Parse(TextReader textReader, XamlParseOptions options, Uri? source)
    {
        var settings = new XmlReaderSettings
                       {
                           DtdProcessing = DtdProcessing.Prohibit, // matrix X39/X40 — no DTDs, no external entities
                           IgnoreComments = true,                  // matrix X37
                           IgnoreProcessingInstructions = true,    // matrix X38
                           IgnoreWhitespace = false,               // we own whitespace handling (XD19)
                           CloseInput = false,
                       };

        using var reader = XmlReader.Create(textReader, settings);
        var parser = new XamlParser(reader, options, source);
        return parser.Run();
    }

    private XamlDocument Run()
    {
        try
        {
            // advance to the root element
            if (!MoveToFirstElement())
            {
                // empty document — no root
                return _builder.Build(null, null);
            }

            ParseElement(parentInDeferred: false, parentInResourceDictionary: false, isRoot: true);
        }
        catch (XmlException xe)
        {
            // Malformed XML / prohibited DTD. The XmlReader's DtdProcessing=Prohibit surfaces a DTD as
            // an XmlException; distinguish it by message for the CUR1001 vs CUR1002 banding.
            int line = xe.LineNumber > 0 ? xe.LineNumber : 1;
            int column = xe.LinePosition > 0 ? xe.LinePosition : 1;

            bool isDtd = xe.Message.IndexOf("DTD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         xe.Message.IndexOf("DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         xe.Message.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0;

            // An undeclared xmlns prefix surfaces as an XmlException ("namespace prefix … is not
            // defined"); re-band it to the resolution diagnostic CUR2003 (matrix X30).
            bool isUndeclaredPrefix = xe.Message.IndexOf("prefix", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                      (xe.Message.IndexOf("undeclared", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       (xe.Message.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        xe.Message.IndexOf("defin", StringComparison.OrdinalIgnoreCase) >= 0));

            string code;
            string message;

            if (isDtd)
            {
                code = XamlDiagnosticCodes.DtdProhibited;
                message = "DTDs and external entities are prohibited (DtdProcessing = Prohibit).";
            }
            else if (isUndeclaredPrefix)
            {
                code = XamlDiagnosticCodes.UndeclaredPrefix;
                message = $"Undeclared xmlns prefix: {xe.Message}";
            }
            else
            {
                code = XamlDiagnosticCodes.MalformedXml;
                message = $"Malformed XML: {xe.Message}";
            }

            _builder.Report(XamlDiagnostic.Error(code, message, _builder.Source, line, column));
        }

        return _builder.Build(
            _rootType,
            _rootClassName,
            _hasDesignInfo
                ? new XamlDesignInfo(_designWidth, _designHeight, _designDataContextType, _designDataContextContent, _designDataContextStaticPath)
                : null);
    }

    /// <summary>
    /// Captures a ROOT-level <c>&lt;d:Owner.DataContext&gt;</c> property element: its single object child
    /// parses as a DETACHED fragment document (the subtree reader keeps the ancestor xmlns scope), so
    /// design data never enters the runtime graph, the loader, or the lowering generator — a designer
    /// host materializes the fragment with the ordinary <c>XamlLoader.Load(document)</c> path. All
    /// failure modes are soft warnings (design data must never break a parse), matching
    /// <see cref="CaptureDesignAttribute"/>.
    /// </summary>
    private void CaptureDesignDataContextElement(int line, int column)
    {
        if (_reader.IsEmptyElement)
        {
            _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                             "d:DataContext (element form) needs a single object element child; the empty element is ignored.",
                             line, column);
            return;
        }

        XamlDocument? fragment = null;
        var sawChild = false;
        int propertyDepth = _reader.Depth;
        while (_reader.Read() && _reader.Depth > propertyDepth)
        {
            if (_reader.NodeType != XmlNodeType.Element)
                continue;

            if (sawChild)
            {
                _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                                 "d:DataContext (element form) takes a single object element; additional elements are ignored.",
                                 _lineInfo.LineNumber, CurrentElementColumn());
                SkipCurrentSubtree();
                continue;
            }

            sawChild = true;
            try
            {
                // The fragment ALWAYS parses in CollectAll mode, whatever the outer parse uses: an
                // unresolvable design-time type under ThrowOnFirstError would otherwise throw
                // XamlParseException THROUGH the main parse — design data must never break a document.
                // The fragment's own Diagnostics carry any misses for the designer host to surface.
                var fragmentOptions = new XamlParseOptions
                {
                    MetadataProvider = _options.MetadataProvider,
                    DiagnosticMode = XamlDiagnosticMode.CollectAll,
                    FoldConstants = _options.FoldConstants,
                    ConverterCulture = _options.ConverterCulture,
                };
                using var subtree = _reader.ReadSubtree();
                fragment = new XamlParser(subtree, fragmentOptions, _builder.Source, isFragment: true).Run();
            }
            catch (Exception)
            {
                fragment = null; // whatever went wrong, the design lane degrades to a warning below
            }
        }

        if (fragment is { HasObjects: true })
        {
            if (_designDataContextType is not null || _designDataContextStaticPath is not null)
                _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                                 "Both the d:DataContext attribute and element form are declared; the element form wins.",
                                 line, column);

            _hasDesignInfo = true;
            _designDataContextContent = fragment;
        }
        else
        {
            _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                             "d:DataContext (element form) did not yield a loadable object; the design-time data context is ignored.",
                             line, column);
        }
    }

    /// <summary>
    /// Registers the root's <c>mc:Ignorable</c> prefixes (space-delimited) as ignorable namespace
    /// URIs, resolved in the declaring scope. An entry with no xmlns declaration is a warning —
    /// the document still parses.
    /// </summary>
    private void RegisterIgnorablePrefixes(string prefixList, int line, int column)
    {
        foreach (var prefix in prefixList.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var uri = _reader.LookupNamespace(prefix);
            if (uri is null or { Length: 0 })
            {
                _builder.Warning(XamlDiagnosticCodes.IgnorablePrefixNotDeclared,
                                 $"mc:Ignorable lists prefix '{prefix}', which has no xmlns declaration in scope.",
                                 line, column);
                continue;
            }

            (_ignorableNamespaces ??= new HashSet<string>(StringComparer.Ordinal)).Add(uri);
        }
    }

    /// <summary>
    /// Captures a ROOT-element design-time attribute into the document's
    /// <see cref="XamlDesignInfo"/>. Unknown <c>d:*</c> names are skipped silently — the
    /// namespace is ignorable by definition, so unrecognized entries are forward-compatible.
    /// </summary>
    private void CaptureDesignAttribute(string localName, string value, int line, int valueColumn)
    {
        switch (localName)
        {
            case "DesignWidth":
            case "DesignHeight":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cells) && cells > 0)
                {
                    _hasDesignInfo = true;
                    if (localName == "DesignWidth")
                        _designWidth = cells;
                    else
                        _designHeight = cells;
                }
                else
                {
                    _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                                     $"d:{localName} must be a positive integer cell count; found '{value}'. The attribute is ignored.",
                                     line, valueColumn);
                }

                break;

            case "DataContext":
            {
                // The {x:Static} INSTANCE form (d:DataContext="{x:Static vm:MyViewModel.DesignInstance}"):
                // captured as the static member PATH — the frontend cannot resolve statics; the designer
                // host resolves it at materialize time. Any other extension is a soft warning.
                if (MarkupExtensionParser.LooksLikeExtension(value))
                {
                    var extension = MarkupExtensionParser.Parse(value, _builder.Source, line, valueColumn);
                    if (extension.Name is "x:Static" or "Static" &&
                        extension.PositionalArguments.Count == 1 &&
                        extension.PositionalArguments[0] is { IsNested: false, Text: { Length: > 0 } staticPath })
                    {
                        _hasDesignInfo = true;
                        _designDataContextStaticPath = staticPath;
                    }
                    else
                    {
                        _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                                         $"d:DataContext accepts a type name or {{x:Static Member.Path}}; found '{value}'. The attribute is ignored.",
                                         line, valueColumn);
                    }
                    break;
                }

                // A plain (optionally prefix-qualified) type name, resolved in the live root
                // scope. A miss is a soft warning: the document must still parse and load
                // everywhere the design-time type does not exist.
                var resolution = ResolveQualifiedType(value, appendExtensionSuffix: false, line, valueColumn, report: false);
                if (resolution.IsResolved)
                {
                    _hasDesignInfo = true;
                    _designDataContextType = resolution.Type;
                }
                else
                {
                    _builder.Warning(XamlDiagnosticCodes.DesignValueInvalid,
                                     $"d:DataContext type '{value}' did not resolve; the design-time data context is ignored.",
                                     line, valueColumn);
                }

                break;
            }
        }
    }

    /// <summary>
    /// Drains the current element's subtree so the enclosing body walk advances exactly one
    /// sibling. Do NOT use <see cref="XmlReader.Skip"/> — it advances PAST the subtree's
    /// EndElement, so the caller's next <c>Read()</c> would over-advance and drop the following
    /// sibling (the <c>ParseArrayElement</c> lesson).
    /// </summary>
    private void SkipCurrentSubtree()
    {
        if (_reader.IsEmptyElement)
            return;

        int subtreeDepth = _reader.Depth;
        while (_reader.Read() &&
               !(_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == subtreeDepth))
        {
        }
    }

    private bool MoveToFirstElement()
    {
        while (_reader.Read())
        {
            if (_reader.NodeType == XmlNodeType.Element)
                return true;
        }

        return false;
    }

    private int CurrentLineInfo() => LineInfo.Pack(_lineInfo.LineNumber, _lineInfo.LinePosition);

    /// <summary>The current element's 1-based column adjusted to point at the '&lt;' (matrix X10).</summary>
    private int CurrentElementColumn() => _lineInfo.LinePosition > 1 ? _lineInfo.LinePosition - 1 : _lineInfo.LinePosition;

    // ── Element parsing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the element the reader is positioned on (NodeType == Element) into an
    /// <see cref="ObjectRecord"/>, recursing into children. Returns the object's index.
    /// </summary>
    private int ParseElement(bool parentInDeferred, bool parentInResourceDictionary, bool isRoot)
    {
        int objectIndex = _builder.ReserveObject();
        // IXmlLineInfo reports an element's LinePosition at the first char of the local name (one past
        // the '<'); the matrix positions an element at its '<' (X10), so back the column up by one.
        int reportLine = _lineInfo.LineNumber;
        int reportColumn = _lineInfo.LinePosition > 1 ? _lineInfo.LinePosition - 1 : _lineInfo.LinePosition;
        int lineInfo = LineInfo.Pack(reportLine, reportColumn);

        string localName = _reader.LocalName;
        string ns = _reader.NamespaceURI;
        bool isEmpty = _reader.IsEmptyElement;

        // The XAML2009 <x:Array Type="T"> intrinsic element is not a resolvable type — it builds a T[] from
        // its item children (XD27). Recognize it before normal type resolution.
        if (XmlnsNamespaces.IsIntrinsics(ns) && string.Equals(localName, "Array", StringComparison.Ordinal))
            return ParseArrayElement(objectIndex, lineInfo, reportLine, reportColumn, isEmpty,
                                     parentInDeferred, parentInResourceDictionary, isRoot);

        // A built-in markup extension in ELEMENT form (<DynamicResource ResourceKey="X"/>, <x:Null/>, …):
        // build the same extension value the curly form would, wrapped in a synthetic object so it flows
        // into ANY VALUE position (dictionary entry — resource aliasing, property-element value, collection
        // item, content). The loader attaches it to the target member, or produces its value as a dict/
        // collection entry. Recognized before type resolution: a bare <DynamicResource> is not a resolvable
        // CLR type. NOT at the document root — a root <Binding/>/<TemplateBinding/> is the bare TYPE (X014),
        // there being no value position to provide into.
        if (!isRoot && BuiltInExtensionElementName(ns, localName) is { } extensionName)
            return ParseExtensionElement(objectIndex, extensionName, parentInDeferred, lineInfo, reportLine, reportColumn);

        // A CUSTOM markup extension in element form (<local:Foo/> where Foo — or its "Extension"-suffixed
        // shorthand FooExtension — derives from MarkupExtension): provide its value, not a bare object.
        // Resolved QUIETLY (exact name first, so a non-extension type of that name — an Icon CONTROL beside
        // IconExtension — stays an object; then the suffix shorthand) so <Foo> doesn't spuriously report Foo
        // as a missing type when only FooExtension exists.
        if (!isRoot)
        {
            var exact = ResolveTypeQuiet(ns, localName);
            bool isCustomExtension = exact is { IsResolved: true, Type.IsMarkupExtension: true }
                || (!exact.IsResolved && ResolveTypeQuiet(ns, localName + "Extension") is { IsResolved: true, Type.IsMarkupExtension: true });
            if (isCustomExtension)
                return ParseExtensionElement(objectIndex,
                    _reader.Prefix.Length > 0 ? _reader.Prefix + ":" + localName : localName,
                    parentInDeferred, lineInfo, reportLine, reportColumn);
        }

        // Resolve the element type. W3: an x:TypeArguments attribute is PEEKED here — position-neutral
        // via GetAttribute — because resolution precedes attribute parsing: the element resolves CLOSED
        // (<AnimationTrack x:TypeArguments="x:Double"> is AnimationTrack<double> from the first touch;
        // members substitute, activation constructs the closed type). The in-attributes directive case
        // is then a consumed no-op.
        var resolution = _reader.GetAttribute("TypeArguments", XmlnsNamespaces.Intrinsics) is { } typeArgsText
                             ? ResolveClosedElementType(ns, localName, typeArgsText, reportLine, reportColumn)
                             : ResolveType(ns, localName, reportLine, reportColumn);
        int typeId;
        XamlType? type = null;

        if (resolution.IsResolved)
        {
            type = resolution.Type;
            typeId = _builder.AddResolvedType(type);

            if (isRoot && type is not null)
                _rootType = type.ClrType.UnderlyingSystemType;
        }
        else
        {
            typeId = _builder.AddResolvedType(null);
        }

        bool isResourceDictionary = parentInResourceDictionary ||
                                    string.Equals(localName, "ResourceDictionary", StringComparison.Ordinal);

        var flags = ObjectFlags.None;
        if (isRoot) flags |= ObjectFlags.IsRoot;
        if (parentInDeferred) flags |= ObjectFlags.InDeferredContent;
        if (isResourceDictionary) flags |= ObjectFlags.InResourceDictionary;
        if (type?.RequiresInitialize == true) flags |= ObjectFlags.NeedsBeginInit;

        var members = new List<MemberRecord>();
        bool hasName = false, hasKey = false;

        // Parse attributes (members + directives; xmlns declarations are captured on the root and rejected
        // elsewhere — the top-level-only policy, CUR2004).
        ParseAttributes(type,
                        localName,
                        ns,
                        members,
                        parentInDeferred,
                        isRoot,
                        ref hasName,
                        ref hasKey,
                        reportLine,
                        reportColumn);

        if (hasName) flags |= ObjectFlags.HasName;
        if (hasKey) flags |= ObjectFlags.HasKey;

        int subtreeStart = objectIndex;

        // A Style pushes its resolved TargetType so enclosed Setters resolve against it (X64/X66).
        bool isStyle = string.Equals(localName, "Style", StringComparison.Ordinal);
        bool pushedStyleTarget = false;

        if (isStyle)
        {
            _styleTargetStack.Push(ResolveStyleTargetType(members));
            pushedStyleTarget = true;
        }

        if (!isEmpty)
        {
            // If the type's content property is ITemplateContent-typed, its implicit child content is a
            // deferred slice — children parse under the deferred flag (events inside are CUR2301, X152).
            // A deferred body has its OWN template namescope, so the enclosing resource-dictionary flag
            // does NOT propagate into it (x:Name inside a template body is a part name, not CUR2304).
            bool contentDefers = ContentPropertyDefers(type);

            // The CR5 ambient: children see THIS element as nearest-enclosing. A DEFERRED body (a
            // template) pushes a BARRIER instead — the body's runtime ancestors are the templated
            // parent's, not the document's (the W2b-audit template-shadow finding) — and shadows the
            // style-target FALLBACK with the template's OWN TargetType (or null), so a Style's
            // TargetType never leaks into its template's parts.
            _elementTypeStack.Push(contentDefers ? new AmbientFrame(null, IsBarrier: true) : new AmbientFrame(type, IsBarrier: false));
            if (contentDefers)
                _styleTargetStack.Push(ResolveStyleTargetType(members));
            try
            {
                ParseElementBody(type,
                                 localName,
                                 members,
                                 inDeferred: parentInDeferred || contentDefers,
                                 inResourceDictionary: isResourceDictionary && !contentDefers,
                                 ownerObjectIndex: objectIndex,
                                 isRoot: isRoot);
            }
            finally
            {
                _elementTypeStack.Pop();
                if (contentDefers)
                    _styleTargetStack.Pop();
            }
        }

        if (pushedStyleTarget)
            _styleTargetStack.Pop();

        // x:Name inside a resource dictionary has no namescope (matrix X104/CUR2304).
        if (hasName && isResourceDictionary)
        {
            _builder.Error(XamlDiagnosticCodes.NameInResourceDictionary,
                           "x:Name is not allowed inside a resource dictionary (no namescope).",
                           reportLine,
                           reportColumn);
        }

        // End-of-object: resolve any Setter property against the lexical TargetType (X64/X66); every
        // OTHER element resolves its UIProperty-typed token members through the general CR5 pass (Setter
        // keeps its bespoke path — Value folding included — and its Property rewrite makes the record's
        // ValueType the TARGET member's type, so the general pass never double-touches a resolved Setter).
        if (string.Equals(localName, "Setter", StringComparison.Ordinal))
            ResolveSetter(members, reportLine, reportColumn);
        else
            ResolveUIPropertyTokenMembers(members);

        // Commit members to the document's flat array.
        int memberStart = _builder.MemberCount;

        foreach (var m in members)
            _builder.AddMember(m);

        int subtreeLength = _builder.ObjectCount - subtreeStart;
        var record = new ObjectRecord(typeId, memberStart, (ushort) members.Count, flags, subtreeLength, lineInfo);

        _builder.SetObject(objectIndex, record);

        return objectIndex;
    }

    /// <summary>
    /// Parses an <c>&lt;x:Array Type="T"&gt;</c> intrinsic element (XAML2009, XD27): the unqualified
    /// <c>Type</c> attribute names the element type T (prefix-bound from the live reader scope, like
    /// <c>{x:Type}</c>); each child element is an array item; the object is flagged <see cref="ObjectFlags.IsArray"/>
    /// with <c>T</c> as its <see cref="ObjectRecord.TypeId"/>. The loader/generator build a <c>T[]</c>. A missing
    /// <c>Type</c> is <c>CUR1204</c>; <c>x:Key</c>/<c>x:Name</c> are honored (an array is a valid keyed resource /
    /// named element); any other attribute is <c>CUR2102</c>.
    /// </summary>
    private int ParseArrayElement(
        int objectIndex, int lineInfo, int reportLine, int reportColumn, bool isEmpty,
        bool parentInDeferred, bool parentInResourceDictionary, bool isRoot)
    {
        var members = new List<MemberRecord>();
        XamlType? elementType = null;
        bool hasName = false, hasKey = false, sawType = false;

        if (_reader.MoveToFirstAttribute())
        {
            do
            {
                string attrNs = _reader.NamespaceURI;
                string attrLocal = _reader.LocalName;
                string attrPrefix = _reader.Prefix;
                string value = _reader.Value;
                int attrLine = _lineInfo.LineNumber;
                int attrColumn = _lineInfo.LinePosition;
                int attrLineInfo = LineInfo.Pack(attrLine, attrColumn);

                if (attrPrefix == "xmlns" || (attrPrefix.Length == 0 && attrLocal == "xmlns"))
                {
                    if (_isFragment)
                        _builder.AddNamespaceDeclaration(attrPrefix == "xmlns" ? attrLocal : string.Empty, value);
                    else if (!isRoot)
                        _builder.Error(XamlDiagnosticCodes.NamespaceNotOnRoot,
                                       "xmlns declarations are only allowed on the root element.", attrLine, attrColumn);
                    continue;
                }

                if (string.Equals(attrPrefix, "xml", StringComparison.Ordinal))
                    continue;

                // x:Key / x:Name directives — an array is a valid keyed resource / named element. The
                // ARRAY path never runs the x:TypeArguments pre-scan (audit): the directive must stay a
                // positioned CUR1202 here, not the consumed no-op the element path earned.
                if (XmlnsNamespaces.IsIntrinsics(attrNs))
                {
                    HandleIntrinsicDirective(attrLocal, value, members, attrLineInfo, attrLine, attrColumn, ref hasName, ref hasKey, typeArgumentsPreScanned: false);
                    continue;
                }

                // The unqualified Type attribute names the element type (prefix-bound from the reader scope).
                if (attrNs.Length == 0 && string.Equals(attrLocal, "Type", StringComparison.Ordinal))
                {
                    sawType = true;
                    var res = ResolveQualifiedType(value, appendExtensionSuffix: false, attrLine, attrColumn, report: true);
                    if (res.IsResolved)
                        elementType = res.Type;
                    continue;
                }

                _builder.Error(XamlDiagnosticCodes.MemberNotFound,
                               $"x:Array accepts only the 'Type' attribute (plus x:Key/x:Name); '{attrLocal}' is not valid.",
                               attrLine, attrColumn);
            }
            while (_reader.MoveToNextAttribute());

            _reader.MoveToElement();
        }

        if (!sawType)
            _builder.Error(XamlDiagnosticCodes.ArrayMissingType,
                           "x:Array requires a Type attribute (e.g. <x:Array Type=\"x:String\">).", reportLine, reportColumn);

        // TypeId carries the ELEMENT type T (null when unresolved — the loader reports the miss).
        int typeId = _builder.AddResolvedType(elementType);

        var flags = ObjectFlags.IsArray;
        if (isRoot) flags |= ObjectFlags.IsRoot;
        if (parentInDeferred) flags |= ObjectFlags.InDeferredContent;
        if (parentInResourceDictionary) flags |= ObjectFlags.InResourceDictionary;
        if (hasName) flags |= ObjectFlags.HasName;
        if (hasKey) flags |= ObjectFlags.HasKey;

        int subtreeStart = objectIndex;

        if (!isEmpty)
        {
            var children = new List<int>();

            while (_reader.Read())
            {
                switch (_reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (_reader.LocalName.IndexOf('.') > 0)
                        {
                            _builder.Error(XamlDiagnosticCodes.MemberNotFound,
                                           "x:Array does not accept property elements; its children are array items.",
                                           _lineInfo.LineNumber, CurrentElementColumn());

                            // Do NOT use _reader.Skip(): it advances PAST this element's EndElement, so the loop's
                            // next Read() would over-advance — dropping the following sibling item, or (for a trailing
                            // property element) skipping the array's own </x:Array> and swallowing the array's siblings
                            // into it. Drain to this element's OWN EndElement so the loop's Read() advances exactly once.
                            if (!_reader.IsEmptyElement)
                            {
                                int propDepth = _reader.Depth;
                                while (_reader.Read() &&
                                       !(_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == propDepth))
                                { }
                            }

                            continue;
                        }

                        children.Add(ParseElement(parentInDeferred, parentInResourceDictionary, isRoot: false));
                        continue;

                    case XmlNodeType.EndElement:
                        goto done;
                }
            }

        done:
            if (children.Count > 0)
                members.Add(new MemberRecord(-1, XamlValueKind.Items, children[0], children.Count, lineInfo));
        }

        if (hasName && parentInResourceDictionary)
            _builder.Error(XamlDiagnosticCodes.NameInResourceDictionary,
                           "x:Name is not allowed inside a resource dictionary (no namescope).", reportLine, reportColumn);

        int memberStart = _builder.MemberCount;
        foreach (var m in members)
            _builder.AddMember(m);

        int subtreeLength = _builder.ObjectCount - subtreeStart;
        _builder.SetObject(objectIndex,
            new ObjectRecord(typeId, memberStart, (ushort) members.Count, flags, subtreeLength, lineInfo));

        return objectIndex;
    }

    /// <summary>True when the type's content property is ITemplateContent-typed (its children defer).</summary>
    private bool ContentPropertyDefers(XamlType? type)
    {
        if (type?.ContentProperty is not {} contentName)
            return false;

        return type.TryGetMember(contentName)?.IsDeferredContent == true;
    }

    // ── Attribute parsing ────────────────────────────────────────────────────────────────────────

    private void ParseAttributes(
        XamlType? type,
        string ownerLocalName,
        string elementNamespace,
        List<MemberRecord> members,
        bool inDeferred,
        bool isRoot,
        ref bool hasName,
        ref bool hasKey,
        int elementLine,
        int elementColumn)
    {
        if (_reader.AttributeCount == 0)
            return;

        // INDEXED attribute iteration throughout (the d:DataContext element-form fix): a ReadSubtree
        // reader's MoveToFirstAttribute/MoveToNextAttribute enumeration silently truncates after ONE
        // attribute on its SECOND pass — the exhaust-then-restart pre-scan below turned every fragment
        // root's member list into "first attribute only" (the designer's <d:DataContext> instance
        // declarations lost all but their first property). MoveToAttribute(i) is table-based and immune.
        if (isRoot)
        {
            // Pre-scan the root tag for mc:Ignorable so ignorability is order-independent within
            // the tag (a d:-style tool attribute may textually precede the mc:Ignorable that
            // blesses it). Costs one extra pass over the root's attributes only.
            for (int attrIndex = 0; attrIndex < _reader.AttributeCount; attrIndex++)
            {
                _reader.MoveToAttribute(attrIndex);
                if (XmlnsNamespaces.IsMarkupCompatibility(_reader.NamespaceURI) &&
                    string.Equals(_reader.LocalName, "Ignorable", StringComparison.Ordinal))
                {
                    RegisterIgnorablePrefixes(_reader.Value, _lineInfo.LineNumber, _lineInfo.LinePosition);
                }
            }
        }

        for (int attrIndex = 0; attrIndex < _reader.AttributeCount; attrIndex++)
        {
            _reader.MoveToAttribute(attrIndex);
            string attrNs = _reader.NamespaceURI;
            string attrLocal = _reader.LocalName;
            string attrPrefix = _reader.Prefix;
            string value = _reader.Value;
            int attrLine = _lineInfo.LineNumber;
            int attrColumn = _lineInfo.LinePosition;
            int attrLineInfo = LineInfo.Pack(attrLine, attrColumn);

            // A markup-extension value's '{' sits past the on-wire attribute name + '="' (the reader
            // reports an attribute's LinePosition at the first char of its name). For the single-line
            // common case this lands an extension-grammar diagnostic on the '{' (matrix XD1 precision,
            // P6 review P2-2); a multi-line value still lands on the attribute's line.
            int valueColumn = attrColumn + _reader.Name.Length + 2;

            // xmlns declarations: capture them on the ROOT element into the document prefix table (the
            // loader's namespace-aware selector resolver consults it for 'prefix|Type' tokens + prefixed
            // TargetTypes); a declaration on any non-root element is CUR2004 — the top-level-only policy
            // (Avalonia parity) that keeps the table unambiguous.
            if (attrPrefix == "xmlns" || (attrPrefix.Length == 0 && attrLocal == "xmlns"))
            {
                if (isRoot || _isFragment)
                {
                    // Fragment mode records non-root declarations too: the subtree reader synthesizes
                    // in-scope declarations lazily onto the first element that USES a prefix.
                    _builder.AddNamespaceDeclaration(attrPrefix == "xmlns" ? attrLocal : string.Empty, value);
                }
                else
                {
                    _builder.Error(XamlDiagnosticCodes.NamespaceNotOnRoot,
                                   $"xmlns declarations are only allowed on the root element; '{_reader.Name}' is declared on " +
                                   $"'{ownerLocalName}'. Move all namespace declarations to the document root.", attrLine, attrColumn);
                }

                continue;
            }

            // xml:space etc. are handled in the body walk; skip the attribute here.
            if (string.Equals(attrPrefix, "xml", StringComparison.Ordinal))
                continue;

            // Markup-compatibility (mc:) attributes are protocol, not members. mc:Ignorable was
            // pre-scanned on the root; anything else in the namespace is skipped.
            if (XmlnsNamespaces.IsMarkupCompatibility(attrNs))
                continue;

            // Design-time (d:) attributes never reach the runtime member pipeline. The ROOT's
            // DesignWidth / DesignHeight / DataContext are captured for designer hosts; unknown
            // d:* names and non-root placements are skipped (ignorable by definition).
            if (XmlnsNamespaces.IsDesignTime(attrNs))
            {
                if (isRoot)
                    CaptureDesignAttribute(attrLocal, value, attrLine, valueColumn);
                continue;
            }

            // Attributes in any other namespace the root marked mc:Ignorable are skipped wholesale.
            if (_ignorableNamespaces is { } ignorable && ignorable.Contains(attrNs))
                continue;

            // x: intrinsic directives.
            if (XmlnsNamespaces.IsIntrinsics(attrNs))
            {
                HandleIntrinsicDirective(attrLocal, value, members, attrLineInfo, attrLine, attrColumn, ref hasName, ref hasKey, typeArgumentsPreScanned: true);
                continue;
            }

            // An undeclared prefix that the reader didn't resolve to a namespace.
            if (attrPrefix.Length > 0 && attrNs.Length == 0)
            {
                _builder.Error(XamlDiagnosticCodes.UndeclaredPrefix,
                               $"Undeclared xmlns prefix '{attrPrefix}'.",
                               attrLine,
                               attrColumn);

                continue;
            }

            // Attached property: Owner.Member (a dotted attribute local name, or a prefixed-owner form).
            // An UNPREFIXED dotted attribute resolves its owner in the document's DEFAULT xmlns (XAML's
            // attached-property rule): an unprefixed XML attribute carries no namespace, so the reader reports an
            // empty NamespaceURI (matrix X75), but XAML binds the unprefixed owner to the in-scope default xmlns —
            // NOT to the owning ELEMENT's namespace. So `DockPanel.Dock` on a `prefix:Element` still resolves
            // DockPanel in the default UI namespace, not the element's prefix. (Falls back to the element namespace
            // only when no default xmlns is declared.)
            int dot = attrLocal.IndexOf('.');

            if (dot > 0)
            {
                string ownerNs = attrPrefix.Length == 0 && attrNs.Length == 0
                    ? (_reader.LookupNamespace(string.Empty) is { Length: > 0 } defaultNs ? defaultNs : elementNamespace)
                    : attrNs;

                HandleAttachedAttribute(attrLocal,
                                        dot,
                                        value,
                                        ownerNs,
                                        members,
                                        inDeferred,
                                        attrLineInfo,
                                        attrLine,
                                        attrColumn,
                                        valueColumn);

                continue;
            }

            // A plain member on the owner type.
            HandleMemberAttribute(type,
                                  ownerLocalName,
                                  memberName: attrLocal,
                                  value,
                                  members,
                                  inDeferred,
                                  attrLineInfo,
                                  attrLine,
                                  attrColumn,
                                  valueColumn);
        }

        _reader.MoveToElement();
    }

    private void HandleIntrinsicDirective(
        string local,
        string value,
        List<MemberRecord> members,
        int lineInfo,
        int line,
        int column,
        ref bool hasName,
        ref bool hasKey,
        bool typeArgumentsPreScanned)
    {
        switch (local)
        {
            case "Class":
                _rootClassName = value;
                members.Add(DirectiveMember(XamlDirectiveKind.Class, value, lineInfo));
                break;

            case "Name":
                hasName = true;
                members.Add(DirectiveMember(XamlDirectiveKind.Name, value, lineInfo));
                break;

            case "Key":
                hasKey = true;
                members.Add(DirectiveMember(XamlDirectiveKind.Key, value, lineInfo));
                break;

            case "DataType":
                // recorded now; build-time path validation is the X4 generator's (matrix X184)
                members.Add(DirectiveMember(XamlDirectiveKind.DataType, value, lineInfo));
                break;

            case "TypeArguments":
                // Consumed by the ParseElement pre-scan (the element resolved CLOSED before attributes
                // parsed) — nothing to record; failures were reported there, positioned (W3). Paths that
                // never ran the pre-scan (x:Array) keep the CUR1202 rejection (audit).
                if (!typeArgumentsPreScanned)
                {
                    _builder.Error(XamlDiagnosticCodes.UnsupportedTypeArguments,
                                   "x:TypeArguments is not supported in this position.",
                                   line,
                                   column);
                }
                break;

            case "Reference":
            case "Array":
            case "FieldModifier":
            case "Shared":
            case "Uid":
                _builder.Error(XamlDiagnosticCodes.UnsupportedIntrinsic,
                               $"x:{local} is unsupported in v1.",
                               line,
                               column);

                break;

            default:
                _builder.Error(XamlDiagnosticCodes.UnknownIntrinsic,
                               $"Unknown x: intrinsic '{local}'.",
                               line,
                               column);

                break;
        }
    }

    private MemberRecord DirectiveMember(XamlDirectiveKind kind, string value, int lineInfo)
        => new(-1, XamlValueKind.Directive, _builder.InternString(value), 0, lineInfo, (int) kind);

    /// <summary>
    /// Warns (CUR2305) on entries in one resource dictionary that share an <c>x:Key</c> — the runtime
    /// <c>ResourceDictionary.Add</c> throws on a duplicate, so this surfaces every collision at parse (all at
    /// once, before the build/load error) to make setting up aliases paste-and-clean. Keys are compared by
    /// their RAW written form (a literal, or a curly key like <c>{x:Type Button}</c>); merged/theme
    /// dictionaries are separate scopes checked independently as they parse.
    /// </summary>
    private void WarnDuplicateResourceKeys(List<int> entryObjectIndices)
    {
        HashSet<string>? seen = null;
        foreach (var idx in entryObjectIndices)
        {
            if (!TryGetRawKey(idx, out var key, out var keyLineInfo))
                continue;

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(key))
            {
                _builder.Warning(XamlDiagnosticCodes.DuplicateResourceKey,
                                 $"Duplicate resource key '{key}' in this dictionary — two entries cannot share a key " +
                                 "(loading throws). Remove or rename one.",
                                 LineInfo.Line(keyLineInfo), LineInfo.Column(keyLineInfo));
            }
        }
    }

    /// <summary>The raw <c>x:Key</c> string of a committed dictionary-entry object (its <c>Key</c> directive
    /// member) and that member's position, or false when the entry carries no key.</summary>
    private bool TryGetRawKey(int objectIndex, out string key, out int keyLineInfo)
    {
        key = string.Empty;
        keyLineInfo = 0;

        var obj = _builder.GetObject(objectIndex);
        if (!obj.HasFlag(ObjectFlags.HasKey))
            return false;

        for (int i = 0; i < obj.MemberCount; i++)
        {
            var m = _builder.GetMember(obj.MemberStart + i);
            if (m.Kind == XamlValueKind.Directive && m.DirectiveKind == (int) XamlDirectiveKind.Key)
            {
                key = _builder.GetString(m.ValueIndex);
                keyLineInfo = m.PackedLineInfo;
                return true;
            }
        }

        return false;
    }

    private void HandleAttachedAttribute(
        string attrLocal,
        int dot,
        string value,
        string attrNs,
        List<MemberRecord> members,
        bool inDeferred,
        int lineInfo,
        int line,
        int column,
        int valueColumn)
    {
        string ownerName = attrLocal.Substring(0, dot);
        string memberName = attrLocal.Substring(dot + 1);

        var ownerResolution = ResolveType(attrNs, ownerName, line, column);

        if (!ownerResolution.IsResolved)
        {
            // ResolveType already reported CUR2001/CUR2002.
            members.Add(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(value), 0, lineInfo));
            return;
        }

        var member = ownerResolution.Type!.TryGetMember(memberName);

        if (member is null)
        {
            ReportMemberNotFound(ownerResolution.Type!, memberName, line, column);
            members.Add(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(value), 0, lineInfo));
            return;
        }

        AddValueMember(member, value, members, inDeferred, lineInfo, line, column, valueColumn);
    }

    private void HandleMemberAttribute(
        XamlType? type,
        string ownerLocalName,
        string memberName,
        string value,
        List<MemberRecord> members,
        bool inDeferred,
        int lineInfo,
        int line,
        int column,
        int valueColumn)
    {
        // Setter Property/Value are deferred-resolved at end-of-object; capture them as text for now.
        if (string.Equals(ownerLocalName, "Setter", StringComparison.Ordinal) &&
            (string.Equals(memberName, "Property", StringComparison.Ordinal) || 
             string.Equals(memberName, "Value", StringComparison.Ordinal) ||
             string.Equals(memberName, "TargetType", StringComparison.Ordinal)))
        {
            var setterMember = type?.TryGetMember(memberName);
            int setterMemberId = setterMember is null ? -1 : _builder.AddResolvedMember(setterMember);

            // Attached-Setter Phase 2 (4C): for a dotted Property name capture the OWNER's namespace now, while
            // the reader's xmlns scope is live — a prefixed owner (my:Owner.Member) resolves its prefix via
            // LookupNamespace, an unprefixed dotted owner uses the in-scope default ns. Stashed as
            // (internedNsId + 1) in the otherwise-unused Text ItemCount slot (0 = no capture); end-of-object
            // ResolveSetter reads it back to resolve the owner (the reader is dead by then).
            int ownerNsToken = 0;

            if (string.Equals(memberName, "Property", StringComparison.Ordinal))
            {
                int dot = value.IndexOf('.');

                if (dot > 0)
                {
                    int colon = value.IndexOf(':');
                    string prefix = colon >= 0 && colon < dot ? value.Substring(0, colon) : string.Empty;

                    if (_reader.LookupNamespace(prefix) is { Length: > 0 } ownerNs)
                        ownerNsToken = _builder.InternString(ownerNs) + 1;
                }
            }

            members.Add(new MemberRecord(setterMemberId, XamlValueKind.Text, _builder.InternString(value), ownerNsToken, lineInfo));
            return;
        }

        if (type is null)
        {
            // Type didn't resolve; keep the raw value so CollectAll can still walk.
            members.Add(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(value), 0, lineInfo));
            return;
        }

        var member = type.TryGetMember(memberName);

        if (member is null)
        {
            ReportMemberNotFound(type, memberName, line, column);
            members.Add(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(value), 0, lineInfo));
            return;
        }

        // An event attribute inside deferred content is rejected (matrix X152 / CUR2301).
        if (member.IsEvent && inDeferred)
        {
            _builder.Error(XamlDiagnosticCodes.EventInDeferredContent,
                           $"Events are not allowed inside deferred content in v1 (member '{memberName}').", 
                           line,
                           column);

            return;
        }

        AddValueMember(member, value, members, inDeferred, lineInfo, line, column, valueColumn);
    }

    /// <summary>
    /// Adds a member whose value is an attribute string — classifying it as an extension, a folded
    /// constant, or text, and handling the <c>{}</c> literal escape. <paramref name="valueColumn"/> is
    /// the column of the value's first character (the <c>{</c> for an extension); it defaults to
    /// <paramref name="column"/> for element-text values whose value position equals the node position.
    /// </summary>
    private void AddValueMember(
        XamlMember member,
        string value,
        List<MemberRecord> members,
        bool inDeferred,
        int lineInfo,
        int line,
        int column,
        int valueColumn = -1)
    {
        if (valueColumn < 0)
            valueColumn = column;

        int memberId = _builder.AddResolvedMember(member);

        if (member.IsEvent)
        {
            members.Add(new MemberRecord(memberId, XamlValueKind.Event, _builder.InternString(value), 0, lineInfo));
            return;
        }

        if (MarkupExtensionParser.LooksLikeExtension(value))
        {
            int extIndex = ParseAndFoldExtension(value, members, member, memberId, inDeferred, lineInfo, line, valueColumn);

            if (extIndex >= 0)
                members.Add(new MemberRecord(memberId, XamlValueKind.Extension, extIndex, 0, lineInfo));

            return;
        }

        // The {} literal escape collapses to a plain literal string.
        string literal = MarkupExtensionParser.UnescapeLiteral(value);

        // Try to fold a context-free literal (XD3).
        if (_options.FoldConstants && TryFoldLiteral(member, literal, line, column, out object? folded))
        {
            int constIndex = _builder.AddConstant(folded);
            members.Add(new MemberRecord(memberId, XamlValueKind.Folded, constIndex, 0, lineInfo));
            return;
        }

        // W2e (the G4 close): a Text value on a member the ROUTE PROBE marked unconvertible fails HERE,
        // positioned, in whichever lane stamped a route — TODAY THE REFLECTION LANE ONLY (the symbol lane
        // and the emitted provider stay RouteKind.Unknown until their converter set is queryable metadata;
        // the deferral is recorded in xaml-conversion-routes.md §1a). Unknown routes are never judged.
        if (member.Route.Kind is RouteKind.None or RouteKind.Ambiguous)
        {
            _builder.Error(XamlDiagnosticCodes.NoConversionRoute,
                           member.Route.Kind == RouteKind.Ambiguous
                               ? $"Ambiguous conversion routes into '{member.ValueType.Name}' for '{member.Name}' — add a converter for the type."
                               : $"No conversion route from text to '{member.ValueType.Name}' for '{member.Name}' — " +
                                 "add a converter, a conversion operator/constructor/Parse method, or use a markup extension.",
                           line,
                           column);
        }

        // W2 CR5: a dotted UIProperty token ("UIElement.Opacity" / "my:Owner.Prop") captures its owner's
        // namespace NOW, while the reader's xmlns scope is live — end-of-object resolution reads it back
        // (the reader is dead by then). Same stash the Setter path uses: (internedNsId + 1) in the Text
        // record's otherwise-unused ItemCount slot; 0 = no capture.
        int ownerNsToken = 0;
        if (IsUIPropertyTyped(member) && literal.IndexOf('.') > 0)
        {
            int colon = literal.IndexOf(':');
            int dot = literal.IndexOf('.');
            string prefix = colon >= 0 && colon < dot ? literal.Substring(0, colon) : string.Empty;

            if (_reader.LookupNamespace(prefix) is { Length: > 0 } ownerNs)
                ownerNsToken = _builder.InternString(ownerNs) + 1;
        }

        members.Add(new MemberRecord(memberId, XamlValueKind.Text, _builder.InternString(literal), ownerNsToken, lineInfo));
    }

    /// <summary>The CR5 predicate: is this member's declared value type the Fork A <c>UIProperty</c>?
    /// Compared by <see cref="IXamlType.FullName"/> so the frontend never references <c>Cursorial.UI</c>.</summary>
    private static bool IsUIPropertyTyped(XamlMember member)
        => string.Equals(member.ValueType.FullName, "Cursorial.UI.UIProperty", StringComparison.Ordinal);

    /// <summary>
    /// The end-of-object CR5 pass: resolves every <c>UIProperty</c>-typed TEXT member's token against the
    /// lexical scope and rewrites the record to a member clone carrying the resolution
    /// (<see cref="XamlMember.ResolvedPropertyMember"/>). A dotted token resolves its owner xmlns-aware
    /// (the namespace captured at attribute time); an unqualified token resolves against the ambient
    /// target — the enclosing Style <c>TargetType</c> first, else the nearest enclosing object element —
    /// and errors CUR2113 (naming the owner-qualified escape) when neither is in scope. Conversion stays
    /// eager and positioned: an unresolvable token never reaches a runtime setter as a raw string.
    /// </summary>
    private void ResolveUIPropertyTokenMembers(List<MemberRecord> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            var record = members[i];

            if (record.MemberId < 0 || record.Kind != XamlValueKind.Text)
                continue;

            var member = _builder.ResolvedMember(record.MemberId);

            if (member is null || member.ResolvedPropertyMember is not null || !IsUIPropertyTyped(member))
                continue;

            string token = _builder.GetString(record.ValueIndex);
            int line = LineInfo.Line(record.PackedLineInfo);
            int column = LineInfo.Column(record.PackedLineInfo);

            XamlMember? resolved;

            if (token.IndexOf('.') > 0)
            {
                string? capturedOwnerNs = record.ItemCount > 0 ? _builder.GetString(record.ItemCount - 1) : null;
                resolved = TryResolveQualifiedSetterMember(token, capturedOwnerNs, targetType: null, line, column);
            }
            else
            {
                // The ambient walk (audit-hardened precedence: ELEMENT stack first — the nearest part
                // wins inside a template — with the style/template TargetType as the FALLBACK):
                //   barrier frame        → stop (deferred template / implicit-RD boundary)
                //   Style                → stop (a selector-only style has no lexical target; a
                //                          TargetType-bearing one serves the fallback)
                //   ResourceDictionary   → stop (an entry parses host-independently — explicit form)
                //   Setter               → transparent (its enclosing Style decides)
                //   pure collection wrapper (no content property, IsCollection) → transparent
                //   unresolved (null)    → transparent (already diagnosed)
                //   anything else        → the ambient (a content-bearing Storyboard stops the walk with
                //                          a deterministic local error rather than an accidental
                //                          resolution against an unrelated ancestor).
                XamlType? ambient = null;

                foreach (var frame in _elementTypeStack)
                {
                    if (frame.IsBarrier)
                        break;

                    var enclosing = frame.Type;
                    if (enclosing is null || enclosing is { ContentProperty: null, IsCollection: true })
                        continue;

                    string enclosingFullName = enclosing.ClrType.FullName;
                    if (string.Equals(enclosingFullName, "Cursorial.UI.Setter", StringComparison.Ordinal))
                        continue;
                    if (string.Equals(enclosingFullName, "Cursorial.UI.Style", StringComparison.Ordinal) ||
                        string.Equals(enclosingFullName, "Cursorial.UI.ResourceDictionary", StringComparison.Ordinal))
                        break;

                    ambient = enclosing;
                    break;
                }

                ambient ??= _styleTargetStack.Count > 0 ? _styleTargetStack.Peek() : null;

                if (ambient is null)
                {
                    _builder.Error(XamlDiagnosticCodes.UIPropertyTokenNoTarget,
                                   $"'{token}' has no lexical target type to resolve against — owner-qualify it " +
                                   $"(e.g. \"UIElement.{token}\") or author it under a TargetType-bearing scope.",
                                   line,
                                   column);
                    continue;
                }

                resolved = ambient.TryGetMember(token);

                if (resolved is null)
                {
                    ReportMemberNotFound(ambient, token, line, column);
                    continue;
                }
            }

            if (resolved?.Property is null)
            {
                // Resolved to a CLR-only member — there is no registered UIProperty identity to assign.
                if (resolved is not null)
                {
                    _builder.Error(XamlDiagnosticCodes.MemberNotFound,
                                   $"'{token}' resolves to a CLR member with no registered UIProperty.",
                                   line,
                                   column);
                }

                continue; // dotted-path failures already reported inside the helper
            }

            int rewrittenId = _builder.AddResolvedMember(member.WithResolvedPropertyMember(resolved));
            members[i] = new MemberRecord(rewrittenId, XamlValueKind.Text, record.ValueIndex, 0, record.PackedLineInfo);
        }
    }

    /// <summary>
    /// Parses a markup extension. Built-in folding extensions (<c>x:Null</c>/<c>x:Type</c>/
    /// <c>x:Static</c>) fold to constants and become a <c>Folded</c> member directly (returning -1 so
    /// the caller does not also add an Extension member). Live extensions return their
    /// <see cref="ExtensionRecord"/> index. The parse-time restriction on <c>{TemplateBinding}</c>
    /// outside a template body (matrix X56) is enforced here.
    /// </summary>
    private int ParseAndFoldExtension(
        string value,
        List<MemberRecord> members,
        XamlMember member,
        int memberId,
        bool inDeferred,
        int lineInfo,
        int line,
        int column)
    {
        MarkupExtensionNode node;

        try
        {
            node = MarkupExtensionParser.Parse(value, _builder.Source, line, column);
        }
        catch (XamlParseException ex)
        {
            // Surface the grammar diagnostic (re-report so CollectAll captures it; ThrowOnFirstError
            // rethrows via Report).
            _builder.Report(ex.Diagnostics[0]);
            return -1;
        }

        // A folded intrinsic (x:Null/x:Type/x:Static) becomes a Folded member here; a live extension
        // returns its index for the caller to add as an Extension member (the historical contract).
        if (!BuildExtensionValue(node, member, inDeferred, line, column, out XamlValueKind valueKind, out int valueIndex))
            return -1; // suppressed (a reported error, or a TemplateBinding outside a template body)

        // A Folded intrinsic (x:Null/x:Type/x:Static) — or a synthetic Object (a built-in primitive) — is added
        // as its own member here; a live Extension returns its index for the caller (the historical contract).
        if (valueKind != XamlValueKind.Extension)
        {
            members.Add(new MemberRecord(memberId, valueKind, valueIndex, 0, lineInfo));
            return -1;
        }

        return valueIndex;
    }

    /// <summary>
    /// Produces the value of a markup extension <paramref name="node"/> — the shared core of the curly form
    /// (<c>{DynamicResource X}</c>) and the ELEMENT form (<c>&lt;DynamicResource ResourceKey="X"/&gt;</c>).
    /// On success returns <see langword="true"/> with <paramref name="folded"/> and <paramref name="valueIndex"/>:
    /// a FOLDED intrinsic (x:Null/x:Type/x:Static) yields a constant index; a LIVE extension yields an
    /// <see cref="ExtensionRecord"/> index. Returns <see langword="false"/> when nothing is emitted (a reported
    /// error, or a <c>{TemplateBinding}</c> outside a template body). <paramref name="member"/> is the target
    /// member for a member-position extension (bindability is enforced against it), or null in a
    /// dictionary/collection-entry position.
    /// </summary>
    private bool BuildExtensionValue(MarkupExtensionNode node, XamlMember? member, bool inDeferred, int line, int column,
                                     out XamlValueKind valueKind, out int valueIndex)
    {
        valueKind = XamlValueKind.Extension; // the default; a folded intrinsic / synthetic-object arm overrides
        valueIndex = -1;
        var kind = ClassifyExtension(node.Name);
        int lineInfo = LineInfo.Pack(node.Line, node.Column);

        // Stamp the resolved xmlns onto THIS node and, recursively, every nested-extension argument — for
        // ALL kinds, not just Custom. The loader/generator re-resolve extension types at build (the reader
        // scope is gone), keyed off the stamp. A nested custom extension living under a built-in outer
        // extension ({Binding Converter={i:MyConverter}}, {StaticResource {i:Key}}) is only reachable here;
        // without this stamp its ResolvedNamespace was null → the build-time resolver fell back to the default
        // UI xmlns and a prefixed project extension was CUR2002 at load/generate (Gallery's {i:EnumItemConverter}).
        StampResolvedNamespaces(node);

        // A built-in primitive as a single-positional-argument markup extension: {x:Boolean True} / {x:Int32
        // 4096}, the curly twin of the element form <x:Boolean>True</x:Boolean>. Build the SAME synthetic node
        // the element form yields (a primitive-typed object with one content Text member) so the emitter and
        // the loader need no new path — they already lower/instantiate the primitive object.
        if (TryBuildPrimitiveObject(node, line, column, out valueIndex))
        {
            valueKind = XamlValueKind.Object;
            return true;
        }

        // A built-in x:Array as a markup extension: {x:Array Type=T, item, item, …} — the curly twin of the element
        // form <x:Array Type="T">items</x:Array>. Builds the SAME IsArray node so the emitter/loader need no new path.
        if (TryBuildArrayObject(node, inDeferred, line, column, out valueIndex))
        {
            valueKind = XamlValueKind.Object;
            return true;
        }

        // {x:Self [Level=N]} — a construction-time self-reference, folded to a typed token each lane resolves to
        // the object the value is being assigned onto (always the assignment TARGET, seeing through enclosing
        // extensions). Level 0 only for now; Level > 0 (the construction-stack walk) is reserved.
        if (TryBuildSelfReference(node, line, column, out valueIndex))
        {
            valueKind = XamlValueKind.Folded;
            return true;
        }

        switch (kind)
        {
            case ExtensionKind.Null:
                valueIndex = _builder.AddConstant(null);
                valueKind = XamlValueKind.Folded;
                return true;

            case ExtensionKind.Type:
            case ExtensionKind.Static:
                // x:Type / x:Static fold at parse against the metadata provider.
                if (TryFoldIntrinsicExtension(kind, node, line, column, out object? f))
                {
                    valueIndex = _builder.AddConstant(f);
                    valueKind = XamlValueKind.Folded;
                    return true;
                }

                return false; // TryFoldIntrinsicExtension reported the miss

            case ExtensionKind.TemplateBinding when !inDeferred:
                _builder.Error(XamlDiagnosticCodes.TemplateBindingOutsideTemplate,
                               "{TemplateBinding} is only legal inside a template body.",
                               node.Line, node.Column);
                return false;

            case ExtensionKind.StaticResource:
            case ExtensionKind.DynamicResource:
            {
                // Carry the key (the primary argument) for X2. The common form is a literal string
                // ({DynamicResource Accent} → Strings, X44/X57). When the key is itself a markup extension
                // ({DynamicResource {x:Static ThemeKeys.X}}, X44a/X57a) the frontend cannot resolve it (no
                // static resolver in netstandard2.0) — store the INNER key node and let the loader resolve
                // it at instantiate (PayloadIsParsedExtension, XD7a).
                var primary = PrimaryArgument(node, kind);
                if (primary is { Nested: {} keyNode })
                {
                    int parsedKey = _builder.AddParsedExtension(keyNode);
                    valueIndex = _builder.AddExtension(new ExtensionRecord(kind, parsedKey, lineInfo, payloadIsParsedExtension: true));
                    return true;
                }

                string key = primary is { Text: {} t } ? t : string.Empty;
                valueIndex = _builder.AddExtension(new ExtensionRecord(kind, _builder.InternString(key), lineInfo));
                return true;
            }

            case ExtensionKind.Binding:
            case ExtensionKind.TemplateBinding:
            {
                // A {Binding}/{TemplateBinding} target must be a registered UIProperty (bindable). A CLR-only
                // member is CUR2210 (matrix X120; doc §4.4). This is checked only when the target member is
                // known here (curly attribute form, or an element form whose scalar member is in hand); an
                // element-form extension whose position is not yet known (member is null — a synthetic object)
                // defers the check to the loader's attach, which errors against the real target (a dictionary/
                // collection entry has no target and is rejected by ProvideExtensionEntryValue).
                if (member is not null && member.Property is null &&
                    member is { IsEvent: false, ValueType.Name: not ("Binding" or "BindingBase") })
                {
                    _builder.Error(XamlDiagnosticCodes.BindingTargetNotBindable,
                                   $"Binding target '{member.Name}' is not a bindable property " +
                                   $"(only registered UIProperties can be data-bound).",
                                   node.Line, node.Column);
                    return false;
                }

                valueIndex = _builder.AddExtension(new ExtensionRecord(kind, _builder.AddParsedExtension(node), lineInfo));
                return true;
            }

            case ExtensionKind.Reference:
            {
                // {x:Reference Name} (positional) or Name=…: store the name; the loader/generator resolve it
                // against the document name scope (forward refs resolved after the tree is built).
                string refName = PrimaryArgument(node, kind) is { Text: { } rt } ? rt : string.Empty;
                if (refName.Length == 0)
                {
                    _builder.Error(XamlDiagnosticCodes.UnsupportedIntrinsic,
                                   "{x:Reference} requires a name (e.g. {x:Reference myButton}).", node.Line, node.Column);
                    return false;
                }

                valueIndex = _builder.AddExtension(new ExtensionRecord(ExtensionKind.Reference, _builder.InternString(refName), lineInfo));
                return true;
            }

            case ExtensionKind.Custom:
            default:
                // A custom extension: resolve its type to surface a CUR2002 did-you-mean at parse (X53).
                ResolveExtensionType(node, line, column);
                valueIndex = _builder.AddExtension(new ExtensionRecord(ExtensionKind.Custom, _builder.AddParsedExtension(node), lineInfo));
                return true;
        }
    }

    /// <summary>The XAML2009 built-in primitive local names (mirrors the schema context / the emitter's
    /// BuiltInPrimitiveLocalNames): usable as element-form types AND, now, single-positional-argument markup
    /// extensions (<c>{x:Boolean True}</c>).</summary>
    private static readonly HashSet<string> BuiltInPrimitiveNames = new(StringComparer.Ordinal)
    {
        "Object", "Boolean", "Byte", "SByte", "Char", "Decimal", "Single", "Double", "Int16", "Int32",
        "Int64", "UInt16", "UInt32", "UInt64", "String", "TimeSpan", "Uri",
    };

    /// <summary>
    /// Builds the synthetic primitive object for a curly <c>{x:Boolean True}</c> / <c>{x:Int32 4096}</c>: a
    /// primitive-typed object carrying a single content <c>Text</c> member (memberId −1), byte-identical to the
    /// <c>&lt;x:Boolean&gt;True&lt;/x:Boolean&gt;</c> element form's node — so downstream needs no new path.
    /// Returns false when the node is not an intrinsic primitive extension (the caller falls to the normal switch).
    /// </summary>
    private bool TryBuildPrimitiveObject(MarkupExtensionNode node, int line, int column, out int objectIndex)
    {
        objectIndex = -1;
        if (!XmlnsNamespaces.IsIntrinsics(node.ResolvedNamespace ?? string.Empty))
            return false;

        int colon = node.Name.IndexOf(':');
        string local = colon >= 0 ? node.Name.Substring(colon + 1) : node.Name;
        if (!BuiltInPrimitiveNames.Contains(local))
            return false;

        if (ResolveType(XmlnsNamespaces.Intrinsics, local, line, column) is not { IsResolved: true } resolution)
            return false; // the element form resolves it; a miss here is already reported by ResolveType

        // The value is the single positional argument (empty → an empty string, as <x:String></x:String>).
        string text = node.PositionalArguments.Count > 0 && node.PositionalArguments[0].Text is { } t ? t : string.Empty;

        int lineInfo = LineInfo.Pack(node.Line, node.Column);
        objectIndex = _builder.ReserveObject();
        int typeId = _builder.AddResolvedType(resolution.Type!);
        int memberStart = _builder.MemberCount;
        _builder.AddMember(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(text), 0, lineInfo));
        _builder.SetObject(objectIndex, new ObjectRecord(typeId, memberStart, (ushort) 1, ObjectFlags.None, subtreeLength: 1, lineInfo));
        return true;
    }

    // A built-in x:Array as a markup extension: {x:Array Type=T, item, item, …} — the curly twin of the element
    // form <x:Array Type="T">items</x:Array>. Builds the SAME IsArray node (element type T as TypeId, one Items
    // member over the item objects) so the emitter/loader need no new path. Type is a REQUIRED named arg (all
    // positionals are items, matching the element form's Type requirement); each item is a curly primitive
    // ({x:Int32 1}), a nested {x:Array}, or a bare value converted to T (as <T>value</T> would be).
    private bool TryBuildArrayObject(MarkupExtensionNode node, bool inDeferred, int line, int column, out int objectIndex)
    {
        objectIndex = -1;
        if (!XmlnsNamespaces.IsIntrinsics(node.ResolvedNamespace ?? string.Empty))
            return false;

        int colon = node.Name.IndexOf(':');
        string local = colon >= 0 ? node.Name.Substring(colon + 1) : node.Name;
        if (!string.Equals(local, "Array", StringComparison.Ordinal))
            return false;

        int lineInfo = LineInfo.Pack(node.Line, node.Column);

        // Type: the REQUIRED named arg — a type token (bare `x:Int32`, or a nested {x:Type T}) resolved via the
        // reader-scope xmlns exactly as the element form's Type attribute.
        var typeArg = node.FindNamed("Type");
        string? typeToken = typeArg?.Text
                            ?? (typeArg?.Nested is { } tn && (tn.Name is "x:Type" or "Type") && tn.PositionalArguments.Count > 0
                                    ? tn.PositionalArguments[0].Text
                                    : null);

        XamlType? elementType = null;
        if (typeToken is { Length: > 0 })
        {
            var res = ResolveQualifiedType(typeToken, appendExtensionSuffix: false, line, column, report: true);
            if (res.IsResolved)
                elementType = res.Type;
        }
        else
        {
            _builder.Error(XamlDiagnosticCodes.ArrayMissingType,
                           "{x:Array} requires a Type named argument (e.g. {x:Array Type=x:String, …}).", line, column);
        }

        int typeId = _builder.AddResolvedType(elementType);

        // Reserve the array object FIRST so its item objects land contiguously after it (the SoA subtree invariant:
        // subtreeLength = ObjectCount − objectIndex covers the array + every item it just built).
        objectIndex = _builder.ReserveObject();

        var children = new List<int>();
        foreach (var arg in node.PositionalArguments)
        {
            if (arg.Nested is { } itemNode)
            {
                // Each nested item builds its own object, at its OWN source position: a curly primitive
                // ({x:Int32 1}), a nested {x:Array}, or ANY OTHER markup extension ({x:Null}/{x:Static}/
                // {Binding}/{StaticResource}/custom) — the last wrapped in the SAME synthetic IsMarkupExtension
                // object the element form's <x:Null/> etc. build, so every item the element form accepts works here
                // (and an unsupported one is rejected by the loader identically). No item is silently dropped.
                if (TryBuildPrimitiveObject(itemNode, itemNode.Line, itemNode.Column, out int primIndex))
                    children.Add(primIndex);
                else if (TryBuildArrayObject(itemNode, inDeferred, itemNode.Line, itemNode.Column, out int arrIndex))
                    children.Add(arrIndex);
                else
                    children.Add(BuildMarkupExtensionItemObject(itemNode, inDeferred));
            }
            else if (arg.Text is { } itemText)
            {
                children.Add(BuildTextItemObject(elementType, itemText, lineInfo));
            }
        }

        var flags = ObjectFlags.IsArray;
        if (inDeferred) flags |= ObjectFlags.InDeferredContent;

        int memberStart = _builder.MemberCount;
        ushort memberCount = 0;
        if (children.Count > 0)
        {
            _builder.AddMember(new MemberRecord(-1, XamlValueKind.Items, children[0], children.Count, lineInfo));
            memberCount = 1;
        }

        int subtreeLength = _builder.ObjectCount - objectIndex;
        _builder.SetObject(objectIndex, new ObjectRecord(typeId, memberStart, memberCount, flags, subtreeLength, lineInfo));
        return true;
    }

    // A bare array item value converted to the element type (the curly analog of <T>value</T>): a synthetic
    // T-typed object with one content Text member — exactly the shape TryBuildPrimitiveObject builds for a primitive.
    private int BuildTextItemObject(XamlType? elementType, string text, int lineInfo)
    {
        int childIndex = _builder.ReserveObject();
        int typeId = _builder.AddResolvedType(elementType);
        int memberStart = _builder.MemberCount;
        _builder.AddMember(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(text), 0, lineInfo));
        _builder.SetObject(childIndex, new ObjectRecord(typeId, memberStart, (ushort) 1, ObjectFlags.None, subtreeLength: 1, lineInfo));
        return childIndex;
    }

    // Wraps a curly-array item that is a markup extension ({x:Null}/{x:Static}/{Binding}/{StaticResource}/custom)
    // in the SAME synthetic IsMarkupExtension object the element form's ParseExtensionElement builds — one value
    // member (a Folded constant or a live Extension record), typeId −1 — so BuildArray/EmitArray instantiate it via
    // the standalone-entry path (ProvideExtensionEntryValue), identical to <x:Null/> etc. as element children. A
    // suppressed build leaves a null value (well-defined instantiation), and BuildExtensionValue reports at the
    // ITEM's own position (itemNode.Line/Column), not the array's.
    private int BuildMarkupExtensionItemObject(MarkupExtensionNode itemNode, bool inDeferred)
    {
        int itemLineInfo = LineInfo.Pack(itemNode.Line, itemNode.Column);
        int childIndex = _builder.ReserveObject();

        if (!BuildExtensionValue(itemNode, member: null, inDeferred, itemNode.Line, itemNode.Column, out var valueKind, out int valueIndex))
        {
            valueKind = XamlValueKind.Folded;
            valueIndex = _builder.AddConstant(null);
        }

        int memberStart = _builder.MemberCount;
        _builder.AddMember(new MemberRecord(-1, valueKind, valueIndex, 0, itemLineInfo));

        int subtreeLength = _builder.ObjectCount - childIndex;
        _builder.SetObject(childIndex, new ObjectRecord(typeId: -1, memberStart, (ushort) 1, ObjectFlags.IsMarkupExtension, subtreeLength, itemLineInfo));
        return childIndex;
    }

    // {x:Self [Level=N]} → a Folded XamlSelfReference token. Level is the OPTIONAL named argument (default 0 —
    // the immediate assignment target); Level > 0 (the construction-stack walk) is reserved and reported, never
    // silently misresolved. Any other argument is malformed.
    private bool TryBuildSelfReference(MarkupExtensionNode node, int line, int column, out int valueIndex)
    {
        valueIndex = -1;
        if (!XmlnsNamespaces.IsIntrinsics(node.ResolvedNamespace ?? string.Empty))
            return false;

        int colon = node.Name.IndexOf(':');
        string local = colon >= 0 ? node.Name.Substring(colon + 1) : node.Name;
        if (!string.Equals(local, "Self", StringComparison.Ordinal))
            return false;

        int level = 0;
        if (node.FindNamed("Level") is { } levelArg)
        {
            if (levelArg.Text is not { } levelText || !int.TryParse(levelText, out level) || level < 0)
            {
                _builder.Error(XamlDiagnosticCodes.MalformedExtensionArgument,
                               "{x:Self} Level must be a non-negative integer.", node.Line, node.Column);
                level = 0;
            }
            else if (level > 0)
            {
                _builder.Error(XamlDiagnosticCodes.UnsupportedIntrinsic,
                               "{x:Self} Level > 0 (the construction-stack walk) is not yet supported.", node.Line, node.Column);
            }
        }

        if (node.PositionalArguments.Count > 0)
            _builder.Error(XamlDiagnosticCodes.MalformedExtensionArgument,
                           "{x:Self} takes no positional arguments (use Level=N).", node.Line, node.Column);

        valueIndex = _builder.AddConstant(new XamlSelfReference(level));
        return true;
    }

    /// <summary>
    /// The canonical extension name for an element in markup-extension ELEMENT form, or null when the element
    /// is not a built-in extension. The resource/binding extensions live in the UI xmlns; the intrinsic
    /// <c>x:</c> extensions in the intrinsics xmlns (returned with the <c>x:</c> prefix <see cref="ClassifyExtension"/>
    /// expects). <c>Binding</c>/<c>TemplateBinding</c> are intercepted here in element form even though they are
    /// also CLR types — element form means the extension (WPF parity), not a bare object.
    /// </summary>
    private static string? BuiltInExtensionElementName(string ns, string localName)
    {
        if (XmlnsNamespaces.IsIntrinsics(ns))
            return localName switch
            {
                "Null" or "Static" or "Type" or "Reference" or "Self" => "x:" + localName,
                _ => null,
            };

        if (string.Equals(ns, XmlnsNamespaces.CursorialUi, StringComparison.Ordinal))
            return localName switch
            {
                "StaticResource" or "DynamicResource" or "Binding" or "TemplateBinding" => localName,
                _ => null,
            };

        return null;
    }

    /// <summary>
    /// Parses a markup extension in ELEMENT form into a synthetic <see cref="ObjectFlags.IsMarkupExtension"/>
    /// object at <paramref name="objectIndex"/>: element attributes map to the extension's NAMED arguments
    /// (a curly value like <c>ResourceKey="{x:Static X}"</c> nests), <c>x:Key</c> is captured for a dictionary
    /// entry, and the produced value (a <see cref="XamlValueKind.Folded"/> constant or a live
    /// <see cref="XamlValueKind.Extension"/> record) is the object's single value member. Property-element
    /// children of the extension are not supported (the curly nested form covers complex argument values).
    /// </summary>
    private int ParseExtensionElement(int objectIndex, string extensionName, bool inDeferred, int lineInfo, int line, int column)
    {
        var node = ReadExtensionNode(extensionName, line, column, out var key);

        var members = new List<MemberRecord>();
        var flags = ObjectFlags.IsMarkupExtension;

        if (key is not null)
        {
            members.Add(DirectiveMember(XamlDirectiveKind.Key, key, lineInfo));
            flags |= ObjectFlags.HasKey;
        }

        // The produced value slot (memberId −1): a Folded constant, or a live ExtensionRecord. A suppressed
        // build (a reported error) leaves a null so instantiation stays well-defined.
        if (BuildExtensionValue(node, member: null, inDeferred, line, column, out XamlValueKind valueKind, out int valueIndex))
            members.Add(new MemberRecord(-1, valueKind, valueIndex, 0, lineInfo));
        else
            members.Add(new MemberRecord(-1, XamlValueKind.Folded, _builder.AddConstant(null), 0, lineInfo));

        int memberStart = _builder.MemberCount;
        foreach (var m in members)
            _builder.AddMember(m);

        _builder.SetObject(objectIndex,
            new ObjectRecord(typeId: -1, memberStart, (ushort) members.Count, flags, subtreeLength: 1, lineInfo));

        return objectIndex;
    }

    /// <summary>
    /// Reads the markup-extension element the reader is positioned on into a <see cref="MarkupExtensionNode"/>:
    /// attributes become NAMED arguments (a curly value nests), <c>x:Key</c> is returned via
    /// <paramref name="key"/> for a dictionary entry, and PROPERTY-ELEMENT children
    /// (<c>&lt;DynamicResource.ResourceKey&gt;…&lt;/&gt;</c>) become named arguments whose value is the child's
    /// text or a nested markup extension (recursed). A non-property-element / non-extension child is skipped.
    /// </summary>
    private MarkupExtensionNode ReadExtensionNode(string extensionName, int line, int column, out string? key)
    {
        key = null;
        bool isEmpty = _reader.IsEmptyElement;
        int elementDepth = _reader.Depth;
        var named = new List<MarkupExtensionNamedArgument>();

        if (_reader.MoveToFirstAttribute())
        {
            do
            {
                string attrLocal = _reader.LocalName;
                string attrPrefix = _reader.Prefix;
                string attrNs = _reader.NamespaceURI;
                string attrValue = _reader.Value;

                if (attrPrefix == "xmlns" || attrLocal == "xmlns")
                    continue;
                if (XmlnsNamespaces.IsIntrinsics(attrNs))
                {
                    if (attrLocal == "Key") key = attrValue; // the dictionary key; x:Name/x:Uid aren't args
                    continue;
                }
                if (XmlnsNamespaces.IsDesignTime(attrNs) ||
                    string.Equals(attrNs, XmlnsNamespaces.MarkupCompatibility, StringComparison.Ordinal))
                    continue;

                var argValue = MarkupExtensionParser.LooksLikeExtension(attrValue)
                    ? MarkupExtensionArgumentValue.FromNested(ParseNestedArgument(attrValue, line, column))
                    : MarkupExtensionArgumentValue.FromText(attrValue);
                named.Add(new MarkupExtensionNamedArgument(attrLocal, argValue, line, column));
            }
            while (_reader.MoveToNextAttribute());

            _reader.MoveToElement();
        }

        // Property-element argument children: <Ext.ArgName>value</Ext.ArgName> — the verbose form of an
        // attribute argument (a nested extension value that is awkward inline).
        if (!isEmpty)
        {
            while (_reader.Read())
            {
                if (_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == elementDepth)
                    break;
                if (_reader.NodeType != XmlNodeType.Element)
                    continue;

                int dot = _reader.LocalName.IndexOf('.');
                if (dot <= 0)
                {
                    SkipCurrentSubtree(); // not a property element of this extension
                    continue;
                }

                string argName = _reader.LocalName.Substring(dot + 1);
                if (ReadExtensionArgumentValue(line, column) is { } argValue)
                    named.Add(new MarkupExtensionNamedArgument(argName, argValue, line, column));
            }
        }

        return new MarkupExtensionNode(extensionName, positionalArguments: null, named, line, column);
    }

    /// <summary>Reads a markup-extension property-element argument's value (the reader is on the
    /// <c>&lt;Ext.ArgName&gt;</c> element): its text, or a single nested markup-extension element (recursed);
    /// a non-extension object child is skipped and yields null.</summary>
    private MarkupExtensionArgumentValue? ReadExtensionArgumentValue(int line, int column)
    {
        if (_reader.IsEmptyElement)
            return null;

        int argDepth = _reader.Depth;
        var text = new StringBuilder();
        MarkupExtensionArgumentValue? nested = null;

        while (_reader.Read())
        {
            if (_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == argDepth)
                break;

            if (_reader.NodeType == XmlNodeType.Element)
            {
                if (BuiltInExtensionElementName(_reader.NamespaceURI, _reader.LocalName) is { } nestedName)
                    nested = MarkupExtensionArgumentValue.FromNested(ReadExtensionNode(nestedName, line, column, out _));
                else
                    SkipCurrentSubtree(); // a non-extension object as an extension argument is unsupported
            }
            else if (_reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                                     or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                text.Append(_reader.Value);
            }
        }

        if (nested is { } n)
            return n;

        string literal = text.ToString().Trim();
        return literal.Length > 0 ? MarkupExtensionArgumentValue.FromText(literal) : null;
    }

    /// <summary>Parses a curly markup extension appearing as an element attribute's value; on a grammar error
    /// reports it and returns an <c>x:Null</c> placeholder so the outer build stays well-defined.</summary>
    private MarkupExtensionNode ParseNestedArgument(string value, int line, int column)
    {
        try
        {
            return MarkupExtensionParser.Parse(value, _builder.Source, line, column);
        }
        catch (XamlParseException ex)
        {
            _builder.Report(ex.Diagnostics[0]);
            return new MarkupExtensionNode("x:Null", positionalArguments: null, namedArguments: null, line, column);
        }
    }

    private static ExtensionKind ClassifyExtension(string name)
        => name switch
           {
               "x:Null" or "Null" => ExtensionKind.Null,
               "x:Static"         => ExtensionKind.Static,
               "x:Type"           => ExtensionKind.Type,
               "StaticResource"   => ExtensionKind.StaticResource,
               "DynamicResource"  => ExtensionKind.DynamicResource,
               "Binding"          => ExtensionKind.Binding,
               "TemplateBinding"  => ExtensionKind.TemplateBinding,
               "x:Reference" or "Reference" => ExtensionKind.Reference,
               _                  => ExtensionKind.Custom,
           };

    /// <summary>
    /// The named argument a built-in extension's single positional argument also binds to (WPF
    /// property-name parity), so <c>{DynamicResource X}</c>, <c>{DynamicResource ResourceKey=X}</c>, and
    /// the element form <c>&lt;DynamicResource ResourceKey="X"/&gt;</c> resolve identically. Also the
    /// primary property name for element form. Null when there is no single primary (<c>x:Null</c>).
    /// </summary>
    internal static string? BuiltInPrimaryArgName(ExtensionKind kind) => kind switch
    {
        ExtensionKind.StaticResource or ExtensionKind.DynamicResource => "ResourceKey",
        ExtensionKind.Type            => "TypeName",
        ExtensionKind.Static          => "Member",
        ExtensionKind.Reference       => "Name",
        ExtensionKind.TemplateBinding => "Property",
        ExtensionKind.Binding         => "Path",
        _                             => null,
    };

    /// <summary>The extension's primary argument: the first positional, else the named primary (WPF parity).</summary>
    private static MarkupExtensionArgumentValue? PrimaryArgument(MarkupExtensionNode node, ExtensionKind kind)
        => node.PositionalArguments.Count > 0
               ? node.PositionalArguments[0]
               : BuiltInPrimaryArgName(kind) is { } named ? node.FindNamed(named) : null;

    private bool TryFoldIntrinsicExtension(ExtensionKind kind, MarkupExtensionNode node, int line, int column, out object? folded)
    {
        folded = null;
        string arg = PrimaryArgument(node, kind) is { Text: {} t } ? t : string.Empty;

        if (kind == ExtensionKind.Type)
        {
            // {x:Type my:Button} — validate the token at parse (binding the prefix from the live reader scope),
            // but carry the TOKEN, not a runtime Type: Type.ClrType.UnderlyingSystemType is provider-dependent
            // (null under the symbol-only generator provider, which silently dropped the type). Each lane
            // resolves the XamlTypeReference itself — the loader to a System.Type, the generator to typeof(...) —
            // exactly as {x:Static} carries a XamlStaticReference.
            var resolution = ResolveQualifiedType(arg, appendExtensionSuffix: false, node.Line, node.Column, report: true);

            if (resolution.IsResolved)
            {
                folded = new XamlTypeReference(arg);
                return true;
            }

            return false; // ResolveQualifiedType reported the miss
        }

        // x:Static — resolved by the loader's metadata provider (reflection). The frontend has no
        // field/property resolver, so fold to a marker the loader replaces, OR leave it to X2 if no
        // resolver is wired. For the frontend, fold to a typed placeholder carrying the member path.
        folded = new XamlStaticReference(arg);
        return true;
    }

    private void ResolveExtensionType(MarkupExtensionNode node, int line, int column)
    {
        // A custom extension name may be prefix-qualified (my:FooExtension); bind the prefix from the live
        // reader scope and try the conventional "Extension" suffix. Reports CUR2002 on a miss (X53). The
        // namespace stamp (for the loader's build-time re-resolution) is applied for every extension node up
        // in BuildExtensionValue, so this only surfaces the top-level parse-time diagnostic.
        _ = ResolveQualifiedType(node.Name, appendExtensionSuffix: true, node.Line, node.Column, report: true);
    }

    /// <summary>
    /// Stamps <paramref name="node"/> (and its nested extension arguments) with the xmlns URI each
    /// extension NAME binds to in the LIVE reader scope — the loader re-resolves extension types at
    /// build time (X2), when the scope is long gone (see <see cref="MarkupExtensionNode.ResolvedNamespace"/>).
    /// Nested nodes are stamped quietly (their diagnostics stay where they always surfaced).
    /// </summary>
    private void StampResolvedNamespaces(MarkupExtensionNode node)
    {
        string name = node.Name;
        int colon = name.IndexOf(':');
        string prefix = colon > 0 ? name.Substring(0, colon) : string.Empty;
        node.ResolvedNamespace = _reader.LookupNamespace(prefix) is { Length: > 0 } bound ? bound : XmlnsNamespaces.CursorialUi;

        foreach (var argument in node.PositionalArguments)
        {
            if (argument.IsNested)
                StampResolvedNamespaces(argument.Nested!);
        }

        foreach (var named in node.NamedArguments)
        {
            if (named.Value.IsNested)
                StampResolvedNamespaces(named.Value.Nested!);
        }
    }

    // ── Element body parsing (content + property elements + collections) ──────────────────────────

    private void ParseElementBody(
        XamlType? type,
        string ownerLocalName,
        List<MemberRecord> members,
        bool inDeferred,
        bool inResourceDictionary,
        int ownerObjectIndex,
        bool isRoot = false)
    {
        var textBuffer = new StringBuilder();
        var contentChildren = new List<int>();
        bool sawPropertyElement = false;
        bool sawContentChild = false;

        // ReSharper disable once UnusedVariable
        int depth = _reader.Depth;

        while (_reader.Read())
        {
            switch (_reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    string elemLocal = _reader.LocalName;
                    string elemNs = _reader.NamespaceURI;
                    int elemLine = _lineInfo.LineNumber;
                    int elemColumn = CurrentElementColumn();
                    int elemLineInfo = LineInfo.Pack(elemLine, elemColumn);

                    // Design-time or mc:Ignorable-marked child elements are designer data, not
                    // content — skip the whole subtree without disturbing the sibling walk. The one
                    // designer-data form the parser DOES capture: a ROOT-level property element whose
                    // member is DataContext (`<d:Owner.DataContext>`) — its single object child parses
                    // as a detached fragment document for XamlDesignInfo (never entering this graph).
                    if (XmlnsNamespaces.IsDesignTime(elemNs))
                    {
                        // Both spellings capture: the property-element form (<d:Owner.DataContext>) and the
                        // bare directive form (<d:DataContext>) — designers write either.
                        int designDot = elemLocal.IndexOf('.');
                        bool isDataContext = designDot > 0
                            ? string.Equals(elemLocal.Substring(designDot + 1), "DataContext", StringComparison.Ordinal)
                            : string.Equals(elemLocal, "DataContext", StringComparison.Ordinal);
                        if (isRoot && isDataContext)
                        {
                            CaptureDesignDataContextElement(elemLine, elemColumn);
                            continue;
                        }

                        SkipCurrentSubtree();
                        continue;
                    }

                    if (_ignorableNamespaces is { } ignorableNs && ignorableNs.Contains(elemNs))
                    {
                        SkipCurrentSubtree();
                        continue;
                    }

                    // Property-element syntax: Owner.Member (a dotted element name on the owner type).
                    int dot = elemLocal.IndexOf('.');

                    if (dot > 0)
                    {
                        sawPropertyElement = true;

                        ParsePropertyElement(type, ownerLocalName, elemLocal, dot, elemNs, members,
                                             inDeferred, elemLine, elemColumn, elemLineInfo);

                        continue;
                    }

                    // A content child element: append to the implicit content.
                    sawContentChild = true;
                    int childIndex = ParseElement(inDeferred, inResourceDictionary, isRoot: false);
                    contentChildren.Add(childIndex);
                    continue;
                }

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    textBuffer.Append(_reader.Value);
                    break;

                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    textBuffer.Append(_reader.Value);
                    break;

                case XmlNodeType.EndElement:
                    goto done;
            }
        }

    done:
        // xml:space="preserve" detection: the reader exposes XmlSpace on the element scope.
        var preserveSpace = _reader.XmlSpace == XmlSpace.Preserve;

        // Commit content text (if any non-whitespace) as the content-property value.
        if (!sawContentChild)
        {
            string text = textBuffer.ToString();
            string normalized = preserveSpace ? text : WhitespaceNormalizer.NormalizeElementText(text);

            if (preserveSpace ? text.Length > 0 : normalized.Length > 0)
            {
                AddContentText(type, 
                               ownerLocalName, 
                               normalized,
                               members,
                               LineInfo.Pack(_lineInfo.LineNumber, _lineInfo.LinePosition),
                               inDeferred);
            }
        }

        if (sawContentChild && contentChildren.Count > 0)
        {
            if (inResourceDictionary)
                WarnDuplicateResourceKeys(contentChildren);

            CommitContentChildren(type,
                                  ownerLocalName,
                                  contentChildren,
                                  members,
                                  LineInfo.Pack(_lineInfo.LineNumber, _lineInfo.LinePosition));
        }

        // X5: a property set twice — attribute + property element, or attribute + IMPLICIT content (the
        // content children commit one member record under the resolved [ContentProperty] name, so a
        // single-valued content property duplicated by an attribute collides by name here; pre-W1 the
        // implicit form was unreachable for such types and the duplication silently last-wins-overwrote).
        if (sawPropertyElement || sawContentChild)
            DetectDuplicateAssignments(members);
    }

    private void AddContentText(
        XamlType? type,
        string ownerLocalName,
        string text,
        List<MemberRecord> members,
        int lineInfo,
        bool inDeferred)
    {
        // The content property names the slot. If the type has no content property, the text is
        // rejected later by the loader; at parse we record it against the content member if resolvable.
        string? contentName = type?.ContentProperty;

        if (contentName is null)
        {
            // No content property known (or type unresolved): record as a synthetic content text member.
            members.Add(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(text), 0, lineInfo));
            return;
        }

        var member = type!.TryGetMember(contentName);

        if (member is null)
        {
            members.Add(new MemberRecord(-1, XamlValueKind.Text, _builder.InternString(text), 0, lineInfo));
            return;
        }

        AddValueMember(member, text, members, inDeferred, lineInfo, LineInfo.Line(lineInfo), LineInfo.Column(lineInfo));
    }

    private void CommitContentChildren(
        XamlType? type,
        string ownerLocalName,
        List<int> children,
        List<MemberRecord> members,
        int lineInfo)
    {
        string? contentName = type?.ContentProperty;
        XamlMember? member = contentName is null ? null : type!.TryGetMember(contentName);
        int memberId = member is null ? -1 : _builder.AddResolvedMember(member);

        bool memberIsDeferred = member?.IsDeferredContent == true;

        if (memberIsDeferred)
        {
            // A single deferred slice: the first child is the slice head.
            members.Add(new MemberRecord(memberId, XamlValueKind.Deferred, children[0], children.Count, lineInfo));
            return;
        }

        if (children.Count == 1 && (type is null || !type.IsCollection))
        {
            members.Add(new MemberRecord(memberId, XamlValueKind.Object, children[0], 0, lineInfo));
        }
        else
        {
            members.Add(new MemberRecord(memberId, XamlValueKind.Items, children[0], children.Count, lineInfo));
        }
    }

    private void ParsePropertyElement(
        XamlType? ownerType,
        string ownerLocalName,
        string elemLocal,
        int dot,
        string elemNs,
        List<MemberRecord> members,
        bool inDeferred,
        int line,
        int column,
        int lineInfo)
    {
        string ownerName = elemLocal.Substring(0, dot);
        string memberName = elemLocal.Substring(dot + 1);
        bool isEmpty = _reader.IsEmptyElement;

        // Resolve the member's owner type (may be a different type than the element for attached props).
        XamlType? memberOwner = ownerType;

        if (!string.Equals(ownerName, ownerLocalName, StringComparison.Ordinal))
        {
            var ownerResolution = ResolveType(elemNs, ownerName, line, column);
            memberOwner = ownerResolution.Type;
        }

        XamlMember? member = memberOwner?.TryGetMember(memberName);

        if (memberOwner is not null && member is null)
        {
            ReportMemberNotFound(memberOwner, memberName, line, column);
        }

        int memberId = member is null ? -1 : _builder.AddResolvedMember(member);
        bool memberIsDeferred = member?.IsDeferredContent == true;
        bool childInDeferred = inDeferred || memberIsDeferred;

        if (isEmpty)
        {
            // an empty property element: no value
            return;
        }

        // Parse the property-element's children/text as the member value. An implicit-resource-dictionary
        // member (<X.Resources>) is an AMBIENT BARRIER (the W2b-audit RD finding): a keyed entry parses
        // host-independently, so the CR5 walk must not see the RD host through this boundary.
        var childObjects = new List<int>();
        var textBuffer = new StringBuilder();
        bool pushedRdBarrier = InResourceDictionary(memberName);
        if (pushedRdBarrier)
            _elementTypeStack.Push(new AmbientFrame(null, IsBarrier: true));
        try
        {

        while (_reader.Read())
        {
            if (_reader.NodeType == XmlNodeType.Element)
            {
                childObjects.Add(ParseElement(childInDeferred, InResourceDictionary(memberName), isRoot: false));
            }
            else if (_reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                                                          or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                textBuffer.Append(_reader.Value);
            }
            else if (_reader.NodeType == XmlNodeType.EndElement)
            {
                break;
            }
        }

        }
        finally
        {
            if (pushedRdBarrier)
                _elementTypeStack.Pop();
        }

        if (childObjects.Count > 0)
        {
            // A <X.Resources>-style implicit dictionary: its entries are these children directly.
            if (InResourceDictionary(memberName))
                WarnDuplicateResourceKeys(childObjects);

            if (memberIsDeferred)
            {
                members.Add(new MemberRecord(memberId, XamlValueKind.Deferred, childObjects[0], childObjects.Count, lineInfo));
            }
            else if (childObjects.Count == 1 && member is not null &&
                     (!member.ValueType.IsCollection || SingleChildIsAssignable(member, childObjects[0])))
            {
                members.Add(new MemberRecord(memberId, XamlValueKind.Object, childObjects[0], 0, lineInfo));
            }
            else
            {
                members.Add(new MemberRecord(memberId, XamlValueKind.Items, childObjects[0], childObjects.Count, lineInfo));
            }
        }
        else
        {
            string text = WhitespaceNormalizer.NormalizeElementText(textBuffer.ToString());

            if (text.Length > 0 && member is not null)
                AddValueMember(member, text, members, childInDeferred, lineInfo, line, column);
        }
    }

    private static bool InResourceDictionary(string memberName)
        => memberName is "Resources" or "MergedDictionaries" or "ThemeDictionaries";

    /// <summary>
    /// The W2 CR8 single-child assignability rule (WPF parity): a lone property-element child whose type
    /// is ASSIGNABLE to a collection-typed member is an assignment
    /// (<c>&lt;Transition.Transitions&gt;&lt;TransitionCollection/&gt;…</c> replaces the collection), not
    /// an item run. A child assignable only as an ITEM — or unresolved, or cross-backend-unanswerable —
    /// keeps the historical Items classification (the conservative fallback).
    /// </summary>
    private bool SingleChildIsAssignable(XamlMember member, int childObjectIndex)
    {
        var childType = _builder.ResolvedType(_builder.GetObject(childObjectIndex).TypeId);
        return childType is not null && member.ValueType.IsAssignableFrom(childType.ClrType);
    }

    // ── Setter resolution (X64/X66) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a Style's target type for Setter resolution from its explicit <c>TargetType</c> attribute
    /// (matrix X64/X66). A Style with no <c>TargetType</c> leaves the target unresolved, so any enclosed
    /// Setter is <c>CUR2110</c> — including a Type-keyed control theme (<c>&lt;Style x:Key="{x:Type T}"&gt;</c>),
    /// which must carry <c>TargetType="T"</c> alongside the key (the key is the dictionary entry's type;
    /// <c>TargetType</c> is what binds its Setters — the two are orthogonal, unlike WPF's
    /// <c>DictionaryKeyProperty(TargetType)</c> collapse).
    /// </summary>
    private XamlType? ResolveStyleTargetType(List<MemberRecord> members)
    {
        foreach (var m in members)
        {
            if (m is { MemberId: >= 0, Kind: XamlValueKind.Text } && 
                string.Equals(_builder.ResolvedMemberName(m.MemberId), "TargetType", StringComparison.Ordinal))
            {
                // The reader is still on the Style element here (ParseAttributes left it via MoveToElement),
                // so a prefix-qualified TargetType (my:Foo) binds its prefix from the live scope. Quiet — an
                // unresolvable TargetType leaves the target null, making enclosed Setters CUR2110 (X64/X66).
                return ResolveQualifiedType(_builder.GetString(m.ValueIndex),
                                            appendExtensionSuffix: false,
                                            LineInfo.Line(m.PackedLineInfo),
                                            LineInfo.Column(m.PackedLineInfo),
                                            report: false).Type;
            }
        }

        return null;
    }

    /// <summary>
    /// End-of-object Setter resolution (matrix X64/X66): resolves the <c>Property</c> name against the
    /// lexical Style <c>TargetType</c> and folds the <c>Value</c> through that property's converter.
    /// A Setter with no resolvable owner is <c>CUR2110</c>; an unknown property is <c>CUR2102</c>.
    /// </summary>
    private void ResolveSetter(List<MemberRecord> members, int line, int column)
    {
        // Find the Property name and its member index.
        string? propertyName = null;
        int propertyMemberSlot = -1;

        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].MemberId < 0)
                continue;

            if (string.Equals(_builder.ResolvedMemberName(members[i].MemberId), "Property", StringComparison.Ordinal) &&
                members[i].Kind == XamlValueKind.Text)
            {
                propertyName = _builder.GetString(members[i].ValueIndex);
                propertyMemberSlot = i;
                break;
            }
        }

        if (propertyName is null)
            return; // no Property to resolve

        // A dotted Property name (Owner.Member — an attached property like Grid.Row / TextElement.TextAttributes,
        // or an owner-qualified plain property like Control.Foreground) resolves the OWNER, NOT the lexical
        // TargetType (matrix X64a/X64c, XD4) — and so needs NO enclosing Style TargetType. An unqualified name
        // resolves against the TargetType (CUR2110 when absent). The TargetType is therefore optional here; the
        // helper enforces it only on the unqualified path. The owner namespace captured at parse time (Phase 2 /
        // 4C, stashed in ItemCount) resolves a prefixed (my:Owner.Member) or in-scope-default dotted owner.
        var targetType = _styleTargetStack.Count > 0 ? _styleTargetStack.Peek() : null;
        int ownerNsToken = members[propertyMemberSlot].ItemCount;
        string? capturedOwnerNs = ownerNsToken > 0 ? _builder.GetString(ownerNsToken - 1) : null;
        var targetMember = TryResolveQualifiedSetterMember(propertyName, capturedOwnerNs, targetType, line, column);

        if (targetMember is null)
            return;

        // Rewrite the "Property" member to carry the RESOLVED target member (so the loader's Setter build
        // reads the target UIProperty directly, matrix X117/X129) — the value text stays the property name.
        int targetMemberId = _builder.AddResolvedMember(targetMember);

        members[propertyMemberSlot] = new MemberRecord(
            targetMemberId,
            XamlValueKind.Text,
            members[propertyMemberSlot].ValueIndex,
            0,
            members[propertyMemberSlot].PackedLineInfo
        );

        // Fold the Value through the target property's converter (if any, context-free).
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].MemberId < 0 || members[i].Kind != XamlValueKind.Text)
                continue;

            if (!string.Equals(_builder.ResolvedMemberName(members[i].MemberId), "Value", StringComparison.Ordinal))
                continue;

            string valueText = _builder.GetString(members[i].ValueIndex);

            // A markup-extension Setter.Value ({StaticResource}/{DynamicResource}/{Binding}…) is NOT a
            // foldable literal — re-classify it as an Extension member (the loader resolves it: a
            // {DynamicResource} stores a ResourceReference carrier, matrix X117).
            if (MarkupExtensionParser.LooksLikeExtension(valueText))
            {
                var rewritten = ClassifySetterValueExtension(valueText, members[i].MemberId, line, column);

                if (rewritten is {} extKind)
                    members[i] = extKind;

                break;
            }

            if (_options.FoldConstants &&
                TryFoldLiteral(targetMember, MarkupExtensionParser.UnescapeLiteral(valueText), line, column, out object? folded))
            {
                int constIndex = _builder.AddConstant(folded);
                members[i] = new MemberRecord(members[i].MemberId, XamlValueKind.Folded, constIndex, 0, members[i].PackedLineInfo);
                break;
            }

            // W2e (audit): the TARGET member's route judges an unfolded text Value exactly as
            // AddValueMember judges a direct attribute — a Setter Value="oops" on a route-less property
            // (Template, a Style-typed slot) previously loaded clean and stayed a silent raw string.
            if (targetMember.Route.Kind is RouteKind.None or RouteKind.Ambiguous)
            {
                _builder.Error(XamlDiagnosticCodes.NoConversionRoute,
                               targetMember.Route.Kind == RouteKind.Ambiguous
                                   ? $"Ambiguous conversion routes into '{targetMember.ValueType.Name}' for Setter '{targetMember.Name}' — add a converter for the type."
                                   : $"No conversion route from text to '{targetMember.ValueType.Name}' for Setter '{targetMember.Name}' — " +
                                     "use a markup extension or a property-element value.",
                               line,
                               column);
            }

            break;
        }
    }

    /// <summary>
    /// Re-classifies a markup-extension <c>Setter.Value</c> through the shared <see cref="BuildExtensionValue"/>
    /// funnel (matrix X117) — an <see cref="ExtensionRecord"/> for a live extension, a Folded token for an
    /// intrinsic (<c>{x:Null}</c>/<c>{x:Static}</c>/<c>{x:Type}</c>), or a synthetic Object for a built-in
    /// primitive (<c>{x:Boolean True}</c>). The rewritten member keeps the original <c>Value</c> <c>MemberId</c>
    /// so the loader's <c>BuildSetter</c> recognizes it as the setter value.
    /// </summary>
    private MemberRecord? ClassifySetterValueExtension(string value, int valueMemberId, int line, int column)
    {
        MarkupExtensionNode node;

        try
        {
            node = MarkupExtensionParser.Parse(value, _builder.Source, line, column);
        }
        catch (XamlParseException ex)
        {
            _builder.Report(ex.Diagnostics[0]);
            return null;
        }

        // The setter value rides the SAME classification as every other value position — one funnel, no
        // bespoke switch. BuildExtensionValue produces: {StaticResource}/{DynamicResource} (with the nested-
        // key split, X117/XD7a), {Binding}/custom (structured node ridden verbatim), {x:Null}/{x:Static}/
        // {x:Type} (Folded token), and a built-in primitive ({x:Boolean True}) as a synthetic Object — each of
        // which the loader's BuildSetter already resolves (Folded→ResolveStaticReference, Object→Instantiate).
        // This retires the setter's fail-closed "not supported in v1" arm that dropped curly intrinsics and
        // primitives. member:null — a setter's bindability is settled at seal against the resolved
        // Setter.Property (BuildExtensionValue skips its bindability check when member is null), and its
        // StampResolvedNamespaces runs internally, so the deferred-scope xmlns stamp is preserved.
        if (!BuildExtensionValue(node, member: null, inDeferred: false, line, column, out var valueKind, out int valueIndex))
            return null; // BuildExtensionValue reported the diagnostic (or {TemplateBinding} outside a template)

        // The rewritten member keeps the original Setter.Value MemberId (Name "Value").
        return new MemberRecord(valueMemberId, valueKind, valueIndex, 0, LineInfo.Pack(line, column));
    }

    // ── Duplicate-assignment detection (X5/CUR1101) ───────────────────────────────────────────────

    private void DetectDuplicateAssignments(List<MemberRecord> members)
    {
        // Compare by member name, not MemberId: an attribute and a property element resolve through
        // two TryGetMember calls and so carry distinct XamlMember instances (distinct MemberIds).
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in members)
        {
            if (m.MemberId < 0 || m.Kind == XamlValueKind.Directive)
                continue;

            string name = _builder.ResolvedMemberName(m.MemberId);

            if (name.Length == 0)
                continue;

            if (!seen.Add(name))
            {
                _builder.Error(XamlDiagnosticCodes.DuplicatePropertyAssignment,
                               $"Property '{name}' was set more than once (attribute, property element, or implicit content).",
                               LineInfo.Line(m.PackedLineInfo), LineInfo.Column(m.PackedLineInfo));
            }
        }
    }

    // ── Type resolution ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a Setter <c>Property</c> name that may be <c>Owner.Member</c>-qualified (an attached property
    /// like <c>Grid.Row</c>, or an owner-qualified plain property like <c>Control.Foreground</c>): a dotted
    /// name resolves the OWNER xmlns-aware and ignores the lexical Style <c>TargetType</c> (matrix X64a/X64c,
    /// XD4); an unqualified name resolves against <paramref name="targetType"/> (the only case the TargetType
    /// is the owner — WPF parity). Returns the resolved member, or <c>null</c> after reporting a diagnostic.
    /// The owner resolves against <paramref name="capturedOwnerNs"/> — the namespace captured at the attribute
    /// (Phase 2 / 4C) by resolving the value-embedded prefix (<c>my:Owner.Member</c>) or the in-scope default
    /// for an unprefixed dotted name — falling back to the default UI namespace when none was captured. This
    /// covers built-in, app-default-namespace, AND <c>prefix:</c>-qualified owners.
    /// </summary>
    private XamlMember? TryResolveQualifiedSetterMember(string propertyName, string? capturedOwnerNs, XamlType? targetType, int line, int column)
    {
        int dot = propertyName.IndexOf('.'); // first dot — matches the attached-attribute path (X75)

        if (dot < 0)
        {
            // Unqualified: the TargetType is the owner (the only such case — WPF parity). With no enclosing
            // Style TargetType there is no owner to resolve against — CUR2110. An unknown member is CUR2102
            // against the TargetType (the X66 behavior, preserved).
            if (targetType is null)
            {
                _builder.Error(XamlDiagnosticCodes.SetterNoTarget,
                               "Setter has no resolvable target type (no enclosing Style TargetType).",
                               line,
                               column);

                return null;
            }

            var unqualified = targetType.TryGetMember(propertyName);

            if (unqualified is null)
                ReportMemberNotFound(targetType, propertyName, line, column);

            return unqualified;
        }

        // The owner part may carry a value-embedded prefix (my:Owner) — strip it; its namespace was captured at
        // parse time into capturedOwnerNs (Phase 2). An unprefixed dotted owner resolves against the captured
        // in-scope default (or the UI default when nothing was captured).
        string ownerPart = propertyName.Substring(0, dot);
        string memberName = propertyName.Substring(dot + 1);
        int colon = ownerPart.IndexOf(':');
        string ownerName = colon >= 0 ? ownerPart.Substring(colon + 1) : ownerPart;
        string ownerNs = capturedOwnerNs ?? XmlnsNamespaces.CursorialUi;

        var ownerResolution = ResolveType(ownerNs, ownerName, line, column);

        if (!ownerResolution.IsResolved)
            return null; // ResolveType already emitted CUR2001/CUR2002 naming the owner

        var member = ownerResolution.Type!.TryGetMember(memberName);

        if (member is null)
            ReportMemberNotFound(ownerResolution.Type!, memberName, line, column); // names the OWNER, not the TargetType

        return member;
    }

    /// <summary>
    /// W3: resolves an <c>x:TypeArguments</c>-bearing element to its CLOSED type. The parser owns
    /// prefix→xmlns binding (the live reader scope); the provider owns everything type-system through
    /// <see cref="IXamlGenericTypeProvider.TryGetClosedType"/> (definition arity lookup, argument
    /// closure, intrinsics, the Cursorial array/nullable suffixes). Every failure is a positioned
    /// diagnostic; the returned resolution is NotFound after reporting so the caller's null-type path
    /// runs without double-reporting.
    /// </summary>
    private XamlTypeResolution ResolveClosedElementType(string ns, string localName, string typeArgsText, int line, int column)
    {
        if (_options.MetadataProvider is not IXamlGenericTypeProvider genericProvider)
        {
            _builder.Error(XamlDiagnosticCodes.UnsupportedTypeArguments,
                           "x:TypeArguments requires a metadata provider with generic-instantiation support.",
                           line,
                           column);
            return XamlTypeResolution.NotFound();
        }

        if (!XamlTypeName.TryParseList(typeArgsText, out var argNames, out var grammarError, out var grammarOffset))
        {
            // The grammar's 0-based offset survives into the message (audit — the diagnostic anchors at
            // the element's '<'; the offset pinpoints the failure inside the attribute value, which the
            // position-neutral pre-scan cannot address directly).
            _builder.Error(XamlDiagnosticCodes.UnsupportedTypeArguments,
                           $"Malformed x:TypeArguments '{typeArgsText}' (at offset {grammarOffset}): {grammarError}",
                           line,
                           column);
            return XamlTypeResolution.NotFound();
        }

        var arguments = new QualifiedTypeName[argNames.Count];
        for (int i = 0; i < arguments.Length; i++)
        {
            if (QualifyTypeName(argNames[i], line, column) is not { } qualified)
                return XamlTypeResolution.NotFound(); // unbound prefix — reported inside
            arguments[i] = qualified;
        }

        var closedName = new QualifiedTypeName(ns, localName, arguments, isArray: false, isNullable: false);
        var resolution = genericProvider.TryGetClosedType(in closedName);

        if (resolution.IsResolved)
            return resolution;

        if (resolution.IsAmbiguous)
        {
            _builder.Error(XamlDiagnosticCodes.AmbiguousType,
                           $"Ambiguous type '{localName}': {string.Join(", ", resolution.AmbiguousCandidates!)}.",
                           line,
                           column);
            return resolution;
        }

        _builder.Error(XamlDiagnosticCodes.TypeNotFound,
                       $"Cannot close '{localName}' with x:TypeArguments \"{typeArgsText}\" — the generic definition, " +
                       "an argument, or a constraint failed to resolve.",
                       line,
                       column);
        return XamlTypeResolution.NotFound();
    }

    /// <summary>Binds a parsed type name's prefixes (recursively) against the live reader scope.</summary>
    private QualifiedTypeName? QualifyTypeName(XamlTypeName name, int line, int column)
    {
        var argNs = name.Prefix is null
                        ? _reader.LookupNamespace(string.Empty) is { Length: > 0 } defaultNs ? defaultNs : XmlnsNamespaces.CursorialUi
                        : _reader.LookupNamespace(name.Prefix);

        if (argNs is null or { Length: 0 })
        {
            _builder.Error(XamlDiagnosticCodes.UndeclaredPrefix,
                           $"Unbound xmlns prefix '{name.Prefix}' in x:TypeArguments.",
                           line,
                           column);
            return null;
        }

        var nested = name.TypeArguments.Count == 0
                         ? Array.Empty<QualifiedTypeName>()
                         : new QualifiedTypeName[name.TypeArguments.Count];

        for (int i = 0; i < name.TypeArguments.Count; i++)
        {
            if (QualifyTypeName(name.TypeArguments[i], line, column) is not { } qualified)
                return null;
            ((QualifiedTypeName[])nested)[i] = qualified;
        }

        return new QualifiedTypeName(argNs, name.Name, nested, name.IsArray, name.IsNullable);
    }

    private XamlTypeResolution ResolveType(string ns, string localName, int line, int column)
    {
        if (_options.MetadataProvider is null)
            return XamlTypeResolution.NotFound();

        // Handle using:/clr-namespace: forms by delegating to the provider's URI handling.
        var resolution = _options.MetadataProvider.TryGetType(ns, localName);

        if (resolution.IsResolved)
            return resolution;

        if (resolution.IsAmbiguous)
        {
            _builder.Error(XamlDiagnosticCodes.AmbiguousType,
                           $"Ambiguous type '{localName}': {string.Join(", ", resolution.AmbiguousCandidates!)}.",
                           line,
                           column);

            return resolution;
        }

        ReportTypeNotFound(ns, localName, line, column);
        return resolution;
    }

    private XamlTypeResolution ResolveTypeQuiet(string ns, string localName)
        => _options.MetadataProvider?.TryGetType(ns, localName) ?? XamlTypeResolution.NotFound();

    /// <summary>
    /// Resolves a possibly <c>prefix:</c>-qualified type reference (a Style <c>TargetType</c>, an
    /// <c>{x:Type}</c> argument, or a custom markup-extension name) to its <see cref="XamlType"/>, binding the
    /// prefix from the LIVE reader scope — so <c>my:Foo</c> resolves against whatever xmlns the surrounding
    /// element declares for <c>my</c>, and an unprefixed name uses the in-scope default xmlns (falling back to
    /// the UI default when the reader cannot bind it). The reader MUST be positioned within the declaring
    /// element's scope — true at every call site (the Style element for <c>TargetType</c>; the live
    /// attribute/content node for a markup extension). With <paramref name="appendExtensionSuffix"/> a failed
    /// non-ambiguous lookup retries with the markup-extension <c>"Extension"</c> suffix convention. When
    /// <paramref name="report"/> is set, an unresolved name emits CUR2002 (did-you-mean) or CUR2003 (ambiguous)
    /// at the given position; otherwise resolution is silent and the caller owns the diagnostic.
    /// </summary>
    private XamlTypeResolution ResolveQualifiedType(string maybeQualified, bool appendExtensionSuffix, int line, int column, bool report)
    {
        string name = maybeQualified;
        string prefix = string.Empty;
        int colon = name.IndexOf(':');

        if (colon > 0)
        {
            prefix = name.Substring(0, colon);
            name = name.Substring(colon + 1);
        }

        string ns = _reader.LookupNamespace(prefix) is { Length: > 0 } bound ? bound : XmlnsNamespaces.CursorialUi;

        // Extension position probes the "Extension"-SUFFIXED form FIRST (WPF parity): {Icon …}
        // must mean IconExtension even when a non-extension type named Icon exists in the same
        // xmlns — the suffix convention exists precisely so a sister class cannot shadow the
        // extension. The bare name is the fallback for extensions named without the suffix
        // (Binding, StaticResource). A suffixed AMBIGUITY is a real answer (reported), not a miss.
        XamlTypeResolution resolution;
        if (appendExtensionSuffix)
        {
            resolution = ResolveTypeQuiet(ns, name + "Extension");
            if (resolution is { IsResolved: false, IsAmbiguous: false })
                resolution = ResolveTypeQuiet(ns, name);
        }
        else
        {
            resolution = ResolveTypeQuiet(ns, name);
        }

        if (!resolution.IsResolved && report)
        {
            if (resolution.IsAmbiguous)
            {
                _builder.Error(XamlDiagnosticCodes.AmbiguousType,
                               $"Ambiguous type '{name}': {string.Join(", ", resolution.AmbiguousCandidates!)}.", line, column);
            }
            else
            {
                ReportTypeNotFound(ns, name, line, column);
            }
        }

        return resolution;
    }

    private void ReportTypeNotFound(string ns, string localName, int line, int column)
    {
        string suggestion = DidYouMean.Suggest(localName, _options.MetadataProvider?.GetKnownTypeNames(ns));

        string message = suggestion.Length > 0
                             ? $"Type '{localName}' was not found in namespace '{ns}'. Did you mean '{suggestion}'?"
                             : $"Type '{localName}' was not found in namespace '{ns}'.";

        _builder.Error(XamlDiagnosticCodes.TypeNotFound, message, line, column);
    }

    private void ReportMemberNotFound(XamlType type, string memberName, int line, int column)
    {
        string suggestion = DidYouMean.Suggest(memberName, _options.MetadataProvider?.GetKnownMemberNames(type.ClrType));

        string message = suggestion.Length > 0
                             ? $"No member '{memberName}' on '{type.ClrType.Name}'. Did you mean '{suggestion}'?"
                             : $"No member '{memberName}' on '{type.ClrType.Name}'.";

        _builder.Error(XamlDiagnosticCodes.MemberNotFound, message, line, column);
    }

    // ── Literal folding (XD3) ──────────────────────────────────────────────────────────────────────

    private bool TryFoldLiteral(XamlMember member, string text, int line, int column, out object? folded)
    {
        folded = null;

        var converter = member.Converter;

        if (converter is null || !converter.IsContextFree)
            return false;

        var ctx = new XamlValueContext(_options.ConverterCulture, member, member.ValueType.UnderlyingSystemType!, _builder.Source, line, column);

        try
        {
            folded = converter.ConvertFromString(text, ctx);
            return true;
        }
        catch (XamlParseException ex)
        {
            _builder.Report(ex.Diagnostics[0]);
            return true; // a diagnostic was reported; do not also store as text
        }
        catch (FormatException fe)
        {
            _builder.Error(XamlDiagnosticCodes.ConversionFailed, fe.Message, line, column);
            return true;
        }
    }
}