using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A resolved, cached XAML-visible type. Built once per CLR type by the metadata provider and shared
/// across documents. The frontend resolves types through <see cref="IXamlTypeMetadataProvider"/> and
/// reads the resolution surface (<see cref="ClrType"/> / <see cref="ContentProperty"/> /
/// <see cref="IsCollection"/> / <see cref="DictionaryKeyType"/> / <see cref="TryGetMember"/>); the
/// runtime delegates (<see cref="Activate"/> / <see cref="AddItem"/> / <see cref="AddDictionaryItem"/>)
/// are the loader's stage-2 concern.
/// </summary>
public sealed class XamlType
{
    /// <summary>Creates a resolved type from an abstract type identity (the symbol backend's path).</summary>
    public XamlType(
        IXamlType clrType,
        Func<object>? activate = null,
        string? contentProperty = null,
        bool isCollection = false,
        Action<object, object?>? addItem = null,
        Action<object, object, object?>? addDictionaryItem = null,
        Type? dictionaryKeyType = null,
        bool requiresInitialize = false,
        Func<string, XamlMember?>? memberResolver = null,
        bool isMarkupExtension = false)
    {
        ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
        Activate = activate;
        ContentProperty = contentProperty;
        IsCollection = isCollection;
        AddItem = addItem;
        AddDictionaryItem = addDictionaryItem;
        DictionaryKeyType = dictionaryKeyType;
        RequiresInitialize = requiresInitialize;
        _memberResolver = memberResolver;
        IsMarkupExtension = isMarkupExtension;
    }

    /// <summary>
    /// Creates a resolved type from a runtime CLR type (the reflection backend's path): the identity is
    /// wrapped in a <see cref="ReflectionXamlType"/>, so the loader's reflection provider keeps passing
    /// <c>typeof(T)</c>.
    /// </summary>
    public XamlType(
        Type clrType,
        Func<object>? activate = null,
        string? contentProperty = null,
        bool isCollection = false,
        Action<object, object?>? addItem = null,
        Action<object, object, object?>? addDictionaryItem = null,
        Type? dictionaryKeyType = null,
        bool requiresInitialize = false,
        Func<string, XamlMember?>? memberResolver = null,
        bool isMarkupExtension = false)
        : this(
            new ReflectionXamlType(clrType ?? throw new ArgumentNullException(nameof(clrType))),
            activate, contentProperty, isCollection, addItem, addDictionaryItem,
            dictionaryKeyType, requiresInitialize, memberResolver, isMarkupExtension) {}

    private readonly Func<string, XamlMember?>? _memberResolver;

    /// <summary>
    /// The type's abstract identity (<see cref="IXamlType"/>): reflection-backed in the loader, symbol-backed
    /// in the generator. Read <see cref="IXamlType.Name"/> / <see cref="IXamlType.IsCollection"/> in the
    /// frontend; the loader reaches through <see cref="IXamlType.UnderlyingSystemType"/> for the runtime type.
    /// </summary>
    public IXamlType ClrType { get; }

    /// <summary>The cached parameterless-activation thunk (loader-side).</summary>
    public Func<object>? Activate { get; }

    /// <summary>
    /// The name of the content property (from a <c>[ContentProperty]</c> attribute), or <c>null</c>
    /// when the type accepts no implicit content (matrix X70).
    /// </summary>
    public string? ContentProperty { get; }

    /// <summary>True when the type is a collection the loader fills via <see cref="AddItem"/>.</summary>
    public bool IsCollection { get; }

    /// <summary>True when the type derives from <c>MarkupExtension</c> — used to recognize a CUSTOM extension
    /// in element form (<c>&lt;local:Foo/&gt;</c>) and provide its value rather than the bare object.</summary>
    public bool IsMarkupExtension { get; }

    /// <summary>The cached collection-add delegate (loader-side).</summary>
    public Action<object, object?>? AddItem { get; }

    /// <summary>The cached keyed-dictionary-add delegate (loader-side).</summary>
    public Action<object, object, object?>? AddDictionaryItem { get; }

    /// <summary>
    /// The key type a dictionary converts <c>x:Key</c> through (matrix XD10 / C-8): a plain
    /// <c>ResourceDictionary</c> keeps literal strings; a theme dictionary collection runs keys
    /// through <c>ThemeVariantKey.Parse</c>. <c>null</c> for a non-dictionary type.
    /// </summary>
    public Type? DictionaryKeyType { get; }

    /// <summary>True when activation must bracket member sets with <c>BeginInit</c>/<c>EndInit</c>.</summary>
    public bool RequiresInitialize { get; }

    /// <summary>
    /// Resolves a member of the given local name, or <c>null</c> if no such member exists. The
    /// metadata provider does the work and caches it; the frontend only branches on the result.
    /// </summary>
    public XamlMember? TryGetMember(string name)
        => _memberResolver?.Invoke(name);
}