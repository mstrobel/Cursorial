using System;

namespace Cursorial.UI.Xaml;

/// <summary>
/// Loader-side conveniences for reaching the runtime <see cref="System.Type"/> behind an
/// <see cref="IXamlType"/> identity. The loader is always driven by a reflection backend
/// (<see cref="ReflectionXamlMetadata"/> / a test provider passing <c>typeof(T)</c>), so
/// <see cref="IXamlType.UnderlyingSystemType"/> is never null on this path — these helpers assert that
/// and hand back the non-null <see cref="System.Type"/> the loader needs for activation, <c>typeof</c>
/// comparisons, and assignability. A symbol backend never reaches the loader.
/// </summary>
internal static class XamlReflectionTypeExtensions
{
    /// <summary>The runtime CLR type behind a resolved <see cref="XamlType"/> (loader path: never null).</summary>
    public static Type SystemType(this XamlType type) => type.ClrType.UnderlyingSystemType!;

    /// <summary>The runtime value type behind a resolved <see cref="XamlMember"/> (loader path: never null).</summary>
    public static Type SystemType(this XamlMember member) => member.ValueType.UnderlyingSystemType!;
}
