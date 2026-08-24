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
            // name with no argument list ("Cursorial.UI.Optional`1"), so cross-backend comparisons agree.
            var symbol = Symbol is INamedTypeSymbol { IsGenericType: true } named ? named.ConstructedFrom : Symbol;
            var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } cns ? cns.ToDisplayString() + "." : string.Empty;
            var arity = symbol is INamedTypeSymbol { Arity: > 0 } n ? "`" + n.Arity : string.Empty;
            return ns + symbol.Name + arity;
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
}
