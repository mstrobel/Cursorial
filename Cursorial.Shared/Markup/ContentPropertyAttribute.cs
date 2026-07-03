using System;

namespace Cursorial.Markup;

/// <summary>
/// Names the content property of a type — the member implicit child content (and content text) fills.
/// The canonical, framework-decoratable form (in <c>Cursorial.Shared</c>, referenced by every layer);
/// both XAML metadata providers (the reflection loader and the build-time symbol provider) honor it,
/// walking the inheritance chain so a base type's declaration covers its subclasses. A type without one
/// — directly or inherited — has no content property and rejects implicit content (<c>CUR2104</c>).
/// </summary>
/// <remarks>
/// Matched by the providers on the attribute's simple name (<c>ContentPropertyAttribute</c>), so an app
/// may use this attribute or any equivalently named one. WPF parity: <c>[ContentProperty("Name")]</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ContentPropertyAttribute : Attribute
{
    /// <summary>Names the content property.</summary>
    public ContentPropertyAttribute(string name)
        => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The content-property name.</summary>
    public string Name { get; }
}
