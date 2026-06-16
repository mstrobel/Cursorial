using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Severity of a <see cref="XamlDiagnostic"/>. The loader treats <see cref="Error"/> as fatal
/// (under <see cref="XamlDiagnosticMode.ThrowOnFirstError"/> it throws on the first one); warnings
/// are collected but never abort a parse.
/// </summary>
public enum XamlDiagnosticSeverity
{
    /// <summary>Informational; never aborts a parse.</summary>
    Info,

    /// <summary>A recoverable concern; collected, never aborts a parse.</summary>
    Warning,

    /// <summary>A fatal problem; under <see cref="XamlDiagnosticMode.ThrowOnFirstError"/> it aborts the parse.</summary>
    Error,
}

/// <summary>
/// Controls how the parser surfaces diagnostics. Mirrors the design-doc §4.2 contract.
/// </summary>
public enum XamlDiagnosticMode
{
    /// <summary>The first <see cref="XamlDiagnosticSeverity.Error"/> aborts the parse with a <see cref="XamlParseException"/>.</summary>
    ThrowOnFirstError,

    /// <summary>All diagnostics are collected into <see cref="XamlDocument.Diagnostics"/>; the parse never throws on a diagnostic.</summary>
    CollectAll,
}

/// <summary>
/// A single parse/resolve/instantiate diagnostic carrying a stable code, a human-readable message,
/// a severity, the source URI (if any), and a 1-based line + column.
/// </summary>
/// <remarks>
/// The <c>CUR1xxx</c> / <c>CUR2xxx</c> / <c>CUR3xxx</c> banding is fixed (design doc §4.2): parse
/// errors are <c>CUR1xxx</c>, resolution errors <c>CUR2xxx</c>, instantiation errors <c>CUR3xxx</c>.
/// Per the XD1 ledger pin, <see cref="Line"/> and <see cref="Column"/> are <b>always</b> 1-based and
/// non-zero for a real position — a diagnostic with <c>Line == 0</c> / <c>Column == 0</c> is a loader bug.
/// </remarks>
public readonly record struct XamlDiagnostic(
    string Code,
    string Message,
    XamlDiagnosticSeverity Severity,
    Uri? Source,
    int Line,
    int Column)
{
    /// <summary>Convenience: an <see cref="XamlDiagnosticSeverity.Error"/> diagnostic.</summary>
    public static XamlDiagnostic Error(string code, string message, Uri? source, int line, int column)
        => new(code, message, XamlDiagnosticSeverity.Error, source, line, column);

    /// <inheritdoc/>
    public override string ToString()
    {
        var location = Source is null ? $"({Line},{Column})" : $"{Source}({Line},{Column})";
        return $"{location}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
    }
}
