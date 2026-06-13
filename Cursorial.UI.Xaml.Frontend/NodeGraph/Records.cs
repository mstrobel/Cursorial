namespace Cursorial.UI.Xaml;

/// <summary>
/// One object node in the depth-first-contiguous node graph (proposal §3.1). Children are stored
/// contiguous and depth-first, so any subtree is <c>(startIndex, <see cref="SubtreeLength"/>)</c> —
/// which is exactly a deferred-content slice. Internal: the node-graph shape is the contract, not
/// the field names (matrix §0.2 — surfaced via <c>InternalsVisibleTo</c>).
/// </summary>
internal readonly struct ObjectRecord
{
    public ObjectRecord(int typeId, int memberStart, ushort memberCount, ObjectFlags flags, int subtreeLength, int lineInfo)
    {
        TypeId = typeId;
        MemberStart = memberStart;
        MemberCount = memberCount;
        Flags = flags;
        SubtreeLength = subtreeLength;
        PackedLineInfo = lineInfo;
    }

    /// <summary>Index into <see cref="XamlDocument.ResolvedTypes"/> (the resolved <see cref="XamlType"/>); -1 when unresolved.</summary>
    public readonly int TypeId;

    /// <summary>Index into <see cref="XamlDocument.Members"/> — the first of a contiguous run.</summary>
    public readonly int MemberStart;

    /// <summary>The count of members in this object's run.</summary>
    public readonly ushort MemberCount;

    /// <summary>The object flags (root / has-name / has-key / needs-init / deferred / …).</summary>
    public readonly ObjectFlags Flags;

    /// <summary>The number of <see cref="ObjectRecord"/>s in this subtree including self — the O(1) slice/skip length.</summary>
    public readonly int SubtreeLength;

    /// <summary>The packed 1-based source position (see <see cref="LineInfo"/>).</summary>
    public readonly int PackedLineInfo;

    public bool HasFlag(ObjectFlags flag) => (Flags & flag) != 0;
}

/// <summary>
/// One member assignment on an <see cref="ObjectRecord"/> (proposal §3.1). The interpretation of
/// <see cref="ValueIndex"/> depends on <see cref="Kind"/>.
/// </summary>
internal readonly struct MemberRecord
{
    public MemberRecord(int memberId, XamlValueKind kind, int valueIndex, int itemCount, int lineInfo, int directiveKind = -1)
    {
        MemberId = memberId;
        Kind = kind;
        ValueIndex = valueIndex;
        ItemCount = itemCount;
        PackedLineInfo = lineInfo;
        DirectiveKind = directiveKind;
    }

    /// <summary>Index into <see cref="XamlDocument.ResolvedMembers"/>; -1 for a directive (no member).</summary>
    public readonly int MemberId;

    /// <summary>The value kind (see <see cref="XamlValueKind"/>).</summary>
    public readonly XamlValueKind Kind;

    /// <summary>
    /// The value index: <c>Folded</c>→<see cref="XamlDocument.Constants"/>; <c>Text</c>/<c>Event</c>/
    /// <c>Directive</c>→<see cref="XamlDocument.Strings"/>; <c>Object</c>/<c>Items</c>/<c>Deferred</c>→
    /// <see cref="XamlDocument.Objects"/> (record index / first-item index / slice head);
    /// <c>Extension</c>→<see cref="XamlDocument.Extensions"/>.
    /// </summary>
    public readonly int ValueIndex;

    /// <summary>For <c>Items</c>: the count of objects in the run. Otherwise 0.</summary>
    public readonly int ItemCount;

    /// <summary>The packed 1-based source position.</summary>
    public readonly int PackedLineInfo;

    /// <summary>For <c>Directive</c>: the <see cref="XamlDirectiveKind"/> as an int; -1 otherwise.</summary>
    public readonly int DirectiveKind;
}

/// <summary>
/// A parsed markup extension (proposal §3.1). Built-ins fold or carry a key/binding payload;
/// custom extensions reference an ordinary object subtree.
/// </summary>
internal readonly struct ExtensionRecord
{
    public ExtensionRecord(ExtensionKind kind, int payload, int lineInfo, bool payloadIsParsedExtension = false)
    {
        Kind = kind;
        PayloadIsParsedExtension = payloadIsParsedExtension;
        Payload = payload;
        PackedLineInfo = lineInfo;
    }

    /// <summary>The extension kind.</summary>
    public readonly ExtensionKind Kind;

    /// <summary>
    /// Discriminates a <c>*Resource</c> key whose key is itself a markup extension. When
    /// <see langword="false"/> (the literal-key form, the default), a <c>*Resource</c>
    /// <see cref="Payload"/> indexes <see cref="XamlDocument.Strings"/>. When <see langword="true"/>
    /// (e.g. <c>{DynamicResource {x:Static ThemeKeys.X}}</c>), it indexes
    /// <see cref="XamlDocument.ParsedExtensions"/> — the nested key extension the loader resolves at
    /// instantiate. Irrelevant for non-<c>*Resource</c> kinds. (Slots into the byte-enum padding after
    /// <see cref="Kind"/> — the struct stays 12 bytes.)
    /// </summary>
    public readonly bool PayloadIsParsedExtension;

    /// <summary>
    /// The payload index: <c>Static</c>/<c>Type</c>/<c>Null</c>→<see cref="XamlDocument.Constants"/>
    /// (folded at parse); <c>*Resource</c>→<see cref="XamlDocument.Strings"/> (the literal key) when
    /// <see cref="PayloadIsParsedExtension"/> is false, else <see cref="XamlDocument.ParsedExtensions"/>
    /// (a nested key markup extension producing the key at instantiate); <c>Binding</c>/
    /// <c>TemplateBinding</c>→<see cref="XamlDocument.ParsedExtensions"/> (the structured argument
    /// node); <c>Custom</c>→<see cref="XamlDocument.ParsedExtensions"/>.
    /// </summary>
    public readonly int Payload;

    /// <summary>The packed 1-based source position.</summary>
    public readonly int PackedLineInfo;
}
