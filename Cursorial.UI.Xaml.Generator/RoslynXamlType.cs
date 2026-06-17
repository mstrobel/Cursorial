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
    public bool IsCollection => SymbolXamlModel.IsCollectionType(Symbol);

    /// <inheritdoc/>
    public Type? UnderlyingSystemType => null;
}
