using System;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Thrown when XAML parsing, resolution, or instantiation fails. Derives from
/// <see cref="FormatException"/> (the design-doc §4.2 shape) so callers that already catch
/// format failures from converters see XAML failures too. Always carries a 1-based
/// <see cref="Line"/> + <see cref="Column"/> and the full collected diagnostic list, the first of
/// which is the one that aborted the parse.
/// </summary>
public sealed class XamlParseException : FormatException
{
    /// <summary>Creates an exception from a single fatal diagnostic.</summary>
    public XamlParseException(XamlDiagnostic diagnostic)
        : this(new[] { diagnostic })
    {
    }

    /// <summary>
    /// Creates an exception from a collected diagnostic list. The first element is treated as the
    /// fatal one (its code/line/column surface on the exception directly).
    /// </summary>
    public XamlParseException(IReadOnlyList<XamlDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        if (diagnostics.Count == 0)
            throw new ArgumentException("A XamlParseException requires at least one diagnostic.", nameof(diagnostics));

        var first = diagnostics[0];
        Code = first.Code;
        SourceUri = first.Source;
        Line = first.Line;
        Column = first.Column;
    }

    /// <summary>The code of the fatal (first) diagnostic.</summary>
    public string Code { get; }

    /// <summary>
    /// The source URI of the document, if known. Distinct from <see cref="System.Exception.Source"/>
    /// (the assembly name) — renamed so standard exception tooling that logs <c>Exception.Source</c>
    /// keeps working (P6 review P1-5).
    /// </summary>
    public Uri? SourceUri { get; }

    /// <summary>The 1-based line of the fatal diagnostic.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the fatal diagnostic.</summary>
    public int Column { get; }

    /// <summary>Every diagnostic collected up to (and including) the fatal one; the fatal one is first.</summary>
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; }

    private static string BuildMessage(IReadOnlyList<XamlDiagnostic> diagnostics)
        => diagnostics is { Count: > 0 } ? diagnostics[0].ToString() : "XAML parse failed.";
}
