using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

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

    // The lexical Style.TargetType stack: a Style pushes its resolved target type before its body is
    // walked so an enclosed Setter can resolve Property/Value against it (matrix X64/X66).
    private readonly Stack<XamlType?> _styleTargetStack = new();

    private XamlParser(XmlReader reader, XamlParseOptions options, Uri? source)
    {
        _reader = reader;
        _lineInfo = reader as IXmlLineInfo ?? throw new InvalidOperationException("XmlReader must implement IXmlLineInfo.");
        _options = options;
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
            bool isDtd = xe.Message.IndexOf("DTD", StringComparison.OrdinalIgnoreCase) >= 0
                         || xe.Message.IndexOf("DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0
                         || xe.Message.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0;
            // An undeclared xmlns prefix surfaces as an XmlException ("namespace prefix … is not
            // defined"); re-band it to the resolution diagnostic CUR2003 (matrix X30).
            bool isUndeclaredPrefix = xe.Message.IndexOf("prefix", StringComparison.OrdinalIgnoreCase) >= 0
                                      && (xe.Message.IndexOf("undeclared", StringComparison.OrdinalIgnoreCase) >= 0
                                          || (xe.Message.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0
                                              && xe.Message.IndexOf("defin", StringComparison.OrdinalIgnoreCase) >= 0));

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

        return _builder.Build(_rootType, _rootClassName);
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

        // Resolve the element type.
        var resolution = ResolveType(ns, localName, reportLine, reportColumn);
        int typeId = -1;
        XamlType? type = null;
        if (resolution.IsResolved)
        {
            type = resolution.Type;
            typeId = _builder.AddResolvedType(type);
            if (isRoot && type is not null)
                _rootType = type.ClrType;
        }
        else
        {
            typeId = _builder.AddResolvedType(null);
        }

        bool isResourceDictionary = parentInResourceDictionary
            || string.Equals(localName, "ResourceDictionary", StringComparison.Ordinal);

        var flags = ObjectFlags.None;
        if (isRoot) flags |= ObjectFlags.IsRoot;
        if (parentInDeferred) flags |= ObjectFlags.InDeferredContent;
        if (isResourceDictionary) flags |= ObjectFlags.InResourceDictionary;
        if (type?.RequiresInitialize == true) flags |= ObjectFlags.NeedsBeginInit;

        var members = new List<MemberRecord>();
        bool hasName = false, hasKey = false;

        // Parse attributes (members + directives + xmlns are already consumed by the reader's NS handling).
        ParseAttributes(type, localName, ns, members, parentInDeferred, ref hasName, ref hasKey, reportLine, reportColumn);

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
            ParseElementBody(type, localName, members, parentInDeferred || contentDefers,
                isResourceDictionary && !contentDefers, objectIndex);
        }

        if (pushedStyleTarget)
            _styleTargetStack.Pop();

        // x:Name inside a resource dictionary has no namescope (matrix X104/CUR2304).
        if (hasName && isResourceDictionary)
        {
            _builder.Error(XamlDiagnosticCodes.NameInResourceDictionary,
                "x:Name is not allowed inside a resource dictionary (no namescope).", reportLine, reportColumn);
        }

        // End-of-object: resolve any Setter property against the lexical TargetType (X64/X66).
        if (string.Equals(localName, "Setter", StringComparison.Ordinal))
        {
            ResolveSetter(members, reportLine, reportColumn);
        }

        // Commit members to the document's flat array.
        int memberStart = _builder.MemberCount;
        foreach (var m in members)
            _builder.AddMember(m);

        int subtreeLength = _builder.ObjectCount - subtreeStart;
        var record = new ObjectRecord(typeId, memberStart, (ushort)members.Count, flags, subtreeLength, lineInfo);
        _builder.SetObject(objectIndex, record);
        return objectIndex;
    }

    /// <summary>True when the type's content property is ITemplateContent-typed (its children defer).</summary>
    private bool ContentPropertyDefers(XamlType? type)
    {
        if (type?.ContentProperty is not { } contentName)
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
        ref bool hasName,
        ref bool hasKey,
        int elementLine,
        int elementColumn)
    {
        if (!_reader.MoveToFirstAttribute())
            return;

        do
        {
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

            // xmlns declarations are handled by the reader; skip them.
            if (attrPrefix == "xmlns" || (attrPrefix.Length == 0 && attrLocal == "xmlns"))
                continue;

            // xml:space etc. are handled in the body walk; skip the attribute here.
            if (string.Equals(attrPrefix, "xml", StringComparison.Ordinal))
                continue;

            // x: intrinsic directives.
            if (XmlnsNamespaces.IsIntrinsics(attrNs))
            {
                HandleIntrinsicDirective(attrLocal, value, members, attrLineInfo, attrLine, attrColumn, ref hasName, ref hasKey);
                continue;
            }

            // An undeclared prefix that the reader didn't resolve to a namespace.
            if (attrPrefix.Length > 0 && attrNs.Length == 0)
            {
                _builder.Error(XamlDiagnosticCodes.UndeclaredPrefix,
                    $"Undeclared xmlns prefix '{attrPrefix}'.", attrLine, attrColumn);
                continue;
            }

            // Attached property: Owner.Member (a dotted attribute local name, or a prefixed-owner form).
            // An unprefixed dotted attribute uses the element's default namespace (XML attributes do not
            // inherit the default xmlns, so the reader reports an empty NamespaceURI — matrix X75).
            int dot = attrLocal.IndexOf('.');
            if (dot > 0)
            {
                string ownerNs = attrPrefix.Length == 0 && attrNs.Length == 0 ? elementNamespace : attrNs;
                HandleAttachedAttribute(attrLocal, dot, value, ownerNs, members, inDeferred, attrLineInfo, attrLine, attrColumn, valueColumn);
                continue;
            }

            // A plain member on the owner type.
            HandleMemberAttribute(type, ownerLocalName, attrLocal, value, members, inDeferred, attrLineInfo, attrLine, attrColumn, valueColumn);
        }
        while (_reader.MoveToNextAttribute());

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
        ref bool hasKey)
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
                _builder.Error(XamlDiagnosticCodes.UnsupportedTypeArguments,
                    "x:TypeArguments (generic instantiation) is unsupported in v1.", line, column);
                break;
            case "Reference":
            case "Array":
            case "FieldModifier":
            case "Shared":
            case "Uid":
                _builder.Error(XamlDiagnosticCodes.UnsupportedIntrinsic,
                    $"x:{local} is unsupported in v1.", line, column);
                break;
            default:
                _builder.Error(XamlDiagnosticCodes.UnknownIntrinsic,
                    $"Unknown x: intrinsic '{local}'.", line, column);
                break;
        }
    }

    private MemberRecord DirectiveMember(XamlDirectiveKind kind, string value, int lineInfo)
        => new(-1, XamlValueKind.Directive, _builder.InternString(value), 0, lineInfo, (int)kind);

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
        if (string.Equals(ownerLocalName, "Setter", StringComparison.Ordinal)
            && (string.Equals(memberName, "Property", StringComparison.Ordinal)
                || string.Equals(memberName, "Value", StringComparison.Ordinal)
                || string.Equals(memberName, "TargetType", StringComparison.Ordinal)))
        {
            var setterMember = type?.TryGetMember(memberName);
            int setterMemberId = setterMember is null ? -1 : _builder.AddResolvedMember(setterMember);
            members.Add(new MemberRecord(setterMemberId, XamlValueKind.Text, _builder.InternString(value), 0, lineInfo));
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
                $"Events are not allowed inside deferred content in v1 (member '{memberName}').", line, column);
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

        members.Add(new MemberRecord(memberId, XamlValueKind.Text, _builder.InternString(literal), 0, lineInfo));
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

        var kind = ClassifyExtension(node.Name);

        switch (kind)
        {
            case ExtensionKind.Null:
            {
                int c = _builder.AddConstant(null);
                members.Add(new MemberRecord(memberId, XamlValueKind.Folded, c, 0, lineInfo));
                return -1;
            }
            case ExtensionKind.Type:
            case ExtensionKind.Static:
            {
                // x:Type / x:Static fold at parse against the metadata provider.
                if (TryFoldIntrinsicExtension(kind, node, line, column, out object? folded))
                {
                    int c = _builder.AddConstant(folded);
                    members.Add(new MemberRecord(memberId, XamlValueKind.Folded, c, 0, lineInfo));
                }
                return -1;
            }
            case ExtensionKind.TemplateBinding when !inDeferred:
                _builder.Error(XamlDiagnosticCodes.TemplateBindingOutsideTemplate,
                    "{TemplateBinding} is only legal inside a template body.", node.Line, node.Column);
                return -1;
            case ExtensionKind.StaticResource:
            case ExtensionKind.DynamicResource:
            {
                // Carry the key (the primary positional argument) for X2.
                string key = node.PositionalArguments.Count > 0 && node.PositionalArguments[0].Text is { } t ? t : string.Empty;
                int payload = _builder.InternString(key);
                return _builder.AddExtension(new ExtensionRecord(kind, payload, LineInfo.Pack(node.Line, node.Column)));
            }
            case ExtensionKind.Binding:
            case ExtensionKind.TemplateBinding:
            {
                // A {Binding}/{TemplateBinding} target must be a registered UIProperty (bindable). A
                // CLR-only member is CUR2210 at parse (matrix X120; doc §4.4).
                if (member.Property is null && !member.IsEvent)
                {
                    _builder.Error(XamlDiagnosticCodes.BindingTargetNotBindable,
                        $"Binding target '{member.Name}' is not a bindable property (only registered UIProperties can be data-bound).",
                        node.Line, node.Column);
                    return -1;
                }
                int parsed = _builder.AddParsedExtension(node);
                return _builder.AddExtension(new ExtensionRecord(kind, parsed, LineInfo.Pack(node.Line, node.Column)));
            }
            case ExtensionKind.Custom:
            default:
            {
                // A custom extension: resolve its type to surface a CUR2002 did-you-mean at parse (X53).
                ResolveExtensionType(node, line, column);
                int parsed = _builder.AddParsedExtension(node);
                return _builder.AddExtension(new ExtensionRecord(ExtensionKind.Custom, parsed, LineInfo.Pack(node.Line, node.Column)));
            }
        }
    }

    private static ExtensionKind ClassifyExtension(string name) => name switch
    {
        "x:Null" or "Null" => ExtensionKind.Null,
        "x:Static" => ExtensionKind.Static,
        "x:Type" => ExtensionKind.Type,
        "StaticResource" => ExtensionKind.StaticResource,
        "DynamicResource" => ExtensionKind.DynamicResource,
        "Binding" => ExtensionKind.Binding,
        "TemplateBinding" => ExtensionKind.TemplateBinding,
        _ => ExtensionKind.Custom,
    };

    private bool TryFoldIntrinsicExtension(ExtensionKind kind, MarkupExtensionNode node, int line, int column, out object? folded)
    {
        folded = null;
        string arg = node.PositionalArguments.Count > 0 && node.PositionalArguments[0].Text is { } t ? t : string.Empty;

        if (kind == ExtensionKind.Type)
        {
            // x:Type Button → typeof(Button) resolved through the default xmlns.
            var resolution = ResolveType(XmlnsNamespaces.CursorialUi, arg, node.Line, node.Column);
            if (resolution.IsResolved)
            {
                folded = resolution.Type!.ClrType;
                return true;
            }
            return false; // ResolveType reported the miss
        }

        // x:Static — resolved by the loader's metadata provider (reflection). The frontend has no
        // field/property resolver, so fold to a marker the loader replaces, OR leave it to X2 if no
        // resolver is wired. For the frontend, fold to a typed placeholder carrying the member path.
        folded = new XamlStaticReference(arg);
        return true;
    }

    private void ResolveExtensionType(MarkupExtensionNode node, int line, int column)
    {
        // Strip a namespace prefix for resolution; default xmlns for an unprefixed name.
        string name = node.Name;
        string ns = XmlnsNamespaces.CursorialUi;
        int colon = name.IndexOf(':');
        if (colon > 0)
            name = name.Substring(colon + 1);

        // Custom extensions conventionally end in "Extension"; try both.
        var resolution = ResolveTypeQuiet(ns, name);
        if (!resolution.IsResolved)
            resolution = ResolveTypeQuiet(ns, name + "Extension");
        if (!resolution.IsResolved)
        {
            ReportTypeNotFound(ns, name, node.Line, node.Column);
        }
    }

    // ── Element body parsing (content + property elements + collections) ──────────────────────────

    private void ParseElementBody(
        XamlType? type,
        string ownerLocalName,
        List<MemberRecord> members,
        bool inDeferred,
        bool inResourceDictionary,
        int ownerObjectIndex)
    {
        var textBuffer = new StringBuilder();
        bool preserveSpace = false;
        var contentChildren = new List<int>();
        bool sawPropertyElement = false;
        bool sawContentChild = false;

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
        preserveSpace = _reader.XmlSpace == XmlSpace.Preserve;

        // Commit content text (if any non-whitespace) as the content-property value.
        if (!sawContentChild)
        {
            string text = textBuffer.ToString();
            string normalized = preserveSpace ? text : WhitespaceNormalizer.NormalizeElementText(text);
            if (preserveSpace ? text.Length > 0 : normalized.Length > 0)
            {
                AddContentText(type, ownerLocalName, normalized, members,
                    LineInfo.Pack(_lineInfo.LineNumber, _lineInfo.LinePosition),
                    inDeferred);
            }
        }

        if (sawContentChild && contentChildren.Count > 0)
        {
            CommitContentChildren(type, ownerLocalName, contentChildren, members,
                LineInfo.Pack(_lineInfo.LineNumber, _lineInfo.LinePosition));
        }

        // X5: a property set as both attribute and property element.
        if (sawPropertyElement)
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

        // Parse the property-element's children/text as the member value.
        var childObjects = new List<int>();
        var textBuffer = new StringBuilder();

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

        if (childObjects.Count > 0)
        {
            if (memberIsDeferred)
            {
                members.Add(new MemberRecord(memberId, XamlValueKind.Deferred, childObjects[0], childObjects.Count, lineInfo));
            }
            else if (childObjects.Count == 1 && member?.ValueType is { } vt && !IsCollectionMember(member))
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
    /// True when a property-element member's value type is collection-shaped — an <see cref="IList"/>,
    /// an <see cref="IList{T}"/>, or a <c>ResourceDictionary</c> (the loader fills it rather than
    /// replacing it). Drives the single-child Object-vs-Items decision so a lone child of a read-only
    /// collection property (one <c>&lt;Style&gt;</c> under <c>&lt;Panel.Styles&gt;</c>) is emitted as an
    /// <c>Items</c> run, not an <c>Object</c> replacement. The loader's <c>IsReadOnlyCollectionMember</c>
    /// is the definitive runtime classification; this parse-time check is its mirror (a member this
    /// predicate flags must be one the loader's collection-fill path handles).
    /// </summary>
    private static bool IsCollectionMember(XamlMember member)
    {
        var valueType = member.ValueType;
        if (valueType == typeof(string) || valueType == typeof(object))
            return false;
        if (typeof(IList).IsAssignableFrom(valueType))
            return true;
        if (string.Equals(valueType.Name, "ResourceDictionary", StringComparison.Ordinal))
            return true;
        foreach (var iface in valueType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                return true;
        }
        return false;
    }

    // ── Setter resolution (X64/X66) ──────────────────────────────────────────────────────────────

    /// <summary>Resolves a Style's <c>TargetType</c> attribute value to a <see cref="XamlType"/>.</summary>
    private XamlType? ResolveStyleTargetType(List<MemberRecord> members)
    {
        foreach (var m in members)
        {
            if (m.MemberId < 0 || m.Kind != XamlValueKind.Text)
                continue;
            if (!string.Equals(_builder.ResolvedMemberName(m.MemberId), "TargetType", StringComparison.Ordinal))
                continue;
            string typeName = _builder.GetString(m.ValueIndex);
            return ResolveTypeQuiet(XmlnsNamespaces.CursorialUi, typeName).Type;
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
        var targetType = _styleTargetStack.Count > 0 ? _styleTargetStack.Peek() : null;
        if (targetType is null)
        {
            _builder.Error(XamlDiagnosticCodes.SetterNoTarget,
                "Setter has no resolvable target type (no enclosing Style TargetType).", line, column);
            return;
        }

        // Find the Property name and its member index.
        string? propertyName = null;
        int propertyMemberSlot = -1;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].MemberId < 0)
                continue;
            if (string.Equals(_builder.ResolvedMemberName(members[i].MemberId), "Property", StringComparison.Ordinal)
                && members[i].Kind == XamlValueKind.Text)
            {
                propertyName = _builder.GetString(members[i].ValueIndex);
                propertyMemberSlot = i;
                break;
            }
        }

        if (propertyName is null)
            return; // no Property to resolve

        var targetMember = targetType.TryGetMember(propertyName);
        if (targetMember is null)
        {
            ReportMemberNotFound(targetType, propertyName, line, column);
            return;
        }

        // Rewrite the "Property" member to carry the RESOLVED target member (so the loader's Setter build
        // reads the target UIProperty directly, matrix X117/X129) — the value text stays the property name.
        int targetMemberId = _builder.AddResolvedMember(targetMember);
        members[propertyMemberSlot] = new MemberRecord(
            targetMemberId, XamlValueKind.Text, members[propertyMemberSlot].ValueIndex, 0, members[propertyMemberSlot].PackedLineInfo);

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
                if (rewritten is { } extKind)
                    members[i] = extKind;
                break;
            }

            if (_options.FoldConstants && TryFoldLiteral(targetMember, MarkupExtensionParser.UnescapeLiteral(valueText), line, column, out object? folded))
            {
                int constIndex = _builder.AddConstant(folded);
                members[i] = new MemberRecord(members[i].MemberId, XamlValueKind.Folded, constIndex, 0, members[i].PackedLineInfo);
            }
            break;
        }
    }

    /// <summary>
    /// Re-classifies a markup-extension <c>Setter.Value</c> as an <see cref="ExtensionRecord"/> member
    /// (matrix X117). The rewritten member keeps the original <c>Value</c> <c>MemberId</c> so the loader
    /// recognizes it as the setter value; <c>{x:Null}</c> folds to the null constant.
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

        // The rewritten member keeps the original Setter.Value MemberId (Name "Value").
        var kind = ClassifyExtension(node.Name);
        switch (kind)
        {
            case ExtensionKind.StaticResource:
            case ExtensionKind.DynamicResource:
            {
                string key = node.PositionalArguments.Count > 0 && node.PositionalArguments[0].Text is { } t ? t : string.Empty;
                int payload = _builder.InternString(key);
                int extIndex = _builder.AddExtension(new ExtensionRecord(kind, payload, LineInfo.Pack(node.Line, node.Column)));
                return new MemberRecord(valueMemberId, XamlValueKind.Extension, extIndex, 0, LineInfo.Pack(line, column));
            }
            case ExtensionKind.Null:
            {
                int c = _builder.AddConstant(null);
                return new MemberRecord(valueMemberId, XamlValueKind.Folded, c, 0, LineInfo.Pack(line, column));
            }
            default:
                _builder.Error(XamlDiagnosticCodes.ConversionFailed,
                    $"A Setter.Value markup extension of kind '{node.Name}' is not supported in v1.", node.Line, node.Column);
                return null;
        }
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
                    $"Property '{name}' was set both as an attribute and as a property element.",
                    LineInfo.Line(m.PackedLineInfo), LineInfo.Column(m.PackedLineInfo));
            }
        }
    }

    // ── Type resolution ──────────────────────────────────────────────────────────────────────────

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
                $"Ambiguous type '{localName}': {string.Join(", ", resolution.AmbiguousCandidates!)}.", line, column);
            return resolution;
        }

        ReportTypeNotFound(ns, localName, line, column);
        return resolution;
    }

    private XamlTypeResolution ResolveTypeQuiet(string ns, string localName)
        => _options.MetadataProvider?.TryGetType(ns, localName) ?? XamlTypeResolution.NotFound();

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

        var ctx = new XamlValueContext(_options.ConverterCulture, member, member.ValueType, _builder.Source, line, column);
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
