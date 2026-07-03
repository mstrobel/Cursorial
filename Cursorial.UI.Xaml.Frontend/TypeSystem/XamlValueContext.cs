using System;
using System.Globalization;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The context handed to an <see cref="Cursorial.UI.Xaml.ITypeConverter"/> when it converts a string to a value.
/// Carries everything a converter legitimately needs at parse time (culture, the target member,
/// the source position for diagnostics) without exposing the live object tree — context-free
/// converters ignore all of it. A <c>readonly ref struct</c> so it never escapes the convert call.
/// <para>
/// A CONTEXT-DEPENDENT converter (<see cref="Cursorial.UI.Xaml.ITypeConverter.IsContextFree"/> <c>== false</c>)
/// runs at load, where the loader supplies <see cref="Services"/> — the WPF <c>ITypeDescriptorContext</c>-style
/// service seam. A converter pulls what it needs from it (e.g. a binding-path type resolver bound to the document's
/// xmlns table). It is <see langword="null"/> at parse time (constant-folding) and for free conversions.
/// </para>
/// </summary>
public readonly ref struct XamlValueContext
{
    /// <summary>Creates a value context.</summary>
    public XamlValueContext(
        CultureInfo culture,
        XamlMember? targetMember,
        Type targetType,
        Uri? source,
        int line,
        int column,
        IServiceProvider? services = null)
    {
        Culture = culture;
        TargetMember = targetMember;
        TargetType = targetType;
        Source = source;
        Line = line;
        Column = column;
        Services = services;
    }

    /// <summary>The culture to parse culture-sensitive values with (invariant by default).</summary>
    public CultureInfo Culture { get; }

    /// <summary>The member the value is being assigned to, if known (null for free conversions).</summary>
    public XamlMember? TargetMember { get; }

    /// <summary>The declared CLR type the value is converting to.</summary>
    public Type TargetType { get; }

    /// <summary>The document source URI, if any (for diagnostics raised from a converter).</summary>
    public Uri? Source { get; }

    /// <summary>The 1-based line of the value's source position.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the value's source position.</summary>
    public int Column { get; }

    /// <summary>The loader's service provider for context-dependent conversions (WPF's <c>ITypeDescriptorContext</c>
    /// analog); <see langword="null"/> at parse-time folding and for free conversions. A converter pulls a service
    /// via <see cref="GetService"/>.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>Resolves a service from <see cref="Services"/>; <see langword="null"/> when no provider is present
    /// or the service is unavailable.</summary>
    public object? GetService(Type serviceType) => Services?.GetService(serviceType);
}
