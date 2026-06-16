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
    /// <summary>Creates a resolved type.</summary>
    public XamlType(
        Type clrType,
        Func<object>? activate = null,
        string? contentProperty = null,
        bool isCollection = false,
        Action<object, object?>? addItem = null,
        Action<object, object, object?>? addDictionaryItem = null,
        Type? dictionaryKeyType = null,
        bool requiresInitialize = false,
        Func<string, XamlMember?>? memberResolver = null)
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
    }

    private readonly Func<string, XamlMember?>? _memberResolver;

    /// <summary>The underlying CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>The cached parameterless-activation thunk (loader-side).</summary>
    public Func<object>? Activate { get; }

    /// <summary>
    /// The name of the content property (from a <c>[ContentProperty]</c> attribute), or <c>null</c>
    /// when the type accepts no implicit content (matrix X70).
    /// </summary>
    public string? ContentProperty { get; }

    /// <summary>True when the type is a collection the loader fills via <see cref="AddItem"/>.</summary>
    public bool IsCollection { get; }

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
