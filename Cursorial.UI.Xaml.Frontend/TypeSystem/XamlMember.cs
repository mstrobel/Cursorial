using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A resolved, cached member (property / event / attached property) of a <see cref="XamlType"/>.
/// Built once per (owner type, member name) by the metadata provider. The frontend resolves
/// members through <see cref="XamlType.TryGetMember"/> and reads <see cref="ValueType"/> /
/// <see cref="Converter"/> / <see cref="IsEvent"/> / <see cref="IsDeferredContent"/>; it never
/// reflects and never touches the runtime delegates (<see cref="SetClr"/> / <see cref="Get"/>),
/// which are the loader's stage-2 concern.
/// </summary>
/// <remarks>
/// The frontend stays type-system-free with respect to <c>Cursorial.UI</c>: a registered Fork A
/// <c>UIProperty</c> is carried as the opaque <see cref="Property"/> reference (the loader downcasts
/// it). The parser only needs to know <i>whether</i> a member maps to a registered property to honor
/// the XD4 resolution order; it does not need the property's concrete type.
/// </remarks>
public sealed class XamlMember
{
    /// <summary>Creates a resolved member from an abstract value-type identity (the symbol backend's path).</summary>
    public XamlMember(
        string name,
        IXamlType valueType,
        object? property = null,
        Action<object, object?>? setClr = null,
        Func<object, object?>? get = null,
        ITypeConverter? converter = null,
        bool isEvent = false,
        bool isAttachable = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        Property = property;
        SetClr = setClr;
        Get = get;
        Converter = converter;
        IsEvent = isEvent;
        IsAttachable = isAttachable;
    }

    /// <summary>
    /// Creates a resolved member from a runtime CLR value type (the reflection backend's path): the value
    /// type is wrapped in a <see cref="ReflectionXamlType"/>, so the loader's reflection provider keeps
    /// passing <c>typeof(T)</c>.
    /// </summary>
    public XamlMember(
        string name,
        Type valueType,
        object? property = null,
        Action<object, object?>? setClr = null,
        Func<object, object?>? get = null,
        ITypeConverter? converter = null,
        bool isEvent = false,
        bool isAttachable = false)
        : this(
            name,
            new ReflectionXamlType(valueType ?? throw new ArgumentNullException(nameof(valueType))),
            property, setClr, get, converter, isEvent, isAttachable) {}

    /// <summary>The member's local name (e.g. <c>Content</c>, <c>Grid.Row</c> uses just <c>Row</c>).</summary>
    public string Name { get; }

    /// <summary>
    /// The member's declared value type, as an abstract identity (<see cref="IXamlType"/>):
    /// reflection-backed in the loader, symbol-backed in the generator. The frontend reads
    /// <see cref="IXamlType.IsCollection"/> for the Object-vs-Items decision; the loader reaches through
    /// <see cref="IXamlType.UnderlyingSystemType"/> for conversion and assignability.
    /// </summary>
    public IXamlType ValueType { get; }

    /// <summary>
    /// The registered Fork A <c>UIProperty</c> backing this member, as an opaque reference, or
    /// <c>null</c> for a CLR-only member. Non-null means the loader assigns via <c>SetValue</c>
    /// (XD4 rule 1); null means the cached CLR setter delegate (XD4 rule 2).
    /// </summary>
    public object? Property { get; }

    /// <summary>The cached CLR setter delegate (loader-side; null for a <c>UIProperty</c>-only member).</summary>
    public Action<object, object?>? SetClr { get; }

    /// <summary>The cached CLR getter delegate (loader-side; used for read-only collection members).</summary>
    public Func<object, object?>? Get { get; }

    /// <summary>The converter for this member's value type, if one is registered.</summary>
    public ITypeConverter? Converter { get; }

    /// <summary>True when this member is a CLR event (binds to a root-instance handler in stage 2).</summary>
    public bool IsEvent { get; }

    /// <summary>True when this is an attached property (resolved against the owner type at parse).</summary>
    public bool IsAttachable { get; }

    /// <summary>
    /// True when the member's <i>static</i> value type is the deferred-content contract type
    /// (<c>ITemplateContent</c>): the matrix XD6 derivation. The frontend cannot reference
    /// <c>ITemplateContent</c> (it lives in <c>Cursorial.UI</c>), so the loader's metadata provider
    /// stamps this flag when it builds the member. Independent of the value's runtime type.
    /// </summary>
    public bool IsDeferredContent { get; init; }

    /// <summary>
    /// The parse-resolved TARGET of a <c>UIProperty</c>-typed member's token VALUE (W2 CR5): for
    /// <c>&lt;DoubleTransition Property="Opacity"&gt;</c> the parser resolves <c>"Opacity"</c> against the
    /// lexical scope and stamps the resolved member here (its <see cref="Property"/> carries the property
    /// identity — the opaque <c>UIProperty</c> in the reflection lane, the owner symbol in the Roslyn
    /// lane; its <see cref="Name"/> the property name). ORTHOGONAL to <see cref="Property"/>, which names
    /// the backing property OF this member and drives XD4 assignment routing — this slot is the resolved
    /// VALUE the assignment delivers. Null everywhere except the parser's CR5 rewrite.
    /// </summary>
    public XamlMember? ResolvedPropertyMember { get; private init; }

    /// <summary>Clones this member with <see cref="ResolvedPropertyMember"/> stamped (the CR5 rewrite).</summary>
    internal XamlMember WithResolvedPropertyMember(XamlMember resolved)
        => new(Name, ValueType, Property, SetClr, Get, Converter, IsEvent, IsAttachable)
           {
               IsDeferredContent = IsDeferredContent,
               ResolvedPropertyMember = resolved,
           };
}