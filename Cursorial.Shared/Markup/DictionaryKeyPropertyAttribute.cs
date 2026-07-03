using System;

namespace Cursorial.Markup;

/// <summary>
/// Names the property whose value supplies a keyed item's implicit dictionary key when no explicit
/// <c>x:Key</c> is given — the WPF <c>[DictionaryKeyProperty]</c> analog. Honored by the XAML metadata
/// providers (matched on the attribute's simple name), walking the inheritance chain.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DictionaryKeyPropertyAttribute : Attribute
{
    /// <summary>Names the property that supplies the implicit dictionary key.</summary>
    public DictionaryKeyPropertyAttribute(string name)
        => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The key-property name.</summary>
    public string Name { get; }
}
