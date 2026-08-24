using System;
using System.Collections;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The reflection-backed <see cref="IXamlType"/>: the default identity for a <see cref="XamlType"/> or
/// <see cref="XamlMember"/> built from a runtime <see cref="System.Type"/>. The <see cref="XamlType"/> /
/// <see cref="XamlMember"/> convenience constructors that accept a <see cref="System.Type"/> wrap it in
/// one of these, so the runtime loader's reflection provider keeps passing <c>typeof(T)</c> unchanged.
/// The generator supplies its own symbol-backed <see cref="IXamlType"/> instead.
/// </summary>
public sealed class ReflectionXamlType : IXamlType
{
    private bool? _isCollection;

    /// <summary>Wraps a runtime CLR type as an <see cref="IXamlType"/>.</summary>
    public ReflectionXamlType(Type systemType)
        => UnderlyingSystemType = systemType ?? throw new ArgumentNullException(nameof(systemType));

    /// <summary>The wrapped runtime type (never <see langword="null"/> for a reflection backend).</summary>
    public Type UnderlyingSystemType { get; }

    /// <inheritdoc/>
    public string Name => UnderlyingSystemType.Name;

    /// <inheritdoc/>
    public string FullName
    {
        get
        {
            // A generic instantiation reports its DEFINITION's full name (arity-suffixed, no argument
            // list) — Type.FullName on a closed generic embeds assembly-qualified arguments, which the
            // cross-backend comparison contract excludes.
            var type = UnderlyingSystemType.IsConstructedGenericType
                           ? UnderlyingSystemType.GetGenericTypeDefinition()
                           : UnderlyingSystemType;
            return type.FullName ?? type.Name;
        }
    }

    /// <inheritdoc/>
    public bool IsAssignableFrom(IXamlType other)
        => other.UnderlyingSystemType is { } source && UnderlyingSystemType.IsAssignableFrom(source);

    private IReadOnlyList<ConversionRouteCandidate>? _routeCandidates;

    /// <inheritdoc/>
    public IReadOnlyList<ConversionRouteCandidate> GetConversionRouteCandidates()
        => _routeCandidates ??= ComputeRouteCandidates(UnderlyingSystemType);

    private static IReadOnlyList<ConversionRouteCandidate> ComputeRouteCandidates(Type t)
    {
        // Mirrors ConversionBridge's structural exclusions (the shared RouteProbe applies the semantic
        // rules — precedence, viability, denials — over these raw candidates).
        if (t.IsAbstract || t.IsInterface || t.IsArray || t == typeof(string) || t == typeof(object))
            return Array.Empty<ConversionRouteCandidate>();

        var candidates = new List<ConversionRouteCandidate>();

        foreach (var method in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (method.IsGenericMethod)
                continue;

            var parameters = method.GetParameters();

            if (method.Name is "op_Implicit" or "op_Explicit" && method.ReturnType == t &&
                parameters.Length == 1 && parameters[0].ParameterType is { } opSource && opSource != t && opSource != typeof(object))
            {
                candidates.Add(new ConversionRouteCandidate(
                    method.Name == "op_Implicit" ? RouteKind.ImplicitOp : RouteKind.ExplicitOp,
                    new ReflectionXamlType(opSource)));
            }
            else if (method.Name == "Parse" && method.ReturnType == t &&
                     parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            {
                candidates.Add(new ConversionRouteCandidate(RouteKind.ParseMethod, new ReflectionXamlType(typeof(string))));
            }
        }

        foreach (var ctor in t.GetConstructors())
        {
            var ctorParameters = ctor.GetParameters();
            if (ctorParameters.Length == 1 && ctorParameters[0].ParameterType is { } ctorSource && ctorSource != t && ctorSource != typeof(object))
                candidates.Add(new ConversionRouteCandidate(RouteKind.Constructor, new ReflectionXamlType(ctorSource)));
        }

        return candidates;
    }

    /// <inheritdoc/>
    public bool IsCollection => _isCollection ??= ComputeIsCollection(UnderlyingSystemType);

    // Mirrors the parser's historical IsCollectionMember predicate verbatim (string/object are never
    // collections; ResourceDictionary is matched by name because the frontend cannot reference it).
    private static bool ComputeIsCollection(Type type)
    {
        if (type == typeof(string) || type == typeof(object))
            return false;
        if (typeof(IList).IsAssignableFrom(type))
            return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>))
            return true; // the member may be DECLARED as the interface itself (IList<T> Children)
        if (string.Equals(type.Name, "ResourceDictionary", StringComparison.Ordinal))
            return true;
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                return true;
        }
        return false;
    }
}
