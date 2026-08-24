using System;

using Microsoft.CodeAnalysis;

namespace Cursorial.UI.Xaml.Generator;

/// <summary>
/// The symbol-backed <see cref="IXamlType"/>: a build-time type identity wrapping a Roslyn
/// <see cref="ITypeSymbol"/>. <see cref="UnderlyingSystemType"/> is always <see langword="null"/> (there
/// is no runtime <see cref="System.Type"/> at generator time); consumers that need the symbol — e.g.
/// <c>x:Name</c> field codegen, <c>x:DataType</c> path walking — downcast a resolved
/// <c>XamlType.ClrType</c>/<c>XamlMember.ValueType</c> to this and read <see cref="Symbol"/>.
/// </summary>
internal sealed class RoslynXamlType : IXamlType
{
    public RoslynXamlType(ITypeSymbol symbol) => Symbol = symbol;

    /// <summary>The wrapped Roslyn type symbol (the generator's type identity).</summary>
    public ITypeSymbol Symbol { get; }

    /// <inheritdoc/>
    public string Name => Symbol.Name;

    /// <inheritdoc/>
    public string FullName
    {
        get
        {
            // Match ReflectionXamlType's contract: the DEFINITION's namespace-qualified, arity-suffixed
            // name with no argument list ("Cursorial.UI.Optional`1"), NESTED types '+'-joined
            // ("Ns.Outer+Inner" — System.Type.FullName parity; without the containing-type walk a nested
            // type named UIProperty would collide with the CR5 sentinel in this lane only).
            var symbol = Symbol is INamedTypeSymbol { IsGenericType: true } named ? named.ConstructedFrom : Symbol;

            static string AritySuffixed(ITypeSymbol s)
                => s is INamedTypeSymbol { Arity: > 0 } n ? s.Name + "`" + n.Arity : s.Name;

            var name = AritySuffixed(symbol);
            for (var container = symbol.ContainingType; container is not null; container = container.ContainingType)
                name = AritySuffixed(container) + "+" + name;

            var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } cns ? cns.ToDisplayString() + "." : string.Empty;
            return ns + name;
        }
    }

    /// <inheritdoc/>
    public bool IsAssignableFrom(IXamlType other)
    {
        if (other is not RoslynXamlType { Symbol: { } source })
            return false; // cross-backend: conservative false (the contract's documented fallback)

        for (ITypeSymbol? walk = source; walk is not null; walk = walk.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(walk, Symbol))
                return true;
        }

        foreach (var iface in source.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, Symbol))
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public bool IsCollection => SymbolXamlModel.IsCollectionType(Symbol);

    /// <inheritdoc/>
    public Type? UnderlyingSystemType => null;

    private System.Collections.Generic.IReadOnlyList<ConversionRouteCandidate>? _routeCandidates;

    /// <inheritdoc/>
    public System.Collections.Generic.IReadOnlyList<ConversionRouteCandidate> GetConversionRouteCandidates()
        => _routeCandidates ??= ComputeRouteCandidates(Symbol);

    private static System.Collections.Generic.IReadOnlyList<ConversionRouteCandidate> ComputeRouteCandidates(ITypeSymbol t)
    {
        // The reflection twin's structural exclusions, over symbols.
        if (t.IsAbstract || t.TypeKind is TypeKind.Interface or TypeKind.Array ||
            t.SpecialType is SpecialType.System_String or SpecialType.System_Object ||
            t is not INamedTypeSymbol named)
            return Array.Empty<ConversionRouteCandidate>();

        var candidates = new System.Collections.Generic.List<ConversionRouteCandidate>();

        foreach (var member in named.GetMembers())
        {
            if (member is IMethodSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public, IsGenericMethod: false } method)
            {
                if (method.Name is "op_Implicit" or "op_Explicit" &&
                    SymbolEqualityComparer.Default.Equals(method.ReturnType, t) &&
                    method.Parameters.Length == 1 &&
                    method.Parameters[0].Type is { SpecialType: not SpecialType.System_Object } opSource &&
                    !SymbolEqualityComparer.Default.Equals(opSource, t))
                {
                    candidates.Add(new ConversionRouteCandidate(
                        method.Name == "op_Implicit" ? RouteKind.ImplicitOp : RouteKind.ExplicitOp,
                        new RoslynXamlType(opSource)));
                }
                else if (method.Name == "Parse" &&
                         SymbolEqualityComparer.Default.Equals(method.ReturnType, t) &&
                         method.Parameters.Length == 1 &&
                         method.Parameters[0].Type.SpecialType == SpecialType.System_String)
                {
                    candidates.Add(new ConversionRouteCandidate(RouteKind.ParseMethod, new RoslynXamlType(method.Parameters[0].Type)));
                }
            }
        }

        foreach (var ctor in named.InstanceConstructors)
        {
            if (ctor is { DeclaredAccessibility: Accessibility.Public, Parameters.Length: 1 } &&
                ctor.Parameters[0].Type is { SpecialType: not SpecialType.System_Object } ctorSource &&
                !SymbolEqualityComparer.Default.Equals(ctorSource, t))
                candidates.Add(new ConversionRouteCandidate(RouteKind.Constructor, new RoslynXamlType(ctorSource)));
        }

        return candidates;
    }
}
