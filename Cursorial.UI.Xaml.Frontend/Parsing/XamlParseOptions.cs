using System;
using System.Globalization;

namespace Cursorial.UI.Xaml;

/// <summary>
/// The frontend parser's inputs: the metadata provider (drives type/member/converter resolution),
/// the diagnostic mode, the constant-folding flag, and the converter culture. The loader's public
/// <c>XamlLoaderOptions</c> projects onto this; a pure-syntax frontend test can pass a stub or null
/// provider (type-resolution rows inject the real provider).
/// </summary>
/// <remarks>
/// This type and the loader's <c>XamlLoaderOptions</c> are parallel by design — the frontend is a
/// netstandard2.0 assembly that cannot reference the loader's types, so the two option types are kept
/// in sync manually. Adding a new option requires updating both and <c>XamlLoaderOptions.ToParseOptions</c>.
/// </remarks>
public sealed class XamlParseOptions
{
    /// <summary>The type metadata provider; <c>null</c> means types/members do not resolve (pure-syntax parsing).</summary>
    public IXamlTypeMetadataProvider? MetadataProvider { get; init; }

    /// <summary>How diagnostics surface. Default <see cref="XamlDiagnosticMode.ThrowOnFirstError"/>.</summary>
    public XamlDiagnosticMode DiagnosticMode { get; init; } = XamlDiagnosticMode.ThrowOnFirstError;

    /// <summary>When true, context-free literals fold to constants at parse (matrix XD3). Default true.</summary>
    public bool FoldConstants { get; init; } = true;

    /// <summary>The culture for context-sensitive folds. Default <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public CultureInfo ConverterCulture { get; init; } = CultureInfo.InvariantCulture;
}
