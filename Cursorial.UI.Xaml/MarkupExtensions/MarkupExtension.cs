// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The base type for a user-authored markup extension (matrix X125/X126; doc §4.2). A custom extension
/// is activated by the loader (parameterless ctor, or positional args mapped by the
/// <see cref="ConstructorArgumentAttribute"/>/ctor-arity convention), its named members are set, then
/// <see cref="ProvideValue"/> is called and the returned value is assigned to the target member through
/// the ordinary converter ladder. The shape mirrors WPF's <c>System.Windows.Markup.MarkupExtension</c>.
/// </summary>
public abstract class MarkupExtension
{
    /// <summary>Produces the value the loader assigns to the target member.</summary>
    /// <param name="serviceProvider">
    /// The ambient services available during the provide-value call: <see cref="IProvideValueTarget"/>,
    /// <see cref="IRootObjectProvider"/>, <see cref="IXamlLineInfo"/>, <see cref="IAmbientResources"/>,
    /// and <see cref="INameScopeProvider"/> (matrix X125). Query via
    /// <see cref="IServiceProvider.GetService"/>.
    /// </param>
    public abstract object? ProvideValue(IServiceProvider serviceProvider);
}

/// <summary>
/// Marks a property as the target of a positional constructor argument by position (matrix X126; the
/// WPF <c>ConstructorArgumentAttribute</c> analog). Optional — the loader also accepts a matching
/// positional constructor or maps positionals to writable members by declaration order.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ConstructorArgumentAttribute(string argumentName) : Attribute
{
    /// <summary>The name of the constructor argument this property mirrors.</summary>
    public string ArgumentName { get; } = argumentName;
}

/// <summary>The provide-value target (matrix X125): the object + member the extension result is assigned to.</summary>
public interface IProvideValueTarget
{
    /// <summary>The object the value will be set on.</summary>
    object? TargetObject { get; }

    /// <summary>The member (a <see cref="UIProperty"/> or a CLR <c>PropertyInfo</c>-shaped descriptor) the value targets.</summary>
    object? TargetProperty { get; }
}

/// <summary>The root-object provider (matrix X125): the document root being instantiated.</summary>
public interface IRootObjectProvider
{
    /// <summary>The root object of the object graph under construction.</summary>
    object? RootObject { get; }
}

/// <summary>The source line/column an extension was authored at (matrix X125).</summary>
public interface IXamlLineInfo
{
    /// <summary>The 1-based source line.</summary>
    int LineNumber { get; }

    /// <summary>The 1-based source column.</summary>
    int LinePosition { get; }
}

/// <summary>The ambient resource lookup available during a provide-value call (matrix X125).</summary>
public interface IAmbientResources
{
    /// <summary>Resolves a resource key against the lexical scope stack captured at the extension's site.</summary>
    bool TryFindResource(object key, out object? value);
}

/// <summary>The name-scope provider (matrix X125): the namescope an extension's target lives in.</summary>
public interface INameScopeProvider
{
    /// <summary>The enclosing name scope, or <see langword="null"/>.</summary>
    INameScope? NameScope { get; }
}

/// <summary>Provides a XamlValueContext, needed by some markup extensions and type converters.</summary>
public interface IXamlValueContextProvider
{
    /// <summary>The currently <see cref="XamlValueContext"/>.</summary>
    XamlValueContext Context { get; }
}